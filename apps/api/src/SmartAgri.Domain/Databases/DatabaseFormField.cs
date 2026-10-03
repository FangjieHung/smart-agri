namespace SmartAgri.Domain.Databases;

/// <summary>A scale field's range: integers, <see cref="Min"/> &lt; <see cref="Max"/>, at most
/// <see cref="DatabaseFormField.MaxScaleSteps"/> steps.</summary>
public sealed record DatabaseScaleRange(int Min, int Max, string MinLabel, string MaxLabel);

/// <summary>
/// One field of a <see cref="DatabaseFormVersion"/>, in the order the form shows it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Id"/> is the field's stable key (<c>field-…</c>): it stays the same across form
/// versions while its <see cref="Label"/>, options or position change, so later tickets can
/// compare one field's values across versions (#147) while each submission keeps the label it
/// was submitted under (#145 snapshots the field with the values). A removed field's id is
/// never reused for a different question.
/// </para>
/// <para>
/// Options only for the choice types, <see cref="Scale"/> only for <see cref="DatabaseFieldType.Scale"/>,
/// <see cref="Unit"/> only for <see cref="DatabaseFieldType.Number"/> — enforced by
/// <see cref="DatabaseFormVersion.Create"/>.
/// </para>
/// </remarks>
public sealed record DatabaseFormField(
    string Id,
    string Label,
    DatabaseFieldType Type,
    bool Required,
    IReadOnlyList<string> Options,
    DatabaseScaleRange? Scale,
    string Unit)
{
    public const int IdMaxLength = 64;

    public const int LabelMaxLength = 100;

    public const int OptionMaxLength = 100;

    public const int MaxOptions = 30;

    public const int UnitMaxLength = 20;

    public const int ScaleLabelMaxLength = 20;

    /// <summary>A scale has at most 11 steps (e.g. 0–10), as the mock's form designer allows.</summary>
    public const int MaxScaleSteps = 11;

    public static bool IsChoice(DatabaseFieldType type) =>
        type is DatabaseFieldType.SingleChoice or DatabaseFieldType.MultipleChoice;
}
