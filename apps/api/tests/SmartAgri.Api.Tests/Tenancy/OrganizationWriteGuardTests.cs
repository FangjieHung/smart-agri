using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Tenancy;

/// <summary>
/// The save interceptor refuses cross-organization writes before any SQL is sent, so
/// these run against an unreachable connection string: reaching the database at all would
/// be a failure. (The "nothing is written" half is proven against real PostgreSQL in
/// <see cref="OrganizationIsolationTests"/>.)
/// </summary>
public class OrganizationWriteGuardTests
{
    private static readonly Organization OrganizationA = new(Guid.NewGuid(), "Org A", "org-a");
    private static readonly Organization OrganizationB = new(Guid.NewGuid(), "Org B", "org-b");

    [Fact]
    public async Task Adding_a_row_of_another_organization_throws()
    {
        await using var dbContext = TenancyTestContexts.Create(OrganizationA.Id);
        dbContext.Accounts.Add(Account.Create(OrganizationB, "admin", "Admin", AccountRole.SmbAdmin));

        await Should.ThrowAsync<CrossOrganizationWriteException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        Should.Throw<CrossOrganizationWriteException>(() => dbContext.SaveChanges());
    }

    [Fact]
    public async Task Adding_any_scoped_row_without_an_organization_throws()
    {
        await using var dbContext = TenancyTestContexts.Create(organizationId: null);
        dbContext.Accounts.Add(Account.Create(OrganizationA, "admin", "Admin", AccountRole.SmbAdmin));

        await Should.ThrowAsync<CrossOrganizationWriteException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Modifying_a_row_of_another_organization_throws()
    {
        await using var dbContext = TenancyTestContexts.Create(OrganizationA.Id);
        var claimOfB = AttachExisting(dbContext, new AccountClaim { Id = 5, UserId = Guid.NewGuid(), ClaimType = "t", ClaimValue = "v" }, OrganizationB.Id);

        claimOfB.ClaimValue = "changed";

        await Should.ThrowAsync<CrossOrganizationWriteException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Moving_a_row_into_another_organization_throws()
    {
        await using var dbContext = TenancyTestContexts.Create(OrganizationA.Id);
        var claim = AttachExisting(dbContext, new AccountClaim { Id = 6, UserId = Guid.NewGuid(), ClaimType = "t", ClaimValue = "v" }, OrganizationA.Id);

        dbContext.Entry(claim).Property(nameof(IOrganizationScoped.OrganizationId)).CurrentValue = OrganizationB.Id;

        await Should.ThrowAsync<CrossOrganizationWriteException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_a_row_of_another_organization_throws()
    {
        await using var dbContext = TenancyTestContexts.Create(OrganizationA.Id);
        var grantOfB = new AccountPermissionGrant(
            Account.Create(OrganizationB, "admin", "Admin", AccountRole.SmbAdmin),
            AccountPermission.ManageAssistants);
        dbContext.Attach(grantOfB);

        dbContext.Remove(grantOfB);

        await Should.ThrowAsync<CrossOrganizationWriteException>(
            () => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Added_rows_without_an_organization_get_the_current_one()
    {
        using var dbContext = TenancyTestContexts.Create(OrganizationA.Id);
        var claim = new AccountClaim { UserId = Guid.NewGuid(), ClaimType = "t", ClaimValue = "v" };
        dbContext.Add(claim);

        OrganizationSaveChangesInterceptor.Enforce(dbContext.ChangeTracker, dbContext.OrganizationContext);

        claim.OrganizationId.ShouldBe(OrganizationA.Id);
    }

    [Fact]
    public void Rows_of_the_current_organization_pass()
    {
        using var dbContext = TenancyTestContexts.Create(OrganizationA.Id);
        var account = Account.Create(OrganizationA, "admin", "Admin", AccountRole.SmbAdmin);
        dbContext.Add(account);
        dbContext.Add(new AccountPermissionGrant(account, AccountPermission.ManageAssistants));

        Should.NotThrow(() => OrganizationSaveChangesInterceptor.Enforce(dbContext.ChangeTracker, dbContext.OrganizationContext));
    }

    [Fact]
    public void Organizations_themselves_are_not_guarded()
    {
        // Organization is the tenant boundary, not organization-scoped data; creating one
        // (setup, seeding) happens with no current organization.
        using var dbContext = TenancyTestContexts.Create(organizationId: null);
        dbContext.Organizations.Add(new Organization(Guid.NewGuid(), "New", "new-org"));

        Should.NotThrow(() => OrganizationSaveChangesInterceptor.Enforce(dbContext.ChangeTracker, dbContext.OrganizationContext));
    }

    /// <summary>Tracks <paramref name="entity"/> as an unchanged row loaded from the
    /// database that belongs to <paramref name="organizationId"/>.</summary>
    private static T AttachExisting<T>(DbContext dbContext, T entity, Guid organizationId)
        where T : class
    {
        dbContext.Attach(entity);
        var property = dbContext.Entry(entity).Property(nameof(IOrganizationScoped.OrganizationId));
        property.CurrentValue = organizationId;
        property.OriginalValue = organizationId;
        property.IsModified = false;
        dbContext.Entry(entity).State.ShouldBe(EntityState.Unchanged);
        return entity;
    }
}
