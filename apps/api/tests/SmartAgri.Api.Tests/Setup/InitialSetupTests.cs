using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Trace;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Setup;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>
/// <c>setup</c> against real PostgreSQL (M1 plan, Slice 11 acceptance). The fixture's
/// database is migrated (with the <c>admin-spa</c> client, as <c>migrate</c> leaves it) and
/// has no organization, so exactly one test in this class may create one.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class InitialSetupTests : IClassFixture<AuthHostFixture>
{
    private readonly AuthHostFixture _host;

    public InitialSetupTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_creates_one_organization_and_admin_once_and_the_password_only_reaches_the_terminal()
    {
        var telemetry = new TelemetryCapture();
        using var factory = _host.Factory.WithWebHostBuilder(telemetry.Attach);
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await SetupCommand.RunAsync(
            factory.Services,
            ["--organization-name", "安心農場", "--organization-code", "AnXin", "--admin-login", "admin", "--admin-display-name", "王小明"],
            console,
            CancellationToken);
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        exitCode.ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        var password = console.PrintedPassword.ShouldNotBeNull();

        // One organization, one administrator: smb-admin, all seven permissions, must
        // change password, lockout on, password stored only as an Identity hash.
        await using (var dbContext = _host.Postgres.CreateDbContext())
        {
            var organization = (await dbContext.Organizations.ToListAsync(CancellationToken)).ShouldHaveSingleItem();
            organization.Name.ShouldBe("安心農場");
            organization.Code.ShouldBe("anxin");

            await using var scoped = _host.Postgres.CreateDbContext(organization.Id);
            var admin = (await scoped.Accounts.ToListAsync(CancellationToken)).ShouldHaveSingleItem();
            admin.LoginName.ShouldBe("admin");
            admin.DisplayName.ShouldBe("王小明");
            admin.Role.ShouldBe(AccountRole.SmbAdmin);
            admin.PasswordChangeRequired.ShouldBeTrue();
            admin.LockoutEnabled.ShouldBeTrue();
            admin.PasswordHash.ShouldNotBeNullOrEmpty();
            admin.PasswordHash.ShouldNotContain(password);
            new PasswordHasher<Account>().VerifyHashedPassword(admin, admin.PasswordHash, password)
                .ShouldNotBe(PasswordVerificationResult.Failed);
            (await scoped.AccountPermissions.Where(grant => grant.AccountId == admin.Id).Select(grant => grant.Permission).ToListAsync(CancellationToken))
                .ShouldBe(Enum.GetValues<AccountPermission>(), ignoreOrder: true);
        }

        // The capture saw the run (its log entry and its SQL spans) but never the password.
        telemetry.Logs.ShouldContain(entry => entry.Contains("Initial setup created organization anxin", StringComparison.Ordinal));
        telemetry.OpenTelemetryLogs.ShouldContain(entry => entry.Contains("Initial setup created organization anxin", StringComparison.Ordinal));
        telemetry.Spans.ShouldContain(span => span.StartsWith("Npgsql", StringComparison.Ordinal));
        telemetry.All.ShouldNotContain(entry => entry.Contains(password, StringComparison.Ordinal));

        // Second run: refused, non-zero, nothing added.
        var again = new FakeSetupConsole(interactive: false);
        var secondExitCode = await SetupCommand.RunAsync(
            _host.Factory.Services,
            ["--organization-name", "第二個", "--organization-code", "second", "--admin-login", "admin2"],
            again,
            CancellationToken);

        secondExitCode.ShouldNotBe(SetupCommand.ExitSuccess);
        again.ErrorWriter.ToString().ShouldContain("已經有組織");
        again.PrintedPassword.ShouldBeNull();
        await using (var dbContext = _host.Postgres.CreateDbContext())
        {
            (await dbContext.Organizations.CountAsync(CancellationToken)).ShouldBe(1);
        }

        // The one-time password signs in (organization code optional: single organization),
        // and then only /me is usable.
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(null, "admin", password);
        var me = await spa.GetMeJsonAsync(token.AccessToken);
        me.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
        me.GetProperty("role").GetString().ShouldBe("smb-admin");
        me.GetProperty("permissions").GetArrayLength().ShouldBe(7);

        var probe = await spa.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken);
        probe.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = JsonDocument.Parse(await probe.Content.ReadAsStringAsync(CancellationToken));
        body.RootElement.GetProperty("reason").GetString().ShouldBe(ForbiddenReason.PasswordChangeRequired.WireName);
    }

    [Fact]
    public async Task Setup_on_an_unmigrated_database_is_refused_and_points_to_migrate()
    {
        var connectionString = await CreateEmptyDatabaseAsync("setup_unmigrated");
        using var factory = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", connectionString));
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await SetupCommand.RunAsync(
            factory.Services,
            ["--organization-name", "安心農場", "--organization-code", "anxin", "--admin-login", "admin"],
            console,
            CancellationToken);

        exitCode.ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("migrate");
        console.PrintedPassword.ShouldBeNull();
    }

    private async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        await using (var connection = new NpgsqlConnection(_host.Postgres.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_host.Postgres.ConnectionString) { Database = name }.ConnectionString;
    }
}
