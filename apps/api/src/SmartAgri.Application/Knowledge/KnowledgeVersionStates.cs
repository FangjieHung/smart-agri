using System.Text.Json.Serialization;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>
/// Where a version stands in review, as the owner sees it (M2 plan, Slice 8: 待確認／已排定生效／
/// 目前有效／已封存). Derived, never stored (<see cref="KnowledgeVersionStates.Of"/>); shown next
/// to — not instead of — the processing status, so "可使用" is never read as "in use". Whether
/// the whole document is disabled is a separate flag.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeVersionState>))]
public enum KnowledgeVersionState
{
    /// <summary>待確認: not approved yet (whatever its processing status).</summary>
    [JsonStringEnumMemberName("pending-review")]
    PendingReview,

    /// <summary>已排定生效: approved, effective from a moment still to come.</summary>
    [JsonStringEnumMemberName("scheduled")]
    Scheduled,

    /// <summary>目前有效: the document's current effective version
    /// (<see cref="RetrievableChunks.CurrentEffectiveVersion"/>).</summary>
    [JsonStringEnumMemberName("effective")]
    Effective,

    /// <summary>已封存: approved and already effective once, but replaced by a later approved
    /// version.</summary>
    [JsonStringEnumMemberName("archived")]
    Archived,
}

/// <summary>Derives <see cref="KnowledgeVersionState"/>.</summary>
public static class KnowledgeVersionStates
{
    /// <param name="isCurrentEffective">Whether the version satisfies
    /// <see cref="RetrievableChunks.CurrentEffectiveVersion"/> at the same <paramref name="now"/>.</param>
    public static KnowledgeVersionState Of(
        KnowledgeReviewState reviewState,
        DateTimeOffset? effectiveFrom,
        bool isCurrentEffective,
        DateTimeOffset now) =>
        reviewState == KnowledgeReviewState.PendingReview ? KnowledgeVersionState.PendingReview
        : isCurrentEffective ? KnowledgeVersionState.Effective
        : effectiveFrom > now ? KnowledgeVersionState.Scheduled
        : KnowledgeVersionState.Archived;
}
