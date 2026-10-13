using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using SmartAgri.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// What a password change does to the account's sign-ins (#360): the one that changed the password goes
/// on, every other one ends.
/// </summary>
/// <remarks>
/// A sign-in is one OpenIddict authorization: the code flow creates an ad-hoc one, and every refresh
/// token after it, and every access token, names it (<c>oi_au_id</c>). The other authorizations and all
/// their tokens are revoked: their refresh tokens get <c>invalid_grant</c> (OpenIddict checks the
/// authorization on every refresh) and their access tokens are refused on the next request
/// (<see cref="Authorization.DatabaseAccountStandingSource"/> reads the authorization's status).
/// The kept one cannot keep the security stamp its refresh tokens carry (<see cref="SignInSession"/>),
/// because the password change rotates it; instead the new stamp is written on the authorization itself
/// (<see cref="SecurityStampProperty"/>), and the token endpoint takes that one over the token's
/// (<see cref="ReadSecurityStampAsync"/>). The sign-in time is untouched, so the 7-day cap still counts
/// from the original sign-in.
/// </remarks>
internal static class PasswordChangeSignIns
{
    /// <summary>Authorization property: the security stamp the sign-in continues with after it changed the
    /// password. Server-side only (the authorizations table), never in a token.</summary>
    public const string SecurityStampProperty = "smartagri_security_stamp";

    /// <summary>
    /// Revokes every authorization and token of <paramref name="subject"/> except
    /// <paramref name="keptAuthorizationId"/> and its tokens, and records <paramref name="securityStamp"/>
    /// on the kept one. When the kept id is <see langword="null"/>, not this subject's or no longer valid,
    /// everything is revoked, as <c>reset-password</c> does.
    /// </summary>
    /// <returns>Whether a sign-in was kept.</returns>
    public static async Task<bool> EndAllButAsync(
        AppDbContext dbContext,
        IOpenIddictAuthorizationManager authorizationManager,
        string subject,
        string? keptAuthorizationId,
        string securityStamp,
        CancellationToken cancellationToken)
    {
        var kept = keptAuthorizationId is null ? null : await authorizationManager.FindByIdAsync(keptAuthorizationId, cancellationToken);
        if (kept is not null
            && (!string.Equals(await authorizationManager.GetSubjectAsync(kept, cancellationToken), subject, StringComparison.Ordinal)
                || !await authorizationManager.HasStatusAsync(kept, Statuses.Valid, cancellationToken)))
        {
            kept = null;
        }

        var keptId = kept is null ? null : keptAuthorizationId;

        await dbContext.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(token => token.Subject == subject
                && token.Status != Statuses.Revoked
                && (keptId == null || token.Authorization == null || token.Authorization.Id != keptId))
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.Status, Statuses.Revoked), cancellationToken);

        await dbContext.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .Where(authorization => authorization.Subject == subject
                && authorization.Status != Statuses.Revoked
                && (keptId == null || authorization.Id != keptId))
            .ExecuteUpdateAsync(update => update.SetProperty(authorization => authorization.Status, Statuses.Revoked), cancellationToken);

        if (kept is null)
        {
            return false;
        }

        var descriptor = new OpenIddictAuthorizationDescriptor();
        await authorizationManager.PopulateAsync(descriptor, kept, cancellationToken);
        descriptor.Properties[SecurityStampProperty] = JsonSerializer.SerializeToElement(securityStamp);
        await authorizationManager.UpdateAsync(kept, descriptor, cancellationToken);
        return true;
    }

    /// <summary>The stamp recorded on the grant's authorization by a password change it made, or
    /// <see langword="null"/> when it made none (the token's own <see cref="SignInSession.SecurityStamp"/>
    /// then applies).</summary>
    public static async Task<string?> ReadSecurityStampAsync(
        IOpenIddictAuthorizationManager authorizationManager,
        string? authorizationId,
        CancellationToken cancellationToken)
    {
        if (authorizationId is null
            || await authorizationManager.FindByIdAsync(authorizationId, cancellationToken) is not { } authorization)
        {
            return null;
        }

        var properties = await authorizationManager.GetPropertiesAsync(authorization, cancellationToken);
        return properties.TryGetValue(SecurityStampProperty, out var stamp) && stamp.ValueKind == JsonValueKind.String
            ? stamp.GetString()
            : null;
    }
}
