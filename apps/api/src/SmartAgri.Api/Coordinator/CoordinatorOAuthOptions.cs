namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Configuration section <c>Coordinator:Facebook</c> (store-coordinator design §5 Slices 6 and 8, #377, #379): the
/// platform's Meta app that shops' owners authorize for their FB 粉專 — platform settings, not any organization's data
/// (<c>Coordinator__Facebook__AppId</c>, <c>Coordinator__Facebook__AppSecret</c>). Optional: with both blank FB is not
/// configured (no authorization links, its callback answers <c>404</c>, FB 貼文 stays a reminder); with only one set the
/// Api refuses to start. The secret is never logged or returned. Read by the FB provider (#379).
/// </summary>
/// <remarks>Nullable, so the blank values compose passes for unset <c>.env</c> entries bind as unset.</remarks>
public sealed class CoordinatorFacebookOptions
{
    public const string SectionName = "Coordinator:Facebook";

    /// <summary>The Meta app's id (public: it is part of the consent URL).</summary>
    public string? AppId { get; set; }

    /// <summary>The Meta app's secret (exchanging codes).</summary>
    public string? AppSecret { get; set; }

    /// <summary>Both values are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);

    /// <summary>Why this configuration may not start, or <see langword="null"/>. Names the missing setting, never a value.</summary>
    public string? Validate() =>
        CoordinatorOAuthSettings.BothOrNeither(SectionName, (nameof(AppId), AppId), (nameof(AppSecret), AppSecret), "FB 粉專");
}

/// <summary>
/// Configuration section <c>Coordinator:Google</c> (store-coordinator design §5 Slices 6 and 7, #377, #378): the
/// platform's Google Cloud OAuth client (a web application) that shops' owners authorize for their Google 商家 —
/// platform settings (<c>Coordinator__Google__ClientId</c>, <c>Coordinator__Google__ClientSecret</c>). Optional: with
/// both blank Google is not configured; with only one set the Api refuses to start. The secret is never logged or
/// returned. Read by the Google provider (#378).
/// </summary>
/// <remarks>Nullable, so the blank values compose passes for unset <c>.env</c> entries bind as unset.</remarks>
public sealed class CoordinatorGoogleOptions
{
    public const string SectionName = "Coordinator:Google";

    /// <summary>The OAuth client's id (public: it is part of the consent URL).</summary>
    public string? ClientId { get; set; }

    /// <summary>The OAuth client's secret (exchanging codes).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Both values are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Why this configuration may not start, or <see langword="null"/>. Names the missing setting, never a value.</summary>
    public string? Validate() =>
        CoordinatorOAuthSettings.BothOrNeither(SectionName, (nameof(ClientId), ClientId), (nameof(ClientSecret), ClientSecret), "Google 商家");
}

internal static class CoordinatorOAuthSettings
{
    /// <summary>Both set or both blank: <see langword="null"/>; otherwise which one is missing (never a value).</summary>
    public static string? BothOrNeither(string section, (string Name, string? Value) first, (string Name, string? Value) second, string what)
    {
        var hasFirst = !string.IsNullOrWhiteSpace(first.Value);
        var hasSecond = !string.IsNullOrWhiteSpace(second.Value);
        if (hasFirst == hasSecond)
        {
            return null;
        }

        var (set, missing) = hasFirst ? (first.Name, second.Name) : (second.Name, first.Name);
        var variables = section.Replace(":", "__", StringComparison.Ordinal);
        return $"{section}:{set} is set but {section}:{missing} is not: set both to let shops' owners authorize their {what}, " +
            $"or leave both empty (environment variables {variables}__{first.Name} and {variables}__{second.Name}).";
    }
}
