using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Tenancy;

/// <summary>
/// Organization isolation against real PostgreSQL (M1 plan, Slice 4 acceptance). All tests
/// share one database per class, so each creates its own organizations with unique codes
/// and only asserts on those.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class OrganizationIsolationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string UniqueViolation = "23505";

    private readonly PostgresFixture _postgres;

    public OrganizationIsolationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var dbContext = _postgres.CreateDbContext();
        await dbContext.Database.MigrateAsync(CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Same_login_name_in_two_organizations_succeeds_but_not_twice_in_one()
    {
        var (organizationA, organizationB) = await CreateTwoOrganizationsAsync();

        await AddAccountAsync(organizationA, "admin");
        await AddAccountAsync(organizationB, "admin");

        var duplicate = await Should.ThrowAsync<DbUpdateException>(() => AddAccountAsync(organizationA, "admin"));
        duplicate.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(UniqueViolation);

        // Differing only in case is still the same name.
        await Should.ThrowAsync<DbUpdateException>(() => AddAccountAsync(organizationA, "ADMIN"));
    }

    [Fact]
    public async Task Per_organization_login_name_index_rejects_duplicates_on_its_own()
    {
        // Identity's global user-name index would also reject a plain duplicate; prove the
        // (OrganizationId, NormalizedLoginName) index is enforced independently of it.
        var (organizationA, _) = await CreateTwoOrganizationsAsync();
        await AddAccountAsync(organizationA, "operator");

        await using var dbContext = _postgres.CreateDbContext(organizationA.Id);
        var duplicate = Account.Create(organizationA, "operator", "Operator", AccountRole.InternalEmployee);
        duplicate.UserName = $"{organizationA.Code}/operator-bypass";
        duplicate.NormalizedUserName = duplicate.UserName.ToUpperInvariant();
        dbContext.Accounts.Add(duplicate);

        var exception = await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(CancellationToken));
        var postgresException = exception.InnerException.ShouldBeOfType<PostgresException>();
        postgresException.SqlState.ShouldBe(UniqueViolation);
        postgresException.ConstraintName.ShouldBe("IX_AspNetUsers_OrganizationId_NormalizedLoginName");
    }

    [Fact]
    public async Task A_context_sees_only_its_own_organization_and_no_organization_sees_nothing()
    {
        var (organizationA, organizationB) = await CreateTwoOrganizationsAsync();
        var accountA = await AddAccountAsync(organizationA, "admin", AccountPermission.ManageAssistants);
        var accountB = await AddAccountAsync(organizationB, "admin", AccountPermission.ReadOwnTracking);

        await using (var asA = _postgres.CreateDbContext(organizationA.Id))
        {
            var accounts = await asA.Accounts.ToListAsync(CancellationToken);
            accounts.ShouldNotBeEmpty();
            accounts.ShouldAllBe(account => account.OrganizationId == organizationA.Id);
            accounts.Select(account => account.Id).ShouldContain(accountA.Id);
            accounts.Select(account => account.Id).ShouldNotContain(accountB.Id);

            // Even asking for B's row by key finds nothing.
            (await asA.Accounts.FindAsync([accountB.Id], CancellationToken)).ShouldBeNull();
            (await asA.Accounts.SingleOrDefaultAsync(account => account.Id == accountB.Id, CancellationToken)).ShouldBeNull();

            var permissions = await asA.AccountPermissions.ToListAsync(CancellationToken);
            permissions.ShouldAllBe(grant => grant.OrganizationId == organizationA.Id);
            permissions.ShouldContain(grant => grant.AccountId == accountA.Id);
        }

        await using (var withoutOrganization = _postgres.CreateDbContext(organizationId: null))
        {
            (await withoutOrganization.Accounts.ToListAsync(CancellationToken)).ShouldBeEmpty();
            (await withoutOrganization.AccountPermissions.ToListAsync(CancellationToken)).ShouldBeEmpty();
            (await withoutOrganization.Accounts.CountAsync(CancellationToken)).ShouldBe(0);

            // Organizations are the tenant boundary, not scoped data.
            (await withoutOrganization.Organizations.CountAsync(CancellationToken)).ShouldBeGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public async Task Saving_a_row_of_another_organization_throws_and_writes_nothing()
    {
        var (organizationA, organizationB) = await CreateTwoOrganizationsAsync();

        await using (var asA = _postgres.CreateDbContext(organizationA.Id))
        {
            asA.Accounts.Add(Account.Create(organizationA, "legit", "Legit", AccountRole.SmbAdmin));
            asA.Accounts.Add(Account.Create(organizationB, "intruder", "Intruder", AccountRole.SmbAdmin));

            await Should.ThrowAsync<CrossOrganizationWriteException>(() => asA.SaveChangesAsync(CancellationToken));
        }

        // Neither row was written: the save is refused as a whole.
        await using (var asB = _postgres.CreateDbContext(organizationB.Id))
        {
            (await asB.Accounts.CountAsync(CancellationToken)).ShouldBe(0);
        }

        await using (var asA = _postgres.CreateDbContext(organizationA.Id))
        {
            (await asA.Accounts.CountAsync(CancellationToken)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Saving_without_an_organization_throws_and_writes_nothing()
    {
        var (organizationA, _) = await CreateTwoOrganizationsAsync();

        await using (var withoutOrganization = _postgres.CreateDbContext(organizationId: null))
        {
            withoutOrganization.Accounts.Add(Account.Create(organizationA, "orphan", "Orphan", AccountRole.SmbAdmin));
            await Should.ThrowAsync<CrossOrganizationWriteException>(
                () => withoutOrganization.SaveChangesAsync(CancellationToken));
        }

        await using var asA = _postgres.CreateDbContext(organizationA.Id);
        (await asA.Accounts.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Forged_update_of_another_organizations_row_changes_nothing()
    {
        // Attaching B's account id under A while claiming OrganizationId = A passes the
        // interceptor, but OrganizationId is a concurrency token, so the UPDATE matches no
        // row and EF reports a concurrency failure instead of overwriting B's data.
        var (organizationA, organizationB) = await CreateTwoOrganizationsAsync();
        var accountB = await AddAccountAsync(organizationB, "admin");

        await using (var asA = _postgres.CreateDbContext(organizationA.Id))
        {
            var forged = Account.Create(organizationA, "admin", "Hijacked", AccountRole.SmbAdmin);
            forged.Id = accountB.Id;
            forged.ConcurrencyStamp = accountB.ConcurrencyStamp;
            asA.Accounts.Update(forged);

            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => asA.SaveChangesAsync(CancellationToken));
        }

        await using var asB = _postgres.CreateDbContext(organizationB.Id);
        var reloaded = await asB.Accounts.SingleAsync(account => account.Id == accountB.Id, CancellationToken);
        reloaded.DisplayName.ShouldBe(accountB.DisplayName);
        reloaded.OrganizationId.ShouldBe(organizationB.Id);
    }

    [Fact]
    public async Task Permission_rows_cannot_point_at_an_account_of_another_organization()
    {
        // Database-level backstop behind the interceptor: the composite foreign key
        // (AccountId, OrganizationId) → AspNetUsers(Id, OrganizationId).
        var (organizationA, organizationB) = await CreateTwoOrganizationsAsync();
        var accountB = await AddAccountAsync(organizationB, "admin");

        await using var asA = _postgres.CreateDbContext(organizationA.Id);
        var grant = new AccountPermissionGrant(accountB, AccountPermission.ManageAssistants);
        asA.AccountPermissions.Add(grant);
        asA.Entry(grant).Property(nameof(IOrganizationScoped.OrganizationId)).CurrentValue = organizationA.Id;

        var exception = await Should.ThrowAsync<DbUpdateException>(() => asA.SaveChangesAsync(CancellationToken));
        exception.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe("23503"); // foreign_key_violation
    }

    [Fact]
    public async Task Account_lookup_finds_accounts_by_organization_code_without_a_current_organization()
    {
        var (organizationA, organizationB) = await CreateTwoOrganizationsAsync();
        var accountA = await AddAccountAsync(organizationA, "admin");
        var accountB = await AddAccountAsync(organizationB, "admin");

        await using var withoutOrganization = _postgres.CreateDbContext(organizationId: null);
        var lookup = new AccountLookup(withoutOrganization);

        (await lookup.FindForSignInAsync(organizationA.Code, "admin", CancellationToken))!.Id.ShouldBe(accountA.Id);
        (await lookup.FindForSignInAsync(organizationB.Code.ToUpperInvariant(), " Admin ", CancellationToken))!.Id.ShouldBe(accountB.Id);
        (await lookup.FindForSignInAsync(organizationA.Code, "nobody", CancellationToken)).ShouldBeNull();
        (await lookup.FindForSignInAsync("no-such-org", "admin", CancellationToken)).ShouldBeNull();
        (await lookup.FindForSignInAsync("bad/code", "admin", CancellationToken)).ShouldBeNull();

        withoutOrganization.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private async Task<(Organization A, Organization B)> CreateTwoOrganizationsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var organizationA = new Organization(Guid.CreateVersion7(), "Organization A", $"a-{suffix}");
        var organizationB = new Organization(Guid.CreateVersion7(), "Organization B", $"b-{suffix}");

        await using var dbContext = _postgres.CreateDbContext(organizationId: null);
        dbContext.Organizations.AddRange(organizationA, organizationB);
        await dbContext.SaveChangesAsync(CancellationToken);

        return (organizationA, organizationB);
    }

    private async Task<Account> AddAccountAsync(
        Organization organization,
        string loginName,
        params AccountPermission[] permissions)
    {
        await using var dbContext = _postgres.CreateDbContext(organization.Id);
        var account = Account.Create(organization, loginName, $"{loginName} of {organization.Code}", AccountRole.SmbAdmin);
        dbContext.Accounts.Add(account);
        dbContext.AccountPermissions.AddRange(permissions.Select(permission => new AccountPermissionGrant(account, permission)));
        await dbContext.SaveChangesAsync(CancellationToken);
        return account;
    }
}
