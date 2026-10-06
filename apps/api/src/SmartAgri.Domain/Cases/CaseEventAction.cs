using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// What a <see cref="CaseEvent"/> records (M7 plan §4). M7-3 only writes <see cref="Created"/>; the
/// rest are the action table's (M7-4), declared now because <see cref="Accepted"/> already decides
/// who has been a case owner (<c>CaseVisibility</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaseEventAction>))]
public enum CaseEventAction
{
    /// <summary>The case was created: <see cref="CaseEvent.Status"/> (pending), <see cref="CaseEvent.ToGroupId"/>
    /// and <see cref="CaseEvent.DueAt"/> are its starting values. No actor for a case opened
    /// automatically from a database submission (decision M).</summary>
    [JsonStringEnumMemberName("created")]
    Created,

    /// <summary>A member of the group accepted it and became the case owner
    /// (<see cref="CaseEvent.OwnerAccountId"/>); everyone who ever did keeps seeing the case.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [JsonStringEnumMemberName("info-requested")]
    InfoRequested,

    [JsonStringEnumMemberName("commented")]
    Commented,

    [JsonStringEnumMemberName("resumed")]
    Resumed,

    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>Moved from <see cref="CaseEvent.FromGroupId"/> to <see cref="CaseEvent.ToGroupId"/>.</summary>
    [JsonStringEnumMemberName("transferred")]
    Transferred,

    /// <summary><see cref="CaseEvent.DueAt"/> is the new due time.</summary>
    [JsonStringEnumMemberName("due-changed")]
    DueChanged,
}
