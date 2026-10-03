using System.Text.RegularExpressions;
using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>A scale's range as sent. Numbers, not integers, so a fractional bound is this
/// rule's <c>422</c> rather than a model-binding failure.</summary>
public sealed record DatabaseScaleDraft(double? Min, double? Max, string? MinLabel, string? MaxLabel);

/// <summary>
/// One field of a form as the editor sends it. Everything is nullable or a plain string on
/// purpose: an unknown <see cref="Type"/>, a blank label or a missing option list is a message
/// under <c>fields[i]…</c> (<see cref="DatabaseFormRules"/>), not a binding error. A missing
/// <see cref="Id"/> is a new field; the server gives it one.
/// </summary>
public sealed record DatabaseFieldDraft(
    string? Id,
    string? Label,
    string? Type,
    bool Required,
    IReadOnlyList<string?>? Options,
    DatabaseScaleDraft? Scale,
    string? Unit);

/// <summary>
/// Validation and normalization of a form the owner saves as the next version (#143), with the
/// frontend mock's messages (<c>validateFields</c> and <c>normalizeField</c> in
/// <c>database-tracking.ts</c>). It reports the first broken rule of each field, keyed
/// <c>fields[i].label</c>, <c>fields[i].options</c> and so on (form-level problems under
/// <c>fields</c>), so the editor can mark the field; the first failure is the summary message.
/// Everything <see cref="DatabaseFormVersion.EnsureValid"/> insists on is checked here first,
/// so a stored version never trips that guard.
/// </summary>
public static partial class DatabaseFormRules
{
    public const string FieldsKey = "fields";

    public const string NoFieldsMessage = "表單至少需要一個欄位。";

    public static readonly string TooManyFieldsMessage = $"表單最多 {DatabaseFormVersion.MaxFields} 個欄位。";

    public const string EmptyFieldMessage = "欄位內容不可為空。";

    public const string UnsupportedTypeMessage = "不支援的欄位類型。";

    public const string LabelRequiredMessage = "請填寫欄位名稱。";

    public static readonly string LabelTooLongMessage = $"欄位名稱請在 {DatabaseFormField.LabelMaxLength} 個字以內。";

    public const string LabelRepeatedMessage = "欄位名稱不可重複。";

    public const string OptionsCountMessage = "單選或多選至少需要 2 個選項。";

    public static readonly string OptionsTooManyMessage = $"單選或多選最多 {DatabaseFormField.MaxOptions} 個選項。";

    public static readonly string OptionTooLongMessage = $"每個選項請在 {DatabaseFormField.OptionMaxLength} 個字以內。";

    public const string OptionRepeatedMessage = "選項不可重複。";

    public const string ScaleRangeMessage = "量尺的最小值必須小於最大值。";

    public const string ScaleStepsMessage = "量尺最多 11 個刻度。";

    public static readonly string ScaleLabelTooLongMessage = $"量尺的說明文字請在 {DatabaseFormField.ScaleLabelMaxLength} 個字以內。";

    public static readonly string UnitTooLongMessage = $"單位請在 {DatabaseFormField.UnitMaxLength} 個字以內。";

    public const string IdMalformedMessage = "欄位編號格式不正確。";

    public const string IdRepeatedMessage = "欄位編號重複。";

    public const string IdRetiredMessage = "這個欄位編號屬於先前已移除的欄位，請改用新的欄位。";

    /// <summary>Key of a message about one field: <c>fields[2]</c> or <c>fields[2].label</c>.</summary>
    public static string KeyFor(int index, string? member = null) =>
        member is null ? $"{FieldsKey}[{index}]" : $"{FieldsKey}[{index}].{member}";

    /// <param name="drafts">The form as sent, in display order.</param>
    /// <param name="retiredFieldIds">Ids of fields that were in an earlier version but are not in
    /// the current one: a removed field's id is never reused for another question.</param>
    /// <returns>The normalized fields (trimmed labels, options and unit; settings the type does
    /// not use cleared; new fields given an id), ready for <see cref="DatabaseFormVersion.Create"/>.</returns>
    public static ValidationResult<IReadOnlyList<DatabaseFormField>> Validate(
        IReadOnlyList<DatabaseFieldDraft?>? drafts,
        IReadOnlySet<string> retiredFieldIds)
    {
        ArgumentNullException.ThrowIfNull(retiredFieldIds);
        if (drafts is null || drafts.Count == 0)
        {
            return ValidationResult<IReadOnlyList<DatabaseFormField>>.Invalid(FieldsKey, NoFieldsMessage);
        }

        if (drafts.Count > DatabaseFormVersion.MaxFields)
        {
            return ValidationResult<IReadOnlyList<DatabaseFormField>>.Invalid(FieldsKey, TooManyFieldsMessage);
        }

        var failures = new List<ValidationFailure>();
        var fields = new List<DatabaseFormField>(drafts.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);

        // Ids sent by the editor are reserved first so a generated id can never collide with one
        // that appears later in the list.
        foreach (var draft in drafts)
        {
            if (draft?.Id is { Length: > 0 } sent)
            {
                ids.Add(sent);
            }
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < drafts.Count; index++)
        {
            var failure = ValidateField(index, drafts[index], retiredFieldIds, seenIds, labels, ids, out var field);
            if (failure is not null)
            {
                failures.Add(failure);
            }
            else
            {
                fields.Add(field!);
            }
        }

        return failures.Count > 0
            ? ValidationResult<IReadOnlyList<DatabaseFormField>>.Invalid(failures)
            : ValidationResult<IReadOnlyList<DatabaseFormField>>.Valid(fields);
    }

    /// <summary>The same fields, setting for setting (a no-op save adds no version).</summary>
    public static bool AreSame(IReadOnlyList<DatabaseFormField> left, IReadOnlyList<DatabaseFormField> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.Label == pair.Second.Label
            && pair.First.Type == pair.Second.Type
            && pair.First.Required == pair.Second.Required
            && pair.First.Unit == pair.Second.Unit
            && pair.First.Scale == pair.Second.Scale
            && pair.First.Options.SequenceEqual(pair.Second.Options));
    }

    private static ValidationFailure? ValidateField(
        int index,
        DatabaseFieldDraft? draft,
        IReadOnlySet<string> retiredFieldIds,
        HashSet<string> seenIds,
        HashSet<string> labels,
        HashSet<string> reservedIds,
        out DatabaseFormField? field)
    {
        field = null;
        if (draft is null)
        {
            return new ValidationFailure(KeyFor(index), EmptyFieldMessage);
        }

        string id;
        if (string.IsNullOrEmpty(draft.Id))
        {
            id = NewFieldId(reservedIds);
        }
        else if (draft.Id.Length > DatabaseFormField.IdMaxLength || !FieldIdPattern().IsMatch(draft.Id))
        {
            return new ValidationFailure(KeyFor(index, "id"), IdMalformedMessage);
        }
        else if (!seenIds.Add(draft.Id))
        {
            return new ValidationFailure(KeyFor(index, "id"), IdRepeatedMessage);
        }
        else if (retiredFieldIds.Contains(draft.Id))
        {
            return new ValidationFailure(KeyFor(index, "id"), IdRetiredMessage);
        }
        else
        {
            id = draft.Id;
        }

        if (draft.Type is null || !WireNames<DatabaseFieldType>.All.Contains(draft.Type))
        {
            return new ValidationFailure(KeyFor(index, "type"), UnsupportedTypeMessage);
        }

        var type = WireNames<DatabaseFieldType>.Parse(draft.Type);
        var label = (draft.Label ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            return new ValidationFailure(KeyFor(index, "label"), LabelRequiredMessage);
        }

        if (label.Length > DatabaseFormField.LabelMaxLength)
        {
            return new ValidationFailure(KeyFor(index, "label"), LabelTooLongMessage);
        }

        if (!labels.Add(label))
        {
            return new ValidationFailure(KeyFor(index, "label"), LabelRepeatedMessage);
        }

        var options = new List<string>();
        if (DatabaseFormField.IsChoice(type))
        {
            options.AddRange((draft.Options ?? []).Select(option => (option ?? string.Empty).Trim()).Where(option => option.Length > 0));
            if (options.Count < 2)
            {
                return new ValidationFailure(KeyFor(index, "options"), OptionsCountMessage);
            }

            if (options.Count > DatabaseFormField.MaxOptions)
            {
                return new ValidationFailure(KeyFor(index, "options"), OptionsTooManyMessage);
            }

            if (options.Any(option => option.Length > DatabaseFormField.OptionMaxLength))
            {
                return new ValidationFailure(KeyFor(index, "options"), OptionTooLongMessage);
            }

            if (options.Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                return new ValidationFailure(KeyFor(index, "options"), OptionRepeatedMessage);
            }
        }

        DatabaseScaleRange? scale = null;
        if (type == DatabaseFieldType.Scale)
        {
            var failure = ValidateScale(index, draft.Scale, out scale);
            if (failure is not null)
            {
                return failure;
            }
        }

        var unit = type == DatabaseFieldType.Number ? (draft.Unit ?? string.Empty).Trim() : string.Empty;
        if (unit.Length > DatabaseFormField.UnitMaxLength)
        {
            return new ValidationFailure(KeyFor(index, "unit"), UnitTooLongMessage);
        }

        field = new DatabaseFormField(id, label, type, draft.Required, options, scale, unit);
        return null;
    }

    private static ValidationFailure? ValidateScale(int index, DatabaseScaleDraft? draft, out DatabaseScaleRange? scale)
    {
        scale = null;

        // A scale sent without a range gets the editor's default, as the mock does.
        draft ??= new DatabaseScaleDraft(1, 5, string.Empty, string.Empty);
        var key = KeyFor(index, "scale");
        if (draft.Min is not { } min || draft.Max is not { } max
            || !IsInteger(min) || !IsInteger(max) || min >= max)
        {
            return new ValidationFailure(key, ScaleRangeMessage);
        }

        if (max - min + 1 > DatabaseFormField.MaxScaleSteps)
        {
            return new ValidationFailure(key, ScaleStepsMessage);
        }

        var minLabel = (draft.MinLabel ?? string.Empty).Trim();
        var maxLabel = (draft.MaxLabel ?? string.Empty).Trim();
        if (minLabel.Length > DatabaseFormField.ScaleLabelMaxLength || maxLabel.Length > DatabaseFormField.ScaleLabelMaxLength)
        {
            return new ValidationFailure(key, ScaleLabelTooLongMessage);
        }

        scale = new DatabaseScaleRange((int)min, (int)max, minLabel, maxLabel);
        return null;
    }

    private static bool IsInteger(double value) =>
        double.IsFinite(value) && value == Math.Floor(value) && Math.Abs(value) <= 1_000_000;

    private static string NewFieldId(HashSet<string> reservedIds)
    {
        string id;
        do
        {
            id = $"field-{Guid.NewGuid():N}"[..18];
        }
        while (!reservedIds.Add(id));

        return id;
    }

    [GeneratedRegex("^field-[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex FieldIdPattern();
}
