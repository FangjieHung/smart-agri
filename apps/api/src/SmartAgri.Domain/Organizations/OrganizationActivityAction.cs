using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Organizations;

/// <summary>
/// What an <see cref="OrganizationActivity"/> row records. Stored as the wire name (not the number),
/// so members can be reordered safely. Later slices add theirs here (M7: case teams and case
/// types).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrganizationActivityAction>))]
public enum OrganizationActivityAction
{
    /// <summary>A manager chose another chat model (M6-2). Detail: <c>from</c> and <c>to</c>, each
    /// <c>{ id, displayName }</c> (<see cref="OrganizationActivity.ChatModelChanged"/>).</summary>
    [JsonStringEnumMemberName("chat-model-changed")]
    ChatModelChanged,

    /// <summary>A manager changed the conversation retention (M6-4): a shorter value is now pending,
    /// or a longer one applies at once. Detail: <c>from</c>, <c>to</c> (days; <see langword="null"/>
    /// is forever) and <c>effectiveAt</c> (<see cref="OrganizationActivity.RetentionChanged"/>).</summary>
    [JsonStringEnumMemberName("retention-changed")]
    RetentionChanged,

    /// <summary>A manager sent the current retention back while a shorter one was pending
    /// (「改回」, M6-4). Detail: <c>days</c>, <c>cancelledDays</c>, <c>cancelledEffectiveAt</c>.</summary>
    [JsonStringEnumMemberName("retention-change-cancelled")]
    RetentionChangeCancelled,

    /// <summary>The daily cleanup made a pending retention current after its buffer (M6-4); a system
    /// action. Detail: <c>from</c>, <c>to</c>.</summary>
    [JsonStringEnumMemberName("retention-took-effect")]
    RetentionTookEffect,

    /// <summary>A cleanup deleted expired conversations (M6-4); a system action, written only when it
    /// deleted something. Detail: <c>days</c>, <c>cutoff</c>, <c>threadCount</c>,
    /// <c>answerOutcomeCount</c> — counts only, never which threads.</summary>
    [JsonStringEnumMemberName("retention-cleanup")]
    RetentionCleanup,

    /// <summary>A manager deleted every member's saved conversations on one assistant at once
    /// (「立即刪除」, M6-5). Detail: <c>assistantId</c>, <c>assistantName</c> and <c>threadCount</c>
    /// (<see cref="OrganizationActivity.ConversationsPurged"/>) — never which threads or what they
    /// said. The members are not notified.</summary>
    [JsonStringEnumMemberName("conversations-purged")]
    ConversationsPurged,
}
