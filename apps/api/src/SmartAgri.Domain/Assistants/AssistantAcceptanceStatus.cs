using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// An assistant's acceptance status (「驗收狀態」; M3.5 plan §3, issue #125). Derived from its test
/// set and test runs whenever it is read, never stored (<c>AssistantAcceptanceRules.Derive</c>).
/// Only shown in this milestone: it does not block use within the organization.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantAcceptanceStatus>))]
public enum AssistantAcceptanceStatus
{
    /// <summary>尚未驗收: no test case, or no run has completed yet.</summary>
    [JsonStringEnumMemberName("not-accepted")]
    NotAccepted,

    /// <summary>通過: every test case of the latest completed run passed.</summary>
    [JsonStringEnumMemberName("passed")]
    Passed,

    /// <summary>未通過: any test case of the latest completed run failed.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>已過期: since the latest completed run was queued, a connected knowledge base or
    /// the assistant itself changed (a later run was asked for with an automatic trigger and has
    /// not completed).</summary>
    [JsonStringEnumMemberName("outdated")]
    Outdated,
}
