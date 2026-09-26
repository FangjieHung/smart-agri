using SmartAgri.Application.Validation;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>A knowledge base's name and purpose, trimmed and within limits.</summary>
public sealed record KnowledgeBaseDetails(string Name, string Purpose);

/// <summary>
/// Validation of the name and purpose sent to <c>POST</c> and <c>PATCH
/// /api/v1/knowledge-bases</c>. The name is required; the purpose may be empty. Every
/// broken rule is reported at once, so a form can mark all of its fields in one round trip.
/// </summary>
public static class KnowledgeBaseDetailsRules
{
    public const string NameField = "name";

    public const string PurposeField = "purpose";

    public const string NameRequiredMessage = "請輸入知識庫名稱。";

    public static readonly string NameTooLongMessage = $"知識庫名稱最多 {KnowledgeBase.NameMaxLength} 個字。";

    public static readonly string PurposeTooLongMessage = $"用途說明最多 {KnowledgeBase.PurposeMaxLength} 個字。";

    /// <summary>For a new knowledge base: a missing purpose means an empty one.</summary>
    public static ValidationResult<KnowledgeBaseDetails> ForCreate(string? name, string? purpose) =>
        Validate(name ?? string.Empty, purpose ?? string.Empty);

    /// <summary>For a partial update: a <see langword="null"/> field keeps its
    /// <paramref name="current"/> value.</summary>
    public static ValidationResult<KnowledgeBaseDetails> ForUpdate(KnowledgeBaseDetails current, string? name, string? purpose)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Validate(name ?? current.Name, purpose ?? current.Purpose);
    }

    private static ValidationResult<KnowledgeBaseDetails> Validate(string name, string purpose)
    {
        var trimmedName = name.Trim();
        var trimmedPurpose = purpose.Trim();
        var failures = new List<ValidationFailure>();

        if (trimmedName.Length == 0)
        {
            failures.Add(new ValidationFailure(NameField, NameRequiredMessage));
        }
        else if (trimmedName.Length > KnowledgeBase.NameMaxLength)
        {
            failures.Add(new ValidationFailure(NameField, NameTooLongMessage));
        }

        if (trimmedPurpose.Length > KnowledgeBase.PurposeMaxLength)
        {
            failures.Add(new ValidationFailure(PurposeField, PurposeTooLongMessage));
        }

        return failures.Count == 0
            ? ValidationResult<KnowledgeBaseDetails>.Valid(new KnowledgeBaseDetails(trimmedName, trimmedPurpose))
            : ValidationResult<KnowledgeBaseDetails>.Invalid(failures);
    }
}
