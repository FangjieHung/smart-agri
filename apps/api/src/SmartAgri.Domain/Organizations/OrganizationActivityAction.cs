using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Organizations;

/// <summary>
/// What an <see cref="OrganizationActivity"/> row records. Stored as the wire name (not the number),
/// so members can be reordered safely. Later slices add theirs here (M7-1: case groups; M7-2:
/// case types; M7-10: a database's auto case; #356: deactivating and reactivating members).
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

    /// <summary>A manager created a case group (承辦組, M7-1). Detail: <c>id</c>, <c>name</c>
    /// (<see cref="OrganizationActivity.CaseGroupCreated"/>).</summary>
    [JsonStringEnumMemberName("case-group-created")]
    CaseGroupCreated,

    /// <summary>A manager renamed a case group (M7-1). Detail: <c>id</c>, <c>name</c> (the new one),
    /// <c>previousName</c>.</summary>
    [JsonStringEnumMemberName("case-group-renamed")]
    CaseGroupRenamed,

    /// <summary>A manager archived a case group (M7-1). Detail: <c>id</c>, <c>name</c>.</summary>
    [JsonStringEnumMemberName("case-group-archived")]
    CaseGroupArchived,

    /// <summary>A manager put an archived case group back in use (M7-1). Detail: <c>id</c>,
    /// <c>name</c>.</summary>
    [JsonStringEnumMemberName("case-group-unarchived")]
    CaseGroupUnarchived,

    /// <summary>A manager created a case type (案件類型, M7-2). Detail: <c>id</c>, <c>name</c>
    /// (<see cref="OrganizationActivity.CaseTypeCreated"/>).</summary>
    [JsonStringEnumMemberName("case-type-created")]
    CaseTypeCreated,

    /// <summary>A manager changed a case type (M7-2), including deactivating or reactivating it.
    /// Detail: <c>id</c>, <c>name</c> (the current one), <c>changed</c> (the wire names of the fields
    /// that changed) and <c>isActive</c> — never the description's text
    /// (<see cref="OrganizationActivity.CaseTypeUpdated"/>).</summary>
    [JsonStringEnumMemberName("case-type-updated")]
    CaseTypeUpdated,

    /// <summary>A manager turned a database's 送出後自動開案 on, off or to another case type (M7-10,
    /// issue #255). Detail: <c>databaseId</c>, <c>databaseName</c>, <c>from</c> and <c>to</c>, each
    /// <c>{ id, name }</c> of a case type or <see langword="null"/> for off
    /// (<see cref="OrganizationActivity.DatabaseAutoCaseChanged"/>).</summary>
    [JsonStringEnumMemberName("database-auto-case-changed")]
    DatabaseAutoCaseChanged,

    /// <summary>A member was deactivated and their work handed to a successor (停用成員並指定接手人,
    /// issue #356). Detail: <c>member</c> and <c>successor</c>, each <c>{ id, displayName }</c>, and
    /// <c>transferred</c> — how many assistants, drafts, knowledge bases, databases, reassigned and
    /// unassigned 處理事項 and handed-over cases (<see cref="OrganizationActivity.MemberDeactivated"/>).</summary>
    [JsonStringEnumMemberName("member-deactivated")]
    MemberDeactivated,

    /// <summary>A deactivated member was put back in use (issue #356). Detail: <c>member</c>
    /// <c>{ id, displayName }</c>. Nothing transferred at deactivation comes back.</summary>
    [JsonStringEnumMemberName("member-reactivated")]
    MemberReactivated,
}
