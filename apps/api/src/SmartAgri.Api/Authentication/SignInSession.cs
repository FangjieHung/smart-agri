using System.Globalization;
using System.Security.Claims;
using OpenIddict.Abstractions;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// What a sign-in carries from <c>/connect/authorize</c> through the authorization code and every
/// refresh token after it (#354): when the account signed in, and its security stamp at that moment.
/// </summary>
/// <remarks>
/// Both are claims without a destination, so OpenIddict keeps them only in authorization codes and
/// refresh tokens (encrypted, never readable by the SPA) — never in access or identity tokens. The token
/// endpoint checks them on every exchange: a rotated stamp (password change, <c>reset-password</c>) ends
/// the sign-in, and so does <see cref="AuthenticationServiceCollectionExtensions.SessionLifetime"/> since
/// <see cref="SignedInAt"/>, however often the SPA refreshed in between. OpenIddict's own options cannot
/// express that absolute cap: sliding refresh tokens each get a fresh lifetime, and turning sliding off
/// would make the 8-hour idle limit an 8-hour absolute one.
/// </remarks>
internal sealed record SignInSession(DateTimeOffset SignedInAt, string SecurityStamp)
{
    /// <summary>When the account signed in, as Unix seconds. Not OpenID Connect's <c>auth_time</c>: OpenIddict
    /// gives that claim special treatment (typed values, identity tokens), and this one is internal.</summary>
    public const string SignedInAtClaim = "smartagri_signed_in_at";

    /// <summary>The account's Identity security stamp when it signed in.</summary>
    public const string SecurityStampClaim = "smartagri_security_stamp";

    /// <summary>The session read from an authorization code or refresh token principal, or
    /// <see langword="null"/> when either claim is missing or malformed (the grant is then refused).</summary>
    public static SignInSession? From(ClaimsPrincipal? principal)
    {
        var signedInAt = principal?.GetClaim(SignedInAtClaim);
        var stamp = principal?.GetClaim(SecurityStampClaim);
        return long.TryParse(signedInAt, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && stamp is not null
            ? new SignInSession(DateTimeOffset.FromUnixTimeSeconds(seconds), stamp)
            : null;
    }

    /// <summary>The moment the sign-in ends however often it is refreshed.</summary>
    public DateTimeOffset EndsAt => SignedInAt + AuthenticationServiceCollectionExtensions.SessionLifetime;

    public bool HasEnded(DateTimeOffset now) => now >= EndsAt;

    /// <summary>Adds the two claims (no destinations) to the principal being issued.</summary>
    public void WriteTo(ClaimsIdentity identity)
    {
        identity.SetClaim(SignedInAtClaim, SignedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        identity.SetClaim(SecurityStampClaim, SecurityStamp);
    }

    /// <summary>
    /// Never lets a token outlive the session: the new refresh token gets the idle lifetime, or less
    /// when the session ends sooner; so does the access token.
    /// </summary>
    public void LimitLifetimes(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var remaining = EndsAt - now;
        principal.SetRefreshTokenLifetime(Min(AuthenticationServiceCollectionExtensions.RefreshTokenLifetime, remaining));
        principal.SetAccessTokenLifetime(Min(AuthenticationServiceCollectionExtensions.AccessTokenLifetime, remaining));
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
