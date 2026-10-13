using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Shouldly;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Setup;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>
/// <c>add-organization</c> and <c>list-organizations</c> against real PostgreSQL (#333). The
/// fixture database is migrated and starts with no organization, so each test creates the
/// first organization itself (as <c>setup</c> would leave it) and then adds a second one.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class AddOrganizationTests : IClassFixture<AuthHostFixture>
{
    private const string FirstPassword = "First-Shop-Pass-1!";
    private const string NewPassword = "Second-Shop-New-Pass-2!";

    private readonly AuthHostFixture _host;

    public AddOrganizationTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_second_organization_admin_signs_in_with_the_one_time_password_changes_it_and_sees_only_its_own_data()
    {
        var first = await _host.CreateOrganizationAsync("第一家店");
        await _host.CreateAccountAsync(first, "admin", FirstPassword, AccountRole.SmbAdmin, "第一家店管理者", AccountPermission.ManageAssistants);
        await _host.CreateAccountAsync(first, "clerk", FirstPassword, AccountRole.InternalEmployee, "第一家店店員", AccountPermission.UseSharedAssistants);
        var code = "shop-" + Guid.NewGuid().ToString("N")[..8];

        var telemetry = new TelemetryCapture();
        using var factory = _host.Factory.WithWebHostBuilder(telemetry.Attach);
        var console = new FakeSetupConsole(interactive: false);
        var exitCode = await SetupCommand.RunAddOrganizationAsync(
            factory.Services,
            ["--organization-name", "第二家店", "--organization-code", code.ToUpperInvariant(), "--admin-login", "admin", "--admin-display-name", "第二家店管理者"],
            console,
            CancellationToken);
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        exitCode.ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        var password = console.PrintedPassword.ShouldNotBeNull();
        telemetry.Logs.ShouldContain(entry => entry.Contains($"add-organization created organization {code}", StringComparison.Ordinal));
        telemetry.Spans.ShouldContain(span => span.StartsWith("Npgsql", StringComparison.Ordinal));
        telemetry.All.ShouldNotContain(entry => entry.Contains(password, StringComparison.Ordinal));

        // Stored as setup stores its admin: smb-admin, every permission, must change password.
        await using (var dbContext = _host.Postgres.CreateDbContext())
        {
            var second = await dbContext.Organizations.SingleAsync(organization => organization.Code == code, CancellationToken);
            second.Name.ShouldBe("第二家店");
            await using var scoped = _host.Postgres.CreateDbContext(second.Id);
            var admin = (await scoped.Accounts.ToListAsync(CancellationToken)).ShouldHaveSingleItem();
            admin.Role.ShouldBe(AccountRole.SmbAdmin);
            admin.PasswordChangeRequired.ShouldBeTrue();
            admin.PasswordHash.ShouldNotBeNull().ShouldNotContain(password);
            (await scoped.AccountPermissions.Select(grant => grant.Permission).ToListAsync(CancellationToken))
                .ShouldBe(Enum.GetValues<AccountPermission>(), ignoreOrder: true);
        }

        // Sign in with the organization code: /me works, everything else is gated until the change.
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(code, "admin", password);
        var me = await spa.GetMeJsonAsync(token.AccessToken);
        me.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
        me.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(
            (await _host.Postgres.CreateDbContext().Organizations.SingleAsync(o => o.Code == code, CancellationToken)).Id);
        (await spa.GetAsync("/api/v1/team", token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await spa.ChangePasswordAsync(token.AccessToken, password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The first organization has two accounts; the second one's admin sees only itself.
        var team = await spa.GetAsync("/api/v1/team", token.AccessToken);
        team.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await team.Content.ReadAsStringAsync(CancellationToken));
        var members = body.RootElement.GetProperty("members").EnumerateArray().ToList();
        members.ShouldHaveSingleItem().GetProperty("displayName").GetString().ShouldBe("第二家店管理者");

        // The first organization's admin still signs in and sees its own two accounts.
        using var firstSpa = _host.CreateSpaClient();
        var firstToken = await firstSpa.SignInAsync(first.Code, "admin", FirstPassword);
        using var firstBody = JsonDocument.Parse(await (await firstSpa.GetAsync("/api/v1/team", firstToken.AccessToken)).Content.ReadAsStringAsync(CancellationToken));
        firstBody.RootElement.GetProperty("members").GetArrayLength().ShouldBe(2);

        // list-organizations shows both, with counts, and nothing personal.
        var list = new StringWriter();
        (await ListOrganizationsCommand.RunAsync(_host.Factory.Services, [], list, new StringWriter(), CancellationToken))
            .ShouldBe(ListOrganizationsCommand.ExitSuccess);
        var output = list.ToString();
        output.ShouldContain($"{code}\t第二家店\t");
        // The account count stays the fourth column (#344 appends the usage columns after it).
        Columns(output, code)[3].ShouldBe("1");
        Columns(output, first.Code)[3].ShouldBe("2");
        foreach (var personal in new[] { "admin", "clerk", "管理者", "店員" })
        {
            output.ShouldNotContain(personal);
        }
    }

    [Fact]
    public async Task A_duplicate_code_in_any_case_is_refused_and_writes_nothing()
    {
        var first = await _host.CreateOrganizationAsync("既有的店");
        await using var before = _host.Postgres.CreateDbContext();
        var organizationsBefore = await before.Organizations.CountAsync(CancellationToken);

        var console = new FakeSetupConsole(interactive: false);
        var exitCode = await SetupCommand.RunAddOrganizationAsync(
            _host.Factory.Services,
            ["--organization-name", "撞代碼的店", "--organization-code", first.Code.ToUpperInvariant(), "--admin-login", "admin"],
            console,
            CancellationToken);

        exitCode.ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain(first.Code);
        console.PrintedPassword.ShouldBeNull();
        await using var after = _host.Postgres.CreateDbContext();
        (await after.Organizations.CountAsync(CancellationToken)).ShouldBe(organizationsBefore);
        await using var scoped = _host.Postgres.CreateDbContext(first.Id);
        (await scoped.Accounts.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Two_adds_of_the_same_code_at_once_create_exactly_one_organization()
    {
        await _host.CreateOrganizationAsync("先存在的店");
        var code = "race-" + Guid.NewGuid().ToString("N")[..8];
        string[] args = ["--organization-name", "搶代碼的店", "--organization-code", code, "--admin-login", "admin"];

        var consoles = new[] { new FakeSetupConsole(interactive: false), new FakeSetupConsole(interactive: false) };
        var exitCodes = await Task.WhenAll(consoles.Select(console =>
            SetupCommand.RunAddOrganizationAsync(_host.Factory.Services, args, console, CancellationToken)));

        exitCodes.Count(exit => exit == SetupCommand.ExitSuccess).ShouldBe(1);
        exitCodes.Count(exit => exit == SetupCommand.ExitRefused).ShouldBe(1);
        // The loser is refused cleanly (duplicate code or "try again"), not through the generic failure path.
        consoles.Single(console => console.PrintedPassword is null).ErrorWriter.ToString().ShouldNotContain("add-organization 失敗");
        consoles.Count(console => console.PrintedPassword is not null).ShouldBe(1);
        await using var dbContext = _host.Postgres.CreateDbContext();
        (await dbContext.Organizations.CountAsync(organization => organization.Code == code, CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Add_organization_on_an_unmigrated_database_is_refused_and_points_to_migrate()
    {
        var connectionString = await CreateEmptyDatabaseAsync("add_org_unmigrated");
        using var factory = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", connectionString));
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await SetupCommand.RunAddOrganizationAsync(
            factory.Services,
            ["--organization-name", "店", "--organization-code", "shop", "--admin-login", "admin"],
            console,
            CancellationToken);

        exitCode.ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("migrate");
        console.PrintedPassword.ShouldBeNull();
    }

    [Fact]
    public async Task List_organizations_rejects_arguments_and_prints_usage_for_help()
    {
        var error = new StringWriter();
        (await ListOrganizationsCommand.RunAsync(_host.Factory.Services, ["--all"], new StringWriter(), error, CancellationToken))
            .ShouldBe(ListOrganizationsCommand.ExitUsage);
        error.ToString().ShouldContain("--all");

        var output = new StringWriter();
        (await ListOrganizationsCommand.RunAsync(_host.Factory.Services, ["--help"], output, error, CancellationToken))
            .ShouldBe(ListOrganizationsCommand.ExitSuccess);
        output.ToString().ShouldContain("list-organizations");
    }

    [Fact]
    public async Task List_organizations_shows_this_months_usage_the_limit_and_the_share()
    {
        // 15 November 2026 in Asia/Taipei: November is [2026-10-31 16:00 UTC, 2026-11-30 16:00 UTC).
        var now = Utc(2026, 11, 15, 4, 0, 0, 0);
        var lastMonth = Utc(2026, 10, 20, 0, 0, 0, 0);

        var limited = await _host.CreateOrganizationAsync("有上限的店");
        await SetLimitAsync(limited, 1_000_000);
        await AddCallAsync(limited, ModelInvocationPurpose.GenerateAnswer, 300_000, 100_000, now.AddDays(-1));
        await AddCallAsync(limited, ModelInvocationPurpose.PublicAnswer, 40_000, 10_000, now.AddHours(-1));
        await AddCallAsync(limited, ModelInvocationPurpose.EmbedQuery, 999_999, null, now.AddHours(-1)); // embeddings never count
        await AddCallAsync(limited, ModelInvocationPurpose.GenerateAnswer, 500_000, 0, lastMonth);

        var unlimited = await _host.CreateOrganizationAsync("沒設上限的店");
        await AddCallAsync(unlimited, ModelInvocationPurpose.LineAnswer, 1_000, 234, now.AddDays(-2));

        var idle = await _host.CreateOrganizationAsync("本月沒用的店");
        await SetLimitAsync(idle, 5_000);

        var lastMonthOnly = await _host.CreateOrganizationAsync("只有上個月用的店");
        await SetLimitAsync(lastMonthOnly, 10_000);
        await AddCallAsync(lastMonthOnly, ModelInvocationPurpose.GenerateAnswer, 9_000, 3_000, lastMonth);

        var near = await _host.CreateOrganizationAsync("快用完的店");
        await SetLimitAsync(near, 10_000);
        await AddCallAsync(near, ModelInvocationPurpose.GenerateAnswer, 8_000, 1_999, now.AddMinutes(-5));

        var exceeded = await _host.CreateOrganizationAsync("用完的店");
        await SetLimitAsync(exceeded, 10_000);
        await AddCallAsync(exceeded, ModelInvocationPurpose.GenerateAnswer, 10_000, 0, now.AddMinutes(-5));

        var suspended = await _host.CreateOrganizationAsync("上限為零的店");
        await SetLimitAsync(suspended, 0);

        await using var factory = AtTime(now);
        var output = await ListAsync(factory);
        var defaultLimit = factory.Services.GetRequiredService<IOptions<PublicChannelsOptions>>().Value.EffectiveDefaultMonthlyTokenLimit
            .ToString("N0", CultureInfo.InvariantCulture);

        output.Split('\n')[0].ShouldBe("組織代碼\t名稱\t建立時間(UTC)\t帳號數\t本月用量\t上限\t使用比例");
        Columns(output, limited.Code)[4..].ShouldBe(["450,000", "1,000,000", "45.0%"]);
        Columns(output, unlimited.Code)[4..].ShouldBe(
            ["1,234", $"未設上限（預設 {defaultLimit}）", "0.0%"]);
        Columns(output, idle.Code)[4..].ShouldBe(["0", "5,000", "0.0%"]);
        Columns(output, lastMonthOnly.Code)[4..].ShouldBe(["0", "10,000", "0.0%"]);
        Columns(output, near.Code)[4..].ShouldBe(["9,999", "10,000", "99.9%（接近上限）"]);
        Columns(output, exceeded.Code)[4..].ShouldBe(["10,000", "10,000", "100.0%（已達上限）"]);
        Columns(output, suspended.Code)[4..].ShouldBe(["0", "0", "-（已達上限）"]);
        output.ShouldContain($"本月為 2026-11（Asia/Taipei）；用量為對話模型 token（輸入＋輸出），未設上限的組織套用部署預設值 {defaultLimit}。");
    }

    [Fact]
    public async Task List_organizations_and_the_quota_check_put_the_month_boundary_at_the_same_instant()
    {
        // November 2026 starts at 00:00 on 1 November in Asia/Taipei, 2026-10-31 16:00 UTC.
        var lastOfOctober = Utc(2026, 10, 31, 15, 59, 59, 999);
        var firstOfNovember = Utc(2026, 10, 31, 16, 0, 0, 0);
        var organization = await _host.CreateOrganizationAsync("跨月的店");
        await SetLimitAsync(organization, 1_000);
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 7, 3, lastOfOctober);
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 200, 100, firstOfNovember);

        foreach (var (now, month, used, share) in new[]
                 {
                     (lastOfOctober, "2026-10", 10L, "1.0%"),
                     (firstOfNovember, "2026-11", 300L, "30.0%"),
                 })
        {
            await using var factory = AtTime(now);

            // The quota check the visitor and LINE channels run, as its own fresh instance (no shared cache).
            var services = factory.Services;
            var quota = await new OrganizationTokenUsage(
                    services.GetRequiredService<IOrganizationTokenUsageSource>(),
                    new FixedClock(now),
                    services.GetRequiredService<IOptions<StatisticsOptions>>().Value.TryResolve().ShouldNotBeNull(),
                    services.GetRequiredService<IOptions<PublicChannelsOptions>>().Value.EffectiveDefaultMonthlyTokenLimit)
                .GetAsync(organization.Id, CancellationToken);
            quota.Month.ShouldBe(month);
            quota.UsedTokens.ShouldBe(used);

            var output = await ListAsync(factory);
            Columns(output, organization.Code)[4..].ShouldBe([quota.UsedTokens.ToString("N0", CultureInfo.InvariantCulture), "1,000", share]);
            output.ShouldContain($"本月為 {month}（");
        }
    }

    private WebApplicationFactory<Program> AtTime(DateTimeOffset now) =>
        _host.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<TimeProvider>(new FixedClock(now))));

    private static async Task<string> ListAsync(WebApplicationFactory<Program> factory)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        (await ListOrganizationsCommand.RunAsync(factory.Services, [], output, error, CancellationToken))
            .ShouldBe(ListOrganizationsCommand.ExitSuccess, error.ToString());
        return output.ToString();
    }

    private static string[] Columns(string output, string code) =>
        output.Split('\n').Single(line => line.StartsWith($"{code}\t", StringComparison.Ordinal)).Split('\t');

    private async Task SetLimitAsync(Organization organization, long? limit)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var tracked = await dbContext.Organizations.SingleAsync(candidate => candidate.Id == organization.Id, CancellationToken);
        tracked.SetMonthlyTokenLimit(limit);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task AddCallAsync(Organization organization, ModelInvocationPurpose purpose, long? input, long? output, DateTimeOffset at)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        dbContext.ModelInvocations.Add(ModelInvocation.Record(
            organization.Id, null, null, purpose, "fake", "fake-model", input, output, 12, true, at));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second, int millisecond) =>
        new(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        await using (var connection = new Npgsql.NpgsqlConnection(_host.Postgres.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken);
            await using var command = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(CancellationToken);
        }

        return new Npgsql.NpgsqlConnectionStringBuilder(_host.Postgres.ConnectionString) { Database = name }.ConnectionString;
    }
}

public class ListOrganizationsCreatedAtTests
{
    [Fact]
    public void The_creation_time_comes_from_a_version_7_id_and_other_ids_have_none()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var created = ListOrganizationsCommand.CreatedAt(Guid.CreateVersion7()).ShouldNotBeNull();
        created.ShouldBeInRange(before, DateTimeOffset.UtcNow.AddSeconds(1));
        ListOrganizationsCommand.CreatedAt(Guid.NewGuid()).ShouldBeNull();
    }
}
