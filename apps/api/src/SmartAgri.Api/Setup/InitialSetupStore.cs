using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartAgri.Api.Tenancy;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Setup;

/// <summary>Which subcommand is running: the shared create path serves both.</summary>
public enum SetupMode
{
    /// <summary><c>setup</c>: the first organization, on a database with none.</summary>
    Initial,

    /// <summary><c>add-organization</c>: a further organization, on a database that already has one.</summary>
    AddOrganization,
}

/// <summary>What the database allows <c>setup</c> to do.</summary>
public enum InitialSetupState
{
    /// <summary>Schema up to date and no organization yet.</summary>
    Ready,

    /// <summary>Migrations have not been applied (run <c>migrate</c> first).</summary>
    PendingMigrations,

    /// <summary>At least one organization exists; setup never runs again.</summary>
    AlreadyInitialized,
}

/// <summary>Validated values for the first organization and its administrator.</summary>
public sealed record InitialSetupRequest(
    string OrganizationName,
    string OrganizationCode,
    string AdminLogin,
    string AdminDisplayName);

/// <summary>Outcome of <see cref="IInitialSetupStore.CreateAsync"/>.</summary>
public sealed record InitialSetupResult(InitialSetupOutcome Outcome, IReadOnlyList<string> Errors, Guid? AccountId = null)
{
    public static InitialSetupResult Created(Guid accountId) => new(InitialSetupOutcome.Created, [], accountId);

    public static readonly InitialSetupResult AlreadyInitialized = new(InitialSetupOutcome.AlreadyInitialized, []);

    public static readonly InitialSetupResult NoOrganization = new(InitialSetupOutcome.NoOrganization, []);

    public static readonly InitialSetupResult DuplicateCode = new(InitialSetupOutcome.DuplicateCode, []);

    public static InitialSetupResult Rejected(IEnumerable<string> errors) => new(InitialSetupOutcome.Rejected, [.. errors]);
}

public enum InitialSetupOutcome
{
    Created,
    AlreadyInitialized,
    Rejected,

    /// <summary><see cref="SetupMode.AddOrganization"/> found no organization at all (use <c>setup</c>).</summary>
    NoOrganization,

    /// <summary><see cref="SetupMode.AddOrganization"/>: the organization code is already taken.</summary>
    DuplicateCode,
}

/// <summary>The database side of <c>setup</c>, behind an interface so the command's
/// prompting, refusal and output rules are testable without a database.</summary>
public interface IInitialSetupStore
{
    Task<InitialSetupState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the organization and its administrator (smb-admin, every permission, must
    /// change password) with <paramref name="password"/>, atomically, and only if there is
    /// still no organization at all (<see cref="SetupMode.Initial"/>), or, for
    /// <see cref="SetupMode.AddOrganization"/>, only if an organization exists and the code
    /// is not taken.
    /// </summary>
    Task<InitialSetupResult> CreateAsync(
        InitialSetupRequest request, string password, SetupMode mode, CancellationToken cancellationToken);
}

/// <summary>EF Core + Identity implementation of <see cref="IInitialSetupStore"/>.</summary>
public sealed class EfInitialSetupStore : IInitialSetupStore
{
    private readonly AppDbContext _dbContext;
    private readonly UserManager<Account> _userManager;
    private readonly ClaimsOrganizationContext _organizationContext;

    public EfInitialSetupStore(
        AppDbContext dbContext,
        UserManager<Account> userManager,
        ClaimsOrganizationContext organizationContext)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _organizationContext = organizationContext;
    }

    public async Task<InitialSetupState> GetStateAsync(CancellationToken cancellationToken)
    {
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            return InitialSetupState.PendingMigrations;
        }

        // Organizations is the tenant table itself, not organization filtered: this sees
        // every organization.
        return await _dbContext.Organizations.AnyAsync(cancellationToken)
            ? InitialSetupState.AlreadyInitialized
            : InitialSetupState.Ready;
    }

    public async Task<InitialSetupResult> CreateAsync(
        InitialSetupRequest request, string password, SetupMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(password);

        try
        {
            // Serializable: two setups started at the same moment both see "no
            // organization"; PostgreSQL then aborts one of them with a serialization
            // failure instead of letting both commit.
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var organization = new Organization(Guid.CreateVersion7(), request.OrganizationName, request.OrganizationCode);
            if (mode == SetupMode.Initial)
            {
                if (await _dbContext.Organizations.AnyAsync(cancellationToken))
                {
                    return InitialSetupResult.AlreadyInitialized;
                }
            }
            else
            {
                if (!await _dbContext.Organizations.AnyAsync(cancellationToken))
                {
                    return InitialSetupResult.NoOrganization;
                }

                // Codes are stored normalized, so this is the same comparison as the unique index.
                if (await _dbContext.Organizations.AnyAsync(existing => existing.Code == organization.Code, cancellationToken))
                {
                    return InitialSetupResult.DuplicateCode;
                }
            }

            _dbContext.Organizations.Add(organization);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // No request, so no organization yet: act for the organization just created,
            // so the organization write guard accepts its account and permission rows.
            _organizationContext.Pin(organization.Id);

            var account = Account.Create(organization, request.AdminLogin, request.AdminDisplayName, AccountRole.SmbAdmin);
            account.RequirePasswordChange();

            // Through UserManager, not a hand-made hash: runs Identity's user-name and
            // password validators with the host's options, exactly as sign-in expects.
            var created = await _userManager.CreateAsync(account, password);
            if (!created.Succeeded)
            {
                return InitialSetupResult.Rejected(created.Errors.Select(error => error.Description));
            }

            _dbContext.AccountPermissions.AddRange(
                Enum.GetValues<AccountPermission>().Select(permission => new AccountPermissionGrant(account, permission)));
            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return InitialSetupResult.Created(account.Id);
        }
        catch (Exception exception) when (IsConcurrentSetup(exception))
        {
            if (mode == SetupMode.Initial)
            {
                return InitialSetupResult.AlreadyInitialized;
            }

            // Another add-organization committed first. Under the serializable transaction a
            // same-code race surfaces as a unique violation or a serialization failure; the
            // latter may also come from an unrelated concurrent add, so tell the two apart by
            // asking again on a clean connection state: nothing of ours was committed.
            return PostgresOf(exception)?.SqlState == PostgresErrorCodes.UniqueViolation
                ? InitialSetupResult.DuplicateCode
                : InitialSetupResult.Rejected(["同時有另一個變更，這次沒有建立任何東西；請稍後再執行一次。"]);
        }
    }

    // Walks the whole chain: with a retrying execution strategy a transient failure inside a
    // user-started transaction is wrapped ("likely due to a transient failure").
    private static PostgresException? PostgresOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static bool IsConcurrentSetup(Exception exception) =>
        PostgresOf(exception)?.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation;
}
