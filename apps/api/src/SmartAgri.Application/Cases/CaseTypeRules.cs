using SmartAgri.Application.Validation;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>The fields of a case type that passed <see cref="CaseTypeRules.Validate"/>: trimmed name
/// and description, the chosen group's id and the handling time in hours.</summary>
public sealed record CaseTypeFields(string Name, string Description, Guid DefaultGroupId, int DefaultDueHours, bool IsActive);

/// <summary>
/// The rules of <c>/api/v1/case-types</c> that need no database (M7 plan §3 B; issue #247): name,
/// description, the default group being chosen at all, and the handling time. Whether the name is
/// taken and whether the group exists or is archived are the endpoint's (they need the
/// organization's other rows).
/// </summary>
public static class CaseTypeRules
{
    public const string NameField = "name";

    public const string DescriptionField = "description";

    public const string DefaultGroupField = "defaultGroupId";

    public const string DefaultDueHoursField = "defaultDueHours";

    public const string NameRequiredMessage = "請輸入案件類型名稱。";

    public static readonly string NameTooLongMessage = $"案件類型名稱請在 {CaseType.NameMaxLength} 個字以內。";

    public static readonly string DescriptionTooLongMessage = $"說明請在 {CaseType.DescriptionMaxLength} 個字以內。";

    public const string DefaultGroupRequiredMessage = "請選擇預設承辦組。";

    public const string DefaultDueHoursMessage = "預設處理時限請在 1 到 2,160 小時（90 天）之間。";

    /// <summary>
    /// Every failure at once (a form shows each under its field), or the normalized fields. A missing
    /// <c>isActive</c> means active: a new type is usually meant to be used.
    /// </summary>
    public static ValidationResult<CaseTypeFields> Validate(
        string? name, string? description, Guid? defaultGroupId, int? defaultDueHours, bool? isActive)
    {
        var failures = new List<ValidationFailure>();

        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length == 0)
        {
            failures.Add(new ValidationFailure(NameField, NameRequiredMessage));
        }
        else if (trimmedName.Length > CaseType.NameMaxLength)
        {
            failures.Add(new ValidationFailure(NameField, NameTooLongMessage));
        }

        var trimmedDescription = (description ?? string.Empty).Trim();
        if (trimmedDescription.Length > CaseType.DescriptionMaxLength)
        {
            failures.Add(new ValidationFailure(DescriptionField, DescriptionTooLongMessage));
        }

        if (defaultGroupId is not { } groupId || groupId == Guid.Empty)
        {
            failures.Add(new ValidationFailure(DefaultGroupField, DefaultGroupRequiredMessage));
        }

        if (defaultDueHours is not (>= CaseType.MinDueHours and <= CaseType.MaxDueHours))
        {
            failures.Add(new ValidationFailure(DefaultDueHoursField, DefaultDueHoursMessage));
        }

        return failures.Count > 0
            ? ValidationResult<CaseTypeFields>.Invalid(failures)
            : ValidationResult<CaseTypeFields>.Valid(
                new CaseTypeFields(trimmedName, trimmedDescription, defaultGroupId!.Value, defaultDueHours!.Value, isActive ?? true));
    }
}
