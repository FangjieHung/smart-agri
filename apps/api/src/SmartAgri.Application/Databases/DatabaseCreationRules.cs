using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>A valid create request: the template, and the trimmed name and purpose.</summary>
public sealed record DatabaseCreation(DatabaseTemplate Template, string Name, string Purpose);

/// <summary>
/// Validation of <c>POST /api/v1/databases</c>, with the mock's messages
/// (<c>createDatabaseFromTemplate</c>). Every broken rule is reported at once, each under its
/// field, so the dialog can mark them in one round trip; the first is the summary message.
/// </summary>
public static class DatabaseCreationRules
{
    public const string TemplateIdField = "templateId";

    public const string NameField = "name";

    public const string PurposeField = "purpose";

    public const string TemplateRequiredMessage = "請選擇一個模板。";

    public const string NameRequiredMessage = "請輸入資料庫名稱。";

    public static readonly string NameTooLongMessage = $"資料庫名稱請在 {Database.NameMaxLength} 個字以內。";

    public static readonly string PurposeTooLongMessage = $"用途說明最多 {Database.PurposeMaxLength} 個字。";

    /// <param name="templateId">The template's wire name (e.g. <c>template-satisfaction</c>); a
    /// plain string so an unknown one is this rule's <c>422</c>, not a model-binding failure.</param>
    /// <param name="purpose">Optional; missing or blank means the template's description.</param>
    public static ValidationResult<DatabaseCreation> Validate(string? templateId, string? name, string? purpose)
    {
        var failures = new List<ValidationFailure>();

        DatabaseTemplate? template = null;
        if (templateId is not null && WireNames<DatabaseTemplateId>.All.Contains(templateId))
        {
            template = DatabaseTemplates.Get(WireNames<DatabaseTemplateId>.Parse(templateId));
        }
        else
        {
            failures.Add(new ValidationFailure(TemplateIdField, TemplateRequiredMessage));
        }

        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length == 0)
        {
            failures.Add(new ValidationFailure(NameField, NameRequiredMessage));
        }
        else if (trimmedName.Length > Database.NameMaxLength)
        {
            failures.Add(new ValidationFailure(NameField, NameTooLongMessage));
        }

        var trimmedPurpose = (purpose ?? string.Empty).Trim();
        if (trimmedPurpose.Length > Database.PurposeMaxLength)
        {
            failures.Add(new ValidationFailure(PurposeField, PurposeTooLongMessage));
        }

        if (failures.Count > 0)
        {
            return ValidationResult<DatabaseCreation>.Invalid(failures);
        }

        return ValidationResult<DatabaseCreation>.Valid(new DatabaseCreation(
            template!,
            trimmedName,
            trimmedPurpose.Length > 0 ? trimmedPurpose : template!.Description));
    }
}
