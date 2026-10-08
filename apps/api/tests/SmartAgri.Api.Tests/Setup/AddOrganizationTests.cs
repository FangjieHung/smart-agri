using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Shouldly;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.Setup;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
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
        output.Split('\n').Single(line => line.StartsWith($"{code}\t", StringComparison.Ordinal)).ShouldEndWith("\t1");
        output.Split('\n').Single(line => line.StartsWith($"{first.Code}\t", StringComparison.Ordinal)).ShouldEndWith("\t2");
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
