namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Configuration section <c>Coordinator:Line</c> (store-coordinator design decision E, #372): the
/// platform's shared 統籌助理 LINE official account — platform settings, not any organization's data
/// (<c>Coordinator__Line__ChannelSecret</c>, <c>Coordinator__Line__ChannelAccessToken</c>). Optional:
/// with both blank the coordinator is off and its webhook answers <c>404</c>; with only one set the Api
/// refuses to start. Both values are secrets: never logged, never returned.
/// </summary>
/// <remarks>Nullable, so the blank values compose passes for unset <c>.env</c> entries bind as unset.</remarks>
public sealed class CoordinatorLineOptions
{
    public const string SectionName = "Coordinator:Line";

    /// <summary>The Messaging API channel's secret (webhook signatures).</summary>
    public string? ChannelSecret { get; set; }

    /// <summary>The channel's long-lived access token (replies, bot info).</summary>
    public string? ChannelAccessToken { get; set; }

    /// <summary>Both values are set: the coordinator is on.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ChannelSecret) && !string.IsNullOrWhiteSpace(ChannelAccessToken);

    /// <summary>The secret as configured, trimmed (only when <see cref="IsConfigured"/>).</summary>
    public string Secret => ChannelSecret?.Trim() ?? string.Empty;

    /// <summary>The access token as configured, trimmed (only when <see cref="IsConfigured"/>).</summary>
    public string AccessToken => ChannelAccessToken?.Trim() ?? string.Empty;

    /// <summary>Why this configuration may not start, or <see langword="null"/>. Names the missing
    /// setting, never a value.</summary>
    public string? Validate()
    {
        var hasSecret = !string.IsNullOrWhiteSpace(ChannelSecret);
        var hasToken = !string.IsNullOrWhiteSpace(ChannelAccessToken);
        if (hasSecret == hasToken)
        {
            return null;
        }

        var (set, missing) = hasSecret
            ? (nameof(ChannelSecret), nameof(ChannelAccessToken))
            : (nameof(ChannelAccessToken), nameof(ChannelSecret));
        return $"{SectionName}:{set} is set but {SectionName}:{missing} is not: set both to turn the 統籌助理 (store coordinator) on, " +
            $"or leave both empty to keep it off (environment variables Coordinator__Line__ChannelSecret and Coordinator__Line__ChannelAccessToken).";
    }
}
