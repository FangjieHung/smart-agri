namespace SmartAgri.Infrastructure.Line;

/// <summary>
/// Configuration section <c>Line</c> (M5b plan §3 H). Optional: every value has a default.
/// </summary>
public sealed class LineOptions
{
    public const string SectionName = "Line";

    /// <summary>LINE's Messaging API, the only base address Production may use.</summary>
    public const string OfficialApiBaseUrl = "https://api.line.me";

    /// <summary>
    /// The Messaging API's base address. Defaults to <see cref="OfficialApiBaseUrl"/>; tests and
    /// end-to-end runs point it at a fake LINE server. In Production anything else refuses to start:
    /// the channel access tokens would be sent there.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary><see cref="ApiBaseUrl"/> as an absolute URI ending in <c>/</c> (so relative paths keep a
    /// base path), or <see langword="null"/> when it is not an absolute http(s) URL without user info,
    /// query or fragment.</summary>
    public Uri? ResolvedApiBaseUrl =>
        Uri.TryCreate(string.IsNullOrWhiteSpace(ApiBaseUrl) ? OfficialApiBaseUrl : ApiBaseUrl.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
            ? new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/")
            : null;

    /// <summary>Whether <see cref="ResolvedApiBaseUrl"/> is LINE's own address.</summary>
    public bool IsOfficial =>
        ResolvedApiBaseUrl is { } uri && string.Equals(uri.AbsoluteUri, OfficialApiBaseUrl + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why this configuration may not start in <paramref name="environmentName"/>, or
    /// <see langword="null"/>.</summary>
    public string? Validate(string environmentName)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        if (ResolvedApiBaseUrl is null)
        {
            return $"{SectionName}:{nameof(ApiBaseUrl)} '{ApiBaseUrl}' must be an absolute http(s) URL such as {OfficialApiBaseUrl} (no query or fragment), or unset.";
        }

        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase) && !IsOfficial)
        {
            return $"{SectionName}:{nameof(ApiBaseUrl)} must be {OfficialApiBaseUrl} (or unset) in Production, not '{ApiBaseUrl}': channel access tokens are sent to it.";
        }

        return null;
    }
}
