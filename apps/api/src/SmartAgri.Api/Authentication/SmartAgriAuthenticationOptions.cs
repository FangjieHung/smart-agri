namespace SmartAgri.Api.Authentication;

/// <summary>
/// Configuration section <c>Authentication</c>. See <c>apps/api/README.md</c>
/// ("Sign-in and tokens") for how each value is used.
/// </summary>
public sealed class SmartAgriAuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// PKCS#12 (<c>.pfx</c>) certificate whose private key signs tokens. Required outside
    /// <c>Development</c>; the Api refuses to start without it.
    /// </summary>
    public string? SigningCertificatePath { get; set; }

    public string? SigningCertificatePassword { get; set; }

    /// <summary>
    /// PKCS#12 certificate whose key encrypts authorization codes and other tokens only
    /// the server reads. Required outside <c>Development</c>.
    /// </summary>
    public string? EncryptionCertificatePath { get; set; }

    public string? EncryptionCertificatePassword { get; set; }

    /// <summary>Optional absolute issuer URI. When unset, OpenIddict uses the request's
    /// own origin.</summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// The Angular login page <c>/connect/authorize</c> sends a signed-out user to, with
    /// <c>?returnUrl=</c> pointing back at the authorization request. A local path (the
    /// SPA and the Api share one origin; the Identity cookie is <c>SameSite=Strict</c>).
    /// </summary>
    public string LoginPath { get; set; } = "/login";

    public AdminSpaClientOptions AdminSpa { get; set; } = new();
}

/// <summary>The <c>admin-spa</c> OAuth client (public, authorization code + PKCE).</summary>
public sealed class AdminSpaClientOptions
{
    public const string ClientId = "admin-spa";

    public const string CallbackPath = "/auth/callback";

    public const string PostLogoutPath = "/login";

    /// <summary>
    /// Origins the admin SPA is served from, e.g. <c>https://admin.example.org</c>. Each
    /// yields the redirect URI <c>{origin}/auth/callback</c> and the post-logout redirect
    /// URI <c>{origin}/login</c>. Applied to the database by the <c>migrate</c>
    /// subcommand.
    /// </summary>
    public string[] Origins { get; set; } = [];
}
