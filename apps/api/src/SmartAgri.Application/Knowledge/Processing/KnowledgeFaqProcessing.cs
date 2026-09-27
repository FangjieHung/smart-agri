using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>
/// What an FAQ version is stored as (M2 plan §4, Slice 10): <b>one</b> unit and <b>one</b>
/// chunk, both located <see cref="KnowledgeLocationLabels.Faq"/>, whose text holds the question
/// and the answer — so a retrieval question can match either, and the excerpt shows both. The
/// version is always <c>ready</c>: the owner typed the text, so there is nothing to judge, and it
/// is never cut into several chunks however long the answer is (at most
/// <see cref="KnowledgeFaqEntry.AnswerMaxLength"/> characters).
/// </summary>
/// <remarks>
/// Processing still runs as the <c>knowledge.process-version</c> job, like a document's, so the
/// chunk is embedded, retried and failed the same way (the plan: 「不經背景解析，但嵌入仍經過佇列」).
/// Pure: no I/O.
/// </remarks>
public static class KnowledgeFaqProcessing
{
    public const string QuestionPrefix = "問：";

    public const string AnswerPrefix = "答：";

    public static ProcessedVersion Process(KnowledgeFaqEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var text = Text(entry);
        var unit = new ProcessedUnit(
            Ordinal: 0,
            KnowledgeUnitLocationKind.Faq,
            KnowledgeLocationLabels.Faq,
            text,
            Readable: true,
            IssueCode: null,
            [new KnowledgeTextChunk(KnowledgeLocationLabels.Faq, text)]);
        return new ProcessedVersion(KnowledgeDocumentStatus.Ready, Issue: null, [unit]);
    }

    /// <summary>「問：…」 on the first line, then 「答：…」 (the answer may span several lines).</summary>
    public static string Text(KnowledgeFaqEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Concat(QuestionPrefix, entry.Question, "\n", AnswerPrefix, entry.Answer);
    }
}
