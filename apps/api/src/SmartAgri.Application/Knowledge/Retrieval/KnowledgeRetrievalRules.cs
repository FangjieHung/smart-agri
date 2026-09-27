using System.Text;
using SmartAgri.Application.Validation;

namespace SmartAgri.Application.Knowledge.Retrieval;

/// <summary>A retrieval preview request that passed <see cref="KnowledgeRetrievalRules.ValidatePreview"/>.</summary>
/// <param name="Question">Trimmed, 1–<see cref="KnowledgeRetrievalRules.QuestionMaxLength"/> characters.</param>
/// <param name="Top">1–<see cref="KnowledgeRetrievalSettings.MaxTop"/>, or <see langword="null"/>
/// for the deployment's default.</param>
public sealed record KnowledgeRetrievalPreviewRequest(string Question, int? Top);

/// <summary>
/// The rules of <c>POST /api/v1/knowledge-bases/{id}/retrieval-preview</c> (M2 plan Slice 9;
/// ticket #43): what may be asked, and how much of each passage is shown.
/// </summary>
public static class KnowledgeRetrievalRules
{
    public const string QuestionField = "question";

    public const string TopField = "top";

    /// <summary>The longest question, in characters (after trimming), as the plan sets it.</summary>
    public const int QuestionMaxLength = 500;

    /// <summary>
    /// The longest excerpt, in Unicode scalars (one Chinese character is one, like the
    /// chunker's limits): about half of a typical 600-character chunk, which covers its
    /// 100-character overlap with the previous chunk and a good part of what is new in it. A
    /// longer text is cut there and ends with <see cref="Ellipsis"/>.
    /// </summary>
    public const int ExcerptMaxLength = 300;

    public const string Ellipsis = "…";

    public const string QuestionRequiredMessage = "請輸入要試查的問題。";

    public static readonly string QuestionTooLongMessage = $"問題最多 {QuestionMaxLength} 個字。";

    public static readonly string TopOutOfRangeMessage = $"筆數必須介於 1 到 {KnowledgeRetrievalSettings.MaxTop} 之間。";

    /// <summary>
    /// A non-blank question of at most <see cref="QuestionMaxLength"/> characters (trimmed),
    /// and a <c>top</c> that is absent or 1–<see cref="KnowledgeRetrievalSettings.MaxTop"/>.
    /// Every broken rule is reported at once.
    /// </summary>
    public static ValidationResult<KnowledgeRetrievalPreviewRequest> ValidatePreview(string? question, int? top)
    {
        var trimmed = (question ?? string.Empty).Trim();
        var failures = new List<ValidationFailure>();
        if (trimmed.Length == 0)
        {
            failures.Add(new ValidationFailure(QuestionField, QuestionRequiredMessage));
        }
        else if (trimmed.Length > QuestionMaxLength)
        {
            failures.Add(new ValidationFailure(QuestionField, QuestionTooLongMessage));
        }

        if (top is < 1 or > KnowledgeRetrievalSettings.MaxTop)
        {
            failures.Add(new ValidationFailure(TopField, TopOutOfRangeMessage));
        }

        return failures.Count == 0
            ? ValidationResult<KnowledgeRetrievalPreviewRequest>.Valid(new KnowledgeRetrievalPreviewRequest(trimmed, top))
            : ValidationResult<KnowledgeRetrievalPreviewRequest>.Invalid(failures);
    }

    /// <summary><paramref name="text"/> as the preview shows it: whole when it has at most
    /// <see cref="ExcerptMaxLength"/> Unicode scalars, otherwise its first
    /// <see cref="ExcerptMaxLength"/> (never half a surrogate pair), without trailing white
    /// space, followed by <see cref="Ellipsis"/>.</summary>
    public static string Excerpt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == ExcerptMaxLength)
            {
                return builder.ToString().TrimEnd() + Ellipsis;
            }

            builder.Append(rune.ToString());
            count++;
        }

        return text;
    }
}
