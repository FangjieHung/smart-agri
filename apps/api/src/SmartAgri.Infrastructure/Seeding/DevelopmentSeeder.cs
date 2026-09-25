using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Infrastructure.Seeding;

/// <summary>
/// Creates <see cref="DevelopmentSeedData.Organizations"/> so local development and E2E
/// have accounts matching the frontend Demo (M1 skeleton plan, Slice 6). Only ever
/// registered in Development (see
/// <c>SmartAgri.Api.Seeding.DevelopmentSeedingServiceCollectionExtensions</c>); this class
/// itself does not check the environment, so tests can exercise it directly.
/// </summary>
/// <remarks>
/// <para>
/// Builds its own short-lived <see cref="AppDbContext"/> instances (like
/// <c>AccountLookup</c>/<c>PostgresFixture</c> do in tests) instead of taking one from DI,
/// because each organization's accounts must be written through a context pinned to that
/// organization (<see cref="FixedOrganizationContext"/>) — the write guard rejects an
/// organization-scoped row written through any other context, and organizations do not
/// yet exist to pin to before this runs.
/// </para>
/// <para>
/// <b>Idempotency.</b> An organization already present (matched by <c>Code</c>) and an
/// account already present (matched by login name within its organization) are left
/// alone — this seeder never edits an existing organization's name or an existing
/// account's display name or role. Permissions are additive only: on every run, each
/// seeded account gets whichever of its <see cref="DevelopmentSeedAccount.Permissions"/>
/// it does not already have; a permission this seeder is not told to grant is never
/// touched, so a permission an operator has manually added beyond the seed is never
/// removed. Because a granted permission is a presence/absence row with nothing else to
/// "change", <c>不覆寫手動改過的權限</c> (never overwrite a manually changed permission,
/// per the ticket) means exactly that: this seeder only ever adds rows, never removes or
/// replaces one. One consequence worth knowing: if an operator manually revokes one of
/// this seed's own permissions from a seeded account, running the seeder again restores
/// it, because "missing" is judged against the seed list, not against seeding history —
/// the seed list is a floor for its own accounts, not a one-time template.
/// </para>
/// </remarks>
public sealed class DevelopmentSeeder
{
    /// <summary>
    /// Configuration key (also an environment variable name — ASP.NET Core's default
    /// configuration includes environment variables) for the password every seeded
    /// account gets. Deliberately has no default: see <see cref="SeedAsync"/>.
    /// </summary>
    public const string PasswordConfigurationKey = "SEED_DEMO_PASSWORD";

    private readonly IConfiguration _configuration;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly ILogger<DevelopmentSeeder> _logger;

    public DevelopmentSeeder(
        IConfiguration configuration,
        DbContextOptions<AppDbContext> dbContextOptions,
        ILogger<DevelopmentSeeder> logger)
    {
        _configuration = configuration;
        _dbContextOptions = dbContextOptions;
        _logger = logger;
    }

    /// <exception cref="InvalidOperationException"><see cref="PasswordConfigurationKey"/>
    /// is not set. Thrown before any database access, so this never depends on a running
    /// database.</exception>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var password = _configuration[PasswordConfigurationKey];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"Refusing to seed development data: '{PasswordConfigurationKey}' is not set. It has no default " +
                "on purpose (a checked-in demo password must never reach production) — set it in your shell " +
                "before running the `migrate` subcommand in Development. See deploy/.env.example and " +
                "apps/api/README.md, \"Development seed data\".");
        }

        var hasher = new PasswordHasher<Account>();

        foreach (var organizationSeed in DevelopmentSeedData.Organizations)
        {
            var organization = await EnsureOrganizationAsync(organizationSeed, cancellationToken);

            // Pinned to this organization: the write guard requires it for every Account/
            // AccountPermissionGrant row below, and it also scopes the lookups (so
            // "anxin"'s admin and "control"'s admin never collide even though they share a
            // login name).
            await using var orgDbContext = new AppDbContext(_dbContextOptions, new FixedOrganizationContext(organization.Id));
            foreach (var accountSeed in organizationSeed.Accounts)
            {
                await EnsureAccountAsync(orgDbContext, organization, accountSeed, password, hasher, cancellationToken);
            }
        }
    }

    private async Task<Organization> EnsureOrganizationAsync(
        DevelopmentSeedOrganization seed,
        CancellationToken cancellationToken)
    {
        // Organization is not IOrganizationScoped (it is the tenant boundary), so no
        // particular organization needs to be current to read or create one.
        await using var dbContext = new AppDbContext(_dbContextOptions, FixedOrganizationContext.None);

        var existing = await dbContext.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Code == seed.Code, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var organization = new Organization(Guid.CreateVersion7(), seed.Name, seed.Code);
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seeded organization {OrganizationName} ({OrganizationCode}).", organization.Name, organization.Code);
        return organization;
    }

    private async Task EnsureAccountAsync(
        AppDbContext dbContext,
        Organization organization,
        DevelopmentSeedAccount seed,
        string password,
        PasswordHasher<Account> hasher,
        CancellationToken cancellationToken)
    {
        var normalizedLoginName = Account.NormalizeLoginName(seed.LoginName);
        var account = await dbContext.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.NormalizedLoginName == normalizedLoginName, cancellationToken);

        if (account is null)
        {
            account = Account.Create(organization, seed.LoginName, seed.DisplayName, seed.Role);
            account.PasswordHash = hasher.HashPassword(account, password);
            dbContext.Accounts.Add(account);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Seeded account {LoginName} in organization {OrganizationCode}.", seed.LoginName, organization.Code);
        }

        // Additive only — see the "Idempotency" remarks on this class.
        var grantedPermissions = await dbContext.AccountPermissions
            .Where(grant => grant.AccountId == account.Id)
            .Select(grant => grant.Permission)
            .ToListAsync(cancellationToken);
        var missingPermissions = seed.Permissions.Except(grantedPermissions).ToList();
        if (missingPermissions.Count == 0)
        {
            return;
        }

        dbContext.AccountPermissions.AddRange(
            missingPermissions.Select(permission => new AccountPermissionGrant(account, permission)));
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Granted {Count} missing permission(s) to {LoginName} in organization {OrganizationCode}.",
            missingPermissions.Count,
            seed.LoginName,
            organization.Code);
    }
}
