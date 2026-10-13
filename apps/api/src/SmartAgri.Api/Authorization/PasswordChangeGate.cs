using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Authorization;

/// <summary>
/// What every authenticated request checks about its account before anything else, read in one query:
/// whether it must change its password, and whether it was deactivated (issue #356).
/// </summary>
/// <param name="PasswordChangeRequired"><c>Account.PasswordChangeRequired</c>.</param>
/// <param name="Deactivated"><c>Account.DeactivatedAt</c> is set.</param>
public sealed record AccountStanding(bool PasswordChangeRequired, bool Deactivated)
{
    /// <summary>An account that cannot be seen in the token's organization: neither flag; the
    /// endpoint answers for a missing account itself.</summary>
    public static readonly AccountStanding Unknown = new(PasswordChangeRequired: false, Deactivated: false);
}

/// <summary>Reads an account's <see cref="AccountStanding"/>.</summary>
public interface IAccountStandingSource
{
    Task<AccountStanding> GetAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>Account.PasswordChangeRequired</c> and <c>Account.DeactivatedAt</c> from the database on
/// every request — like permissions, neither is carried in the access token, so changing the password
/// lifts the gate at once for the token already in hand, and deactivating a member stops the access
/// tokens they already hold on their very next call (not only when they expire). Runs under the
/// organization filter (the token's <c>org_id</c>); an account that cannot be seen there is
/// <see cref="AccountStanding.Unknown"/> and left to the endpoint.
/// </summary>
public sealed class DatabaseAccountStandingSource : IAccountStandingSource
{
    private readonly AppDbContext _dbContext;

    public DatabaseAccountStandingSource(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AccountStanding> GetAsync(Guid accountId, CancellationToken cancellationToken) =>
        await _dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.Id == accountId)
            .Select(account => new AccountStanding(account.PasswordChangeRequired, account.DeactivatedAt != null))
            .SingleOrDefaultAsync(cancellationToken)
        ?? AccountStanding.Unknown;
}

/// <summary>What <see cref="PasswordChangeGate.EvaluateAsync"/> decided for a request.</summary>
public enum AccountGateDecision
{
    /// <summary>Carry on with the endpoint's own authorization result.</summary>
    Pass,

    /// <summary>Refuse with <c>403 password-change-required</c>.</summary>
    PasswordChangeRequired,

    /// <summary>The account was deactivated: refuse as not signed in (<c>401</c>, no body), so the SPA
    /// goes back to the login page, where the account can no longer sign in.</summary>
    Deactivated,
}

/// <summary>Endpoint metadata: callable by an account that still has to change its
/// password (see <see cref="PasswordChangeGate"/>).</summary>
public sealed class AllowedWhilePasswordChangeRequiredMetadata
{
    public static readonly AllowedWhilePasswordChangeRequiredMetadata Instance = new();

    private AllowedWhilePasswordChangeRequiredMetadata()
    {
    }
}

/// <summary>
/// "Must change password" gate (M1 plan, Slice 11). An account created by <c>setup</c>
/// signs in with a one-time password; until it sets its own it may only call endpoints
/// marked <see cref="AllowWhilePasswordChangeRequired{TBuilder}"/> (<c>GET /api/v1/me</c>
/// and <c>POST /api/v1/auth/change-password</c>). Every other protected endpoint answers
/// <see cref="Errors.ForbiddenReason.PasswordChangeRequired"/>. The same read also refuses a
/// deactivated account (issue #356) on every protected endpoint, exempt ones included.
/// </summary>
/// <remarks>
/// Enforced in <see cref="ApiAuthorizationResultHandler"/>, i.e. after the authorization
/// middleware evaluated the endpoint's policy, for every endpoint that is not
/// <c>AllowAnonymous()</c> — the fallback policy makes that every endpoint that declares
/// nothing, so a new endpoint is gated without opting in. Anonymous endpoints (sign-in,
/// <c>/connect/*</c>, health) are not gated: the flagged account must still be able to
/// sign in and obtain the token it calls change-password with.
/// </remarks>
public static class PasswordChangeGate
{
    /// <summary>Lets accounts that must change their password call this endpoint.</summary>
    public static TBuilder AllowWhilePasswordChangeRequired<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AllowedWhilePasswordChangeRequiredMetadata.Instance);

    /// <summary>
    /// Decides for a signed-in caller (one query, <see cref="IAccountStandingSource"/>): a deactivated
    /// account is refused everywhere, exempt endpoints included; otherwise an account whose flag is set
    /// is refused <c>password-change-required</c> by every endpoint that is not exempt. Without an
    /// account id (not signed in) nothing is read.
    /// </summary>
    public static async Task<AccountGateDecision> EvaluateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AccountClaims.GetAccountId(context.User) is not { } accountId)
        {
            return AccountGateDecision.Pass;
        }

        var source = context.RequestServices.GetRequiredService<IAccountStandingSource>();
        var standing = await source.GetAsync(accountId, context.RequestAborted);
        if (standing.Deactivated)
        {
            return AccountGateDecision.Deactivated;
        }

        var exempt = context.GetEndpoint()?.Metadata.GetMetadata<AllowedWhilePasswordChangeRequiredMetadata>() is not null;
        return standing.PasswordChangeRequired && !exempt ? AccountGateDecision.PasswordChangeRequired : AccountGateDecision.Pass;
    }
}
