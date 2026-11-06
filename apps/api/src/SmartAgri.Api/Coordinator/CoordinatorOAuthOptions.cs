namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Configuration section <c>Coordinator:Facebook</c> (store-coordinator design §5 Slices 6 and 8, #377, #379): the
/// platform's Meta app that shops' owners authorize for their FB 粉專 — platform settings, not any organization's data
/// (<c>Coordinator__Facebook__AppId</c>, <c>Coordinator__Facebook__AppSecret</c>, optionally
/// <c>Coordinator__Facebook__ConfigId</c>). Optional: with both blank FB is not configured (no authorization links, its
/// callback answers <c>404</c>, FB 貼文 stays a reminder); with only one set the Api refuses to start. The secret is
/// never logged or returned. Read by <see cref="CoordinatorFacebookOAuthProvider"/>, <see cref="FacebookGraphClient"/>
/// (the <c>appsecret_proof</c>) and <see cref="CoordinatorFacebookPostExecutor"/> (#379).
/// </summary>
/// <remarks>Nullable, so the blank values compose passes for unset <c>.env</c> entries bind as unset.</remarks>
public sealed class CoordinatorFacebookOptions
{
    public const string SectionName = "Coordinator:Facebook";

    /// <summary>The Meta app's id (public: it is part of the consent URL).</summary>
    public string? AppId { get; set; }

    /// <summary>The Meta app's secret (exchanging codes, and the <c>appsecret_proof</c> of every Graph call).</summary>
    public string? AppSecret { get; set; }

    /// <summary>
    /// Optional: the id of a Facebook Login for Business <em>configuration</em> (App Dashboard → Facebook 登入（企業版） →
    /// 設定), which then replaces <c>scope</c> on the consent screen (#379). Its token type must be a User access token
    /// with <c>pages_show_list</c>, <c>pages_manage_posts</c> and <c>pages_read_engagement</c>. Digits only; blank means
    /// the consent screen asks for those three permissions by <c>scope</c>. Public (part of the consent URL).
    /// </summary>
    public string? ConfigId { get; set; }

    /// <summary>Both values are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);

    /// <summary><see cref="ConfigId"/> trimmed, or <see langword="null"/> when blank.</summary>
    public string? ResolvedConfigId => string.IsNullOrWhiteSpace(ConfigId) ? null : ConfigId.Trim();

    /// <summary>Why this configuration may not start, or <see langword="null"/>. Names the missing setting, never a value.</summary>
    public string? Validate()
    {
        if (CoordinatorOAuthSettings.BothOrNeither(SectionName, (nameof(AppId), AppId), (nameof(AppSecret), AppSecret), "FB 粉專") is { } error)
        {
            return error;
        }

        if (ResolvedConfigId is { } configId)
        {
            if (!IsConfigured)
            {
                return $"{SectionName}:{nameof(ConfigId)} is set but {SectionName}:{nameof(AppId)} and {SectionName}:{nameof(AppSecret)} are not: "
                    + "set all three, or leave them all empty (environment variables Coordinator__Facebook__*).";
            }

            if (!configId.All(char.IsAsciiDigit) || configId.Length > 32)
            {
                return $"{SectionName}:{nameof(ConfigId)} is the id of a Facebook Login for Business configuration: digits only.";
            }
        }

        return null;
    }
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
