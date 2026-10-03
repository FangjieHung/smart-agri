using System.Globalization;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>An answer as received: a single string, or a list of strings (multiple choice).
/// Anything else on the wire is turned into an empty answer by the caller.</summary>
public sealed record DatabaseAnswerInput(string? Text, IReadOnlyList<string>? Choices)
{
    public static DatabaseAnswerInput FromText(string? text) => new(text, null);

    public static DatabaseAnswerInput FromChoices(IReadOnlyList<string> choices) => new(null, choices);
}

/// <summary>
/// One field's validated answer. <see cref="Display"/> is the text shown in the preview and
/// the receipt (<c>未填寫</c> when left empty); the typed members are what a stored submission
/// keeps: <see cref="Text"/> for text, date and single choice, <see cref="Number"/> for number
/// and scale, <see cref="Choices"/> (in the form's option order) for multiple choice.
/// </summary>
public sealed record DatabaseAnswerEntry(
    DatabaseFormField Field,
    string Display,
    string? Text,
    double? Number,
    IReadOnlyList<string> Choices)
{
    public bool IsEmpty => Text is null && Number is null && Choices.Count == 0;
}

/// <summary>
/// The one server-side rule for answers against a form: the trial fill (#143, which writes
/// nothing) and the real submission (#145) both call <see cref="Validate"/>, so a trial that
/// passes is a submission that passes. Messages and the display format are the frontend
/// mock's (<c>evaluateTrial</c> in <c>database-tracking.ts</c>). Every broken field is
/// reported, keyed <c>answers.&lt;field id&gt;</c>.
/// </summary>
public static class DatabaseAnswerRules
{
    public const string AnswersKey = "answers";

    public const string NotFilledDisplay = "未填寫";

    public static string KeyFor(string fieldId) => $"{AnswersKey}.{fieldId}";

    /// <param name="fields">The form's fields (a stored version's).</param>
    /// <param name="answers">By field id; ids that are not fields of the form are ignored.</param>
    public static ValidationResult<IReadOnlyList<DatabaseAnswerEntry>> Validate(
        IReadOnlyList<DatabaseFormField> fields,
        IReadOnlyDictionary<string, DatabaseAnswerInput> answers)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(answers);

        var failures = new List<ValidationFailure>();
        var entries = new List<DatabaseAnswerEntry>(fields.Count);
        foreach (var field in fields)
        {
            answers.TryGetValue(field.Id, out var answer);
            var failure = ValidateOne(field, answer, out var entry);
            if (failure is not null)
            {
                failures.Add(new ValidationFailure(KeyFor(field.Id), $"「{field.Label}」{failure}"));
            }
            else
            {
                entries.Add(entry!);
            }
        }

        return failures.Count > 0
            ? ValidationResult<IReadOnlyList<DatabaseAnswerEntry>>.Invalid(failures)
            : ValidationResult<IReadOnlyList<DatabaseAnswerEntry>>.Valid(entries);
    }

    /// <summary>The message after the quoted label, or <see langword="null"/> when valid.</summary>
    private static string? ValidateOne(DatabaseFormField field, DatabaseAnswerInput? answer, out DatabaseAnswerEntry? entry)
    {
        entry = null;
        var multiple = field.Type == DatabaseFieldType.MultipleChoice;
        var text = multiple ? string.Empty : (answer?.Text ?? string.Empty).Trim();
        var chosen = multiple ? (answer?.Choices ?? []).Where(item => item is not null).ToList() : [];
        var empty = multiple ? chosen.Count == 0 : text.Length == 0;

        if (empty)
        {
            if (field.Required)
            {
                return "為必填。";
            }

            entry = new DatabaseAnswerEntry(field, NotFilledDisplay, null, null, []);
            return null;
        }

        switch (field.Type)
        {
            case DatabaseFieldType.Number:
                if (!TryParseNumber(text, out var number))
                {
                    return "請輸入數字。";
                }

                entry = new DatabaseAnswerEntry(field, FormatNumber(number, field.Unit), null, number, []);
                return null;

            case DatabaseFieldType.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    return "請輸入日期。";
                }

                entry = new DatabaseAnswerEntry(field, text, text, null, []);
                return null;

            case DatabaseFieldType.SingleChoice:
                if (!field.Options.Contains(text, StringComparer.Ordinal))
                {
                    return "請從選項中選擇。";
                }

                entry = new DatabaseAnswerEntry(field, text, text, null, []);
                return null;

            case DatabaseFieldType.MultipleChoice:
                if (!chosen.All(item => field.Options.Contains(item, StringComparer.Ordinal)))
                {
                    return "請從選項中選擇。";
                }

                var inOrder = field.Options.Where(option => chosen.Contains(option, StringComparer.Ordinal)).ToList();
                entry = new DatabaseAnswerEntry(field, string.Join('、', inOrder), null, null, inOrder);
                return null;

            case DatabaseFieldType.Scale:
                var scale = field.Scale;
                if (scale is null || !TryParseNumber(text, out var score) || score != Math.Floor(score)
                    || score < scale.Min || score > scale.Max)
                {
                    return $"請選擇 {scale?.Min ?? 1} 到 {scale?.Max ?? 5} 之間的分數。";
                }

                entry = new DatabaseAnswerEntry(field, $"{(int)score} / {scale.Max}", null, score, []);
                return null;

            default:
                entry = new DatabaseAnswerEntry(field, text, text, null, []);
                return null;
        }
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Grouped thousands and at most three decimals, like the mock's
    /// <c>Intl.NumberFormat('en-US')</c>, then the unit after a space.</summary>
    public static string FormatNumber(double value, string unit)
    {
        var formatted = value.ToString("#,##0.###", CultureInfo.InvariantCulture);
        return unit.Length > 0 ? $"{formatted} {unit}" : formatted;
    }
}
