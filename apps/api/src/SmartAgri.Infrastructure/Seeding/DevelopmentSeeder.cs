using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartAgri.Domain.Accounts;
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
    private readonly IServiceProvider _services;

    public DevelopmentSeeder(
        IConfiguration configuration,
        DbContextOptions<AppDbContext> dbContextOptions,
        ILogger<DevelopmentSeeder> logger,
        IServiceProvider services)
    {
        _configuration = configuration;
        _dbContextOptions = dbContextOptions;
        _logger = logger;
        _services = services;
    }

    /// <remarks>
    /// If <see cref="PasswordConfigurationKey"/> is not set (or is blank), this logs a
    /// warning and returns without touching the database — it never throws for that case.
    /// This lets the documented first-install flow (`migrate`, then `setup` against an
    /// organization-less database; see apps/api/README.md, "Development seed data") work
    /// without demo data. <see cref="PasswordConfigurationKey"/> has no default on purpose
    /// (a checked-in demo password must never reach production); set it before running the
    /// `migrate` subcommand in Development to opt into the demo accounts.
    /// <para>
    /// A non-blank value is first run through the exact same
    /// <see cref="IPasswordValidator{TUser}"/>s a real account's password goes through
    /// (<c>SmartAgri.Api.Authentication.AuthenticationServiceCollectionExtensions.AddSmartAgriAuthentication</c>
    /// — see <see cref="EnsurePasswordMeetsIdentityRulesAsync"/>), before any organization
    /// or account is created. A password that fails throws
    /// <see cref="InvalidOperationException"/> — which fails the `migrate` subcommand with
    /// a non-zero exit code — listing the failing rules and never the password itself; the
    /// database is left exactly as it was (issue #29). Only once validation passes is the
    /// password hashed with <see cref="PasswordHasher{TUser}"/> directly and seeded exactly
    /// as before (there is no Identity <c>UserManager.CreateAsync</c> here — see the class
    /// remarks on why each organization needs its own <see cref="FixedOrganizationContext"/>).
    /// </para>
    /// </remarks>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var password = _configuration[PasswordConfigurationKey];
        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning(
                "{ConfigurationKey} is not set; skipping demo seed data. Set it to create the demo accounts. " +
                "See deploy/.env.example and apps/api/README.md, \"Development seed data\".",
                PasswordConfigurationKey);
            return;
        }

        await EnsurePasswordMeetsIdentityRulesAsync(password, cancellationToken);

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

    /// <summary>
    /// Runs <paramref name="password"/> through every registered
    /// <see cref="IPasswordValidator{TUser}"/> (<see cref="UserManager{TUser}.PasswordValidators"/>)
    /// — the exact validators and password rules a real account goes through — before
    /// <see cref="SeedAsync"/> creates anything.
    /// </summary>
    /// <remarks>
    /// Resolves <see cref="UserManager{TUser}"/> from the current DI scope (<c>_services</c>)
    /// rather than taking it as a constructor parameter, so the unset/blank path in
    /// <see cref="SeedAsync"/> — the documented first-install flow — never needs Identity's
    /// user store wired up. Validates against a throwaway, never-saved probe account
    /// instead of one of <see cref="DevelopmentSeedData.Organizations"/>'s real accounts:
    /// <c>SEED_DEMO_PASSWORD</c> is one password shared by every seeded account across both
    /// organizations, none of which exist in the database yet at this point, and the
    /// registered validator only ever inspects the password string itself — never the
    /// database or the user argument's identity.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The password fails one or more rules.
    /// The message lists the failing rules' codes and descriptions and never the password
    /// itself.</exception>
    private async Task EnsurePasswordMeetsIdentityRulesAsync(string password, CancellationToken cancellationToken)
    {
        var userManager = _services.GetRequiredService<UserManager<Account>>();
        var probeOrganization = new Organization(Guid.CreateVersion7(), "seed-password-probe", "seed-password-probe");
        var probeAccount = Account.Create(probeOrganization, "seed-password-probe", "Seed Password Probe", AccountRole.SmbAdmin);

        var errors = new List<IdentityError>();
        foreach (var validator in userManager.PasswordValidators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await validator.ValidateAsync(userManager, probeAccount, password);
            if (!result.Succeeded)
            {
                errors.AddRange(result.Errors);
            }
        }

        if (errors.Count > 0)
        {
            // Only codes and (already password-agnostic) descriptions ever reach this
            // message — never the password itself.
            throw new InvalidOperationException(
                $"{PasswordConfigurationKey} 不符合 Identity 密碼規則（與正式帳號相同），未建立任何種子組織或帳號：" +
                string.Join("；", errors.Select(error => $"{error.Code}：{error.Description}")));
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
