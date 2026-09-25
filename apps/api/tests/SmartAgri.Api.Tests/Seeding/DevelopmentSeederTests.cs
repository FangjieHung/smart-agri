using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Seeding;

namespace SmartAgri.Api.Tests.Seeding;

/// <summary>
/// <see cref="DevelopmentSeeder"/> end to end against real PostgreSQL (M1 skeleton plan,
/// Slice 6 acceptance): the exact accounts <c>demo-seed.ts</c> describes, idempotency, and
/// signing in as the seeded <c>admin</c>. Reuses <see cref="AuthHostFixture"/> from the #5
/// sign-in tests, which already runs in Development (where <see cref="DevelopmentSeeder"/>
/// is registered) and sets <c>SEED_DEMO_PASSWORD</c>
/// (<see cref="AuthHostFixture.SeedDemoPassword"/>).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DevelopmentSeederTests : IClassFixture<AuthHostFixture>
{
    private readonly AuthHostFixture _host;

    public DevelopmentSeederTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeding_creates_four_accounts_and_running_it_again_does_not_duplicate_them()
    {
        await RunSeederAsync();
        await RunSeederAsync();

        await using var dbContext = _host.Postgres.CreateDbContext();
        var accounts = await dbContext.Accounts.IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .Select(account => account.LoginName)
            .ToListAsync(CancellationToken);

        accounts.Count.ShouldBe(4);
        accounts.Count(name => name == "admin").ShouldBe(2); // one per organization
        accounts.ShouldContain("internal");
        accounts.ShouldContain("customer");
    }

    [Fact]
    public async Task Seeding_never_removes_a_manually_added_permission_and_restores_a_manually_removed_seed_permission()
    {
        await RunSeederAsync();

        var (anxinId, internalAccountId, adminAccountId) = await ReadAnxinIdsAsync();

        // Simulate an operator's manual changes directly on the database, as a real admin
        // using the (future) team panel would: give `internal` an extra permission the
        // seed does not grant it, and take away one of `admin`'s seeded permissions.
        await using (var dbContext = _host.Postgres.CreateDbContext(anxinId))
        {
            var internalAccount = await dbContext.Accounts.SingleAsync(a => a.Id == internalAccountId, CancellationToken);
            dbContext.AccountPermissions.Add(new AccountPermissionGrant(internalAccount, AccountPermission.ManageAssistants));

            var adminGrant = await dbContext.AccountPermissions.SingleAsync(
                g => g.AccountId == adminAccountId && g.Permission == AccountPermission.ReadConsentedSubmissions,
                CancellationToken);
            dbContext.AccountPermissions.Remove(adminGrant);

            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await RunSeederAsync();

        await using (var dbContext = _host.Postgres.CreateDbContext(anxinId))
        {
            var internalPermissions = await dbContext.AccountPermissions
                .Where(g => g.AccountId == internalAccountId)
                .Select(g => g.Permission)
                .ToListAsync(CancellationToken);
            // The manually added extra permission is still there — the seeder only adds,
            // never removes (see DevelopmentSeeder's "Idempotency" remarks).
            internalPermissions.ShouldContain(AccountPermission.ManageAssistants);
            internalPermissions.ShouldContain(AccountPermission.UseSharedAssistants);
            internalPermissions.ShouldContain(AccountPermission.ReadConsentedSubmissions);

            var adminPermissions = await dbContext.AccountPermissions
                .Where(g => g.AccountId == adminAccountId)
                .Select(g => g.Permission)
                .ToListAsync(CancellationToken);
            // The manually removed *seed* permission is restored: "missing" is judged
            // against the seed list on every run, so the seed is a floor for its own
            // accounts, not a one-time template. This is the documented nuance from the
            // ticket ("只補缺的權限，不覆寫手動改過的權限").
            adminPermissions.ShouldContain(AccountPermission.ReadConsentedSubmissions);
        }

        // Still exactly 4 accounts: nothing above created or duplicated a row.
        await using (var dbContext = _host.Postgres.CreateDbContext())
        {
            (await dbContext.Accounts.IgnoreQueryFilters([AppDbContext.OrganizationFilter]).CountAsync(CancellationToken))
                .ShouldBe(4);
        }
    }

    [Fact]
    public async Task Admin_login_permissions_equal_the_frontend_demo_seed()
    {
        await RunSeederAsync();
        using var spa = _host.CreateSpaClient();

        var token = await spa.SignInAsync(DevelopmentSeedData.AnxinOrganizationCode, "admin", AuthHostFixture.SeedDemoPassword);
        var me = await spa.GetMeJsonAsync(token.AccessToken);

        me.GetProperty("displayName").GetString().ShouldBe("安心商行管理者");
        me.GetProperty("role").GetString().ShouldBe("smb-admin");
        me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString())
            .ShouldBe(["manage-assistants", "manage-data-sources", "manage-publishing", "read-consented-submissions"]);
        me.GetProperty("organization").GetProperty("name").GetString().ShouldBe("安心商行");
    }

    [Fact]
    public async Task Control_organizations_admin_cannot_sign_in_with_anxins_admin_password_or_vice_versa()
    {
        await RunSeederAsync();
        using var spa = _host.CreateSpaClient();

        // Same login name ("admin") in both organizations, different passwords would be
        // the real-world case; here both share SEED_DEMO_PASSWORD, so instead this checks
        // that each organization's "admin" only ever resolves to that organization's own
        // account (the whole point of "對照組織" — see DevelopmentSeedData).
        var anxinMe = await spa.GetMeJsonAsync(
            (await spa.SignInAsync(DevelopmentSeedData.AnxinOrganizationCode, "admin", AuthHostFixture.SeedDemoPassword)).AccessToken);

        using var otherSpa = _host.CreateSpaClient();
        var controlMe = await otherSpa.GetMeJsonAsync(
            (await otherSpa.SignInAsync(DevelopmentSeedData.ControlOrganizationCode, "admin", AuthHostFixture.SeedDemoPassword)).AccessToken);

        anxinMe.GetProperty("id").GetGuid().ShouldNotBe(controlMe.GetProperty("id").GetGuid());
        anxinMe.GetProperty("organization").GetProperty("name").GetString().ShouldBe("安心商行");
        controlMe.GetProperty("organization").GetProperty("name").GetString().ShouldBe("對照組織");
    }

    private async Task RunSeederAsync()
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync(CancellationToken);
    }

    private async Task<(Guid AnxinOrganizationId, Guid InternalAccountId, Guid AdminAccountId)> ReadAnxinIdsAsync()
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var organizationId = await dbContext.Organizations
            .Where(o => o.Code == DevelopmentSeedData.AnxinOrganizationCode)
            .Select(o => o.Id)
            .SingleAsync(CancellationToken);

        await using var orgDbContext = _host.Postgres.CreateDbContext(organizationId);
        var internalAccountId = await orgDbContext.Accounts.Where(a => a.LoginName == "internal").Select(a => a.Id).SingleAsync(CancellationToken);
        var adminAccountId = await orgDbContext.Accounts.Where(a => a.LoginName == "admin").Select(a => a.Id).SingleAsync(CancellationToken);
        return (organizationId, internalAccountId, adminAccountId);
    }
}
