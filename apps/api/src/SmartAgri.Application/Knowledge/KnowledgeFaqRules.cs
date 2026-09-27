using System.Text;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>An FAQ request that passed <see cref="KnowledgeFaqRules.Validate"/>: what is stored.</summary>
/// <param name="Entry">The normalized question and answer.</param>
/// <param name="Name">What the entry is listed as (<see cref="KnowledgeFaqRules.NameFor"/>).</param>
/// <param name="Content">The stored bytes (<see cref="KnowledgeFaqEntry.ToContent"/>).</param>
/// <param name="Sha256">Of <paramref name="Content"/>, lower-case hex, as stored and compared for
/// duplicates.</param>
public sealed record KnowledgeFaqDraft(KnowledgeFaqEntry Entry, string Name, byte[] Content, string Sha256);

/// <summary>
/// The rules of <c>POST /api/v1/knowledge-bases/{id}/faqs</c> and <c>PUT .../faqs/{docId}</c>
/// (M2 plan Slice 10; ticket #44): what an FAQ entry may contain, what it is listed as, and when
/// it duplicates what the knowledge base already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Normalization.</b> The question is one line (<see cref="ExtractedText.CleanLine"/>: white
/// space runs, line breaks included, become one space); the answer keeps its lines
/// (<see cref="ExtractedText.Clean"/>: <c>\n</c> line breaks, no trailing spaces, at most one blank
/// line in a row). Both are NFC-normalized and trimmed, exactly as text read from a file is, so
/// the same entry typed twice gives the same bytes and the same SHA-256.
/// </para>
/// <para>
/// <b>Limits</b>, in UTF-16 characters after normalization: a question of 1–500 (the same limit
/// as a retrieval question), an answer of 1–4000. An FAQ is one chunk however long it is (plan
/// §4); 4000 characters is four of a document's largest chunks, room for a table of fees or a
/// step-by-step answer.
/// </para>
/// <para>
/// <b>Name.</b> The entry is listed under its question, cut to
/// <see cref="KnowledgeDocument.NameMaxLength"/> characters (ending in 「…」) when longer. Names are
/// unique per knowledge base (documents and FAQ entries alike), so the same question twice is
/// <c>duplicate-name</c> — the owner edits the existing entry instead. Only the first 254
/// characters of a longer question take part in that comparison.
/// </para>
/// <para>
/// <b>Duplicates</b>, content first (like uploads): content any version of the knowledge base
/// already has is <c>duplicate-content</c>; then a name another item has is <c>duplicate-name</c>.
/// Both refusals use <see cref="KnowledgeUploadRejection"/>'s reasons, so the frontend handles
/// them like an upload's, and both name the <see cref="QuestionField"/>.
/// </para>
/// </remarks>
public static class KnowledgeFaqRules
{
    public const string QuestionField = "question";

    public const string AnswerField = "answer";

    public const string QuestionRequiredMessage = "請輸入問題。";

    public const string AnswerRequiredMessage = "請輸入答案。";

    public const string Ellipsis = "…";

    public static readonly string QuestionTooLongMessage = $"問題最多 {KnowledgeFaqEntry.QuestionMaxLength} 個字。";

    public static readonly string AnswerTooLongMessage = $"答案最多 {KnowledgeFaqEntry.AnswerMaxLength} 個字。";

    /// <summary>
    /// A non-blank question and answer within the limits, normalized (see the remarks); every
    /// broken rule is reported at once. The draft carries everything the endpoint stores.
    /// </summary>
    public static ValidationResult<KnowledgeFaqDraft> Validate(string? question, string? answer)
    {
        var normalizedQuestion = ExtractedText.CleanLine(question ?? string.Empty);
        var normalizedAnswer = ExtractedText.Clean(answer ?? string.Empty);
        var failures = new List<ValidationFailure>();
        Check(normalizedQuestion, KnowledgeFaqEntry.QuestionMaxLength, QuestionField, QuestionRequiredMessage, QuestionTooLongMessage, failures);
        Check(normalizedAnswer, KnowledgeFaqEntry.AnswerMaxLength, AnswerField, AnswerRequiredMessage, AnswerTooLongMessage, failures);
        if (failures.Count > 0)
        {
            return ValidationResult<KnowledgeFaqDraft>.Invalid(failures);
        }

        var entry = new KnowledgeFaqEntry(normalizedQuestion, normalizedAnswer);
        var content = entry.ToContent();
        return ValidationResult<KnowledgeFaqDraft>.Valid(
            new KnowledgeFaqDraft(entry, NameFor(entry.Question), content, KnowledgeUploadRules.Sha256(content)));
    }

    /// <summary>What an entry with <paramref name="question"/> (already normalized) is listed
    /// as: the question itself, or its first 254 characters and 「…」 when it is longer than
    /// <see cref="KnowledgeDocument.NameMaxLength"/> (never half a surrogate pair, never a
    /// space before the 「…」).</summary>
    public static string NameFor(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (question.Length <= KnowledgeDocument.NameMaxLength)
        {
            return question;
        }

        var cut = KnowledgeDocument.NameMaxLength - Ellipsis.Length;
        if (char.IsHighSurrogate(question[cut - 1]))
        {
            cut--;
        }

        return new StringBuilder(question, 0, cut, KnowledgeDocument.NameMaxLength).ToString().TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// The duplicate rules for a new entry, given what the knowledge base already holds: a version
    /// with the same content (<paramref name="sameContent"/>) is refused first, whatever its name;
    /// otherwise an item already named <paramref name="name"/>. <see langword="null"/> when neither
    /// applies.
    /// </summary>
    public static KnowledgeUploadRejection? CheckNewEntryDuplicates(string name, KnowledgeExistingContent? sameContent, bool nameTaken)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (sameContent is not null)
        {
            return new KnowledgeUploadRejection(
                KnowledgeUploadRejectionReason.DuplicateContent,
                $"這個知識庫已經有內容完全相同的「{sameContent.DocumentName}」，不需要重複新增。",
                sameContent.DocumentName);
        }

        return nameTaken ? DuplicateName(name) : null;
    }

    /// <summary>
    /// The duplicate rules for an edit of <paramref name="documentId"/>: content any version
    /// already has — this entry's own included (an unchanged edit, or one back to an older
    /// version, which stays in the history) — is refused first; then a new name another item has.
    /// <paramref name="nameTakenByAnother"/> is only asked when the name changes.
    /// </summary>
    public static KnowledgeUploadRejection? CheckEditDuplicates(
        Guid documentId,
        string name,
        KnowledgeExistingContent? sameContent,
        bool nameTakenByAnother)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (sameContent is not null)
        {
            var message = sameContent.DocumentId == documentId
                ? $"內容與這則 FAQ 的第 {sameContent.VersionNumber} 版完全相同，不需要再新增一個版本。"
                : $"內容與「{sameContent.DocumentName}」完全相同，不能當作這則 FAQ 的新版本。";
            return new KnowledgeUploadRejection(KnowledgeUploadRejectionReason.DuplicateContent, message, sameContent.DocumentName);
        }

        return nameTakenByAnother ? DuplicateName(name) : null;
    }

    private static KnowledgeUploadRejection DuplicateName(string name) =>
        new(
            KnowledgeUploadRejectionReason.DuplicateName,
            $"這個知識庫已經有「{name}」。同一個問題請直接編輯原本那一則，或改寫問題。");

    private static void Check(
        string value,
        int maxLength,
        string field,
        string requiredMessage,
        string tooLongMessage,
        List<ValidationFailure> failures)
    {
        if (value.Length == 0)
        {
            failures.Add(new ValidationFailure(field, requiredMessage));
        }
        else if (value.Length > maxLength)
        {
            failures.Add(new ValidationFailure(field, tooLongMessage));
        }
    }
}
