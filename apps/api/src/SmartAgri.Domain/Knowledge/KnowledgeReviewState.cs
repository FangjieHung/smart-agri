using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// Whether a person approved a <see cref="KnowledgeDocumentVersion"/> (M2 plan §4 and §7
/// decision 4): every version, version 1 included, starts
/// <see cref="PendingReview"/>, and only an approved one can ever be retrieved. Being
/// processed <c>ready</c> only means its text could be read, not that its content is right
/// or current. Stored by wire name, separately from the processing status.
/// </summary>
/// <remarks>
/// "Scheduled", "in effect" and "archived" are not stored: they follow from
/// <see cref="KnowledgeDocumentVersion.EffectiveFrom"/>, the clock and the document's other
/// approved versions (the Application layer's <c>RetrievableChunks</c> rule), so no job has
/// to flip them when an effective date arrives.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeReviewState>))]
public enum KnowledgeReviewState
{
    [JsonStringEnumMemberName("pending-review")]
    PendingReview,

    [JsonStringEnumMemberName("approved")]
    Approved,
}
