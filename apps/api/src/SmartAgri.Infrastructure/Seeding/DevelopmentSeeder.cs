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
/// <b>Idempotency.</b> "只補缺的" (only fill in what is missing) is read at the level of
/// organizations and accounts, not permissions: a missing organization is created, and a
/// missing account is created with its full
/// <see cref="DevelopmentSeedAccount.Permissions"/> list. An account that already exists
/// is left completely untouched — its display name, role, password and permissions are
/// never modified, added to, or removed, regardless of what they currently are. This is
/// deliberate: an operator (or a test) may have changed a seeded account's permissions
/// since it was created — for example revoking one in the team panel — and
/// <c>不覆寫手動改過的權限</c> (never overwrite a manually changed permission, per the
/// ticket) means that change must survive every later run of this seeder, including one
/// that re-grants nothing and one that removes nothing. Concretely: seed, then revoke a
/// permission by hand, then seed again — the revoked permission stays revoked, forever,
/// until the account itself is dropped and recreated.
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
        var exists = await dbContext.Accounts.AsNoTracking()
            .AnyAsync(candidate => candidate.NormalizedLoginName == normalizedLoginName, cancellationToken);
        if (exists)
        {
            // Already seeded (or otherwise present): leave it completely alone, including
            // its permissions — see the "Idempotency" remarks on this class.
            return;
        }

        var account = Account.Create(organization, seed.LoginName, seed.DisplayName, seed.Role);
        account.PasswordHash = hasher.HashPassword(account, password);
        dbContext.Accounts.Add(account);
        dbContext.AccountPermissions.AddRange(
            seed.Permissions.Select(permission => new AccountPermissionGrant(account, permission)));
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seeded account {LoginName} in organization {OrganizationCode} with {Count} permission(s).",
            seed.LoginName,
            organization.Code,
            seed.Permissions.Count);
    }
}
