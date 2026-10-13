using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using SmartAgri.Api.Authentication;
using SmartAgri.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Authorization;

/// <summary>
/// What every authenticated request checks about its account before anything else, read in one query:
/// whether it must change its password, whether it was deactivated (issue #356), and whether the sign-in
/// the access token belongs to was ended (#360).
/// </summary>
/// <param name="PasswordChangeRequired"><c>Account.PasswordChangeRequired</c>.</param>
/// <param name="Deactivated"><c>Account.DeactivatedAt</c> is set.</param>
/// <param name="SignInEnded">The OpenIddict authorization the access token names is no longer valid —
/// another sign-in of the account changed the password (<see cref="PasswordChangeSignIns"/>).</param>
public sealed record AccountStanding(bool PasswordChangeRequired, bool Deactivated, bool SignInEnded = false)
{
    /// <summary>An account that cannot be seen in the token's organization: no flag; the
    /// endpoint answers for a missing account itself.</summary>
    public static readonly AccountStanding Unknown = new(PasswordChangeRequired: false, Deactivated: false);
}

/// <summary>Reads an account's <see cref="AccountStanding"/>.</summary>
public interface IAccountStandingSource
{
    /// <param name="accountId">The token's <c>sub</c>.</param>
    /// <param name="authorizationId">The OpenIddict authorization (sign-in) the access token names
    /// (<c>oi_au_id</c>), or <see langword="null"/> when it names none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AccountStanding> GetAsync(Guid accountId, string? authorizationId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>Account.PasswordChangeRequired</c> and <c>Account.DeactivatedAt</c> from the database on
/// every request — like permissions, neither is carried in the access token, so changing the password
/// lifts the gate at once for the token already in hand, and deactivating a member stops the access
/// tokens they already hold on their very next call (not only when they expire). The same query reads
/// whether the token's authorization is still valid (#360), so changing the password on one device stops
/// the access tokens of the account's other sign-ins on their next call too. Runs under the
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

    public async Task<AccountStanding> GetAsync(Guid accountId, string? authorizationId, CancellationToken cancellationToken) =>
        await _dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.Id == accountId)
            .Select(account => new AccountStanding(
                account.PasswordChangeRequired,
                account.DeactivatedAt != null,
                // Gone counts as ended too: nothing deletes a live sign-in's authorization.
                authorizationId != null
                    && !_dbContext.Set<OpenIddictEntityFrameworkCoreAuthorization>()
                        .Any(authorization => authorization.Id == authorizationId && authorization.Status == Statuses.Valid)))
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

    /// <summary>The token's sign-in was ended by a password change on another sign-in (#360): refuse as
    /// not signed in (<c>401</c>, no body); the SPA's refresh is refused too, so it goes back to the login
    /// page.</summary>
    SignInEnded,
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
/// deactivated account (issue #356), and an access token whose sign-in was ended by a password change
/// elsewhere (#360), on every protected endpoint, exempt ones included.
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
    /// account, or a token whose sign-in was ended, is refused everywhere, exempt endpoints included;
    /// otherwise an account whose flag is set
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
        var standing = await source.GetAsync(accountId, context.User.GetAuthorizationId(), context.RequestAborted);
        if (standing.Deactivated)
        {
            return AccountGateDecision.Deactivated;
        }

        if (standing.SignInEnded)
        {
            return AccountGateDecision.SignInEnded;
        }

        var exempt = context.GetEndpoint()?.Metadata.GetMetadata<AllowedWhilePasswordChangeRequiredMetadata>() is not null;
        return standing.PasswordChangeRequired && !exempt ? AccountGateDecision.PasswordChangeRequired : AccountGateDecision.Pass;
    }
}
