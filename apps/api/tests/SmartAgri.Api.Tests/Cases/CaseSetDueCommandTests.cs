using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using SmartAgri.Api.Cases;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Api.Tests.Cases;

/// <summary>
/// The one-shot <c>case-set-due</c> subcommand (M7 plan §3 E, decision H; issue #256) against real
/// PostgreSQL: in Development and Testing it moves an open case's due time — also into the past, which
/// the API refuses — and records one actor-less <c>due-changed</c> event; in any other environment it
/// refuses before touching the database, so the case stays exactly as it was. The API-mode E2E
/// (<c>cases-api.cy.ts</c>) runs it with <c>cy.exec</c> to make a case overdue.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class CaseSetDueCommandTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Case-Set-Due-Pass-1!";

    private readonly AuthHostFixture _host;

    public CaseSetDueCommandTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task In_development_it_moves_the_due_time_into_the_past_with_one_due_changed_event_without_an_actor()
    {
        var (organization, caseId) = await CreateCaseAsync();
        var past = WholeSeconds(_host.Clock.GetUtcNow().AddHours(-2));

        var result = await RunAsync(
            _host.Factory.Services, "--organization", organization.Code.ToUpperInvariant(), "--case", caseId.ToString(), "--due", past.ToString("o"));

        result.Exit.ShouldBe(CaseSetDueCommand.ExitSuccess, result.Error);
        result.Output.ShouldContain(organization.Code);
        result.Output.ShouldContain(caseId.ToString());
        var (item, events) = await ReadAsync(organization, caseId);
        item.DueAt.ShouldBe(past);
        item.EventCount.ShouldBe(2);
        var changed = events.Last();
        (changed.Ordinal, changed.Action, changed.ActorAccountId, changed.DueAt, changed.Note)
            .ShouldBe((2, CaseEventAction.DueChanged, (Guid?)null, (DateTimeOffset?)past, (string?)null));
    }

    [Fact]
    public async Task In_testing_it_also_runs()
    {
        var (organization, caseId) = await CreateCaseAsync();
        var past = WholeSeconds(_host.Clock.GetUtcNow().AddDays(-1));

        var result = await RunAsync(
            ServicesFor("Testing"), "--organization", organization.Code, "--case", caseId.ToString(), "--due=" + past.ToString("o"));

        result.Exit.ShouldBe(CaseSetDueCommand.ExitSuccess, result.Error);
        (await ReadAsync(organization, caseId)).Case.DueAt.ShouldBe(past);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_development_and_testing_it_refuses_with_a_non_zero_exit_and_changes_nothing(string environment)
    {
        var (organization, caseId) = await CreateCaseAsync();
        var (before, beforeEvents) = await ReadAsync(organization, caseId);

        var result = await RunAsync(
            ServicesFor(environment), "--organization", organization.Code, "--case", caseId.ToString(), "--due", "2020-01-01T00:00:00Z");

        result.Exit.ShouldNotBe(CaseSetDueCommand.ExitSuccess);
        result.Exit.ShouldBe(CaseSetDueCommand.ExitUsage);
        result.Error.ShouldContain("只能在 Development 或 Testing 使用");
        result.Error.ShouldContain(environment);
        var (after, afterEvents) = await ReadAsync(organization, caseId);
        (after.DueAt, after.EventCount, after.UpdatedAt).ShouldBe((before.DueAt, before.EventCount, before.UpdatedAt));
        afterEvents.Count.ShouldBe(beforeEvents.Count);
    }

    [Fact]
    public async Task An_unknown_organization_another_organizations_case_or_a_closed_case_is_exit_1_and_nothing_changes()
    {
        var (organization, caseId) = await CreateCaseAsync();
        var (other, otherCaseId) = await CreateCaseAsync();
        var due = _host.Clock.GetUtcNow().AddHours(-1).ToString("o");

        (await RunAsync(_host.Factory.Services, "--organization", "no-such-org", "--case", caseId.ToString(), "--due", due))
            .Exit.ShouldBe(CaseSetDueCommand.ExitFailed);
        var foreign = await RunAsync(_host.Factory.Services, "--organization", organization.Code, "--case", otherCaseId.ToString(), "--due", due);
        foreign.Exit.ShouldBe(CaseSetDueCommand.ExitFailed);
        foreign.Error.ShouldContain("沒有案件");
        (await RunAsync(_host.Factory.Services, "--organization", organization.Code, "--case", Guid.CreateVersion7().ToString(), "--due", due))
            .Exit.ShouldBe(CaseSetDueCommand.ExitFailed);
        (await ReadAsync(other, otherCaseId)).Case.EventCount.ShouldBe(1);

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            var item = await dbContext.Cases.SingleAsync(candidate => candidate.Id == caseId, CancellationToken);
            dbContext.CaseEvents.Add(item.Cancel(item.CreatedByAccountId!.Value, "重複的案件", _host.Clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var closed = await RunAsync(_host.Factory.Services, "--organization", organization.Code, "--case", caseId.ToString(), "--due", due);
        closed.Exit.ShouldBe(CaseSetDueCommand.ExitFailed);
        closed.Error.ShouldContain("已結案");
        (await ReadAsync(organization, caseId)).Case.EventCount.ShouldBe(2);
    }

    [Fact]
    public async Task Missing_or_invalid_arguments_are_exit_2_and_help_is_exit_0()
    {
        var id = Guid.CreateVersion7().ToString();
        var cases = new[]
        {
            Array.Empty<string>(),
            ["--organization", "x", "--case", id],
            ["--organization", "x", "--due", "2026-10-01T00:00:00Z"],
            ["--case", id, "--due", "2026-10-01T00:00:00Z"],
            ["--organization", "x", "--case", "not-a-guid", "--due", "2026-10-01T00:00:00Z"],
            ["--organization", "x", "--case", Guid.Empty.ToString(), "--due", "2026-10-01T00:00:00Z"],
            ["--organization", "x", "--case", id, "--due", "yesterday"],
            ["--organization"],
            ["--nope"],
        };
        foreach (var args in cases)
        {
            (await RunAsync(_host.Factory.Services, args)).Exit.ShouldBe(CaseSetDueCommand.ExitUsage, string.Join(' ', args));
        }

        var help = await RunAsync(_host.Factory.Services, "--help");
        help.Exit.ShouldBe(CaseSetDueCommand.ExitSuccess);
        help.Output.ShouldStartWith("用法：case-set-due");
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>A host in <paramref name="environment"/> on the same database (and clock) as the fixture.</summary>
    private ServiceProvider ServicesFor(string environment) =>
        new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment })
            .AddSingleton(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_host.Postgres.ConnectionString).Options)
            .AddSingleton<TimeProvider>(_host.Clock)
            .BuildServiceProvider();

    /// <summary>PostgreSQL keeps microseconds and the test clock adds ticks: compare whole seconds.</summary>
    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);

    private static async Task<(int Exit, string Output, string Error)> RunAsync(IServiceProvider services, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CaseSetDueCommand.RunAsync(services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>An organization with one 待受理 case in 設備組, due in 72 hours.</summary>
    private async Task<(Organization Organization, Guid CaseId)> CreateCaseAsync()
    {
        var organization = await _host.CreateOrganizationAsync("時限商行");
        var creator = await _host.CreateAccountAsync(organization, "internal", Password, AccountRole.InternalEmployee);
        var now = _host.Clock.GetUtcNow();
        var group = CaseGroup.Create(organization.Id, "設備組", now);
        var type = CaseType.Create(organization.Id, "設備故障報修", "", group, 72, isActive: true, now);
        var (item, created) = Case.Create(CaseOrigin.Manual, type, group, creator.Id, "冷藏庫溫度降不下來", "", now.AddHours(72), CaseLinks.None, now);
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        dbContext.CaseGroups.Add(group);
        dbContext.CaseTypes.Add(type);
        dbContext.Cases.Add(item);
        dbContext.CaseEvents.Add(created);
        await dbContext.SaveChangesAsync(CancellationToken);
        return (organization, item.Id);
    }

    private async Task<(Case Case, List<CaseEvent> Events)> ReadAsync(Organization organization, Guid caseId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var item = await dbContext.Cases.AsNoTracking().SingleAsync(candidate => candidate.Id == caseId, CancellationToken);
        var events = await dbContext.CaseEvents.AsNoTracking()
            .Where(candidate => candidate.CaseId == caseId)
            .OrderBy(candidate => candidate.Ordinal)
            .ToListAsync(CancellationToken);
        return (item, events);
    }
}
