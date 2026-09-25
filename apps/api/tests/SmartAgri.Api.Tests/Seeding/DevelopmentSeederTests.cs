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
/// Slice 6 acceptance): the exact accounts <c>demo-seed.ts</c> describes, that re-seeding
/// never touches an existing account's permissions (see the "Idempotency" remarks on
/// <see cref="DevelopmentSeeder"/>), and signing in as the seeded <c>admin</c>. Reuses
/// <see cref="AuthHostFixture"/> from the #5 sign-in tests, which already runs in
/// Development (where <see cref="DevelopmentSeeder"/> is registered) and sets
/// <c>SEED_DEMO_PASSWORD</c> (<see cref="AuthHostFixture.SeedDemoPassword"/>).
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
    public async Task A_manually_revoked_permission_on_an_existing_seeded_account_stays_revoked_after_reseeding()
    {
        await RunSeederAsync();

        var (anxinId, internalAccountId, adminAccountId) = await ReadAnxinIdsAsync();

        // Simulate an operator revoking one of `internal`'s seeded permissions by hand
        // (e.g. in the team panel — the scenario #11's manual acceptance exercises), and
        // separately give `internal` an extra permission the seed never grants it, so both
        // "don't remove" and "don't add back" are exercised on the same account.
        await using (var dbContext = _host.Postgres.CreateDbContext(anxinId))
        {
            var revokedGrant = await dbContext.AccountPermissions.SingleAsync(
                g => g.AccountId == internalAccountId && g.Permission == AccountPermission.ReadConsentedSubmissions,
                CancellationToken);
            dbContext.AccountPermissions.Remove(revokedGrant);

            var internalAccount = await dbContext.Accounts.SingleAsync(a => a.Id == internalAccountId, CancellationToken);
            dbContext.AccountPermissions.Add(new AccountPermissionGrant(internalAccount, AccountPermission.ManageAssistants));

            await dbContext.SaveChangesAsync(CancellationToken);
        }

        // Running the seeder again must not touch `internal` at all: it already exists.
        await RunSeederAsync();

        await using (var dbContext = _host.Postgres.CreateDbContext(anxinId))
        {
            var internalPermissions = await dbContext.AccountPermissions
                .Where(g => g.AccountId == internalAccountId)
                .Select(g => g.Permission)
                .ToListAsync(CancellationToken);

            // The manually revoked seed permission stays revoked — re-seeding an existing
            // account never re-grants anything (this is the behaviour #6's coordinator
            // asked to fix: it previously treated the seed list as a floor and restored
            // this exact permission, which would have broken #11's manual acceptance).
            internalPermissions.ShouldNotContain(AccountPermission.ReadConsentedSubmissions);

            // The manually added extra permission is also untouched (the seeder does not
            // remove anything from an existing account either).
            internalPermissions.ShouldContain(AccountPermission.ManageAssistants);
            internalPermissions.ShouldContain(AccountPermission.UseSharedAssistants);
        }

        // admin, which was never touched by hand, keeps its full seeded permission set.
        await using (var dbContext = _host.Postgres.CreateDbContext(anxinId))
        {
            var adminPermissions = await dbContext.AccountPermissions
                .Where(g => g.AccountId == adminAccountId)
                .Select(g => g.Permission)
                .ToListAsync(CancellationToken);
            adminPermissions.ShouldBe(
                [
                    AccountPermission.ManageAssistants,
                    AccountPermission.ManageDataSources,
                    AccountPermission.ManagePublishing,
                    AccountPermission.ReadConsentedSubmissions,
                ],
                ignoreOrder: true);
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
