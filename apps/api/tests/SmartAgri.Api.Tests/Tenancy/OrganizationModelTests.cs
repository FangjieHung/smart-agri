using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Tenancy;

/// <summary>
/// Guards against a new entity being added without organization isolation: every entity
/// in <see cref="AppDbContext"/>'s model must be <see cref="IOrganizationScoped"/> and
/// carry the named <c>"Organization"</c> query filter, except for a short, explicit
/// whitelist of tables that are not per-organization data. No database needed.
/// </summary>
public class OrganizationModelTests
{
    /// <summary>
    /// OpenIddict's four EF Core entity types (Slice 5 adds them). They describe OAuth
    /// clients, scopes, and grants/tokens keyed by subject — not organization data.
    /// </summary>
    private static readonly string[] OpenIddictEntityNames =
    [
        "OpenIddictEntityFrameworkCoreApplication",
        "OpenIddictEntityFrameworkCoreAuthorization",
        "OpenIddictEntityFrameworkCoreScope",
        "OpenIddictEntityFrameworkCoreToken",
    ];

    [Fact]
    public void Every_entity_outside_the_whitelist_is_organization_scoped_and_filtered()
    {
        using var dbContext = TenancyTestContexts.Create();

        var offenders = dbContext.Model.GetEntityTypes()
            .Where(entityType => !IsWhitelisted(entityType))
            .Where(entityType => !IsOrganizationScopedAndFiltered(entityType))
            .Select(entityType => entityType.DisplayName())
            .ToList();

        offenders.ShouldBeEmpty(
            "These entities are not organization scoped. Implement IOrganizationScoped " +
            "(AppDbContext then applies the \"Organization\" filter automatically), or — only " +
            "if the table genuinely is not per-organization data — extend the whitelist here.");
    }

    [Fact]
    public void The_check_is_not_vacuous()
    {
        using var dbContext = TenancyTestContexts.Create();

        var scoped = dbContext.Model.GetEntityTypes()
            .Where(IsOrganizationScopedAndFiltered)
            .Select(entityType => entityType.ClrType)
            .ToList();

        scoped.ShouldBe(
            [
                typeof(Account), typeof(AccountClaim), typeof(AccountLogin), typeof(AccountToken), typeof(AccountPermissionGrant),
                typeof(KnowledgeBase), typeof(KnowledgeBaseShare), typeof(KnowledgeActivity),
                typeof(KnowledgeDocument), typeof(KnowledgeDocumentVersion), typeof(KnowledgeFileContent),
                typeof(BackgroundJob),
            ],
            ignoreOrder: true);
        dbContext.Model.FindEntityType(typeof(Organization)).ShouldNotBeNull();
    }

    [Fact]
    public void Organization_id_is_a_concurrency_token_on_every_scoped_entity()
    {
        // Makes every UPDATE/DELETE also match on OrganizationId, so a row of another
        // organization attached under a forged OrganizationId affects zero rows.
        using var dbContext = TenancyTestContexts.Create();

        foreach (var entityType in dbContext.Model.GetEntityTypes().Where(IsOrganizationScopedAndFiltered))
        {
            entityType.FindProperty(nameof(IOrganizationScoped.OrganizationId))!.IsConcurrencyToken
                .ShouldBeTrue(entityType.DisplayName());
        }
    }

    [Fact]
    public void Login_names_are_unique_per_organization_and_codes_globally()
    {
        using var dbContext = TenancyTestContexts.Create();

        var account = dbContext.Model.FindEntityType(typeof(Account))!;
        account.GetIndexes()
            .ShouldContain(index => index.IsUnique
                && index.Properties.Select(property => property.Name).SequenceEqual(
                    new[] { nameof(Account.OrganizationId), nameof(Account.NormalizedLoginName) }));

        var organization = dbContext.Model.FindEntityType(typeof(Organization))!;
        organization.GetIndexes()
            .ShouldContain(index => index.IsUnique
                && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Organization.Code) }));
    }

    private static bool IsOrganizationScopedAndFiltered(IEntityType entityType) =>
        typeof(IOrganizationScoped).IsAssignableFrom(entityType.ClrType)
        && entityType.GetRootType().FindDeclaredQueryFilter(AppDbContext.OrganizationFilter) is not null;

    private static bool IsWhitelisted(IEntityType entityType)
    {
        var clrType = entityType.ClrType;

        if (clrType == typeof(Organization))
        {
            return true;
        }

        // Identity's role tables (not mapped today; roles are Account.Role). Roles are
        // shared definitions, not organization data. AspNetUserRoles is NOT whitelisted:
        // it links an account to a role and so belongs to the account's organization.
        if (DerivesFromGeneric(clrType, typeof(IdentityRole<>)) || DerivesFromGeneric(clrType, typeof(IdentityRoleClaim<>)))
        {
            return true;
        }

        return clrType.Namespace?.StartsWith("OpenIddict.EntityFrameworkCore", StringComparison.Ordinal) == true
            && OpenIddictEntityNames.Any(name => DerivesFromName(clrType, name));
    }

    private static bool DerivesFromGeneric(Type type, Type genericDefinition)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericDefinition)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DerivesFromName(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var currentName = current.IsGenericType ? current.Name[..current.Name.IndexOf('`')] : current.Name;
            if (currentName == name)
            {
                return true;
            }
        }

        return false;
    }
}
