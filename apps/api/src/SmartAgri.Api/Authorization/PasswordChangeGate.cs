using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Authorization;

/// <summary>Reads whether an account must change its password before using the API.</summary>
public interface IPasswordChangeRequirementSource
{
    Task<bool> IsPasswordChangeRequiredAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>Account.PasswordChangeRequired</c> from the database on every request — like
/// permissions, the flag is never carried in the access token, so changing the password
/// lifts the gate at once for the token already in hand. Runs under the organization
/// filter (the token's <c>org_id</c>); an account that cannot be seen there is reported
/// as not flagged and left to the endpoint, which answers for a missing account itself.
/// </summary>
public sealed class DatabasePasswordChangeRequirementSource : IPasswordChangeRequirementSource
{
    private readonly AppDbContext _dbContext;

    public DatabasePasswordChangeRequirementSource(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsPasswordChangeRequiredAsync(Guid accountId, CancellationToken cancellationToken) =>
        await _dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.Id == accountId)
            .Select(account => account.PasswordChangeRequired)
            .SingleOrDefaultAsync(cancellationToken);
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
/// <see cref="Errors.ForbiddenReason.PasswordChangeRequired"/>.
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
    /// True when the request must be refused with <c>password-change-required</c>: the
    /// caller is a signed-in account whose flag is set and the endpoint is not exempt.
    /// </summary>
    public static async Task<bool> BlocksAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<AllowedWhilePasswordChangeRequiredMetadata>() is not null)
        {
            return false;
        }

        if (AccountClaims.GetAccountId(context.User) is not { } accountId)
        {
            return false;
        }

        var source = context.RequestServices.GetRequiredService<IPasswordChangeRequirementSource>();
        return await source.IsPasswordChangeRequiredAsync(accountId, context.RequestAborted);
    }
}
