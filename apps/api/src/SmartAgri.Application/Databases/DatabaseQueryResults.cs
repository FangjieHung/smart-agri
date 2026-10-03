using System.Text.Json.Serialization;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>A queried period as the wire shows it: calendar days of the statistics time zone, <c>yyyy-MM-dd</c>, both ends
/// included. <see cref="Period"/> is the named period's wire name, or <see langword="null"/> for a
/// custom range (and for the "previous period" a result compares with).</summary>
public sealed record DatabaseQueryPeriodView(string? Period, string From, string To, string Label)
{
    public static DatabaseQueryPeriodView Of(DatabaseQueryPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return new(
            period.Name is { } name ? SmartAgri.Domain.WireNames<DatabaseQueryPeriodName>.ToWire(name) : null,
            DatabaseQueryPeriod.Format(period.From),
            DatabaseQueryPeriod.Format(period.To),
            period.Label);
    }
}

/// <summary>One number field's total over the period and over the one before it. <see cref="Display"/>
/// and <see cref="ChangeLabel"/> are formatted like a receipt's numbers (thousands separators, the
/// unit after a space); <see cref="RecordCount"/> is how many active records had a value for the
/// field in the period.</summary>
public sealed record DatabaseFieldSum(
    string FieldId,
    string Label,
    string Unit,
    double Sum,
    string Display,
    int RecordCount,
    double PreviousSum,
    string PreviousDisplay,
    int PreviousRecordCount,
    double Change,
    string ChangeLabel);

/// <summary><c>record-count</c>: active records in the period and in the period before it.</summary>
public sealed record DatabaseRecordCountResult(
    DatabaseQueryPeriodView Period,
    DatabaseQueryPeriodView PreviousPeriod,
    Guid? SubjectId,
    int Count,
    int PreviousCount,
    int Change,
    string ChangeLabel);

/// <summary><c>field-sum</c>: the sum of one number field. <see cref="Field"/>.RecordCount is 0 when
/// the period holds no value for it; the sum is then 0, not an error.</summary>
public sealed record DatabaseFieldSumResult(
    DatabaseQueryPeriodView Period,
    DatabaseQueryPeriodView PreviousPeriod,
    Guid? SubjectId,
    DatabaseFieldSum Field);

/// <summary><c>period-summary</c>: the record count and the sums of every number field.</summary>
public sealed record DatabasePeriodSummaryResult(
    DatabaseQueryPeriodView Period,
    DatabaseQueryPeriodView PreviousPeriod,
    Guid? SubjectId,
    int RecordCount,
    int PreviousRecordCount,
    int RecordCountChange,
    string RecordCountChangeLabel,
    IReadOnlyList<DatabaseFieldSum> Sums);

[JsonConverter(typeof(JsonStringEnumConverter<DatabaseTrendDirection>))]
public enum DatabaseTrendDirection
{
    [JsonStringEnumMemberName("up")]
    Up,

    [JsonStringEnumMemberName("down")]
    Down,

    [JsonStringEnumMemberName("flat")]
    Flat,
}

/// <summary>One value on a trend: which record, its UTC date and the value (with its receipt text).</summary>
public sealed record DatabaseComparisonPoint(Guid RecordId, string Date, double Value, string Display);

/// <summary>The vertical range a chart of a metric should span.</summary>
public sealed record DatabaseComparisonAxis(double Min, double Max);

/// <summary>One number or scale field of a subject across its records: first, previous and current
/// value, the changes (numbers and their labels, <c>持平</c> for none) and all the points in time
/// order for the chart.</summary>
public sealed record DatabaseMetricComparison(
    string FieldId,
    string Label,
    string Unit,
    DatabaseComparisonPoint First,
    DatabaseComparisonPoint Previous,
    DatabaseComparisonPoint Current,
    double ChangeFromPrevious,
    double ChangeFromFirst,
    string ChangeFromPreviousLabel,
    string ChangeFromFirstLabel,
    DatabaseTrendDirection Direction,
    IReadOnlyList<DatabaseComparisonPoint> Points,
    DatabaseComparisonAxis Axis,
    string Summary);

[JsonConverter(typeof(JsonStringEnumConverter<DatabaseComparisonStatus>))]
public enum DatabaseComparisonStatus
{
    /// <summary>Not enough records to compare: no trend is drawn.</summary>
    [JsonStringEnumMemberName("insufficient-records")]
    InsufficientRecords,

    [JsonStringEnumMemberName("available")]
    Available,
}

/// <summary>
/// A subject's comparison. <see cref="DatabaseComparisonStatus.InsufficientRecords"/> (fewer than two
/// active records, or no number or scale field with two values) carries a <see cref="Message"/> and
/// no metrics; <see cref="DatabaseComparisonStatus.Available"/> a <see cref="Summary"/> and metrics.
/// The member that does not apply is <see langword="null"/>.
/// </summary>
public sealed record DatabaseSubjectComparison(
    DatabaseComparisonStatus Status,
    int RecordCount,
    string? Message,
    string? Summary,
    IReadOnlyList<DatabaseMetricComparison> Metrics);

/// <summary><c>subject-comparison</c>.</summary>
public sealed record DatabaseSubjectComparisonResult(Guid SubjectId, DatabaseSubjectComparison Comparison);

/// <summary>One number or scale value of an active record, as stored.</summary>
public sealed record DatabaseQueryEntry(
    string FieldId, string Label, DatabaseFieldType Type, string Unit, string Display, double Value);

/// <summary>An active record with its number and scale values, in form order.</summary>
public sealed record DatabaseQueryRecord(Guid Id, DateTimeOffset SubmittedAt, IReadOnlyList<DatabaseQueryEntry> Numbers);

/// <summary>One group of the stored number entries of a period: the sum and count of a field's
/// values under one unit.</summary>
public sealed record DatabaseSumRow(string FieldId, string Unit, double Sum, int Count);

/// <summary>
/// The arithmetic of the fixed queries, over rows the service has already read (active records the
/// caller may read, nothing else). Pure and database-free; the numbers, units and texts equal the
/// frontend mock's <c>compareRecords</c> and <c>formatSigned</c> (<c>database-tracking.ts</c>).
/// </summary>
public static class DatabaseQueryResults
{
    public const string ScaleUnit = "分";

    public const string NoChangeLabel = "持平";

    public const string RecordUnit = "筆";

    /// <summary>
    /// First, previous and current value of each number or scale field of one subject.
    /// </summary>
    /// <param name="chronological">The subject's active records, oldest first.</param>
    /// <param name="fields">Resolves a field id to its definition (<see cref="DatabaseFixedQueries.FieldReferences"/>);
    /// a scale field's chart axis starts from its range.</param>
    /// <param name="timeZone">The statistics time zone: the dates of the points and of the summary
    /// are its calendar days.</param>
    /// <param name="onlyFieldId">When given, only this field is compared.</param>
    /// <remarks>
    /// Fields are matched by their stable id across form versions. The metrics are those of the
    /// <b>latest</b> record (a field removed from the form since is not compared) and a metric's
    /// points are the records that hold a value for the id <b>with the same type and unit as the
    /// latest one</b>, so a field whose unit was changed is never mixed. A metric needs two points.
    /// </remarks>
    public static DatabaseSubjectComparison Compare(
        IReadOnlyList<DatabaseQueryRecord> chronological,
        IReadOnlyDictionary<string, DatabaseFieldReference> fields,
        TimeZoneInfo timeZone,
        string? onlyFieldId = null)
    {
        ArgumentNullException.ThrowIfNull(chronological);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(timeZone);
        var recordCount = chronological.Count;
        if (recordCount < 2)
        {
            return Insufficient(recordCount, $"目前只有 {recordCount} 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。");
        }

        var latest = chronological[^1];
        var metrics = new List<DatabaseMetricComparison>();
        foreach (var anchor in latest.Numbers.Where(entry => onlyFieldId is null || entry.FieldId == onlyFieldId))
        {
            var points = new List<(DatabaseQueryRecord Record, DatabaseQueryEntry Entry)>();
            foreach (var record in chronological)
            {
                var match = record.Numbers.FirstOrDefault(entry =>
                    entry.FieldId == anchor.FieldId && entry.Type == anchor.Type && entry.Unit == anchor.Unit);
                if (match is not null)
                {
                    points.Add((record, match));
                }
            }

            if (points.Count >= 2)
            {
                metrics.Add(Metric(anchor, points, fields, timeZone));
            }
        }

        if (metrics.Count == 0)
        {
            return Insufficient(
                recordCount, $"目前有 {recordCount} 筆紀錄，但沒有任何數字或量尺欄位累積 2 筆以上的數值，無法比較。");
        }

        return new DatabaseSubjectComparison(
            DatabaseComparisonStatus.Available,
            recordCount,
            null,
            $"比較 {recordCount} 筆已同意提交的紀錄（{DatabaseQueryPeriod.Format(DatabaseFixedQueries.DayOf(chronological[0].SubmittedAt, timeZone))} 至 {DatabaseQueryPeriod.Format(DatabaseFixedQueries.DayOf(latest.SubmittedAt, timeZone))}）。",
            metrics);
    }

    /// <summary>The comparison of a subject with too little data.</summary>
    public static DatabaseSubjectComparison Insufficient(int recordCount, string message) =>
        new(DatabaseComparisonStatus.InsufficientRecords, recordCount, message, null, []);

    /// <summary>
    /// The sums of every number field (those of the current form first, then fields removed from
    /// it that still have values) over a period, next to the period before it.
    /// </summary>
    /// <param name="current">The period's <see cref="DatabaseSumRow"/>s.</param>
    /// <param name="previous">The previous period's.</param>
    /// <param name="fields">Field definitions; only <b>number</b> fields are summed, and a row counts
    /// only when its unit is the field's latest unit (values stored under an earlier unit are not
    /// added to a total in the new one).</param>
    /// <param name="currentFormVersion">The database's current form version number.</param>
    /// <param name="onlyFieldId">When given, only this field.</param>
    public static IReadOnlyList<DatabaseFieldSum> Sums(
        IReadOnlyList<DatabaseSumRow> current,
        IReadOnlyList<DatabaseSumRow> previous,
        IReadOnlyDictionary<string, DatabaseFieldReference> fields,
        int currentFormVersion,
        string? onlyFieldId = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(fields);
        var lines = new List<DatabaseFieldSum>();
        var ordered = fields.Values
            .Where(reference => reference.Field.Type == DatabaseFieldType.Number)
            .Where(reference => onlyFieldId is null || reference.Field.Id == onlyFieldId)
            .OrderByDescending(reference => reference.VersionNumber)
            .ThenBy(reference => reference.Position);
        foreach (var reference in ordered)
        {
            var field = reference.Field;
            var now = Total(current, field);
            var before = Total(previous, field);
            var inCurrentForm = reference.VersionNumber == currentFormVersion;
            if (!inCurrentForm && now.Count == 0 && before.Count == 0 && onlyFieldId is null)
            {
                continue;
            }

            var change = now.Sum - before.Sum;
            lines.Add(new DatabaseFieldSum(
                field.Id,
                field.Label,
                field.Unit,
                now.Sum,
                DatabaseAnswerRules.FormatNumber(now.Sum, field.Unit),
                now.Count,
                before.Sum,
                DatabaseAnswerRules.FormatNumber(before.Sum, field.Unit),
                before.Count,
                change,
                FormatSigned(change, field.Unit)));
        }

        return lines;
    }

    /// <summary>A count change as the trend tab shows it: <c>+3 筆</c>, <c>-2 筆</c> or <c>持平</c>.</summary>
    public static string CountChangeLabel(int change) => FormatSigned(change, RecordUnit);

    /// <summary><c>持平</c> for zero, otherwise a sign and the absolute value formatted with the unit
    /// (the mock's <c>formatSigned</c>).</summary>
    public static string FormatSigned(double value, string unit)
    {
        if (value == 0)
        {
            return NoChangeLabel;
        }

        var sign = value > 0 ? "+" : "-";
        return $"{sign}{DatabaseAnswerRules.FormatNumber(Math.Abs(value), unit)}";
    }

    private static (double Sum, int Count) Total(IReadOnlyList<DatabaseSumRow> rows, DatabaseFormField field)
    {
        var matching = rows.Where(row => row.FieldId == field.Id && row.Unit == field.Unit).ToList();
        return (matching.Sum(row => row.Sum), matching.Sum(row => row.Count));
    }

    private static DatabaseMetricComparison Metric(
        DatabaseQueryEntry anchor,
        List<(DatabaseQueryRecord Record, DatabaseQueryEntry Entry)> points,
        IReadOnlyDictionary<string, DatabaseFieldReference> fields,
        TimeZoneInfo timeZone)
    {
        DatabaseComparisonPoint Point((DatabaseQueryRecord Record, DatabaseQueryEntry Entry) point) => new(
            point.Record.Id,
            DatabaseQueryPeriod.Format(DatabaseFixedQueries.DayOf(point.Record.SubmittedAt, timeZone)),
            point.Entry.Value,
            point.Entry.Display);

        var views = points.Select(Point).ToList();
        var first = views[0];
        var previous = views[^2];
        var current = views[^1];
        var unit = anchor.Type == DatabaseFieldType.Scale ? ScaleUnit : anchor.Unit;
        var changeFromPrevious = current.Value - previous.Value;
        var changeFromFirst = current.Value - first.Value;
        var previousLabel = FormatSigned(changeFromPrevious, unit);
        var firstLabel = FormatSigned(changeFromFirst, unit);
        var values = views.Select(point => point.Value).ToList();

        var min = values.Min();
        var max = values.Max();
        if (anchor.Type == DatabaseFieldType.Scale
            && fields.TryGetValue(anchor.FieldId, out var reference)
            && reference.Field is { Type: DatabaseFieldType.Scale, Scale: { } range })
        {
            min = Math.Min(min, range.Min);
            max = Math.Max(max, range.Max);
        }

        return new DatabaseMetricComparison(
            anchor.FieldId,
            anchor.Label,
            unit,
            first,
            previous,
            current,
            changeFromPrevious,
            changeFromFirst,
            previousLabel,
            firstLabel,
            changeFromPrevious > 0 ? DatabaseTrendDirection.Up : changeFromPrevious < 0 ? DatabaseTrendDirection.Down : DatabaseTrendDirection.Flat,
            views,
            new DatabaseComparisonAxis(min, max),
            $"{anchor.Label}：本次 {current.Display}，較上次 {previousLabel}，較首次 {firstLabel}。");
    }
}
