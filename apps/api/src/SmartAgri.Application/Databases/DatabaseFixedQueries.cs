using System.Globalization;
using System.Text.Json.Serialization;
using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>
/// The fixed statistics queries over a database's records (M4 #147). The only way anything reads
/// counts, sums or comparisons: the trend tab now, the assistant's conversation tool (#149) and
/// the scheduled report (#150) later. A caller names a <see cref="DatabaseQueryKind"/> and supplies
/// <b>only</b> the parameters that kind defines; there is no expression, SQL or free-form filter.
/// </summary>
public enum DatabaseQueryKind
{
    /// <summary>How many active records fall in a period (and in the period before it).</summary>
    [JsonStringEnumMemberName("record-count")]
    RecordCount,

    /// <summary>The sum of one number field over a period (and the period before it).</summary>
    [JsonStringEnumMemberName("field-sum")]
    FieldSum,

    /// <summary>The record count and the sum of every number field over a period (and the period
    /// before it): what the trend tab shows.</summary>
    [JsonStringEnumMemberName("period-summary")]
    PeriodSummary,

    /// <summary>One subject's first, previous and current value of each number or scale field.</summary>
    [JsonStringEnumMemberName("subject-comparison")]
    SubjectComparison,
}

/// <summary>The named periods a query accepts instead of a custom <c>from</c>/<c>to</c>.</summary>
public enum DatabaseQueryPeriodName
{
    [JsonStringEnumMemberName("this-week")]
    ThisWeek,

    [JsonStringEnumMemberName("last-week")]
    LastWeek,

    [JsonStringEnumMemberName("this-month")]
    ThisMonth,

    [JsonStringEnumMemberName("last-month")]
    LastMonth,

    [JsonStringEnumMemberName("last-7-days")]
    Last7Days,

    [JsonStringEnumMemberName("last-30-days")]
    Last30Days,
}

/// <summary>A closed range of UTC calendar days, both ends included.</summary>
/// <param name="Name">The named period it came from; <see langword="null"/> for a custom range.</param>
public sealed record DatabaseQueryPeriod(DatabaseQueryPeriodName? Name, DateOnly From, DateOnly To)
{
    /// <summary>The first instant of <see cref="From"/> (UTC).</summary>
    public DateTimeOffset Start => new(From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>The first instant after <see cref="To"/> (UTC), exclusive.</summary>
    public DateTimeOffset EndExclusive => new(To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public int Days => To.DayNumber - From.DayNumber + 1;

    public string Label
    {
        get
        {
            var range = $"{Format(From)} 至 {Format(To)}";
            return Name is { } name ? $"{DatabaseFixedQueries.NameLabel(name)}（{range}）" : range;
        }
    }

    internal static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>What a valid request asks for: every value is a defined one, already checked against
/// the database's fields.</summary>
/// <param name="Period">Required for every kind except <see cref="DatabaseQueryKind.SubjectComparison"/>.</param>
/// <param name="FieldId">A number field (sum) or a number or scale field (comparison).</param>
/// <param name="SubjectId">The tracked subject (the submitting account). The caller still has to
/// confirm that the subject submitted to this database.</param>
public sealed record DatabaseQuerySpec(
    DatabaseQueryKind Kind,
    DatabaseQueryPeriod? Period,
    string? FieldId,
    Guid? SubjectId);

/// <summary>A field as the queries see it: its definition in the <b>latest</b> form version that
/// contains it. A field removed from the form keeps being queryable through its last definition.</summary>
public sealed record DatabaseFieldReference(DatabaseFormField Field, int VersionNumber, int Position);

/// <summary>One query the server offers: what it is called on the wire and which parameters it
/// takes. The list a model (#149) is given to choose from.</summary>
public sealed record DatabaseQueryDefinition(
    DatabaseQueryKind Kind,
    string Description,
    IReadOnlyList<string> RequiredParameters,
    IReadOnlyList<string> OptionalParameters)
{
    public string Name => WireNames<DatabaseQueryKind>.ToWire(Kind);

    public IEnumerable<string> AllowedParameters => RequiredParameters.Concat(OptionalParameters);
}

/// <summary>
/// The definitions and the parameter rules of the fixed queries. Pure: it needs no database, so it
/// is the same for every caller and tested without one. The caller supplies <c>today</c> and the
/// database's <see cref="FieldReferences"/>; permission and the subject's existence are checked by
/// the service that runs the query, <b>before</b> it reveals anything about fields or subjects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dates are UTC calendar days</b>, the same day the records' date labels show (the frontend
/// slices the ISO instant). A record belongs to the day its <c>SubmittedAt</c> falls on in UTC;
/// weeks run Monday to Sunday. A period always spans its whole range (<c>this-week</c> ends on
/// Sunday, <c>this-month</c> on the last day), and the <b>previous period</b> a result compares with
/// is the full period before it: last week, last month, or the same number of days right before a
/// rolling or custom range.
/// </para>
/// <para>
/// A custom range is two real <c>yyyy-MM-dd</c> dates, <c>from</c> not after <c>to</c>, at most
/// <see cref="MaxCustomDays"/> days, within <see cref="EarliestDate"/> and <see cref="LatestDate"/>.
/// </para>
/// </remarks>
public static class DatabaseFixedQueries
{
    public const string PeriodKey = "period";

    public const string FromKey = "from";

    public const string ToKey = "to";

    public const string FieldIdKey = "fieldId";

    public const string SubjectIdKey = "subjectId";

    public const int MaxCustomDays = 366;

    public static readonly DateOnly EarliestDate = new(2000, 1, 1);

    public static readonly DateOnly LatestDate = new(2100, 12, 31);

    public const string SubjectNotFoundMessage = "找不到這位追蹤對象。";

    public const string FieldNotFoundMessage = "找不到這個欄位。";

    public const string UnknownParameterMessage = "這項查詢不支援這個參數。";

    public static IReadOnlyList<DatabaseQueryDefinition> Definitions { get; } =
    [
        new(
            DatabaseQueryKind.RecordCount,
            "依期間計算有效紀錄筆數，並與前一期比較；可只算某一位追蹤對象。",
            [],
            [PeriodKey, FromKey, ToKey, SubjectIdKey]),
        new(
            DatabaseQueryKind.FieldSum,
            "依期間加總一個數字欄位，並與前一期比較；可只算某一位追蹤對象。",
            [FieldIdKey],
            [PeriodKey, FromKey, ToKey, SubjectIdKey]),
        new(
            DatabaseQueryKind.PeriodSummary,
            "依期間計算有效紀錄筆數與每個數字欄位的加總，並與前一期比較；可只算某一位追蹤對象。",
            [],
            [PeriodKey, FromKey, ToKey, SubjectIdKey]),
        new(
            DatabaseQueryKind.SubjectComparison,
            "一位追蹤對象的首次、上次與本次數值；可只比較一個數字或量尺欄位。",
            [SubjectIdKey],
            [FieldIdKey]),
    ];

    public static DatabaseQueryDefinition Definition(DatabaseQueryKind kind) =>
        Definitions.Single(definition => definition.Kind == kind);

    /// <summary>The field definitions the queries resolve ids against: for each id, its latest
    /// version that has it (history included, so an id from an older version is still known).</summary>
    public static IReadOnlyDictionary<string, DatabaseFieldReference> FieldReferences(
        IEnumerable<(int VersionNumber, IReadOnlyList<DatabaseFormField> Fields)> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var references = new Dictionary<string, DatabaseFieldReference>(StringComparer.Ordinal);
        foreach (var (versionNumber, fields) in versions.OrderByDescending(version => version.VersionNumber))
        {
            for (var position = 0; position < fields.Count; position++)
            {
                references.TryAdd(fields[position].Id, new DatabaseFieldReference(fields[position], versionNumber, position));
            }
        }

        return references;
    }

    /// <summary>
    /// Checks <paramref name="parameters"/> against the definition of <paramref name="kind"/>:
    /// every key must be one the kind defines, every required one must be present, and each value
    /// must be a defined one. Every failure is reported (field = the parameter's name).
    /// </summary>
    /// <param name="parameters">The caller's parameters by name; a key whose value is blank is an
    /// invalid value, not an absent one.</param>
    /// <param name="today">The current UTC day, for the named periods.</param>
    public static ValidationResult<DatabaseQuerySpec> Validate(
        DatabaseQueryKind kind,
        IReadOnlyDictionary<string, string?> parameters,
        DateOnly today,
        IReadOnlyDictionary<string, DatabaseFieldReference> fields)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(fields);
        var definition = Definition(kind);
        var failures = new List<ValidationFailure>();

        var allowed = definition.AllowedParameters.ToHashSet(StringComparer.Ordinal);
        foreach (var key in parameters.Keys.Order(StringComparer.Ordinal))
        {
            if (!allowed.Contains(key))
            {
                failures.Add(new ValidationFailure(key, UnknownParameterMessage));
            }
        }

        foreach (var key in definition.RequiredParameters.Where(key => !parameters.ContainsKey(key)))
        {
            failures.Add(new ValidationFailure(key, key == SubjectIdKey ? "請指定追蹤對象。" : "請指定欄位。"));
        }

        DatabaseQueryPeriod? period = null;
        if (definition.AllowedParameters.Contains(PeriodKey))
        {
            period = ReadPeriod(parameters, today, failures);
        }

        string? fieldId = null;
        if (parameters.TryGetValue(FieldIdKey, out var rawField) && allowed.Contains(FieldIdKey))
        {
            fieldId = ReadField(kind, rawField, fields, failures);
        }

        Guid? subjectId = null;
        if (parameters.TryGetValue(SubjectIdKey, out var rawSubject) && allowed.Contains(SubjectIdKey))
        {
            if (rawSubject is { Length: > 0 } && Guid.TryParseExact(rawSubject, "D", out var parsed) && parsed != Guid.Empty)
            {
                subjectId = parsed;
            }
            else
            {
                failures.Add(new ValidationFailure(SubjectIdKey, SubjectNotFoundMessage));
            }
        }

        return failures.Count > 0
            ? ValidationResult<DatabaseQuerySpec>.Invalid(failures)
            : ValidationResult<DatabaseQuerySpec>.Valid(new DatabaseQuerySpec(kind, period, fieldId, subjectId));
    }

    private static DatabaseQueryPeriod? ReadPeriod(
        IReadOnlyDictionary<string, string?> parameters, DateOnly today, List<ValidationFailure> failures)
    {
        var hasName = parameters.ContainsKey(PeriodKey);
        var hasFrom = parameters.ContainsKey(FromKey);
        var hasTo = parameters.ContainsKey(ToKey);

        if (!hasName && !hasFrom && !hasTo)
        {
            failures.Add(new ValidationFailure(PeriodKey, "請指定統計期間，或同時指定起訖日期。"));
            return null;
        }

        if (hasName && (hasFrom || hasTo))
        {
            failures.Add(new ValidationFailure(PeriodKey, "統計期間與起訖日期只能擇一指定。"));
            return null;
        }

        if (hasName)
        {
            var name = parameters[PeriodKey];
            if (name is not null && WireNames<DatabaseQueryPeriodName>.All.Contains(name))
            {
                return Resolve(WireNames<DatabaseQueryPeriodName>.Parse(name), today);
            }

            failures.Add(new ValidationFailure(PeriodKey, "不支援的統計期間。"));
            return null;
        }

        if (hasFrom != hasTo)
        {
            failures.Add(new ValidationFailure(hasFrom ? ToKey : FromKey, "起訖日期要同時指定。"));
            return null;
        }

        var before = failures.Count;
        var from = ReadDate(FromKey, parameters[FromKey], failures);
        var to = ReadDate(ToKey, parameters[ToKey], failures);
        if (failures.Count > before || from is null || to is null)
        {
            return null;
        }

        if (from > to)
        {
            failures.Add(new ValidationFailure(FromKey, "起始日期不能晚於結束日期。"));
            return null;
        }

        if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxCustomDays)
        {
            failures.Add(new ValidationFailure(ToKey, $"統計期間最長 {MaxCustomDays} 天。"));
            return null;
        }

        return new DatabaseQueryPeriod(null, from.Value, to.Value);
    }

    private static DateOnly? ReadDate(string key, string? text, List<ValidationFailure> failures)
    {
        if (text is { Length: 10 }
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && date >= EarliestDate
            && date <= LatestDate)
        {
            return date;
        }

        failures.Add(new ValidationFailure(key, "請輸入 yyyy-MM-dd 格式的日期。"));
        return null;
    }

    private static string? ReadField(
        DatabaseQueryKind kind,
        string? text,
        IReadOnlyDictionary<string, DatabaseFieldReference> fields,
        List<ValidationFailure> failures)
    {
        if (text is null || !fields.TryGetValue(text, out var reference))
        {
            failures.Add(new ValidationFailure(FieldIdKey, FieldNotFoundMessage));
            return null;
        }

        var type = reference.Field.Type;
        if (kind == DatabaseQueryKind.FieldSum && type != DatabaseFieldType.Number)
        {
            failures.Add(new ValidationFailure(FieldIdKey, "只有數字欄位可以加總。"));
            return null;
        }

        if (kind == DatabaseQueryKind.SubjectComparison && type is not (DatabaseFieldType.Number or DatabaseFieldType.Scale))
        {
            failures.Add(new ValidationFailure(FieldIdKey, "只有數字或量尺欄位可以比較。"));
            return null;
        }

        return text;
    }

    /// <summary>The range a named period covers on <paramref name="today"/>.</summary>
    public static DatabaseQueryPeriod Resolve(DatabaseQueryPeriodName name, DateOnly today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        return name switch
        {
            DatabaseQueryPeriodName.ThisWeek => new(name, monday, monday.AddDays(6)),
            DatabaseQueryPeriodName.LastWeek => new(name, monday.AddDays(-7), monday.AddDays(-1)),
            DatabaseQueryPeriodName.ThisMonth => new(name, firstOfMonth, firstOfMonth.AddMonths(1).AddDays(-1)),
            DatabaseQueryPeriodName.LastMonth => new(name, firstOfMonth.AddMonths(-1), firstOfMonth.AddDays(-1)),
            DatabaseQueryPeriodName.Last7Days => new(name, today.AddDays(-6), today),
            DatabaseQueryPeriodName.Last30Days => new(name, today.AddDays(-29), today),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a declared period."),
        };
    }

    /// <summary>The full period right before <paramref name="period"/>: the calendar month before
    /// a month, otherwise the same number of days ending the day before it starts.</summary>
    public static DatabaseQueryPeriod Previous(DatabaseQueryPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        if (period.Name is DatabaseQueryPeriodName.ThisMonth or DatabaseQueryPeriodName.LastMonth)
        {
            var from = period.From.AddMonths(-1);
            return new DatabaseQueryPeriod(null, from, period.From.AddDays(-1));
        }

        return new DatabaseQueryPeriod(null, period.From.AddDays(-period.Days), period.From.AddDays(-1));
    }

    internal static string NameLabel(DatabaseQueryPeriodName name) => name switch
    {
        DatabaseQueryPeriodName.ThisWeek => "本週",
        DatabaseQueryPeriodName.LastWeek => "上週",
        DatabaseQueryPeriodName.ThisMonth => "本月",
        DatabaseQueryPeriodName.LastMonth => "上月",
        DatabaseQueryPeriodName.Last7Days => "近 7 天",
        DatabaseQueryPeriodName.Last30Days => "近 30 天",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a declared period."),
    };
}
