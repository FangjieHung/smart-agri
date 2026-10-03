using System.Text.RegularExpressions;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Databases;

/// <summary>
/// One version of a database's form (表單版本). Versions are immutable: changing the form
/// (#143) adds version <c>n + 1</c> instead of editing a row, so a submission can always point
/// at the exact form it was filled in (#145) and old receipts keep their field names. The
/// database's current form is its highest <see cref="VersionNumber"/>; creating a database from
/// a template writes version 1.
/// </summary>
/// <remarks>
/// <see cref="Fields"/> is stored as <c>jsonb</c>: a version is always read and written whole,
/// never queried by field. The guards in <see cref="Create"/> are invariants (a caller bug if
/// violated); user-facing validation with per-field messages belongs to the Application layer
/// and always runs first (the form editor, #143).
/// </remarks>
public sealed partial class DatabaseFormVersion : IOrganizationScoped
{
    public const int MaxFields = 50;

    /// <summary>For EF Core materialization.</summary>
    private DatabaseFormVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid DatabaseId { get; private set; }

    /// <summary>1 for the template's initial form, then consecutive per database.</summary>
    public int VersionNumber { get; private set; }

    public IReadOnlyList<DatabaseFormField> Fields { get; private set; } = [];

    /// <summary>Who saved this version (the database's creator for version 1). An audit value:
    /// deliberately no foreign key, so the history outlives the account.</summary>
    public Guid CreatedByAccountId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>A new version of <paramref name="database"/>'s form.</summary>
    public static DatabaseFormVersion Create(
        Database database,
        int versionNumber,
        IReadOnlyList<DatabaseFormField> fields,
        Guid createdByAccountId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfLessThan(versionNumber, 1);
        if (createdByAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(createdByAccountId));
        }

        EnsureValid(fields);

        return new DatabaseFormVersion
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = database.OrganizationId,
            DatabaseId = database.Id,
            VersionNumber = versionNumber,
            Fields = [.. fields],
            CreatedByAccountId = createdByAccountId,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// The form invariants every stored version satisfies — the same rules as the frontend's
    /// <c>validateFields</c> (<c>database-tracking.ts</c>): at least one field; unique ids and
    /// labels; choice fields have at least two distinct options; a scale has an integer range
    /// of at most <see cref="DatabaseFormField.MaxScaleSteps"/> steps; nothing set that the
    /// type does not use.
    /// </summary>
    public static void EnsureValid(IReadOnlyList<DatabaseFormField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count is 0 or > MaxFields)
        {
            throw new ArgumentException($"A form has 1-{MaxFields} fields.", nameof(fields));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field, nameof(fields));
            if (field.Id is null || field.Id.Length > DatabaseFormField.IdMaxLength || !FieldIdPattern().IsMatch(field.Id) || !ids.Add(field.Id))
            {
                throw new ArgumentException($"Field id '{field.Id}' is malformed or repeated.", nameof(fields));
            }

            if (!Enum.IsDefined(field.Type))
            {
                throw new ArgumentException($"Field '{field.Id}' has an undeclared type.", nameof(fields));
            }

            if (field.Label is null || field.Label.Trim().Length == 0 || field.Label != field.Label.Trim()
                || field.Label.Length > DatabaseFormField.LabelMaxLength || !labels.Add(field.Label))
            {
                throw new ArgumentException($"Field '{field.Id}' needs a unique, trimmed label.", nameof(fields));
            }

            EnsureOptions(field);
            EnsureScale(field);

            if (field.Unit is null || field.Unit != field.Unit.Trim() || field.Unit.Length > DatabaseFormField.UnitMaxLength
                || (field.Type != DatabaseFieldType.Number && field.Unit.Length > 0))
            {
                throw new ArgumentException($"Field '{field.Id}' has a unit its type does not use.", nameof(fields));
            }
        }
    }

    private static void EnsureOptions(DatabaseFormField field)
    {
        if (field.Options is null)
        {
            throw new ArgumentException($"Field '{field.Id}' has no options list.", nameof(field));
        }

        if (!DatabaseFormField.IsChoice(field.Type))
        {
            if (field.Options.Count > 0)
            {
                throw new ArgumentException($"Field '{field.Id}' is not a choice but has options.", nameof(field));
            }

            return;
        }

        var distinct = new HashSet<string>(StringComparer.Ordinal);
        if (field.Options.Count is < 2 or > DatabaseFormField.MaxOptions
            || field.Options.Any(option => option is null || option.Trim().Length == 0 || option != option.Trim()
                || option.Length > DatabaseFormField.OptionMaxLength || !distinct.Add(option)))
        {
            throw new ArgumentException(
                $"Choice field '{field.Id}' needs 2-{DatabaseFormField.MaxOptions} distinct, trimmed options.", nameof(field));
        }
    }

    private static void EnsureScale(DatabaseFormField field)
    {
        if (field.Type != DatabaseFieldType.Scale)
        {
            if (field.Scale is not null)
            {
                throw new ArgumentException($"Field '{field.Id}' is not a scale but has a range.", nameof(field));
            }

            return;
        }

        var scale = field.Scale;
        if (scale is null || scale.Min >= scale.Max || scale.Max - scale.Min + 1 > DatabaseFormField.MaxScaleSteps
            || scale.MinLabel is null || scale.MaxLabel is null
            || scale.MinLabel.Length > DatabaseFormField.ScaleLabelMaxLength
            || scale.MaxLabel.Length > DatabaseFormField.ScaleLabelMaxLength)
        {
            throw new ArgumentException(
                $"Scale field '{field.Id}' needs Min < Max and at most {DatabaseFormField.MaxScaleSteps} steps.", nameof(field));
        }
    }

    [GeneratedRegex("^field-[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex FieldIdPattern();
}
