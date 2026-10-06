using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// Where a <see cref="Case"/> is (case ADR「狀態與流轉」; M7 plan §3 D): only these five.
/// <see cref="Pending"/>, <see cref="InProgress"/> and <see cref="AwaitingInfo"/> are open;
/// <see cref="Completed"/> and <see cref="Cancelled"/> are closed and never reopened (a new case
/// links the old one instead). Moves only through the action table (M7-4, <c>CaseActionRules</c>); a new case is
/// always <see cref="Pending"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaseStatus>))]
public enum CaseStatus
{
    /// <summary>待受理: waiting for a member of the case's group to accept it.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>處理中: the case owner is working on it.</summary>
    [JsonStringEnumMemberName("in-progress")]
    InProgress,

    /// <summary>待補件: the case owner asked the creator for more information (the due time keeps running).</summary>
    [JsonStringEnumMemberName("awaiting-info")]
    AwaitingInfo,

    /// <summary>已完成, with a resolution.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>已取消.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

/// <summary>Helpers over <see cref="CaseStatus"/>.</summary>
public static class CaseStatuses
{
    /// <summary>未結案: the default of the case list (decision Q), and what keeps a group from being archived.</summary>
    public static readonly IReadOnlyList<CaseStatus> Open = [CaseStatus.Pending, CaseStatus.InProgress, CaseStatus.AwaitingInfo];

    /// <summary>已結案.</summary>
    public static readonly IReadOnlyList<CaseStatus> Closed = [CaseStatus.Completed, CaseStatus.Cancelled];

    public static bool IsOpen(this CaseStatus status) => status is CaseStatus.Pending or CaseStatus.InProgress or CaseStatus.AwaitingInfo;
}
