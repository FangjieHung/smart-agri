using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// Creates or updates the <c>admin-spa</c> OAuth client in OpenIddict's application table.
/// Idempotent: running it again with the same configuration changes nothing; with changed
/// <see cref="AdminSpaClientOptions.Origins"/> it rewrites the redirect URIs.
/// </summary>
/// <remarks>
/// Invoked by the <c>migrate</c> subcommand, not on web startup. The web host never
/// touches the database while starting (<c>/health/live</c> must answer without it, and
/// the M1 plan keeps schema changes out of normal startup); the client row is deployment
/// state of the same kind as the schema, so it is applied at the same step — the
/// container entrypoint and the documented local workflow both run <c>migrate</c> first.
/// </remarks>
public sealed class AdminSpaClientRegistrar
{
    private readonly IOpenIddictApplicationManager _applications;
    private readonly SmartAgriAuthenticationOptions _options;
    private readonly ILogger<AdminSpaClientRegistrar> _logger;

    public AdminSpaClientRegistrar(
        IOpenIddictApplicationManager applications,
        IOptions<SmartAgriAuthenticationOptions> options,
        ILogger<AdminSpaClientRegistrar> logger)
    {
        _applications = applications;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The descriptor for the configured origins.</summary>
    /// <exception cref="InvalidOperationException">An origin is not an absolute http(s)
    /// URI without path, query or fragment.</exception>
    public static OpenIddictApplicationDescriptor Describe(IEnumerable<string> origins)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = AdminSpaClientOptions.ClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Smart Agri admin",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
            },
            Requirements =
            {
                Requirements.Features.ProofKeyForCodeExchange,
            },
        };

        foreach (var origin in origins)
        {
            var baseUri = ParseOrigin(origin);
            descriptor.RedirectUris.Add(new Uri(baseUri, AdminSpaClientOptions.CallbackPath));
            descriptor.PostLogoutRedirectUris.Add(new Uri(baseUri, AdminSpaClientOptions.PostLogoutPath));
        }

        return descriptor;
    }

    /// <returns><see langword="false"/> when no origin is configured (nothing is written
    /// and sign-in through the SPA stays unavailable).</returns>
    public async Task<bool> EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (_options.AdminSpa.Origins.Length == 0)
        {
            _logger.LogWarning(
                "No {Setting} configured; the {ClientId} client was not registered, so signing in through the admin SPA is unavailable.",
                $"{SmartAgriAuthenticationOptions.SectionName}:AdminSpa:Origins",
                AdminSpaClientOptions.ClientId);
            return false;
        }

        var descriptor = Describe(_options.AdminSpa.Origins);
        var existing = await _applications.FindByClientIdAsync(AdminSpaClientOptions.ClientId, cancellationToken);
        if (existing is null)
        {
            await _applications.CreateAsync(descriptor, cancellationToken);
            _logger.LogInformation("Registered the {ClientId} client.", AdminSpaClientOptions.ClientId);
        }
        else
        {
            await _applications.UpdateAsync(existing, descriptor, cancellationToken);
            _logger.LogInformation("Updated the {ClientId} client.", AdminSpaClientOptions.ClientId);
        }

        return true;
    }

    private static Uri ParseOrigin(string origin)
    {
        if (!Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"'{origin}' in {SmartAgriAuthenticationOptions.SectionName}:AdminSpa:Origins must be an absolute http(s) origin such as https://admin.example.org.");
        }

        return uri;
    }
}
