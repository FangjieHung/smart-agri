using System.Text;
using System.Text.Json.Serialization;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Answers;

/// <summary>
/// The answering rules <see cref="GroundedAnswerService"/> follows, decoupled from the
/// <see cref="Assistant"/> entity so a wizard draft (#78, trial answers) can be answered with
/// the same pipeline before any assistant exists.
/// </summary>
/// <param name="Name">The assistant's name, as the prompt introduces it.</param>
/// <param name="Purpose">What it is for (用途), for the prompt.</param>
/// <param name="Tone">The voice to answer in.</param>
/// <param name="RoleInstructions">Free-text role instructions (角色設定); may be empty.</param>
/// <param name="KnowledgeScope">Whether a question below the relevance threshold is refused
/// (<see cref="AssistantKnowledgeScope.CompanyDataOnly"/>, no model call) or answered from
/// general knowledge.</param>
/// <param name="RefusalMessage">The text of every <c>no-result</c> reply.</param>
/// <param name="MinScore">The assistant's own relevance threshold; <see langword="null"/> for the
/// deployment's (<see cref="KnowledgeRetrievalSettings.MinScore"/>).</param>
/// <param name="OwnerAccountId">Whose access decides which knowledge bases may be searched
/// (<see cref="Assistants.AssistantKnowledgeAccess.ConnectableBy"/>, re-checked on every
/// answer): the assistant's owner, or the draft's.</param>
/// <param name="KnowledgeBaseIds">The knowledge bases the assistant (or draft) names. Any the
/// owner can no longer connect — e.g. a share withdrawn after the assistant was built — are
/// dropped at answer time.</param>
public sealed record GroundedAnswerProfile(
    string Name,
    string Purpose,
    AssistantTone Tone,
    string RoleInstructions,
    AssistantKnowledgeScope KnowledgeScope,
    string RefusalMessage,
    double? MinScore,
    Guid OwnerAccountId,
    IReadOnlyCollection<Guid> KnowledgeBaseIds)
{
    /// <summary>An assistant's own rules, with the knowledge bases currently connected to it
    /// (<see cref="IAnswerKnowledgeBases.ConnectedToAsync"/>).</summary>
    public static GroundedAnswerProfile For(Assistant assistant, IReadOnlyCollection<Guid> connectedKnowledgeBaseIds)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(connectedKnowledgeBaseIds);
        return new GroundedAnswerProfile(
            assistant.Name,
            assistant.Purpose,
            assistant.Tone,
            assistant.RoleInstructions,
            assistant.KnowledgeScope,
            assistant.RefusalMessage,
            assistant.MinScore,
            assistant.OwnerAccountId,
            connectedKnowledgeBaseIds);
    }
}

/// <summary>Who said an earlier turn of the conversation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConversationAuthor>))]
public enum ConversationAuthor
{
    [JsonStringEnumMemberName("account")]
    Account,

    [JsonStringEnumMemberName("assistant")]
    Assistant,
}

/// <summary>One earlier turn, as plain text. Only ever context for the model: citations never
/// come from here, whoever supplied it (a saved thread, or a client that keeps no
/// conversations).</summary>
public sealed record ConversationTurn(ConversationAuthor Author, string Text);

/// <summary>One question to answer.</summary>
/// <param name="Profile">The rules to answer by.</param>
/// <param name="Question">The question, validated by the caller (non-blank; Slice 7 caps it at
/// 2,000 characters). Trimmed here.</param>
/// <param name="History">Earlier turns, oldest first. Only the newest ones that fit
/// <see cref="GroundedAnswerPrompt.HistoryMaxCharacters"/> reach the model; the last
/// <see cref="ConversationAuthor.Account"/> turn also joins the retrieval query (M3 plan §7 D).</param>
/// <param name="AccountId">Who asked: recorded on every model call it causes.</param>
/// <param name="AssistantId">For which assistant; <see langword="null"/> for a draft's trial
/// answer.</param>
public sealed record GroundedAnswerRequest(
    GroundedAnswerProfile Profile,
    string Question,
    IReadOnlyList<ConversationTurn> History,
    Guid AccountId,
    Guid? AssistantId);

/// <summary>The three reply kinds this pipeline produces; wire names equal the frontend's
/// <c>ChatReplyView['kind']</c>. They never mix within one reply (M3 plan §7 B).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GroundedReplyKind>))]
public enum GroundedReplyKind
{
    [JsonStringEnumMemberName("company-data")]
    CompanyData,

    [JsonStringEnumMemberName("general-knowledge")]
    GeneralKnowledge,

    [JsonStringEnumMemberName("no-result")]
    NoResult,
}

/// <summary>Why an answer became <c>no-result</c>. Recorded as a metric tag
/// (<see cref="GroundedAnswerTelemetry.RejectionsInstrument"/>) — the reason only, never the
/// question or the model's text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GroundedRejectionReason>))]
public enum GroundedRejectionReason
{
    /// <summary><c>company-data-only</c> and no passage reached the threshold (including: no
    /// knowledge base left to search). The model was not called.</summary>
    [JsonStringEnumMemberName("below-threshold")]
    BelowThreshold,

    /// <summary>The model cited a passage number outside 1…k.</summary>
    [JsonStringEnumMemberName("citation-out-of-range")]
    CitationOutOfRange,

    /// <summary>The model's answer cited no passage at all.</summary>
    [JsonStringEnumMemberName("no-citation")]
    NoCitation,

    /// <summary>The model answered with <see cref="Ai.ChatAnswerMarkers.CannotAnswer"/>.</summary>
    [JsonStringEnumMemberName("cannot-answer")]
    CannotAnswer,

    /// <summary>A general-knowledge answer that was empty once citation markers were removed.</summary>
    [JsonStringEnumMemberName("empty-answer")]
    EmptyAnswer,
}

/// <summary>
/// One passage a <c>company-data</c> reply cites, as it should be saved (M3 plan §4,
/// <c>ChatMessageCitation</c>): a snapshot, so the conversation keeps showing it after the
/// document changes or is deleted.
/// </summary>
/// <param name="Ordinal">1-based, in the order the reply first cites it; the reply's text uses
/// these numbers.</param>
/// <param name="Excerpt">The passage's first <see cref="ExcerptMaxLength"/> characters
/// (<c>ChatCitationView.excerpt</c>).</param>
/// <param name="Text">The passage's whole text (the citation drawer's 原文).</param>
/// <param name="Score">Its relevance to the question (cosine similarity).</param>
public sealed record GroundedCitation(
    int Ordinal,
    Guid ChunkId,
    Guid KnowledgeBaseId,
    string KnowledgeBaseName,
    Guid DocumentId,
    string DocumentName,
    Guid VersionId,
    int VersionNumber,
    string LocationLabel,
    string Excerpt,
    string Text,
    double Score,
    DateTimeOffset? VersionEffectiveFrom)
{
    /// <summary>The longest excerpt, in Unicode scalars (M3 plan §4: 「取段落前 200 字」).</summary>
    public const int ExcerptMaxLength = 200;

    internal static GroundedCitation From(int ordinal, RetrievedKnowledgePassage passage, string knowledgeBaseName) => new(
        ordinal,
        passage.ChunkId,
        passage.KnowledgeBaseId,
        knowledgeBaseName,
        passage.DocumentId,
        passage.DocumentName,
        passage.VersionId,
        passage.VersionNumber,
        passage.LocationLabel,
        ExcerptOf(passage.Text),
        passage.Text,
        passage.Score,
        passage.VersionEffectiveFrom);

    /// <summary>The first <see cref="ExcerptMaxLength"/> Unicode scalars of
    /// <paramref name="text"/> (never half a surrogate pair), followed by 「…」 when cut.</summary>
    public static string ExcerptOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == ExcerptMaxLength)
            {
                return builder.ToString().TrimEnd() + KnowledgeRetrievalRules.Ellipsis;
            }

            builder.Append(rune.ToString());
            count++;
        }

        return text;
    }
}

/// <summary>
/// The final, validated reply — the one to save and to send as <c>smartagri.reply</c>. Its
/// shape follows the frontend's <c>ChatReplyView</c> union: <see cref="Citations"/> only for
/// <c>company-data</c>, <see cref="Notice"/> only for <c>general-knowledge</c>,
/// <see cref="NextSteps"/> only for <c>no-result</c>.
/// </summary>
/// <param name="Text">For <c>company-data</c>, the model's answer with its citation markers
/// renumbered to <see cref="GroundedCitation.Ordinal"/> (<c>[1]</c>, <c>[2]</c>, … in half-width
/// brackets); for <c>general-knowledge</c>, the answer with every marker removed; for
/// <c>no-result</c>, the profile's <see cref="GroundedAnswerProfile.RefusalMessage"/>.</param>
/// <param name="RejectionReason">Why a <c>no-result</c> reply is one; <see langword="null"/>
/// otherwise.</param>
public sealed record GroundedReply(
    GroundedReplyKind Kind,
    string Text,
    IReadOnlyList<GroundedCitation> Citations,
    string? Notice,
    IReadOnlyList<string> NextSteps,
    GroundedRejectionReason? RejectionReason)
{
    /// <summary>What a <c>general-knowledge</c> reply always carries (the frontend's
    /// <c>CHAT_GENERAL_KNOWLEDGE_NOTICE</c>).</summary>
    public const string GeneralKnowledgeNotice = "這不是組織資料，是一般知識補充，僅供參考。";

    /// <summary>The fixed suggestions of every <c>no-result</c> reply.</summary>
    public static IReadOnlyList<string> NoResultNextSteps { get; } =
    [
        "換個說法再問一次，或把問題問得更具體一些。",
        "仍然找不到時，請聯絡這個助理的管理者補充相關資料。",
    ];

    internal static GroundedReply CompanyData(string text, IReadOnlyList<GroundedCitation> citations) =>
        new(GroundedReplyKind.CompanyData, text, citations, null, [], null);

    internal static GroundedReply GeneralKnowledge(string text) =>
        new(GroundedReplyKind.GeneralKnowledge, text, [], GeneralKnowledgeNotice, [], null);

    internal static GroundedReply NoResult(string refusalMessage, GroundedRejectionReason reason) =>
        new(GroundedReplyKind.NoResult, refusalMessage, [], null, NoResultNextSteps, reason);
}

/// <summary>
/// What <see cref="GroundedAnswerService.StreamAsync"/> yields: any number of
/// <see cref="GroundedAnswerTextDelta"/>s, then exactly one final
/// <see cref="GroundedAnswerCompleted"/> or <see cref="GroundedAnswerRejected"/>. Protocol-free:
/// the Api turns them into AG-UI events (M3 plan §3).
/// </summary>
public abstract record GroundedAnswerEvent;

/// <summary>
/// Model text as it arrives, unvalidated and with its citation markers exactly as the model
/// wrote them (<c>[2]</c>, <c>【1】</c>, …): show it as "being checked"; the final event's
/// <see cref="GroundedReply.Text"/> replaces it (renumbered, or a <c>no-result</c> in its place).
/// The <see cref="Ai.ChatAnswerMarkers.CannotAnswer"/> marker is held back, never sent.
/// </summary>
public sealed record GroundedAnswerTextDelta(string Text) : GroundedAnswerEvent;

/// <summary>The answer passed: a <c>company-data</c> or <c>general-knowledge</c> reply.</summary>
public sealed record GroundedAnswerCompleted(GroundedReply Reply) : GroundedAnswerEvent;

/// <summary>The answer became <c>no-result</c>, for <paramref name="Reason"/>; replace
/// anything shown so far with <paramref name="Reply"/>.</summary>
public sealed record GroundedAnswerRejected(GroundedRejectionReason Reason, GroundedReply Reply) : GroundedAnswerEvent;

/// <summary><see cref="GroundedAnswerService.AnswerAsync"/>'s result: the final reply plus what
/// retrieval found (every passage with its score, and the threshold used), so a wizard trial
/// (#78) can show why it answered as it did.</summary>
public sealed record GroundedAnswerResult(GroundedReply Reply, KnowledgeRetrievalResult Retrieval);
