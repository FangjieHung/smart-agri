using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// The three checks of the LINE channel's 「測試連線」 (M5b plan §3 B, decision B), in the order they
/// run. Run by M5b #230; until then a channel has no results.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LineConnectionCheckKind>))]
public enum LineConnectionCheckKind
{
    /// <summary><c>GET /v2/bot/info</c> accepts the access token, and its basic id is the official
    /// account id the owner entered.</summary>
    [JsonStringEnumMemberName("access-token")]
    AccessToken,

    /// <summary><c>PUT /v2/bot/channel/webhook/endpoint</c> set the webhook URL to this server's.</summary>
    [JsonStringEnumMemberName("webhook-endpoint")]
    WebhookEndpoint,

    /// <summary><c>POST /v2/bot/channel/webhook/test</c>: LINE delivered a signed test event that this
    /// server verified with the stored channel secret.</summary>
    [JsonStringEnumMemberName("webhook-test")]
    WebhookTest,
}

/// <summary>A connection check's result. <see cref="Pending"/> is only ever shown, never stored: it
/// stands for a check that has not run since the connection settings last changed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LineConnectionCheckState>))]
public enum LineConnectionCheckState
{
    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("passed")]
    Passed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>Not run because an earlier check failed (a wrong token stops the other two).</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>
/// One stored connection check result (an element of <see cref="AssistantLineChannel.ConnectionChecks"/>,
/// a <c>jsonb</c> array). <see cref="Message"/> explains a failure to the owner; it never contains a
/// credential.
/// </summary>
public sealed record LineConnectionCheck(LineConnectionCheckKind Check, LineConnectionCheckState State, string Message)
{
    /// <summary>The checks a connection test runs, in order.</summary>
    public static IReadOnlyList<LineConnectionCheckKind> All { get; } =
        [LineConnectionCheckKind.AccessToken, LineConnectionCheckKind.WebhookEndpoint, LineConnectionCheckKind.WebhookTest];

    /// <summary>True when <paramref name="checks"/> has every one of <see cref="All"/>, each
    /// <see cref="LineConnectionCheckState.Passed"/> — what enabling the channel needs, and what keeps
    /// an enabled channel usable.</summary>
    public static bool AllPassed(IReadOnlyCollection<LineConnectionCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return All.All(kind => checks.Any(check => check.Check == kind && check.State == LineConnectionCheckState.Passed))
            && checks.All(check => check.State == LineConnectionCheckState.Passed);
    }
}
