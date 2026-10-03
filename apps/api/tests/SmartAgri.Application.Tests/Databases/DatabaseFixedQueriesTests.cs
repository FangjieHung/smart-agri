using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

/// <summary>
/// The parameter rules and the period arithmetic of the fixed statistics queries (M4 #147): only
/// defined parameters and values are accepted, periods are UTC days with Monday-based weeks, and
/// the previous period is always the full period before.
/// </summary>
public class DatabaseFixedQueriesTests
{
    // 2026-10-03 is a Saturday.
    private static readonly DateOnly Today = new(2026, 10, 3);

    private static readonly IReadOnlyDictionary<string, DatabaseFieldReference> Fields = DatabaseFixedQueries.FieldReferences(
    [
        (1, [Field("field-weight", DatabaseFieldType.Number, "公斤"), Field("field-note", DatabaseFieldType.Text), Field("field-old", DatabaseFieldType.Number, "件")]),
        (2, [Field("field-weight", DatabaseFieldType.Number, "kg"), Field("field-score", DatabaseFieldType.Scale), Field("field-note", DatabaseFieldType.Text)]),
    ]);

    private static DatabaseFormField Field(string id, DatabaseFieldType type, string unit = "") => new(
        id,
        id,
        type,
        false,
        [],
        type == DatabaseFieldType.Scale ? new DatabaseScaleRange(1, 5, "低", "高") : null,
        unit);

    private static IReadOnlyDictionary<string, string?> Parameters(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    [Theory]
    [InlineData(DatabaseQueryPeriodName.ThisWeek, "2026-09-28", "2026-10-04")]
    [InlineData(DatabaseQueryPeriodName.LastWeek, "2026-09-21", "2026-09-27")]
    [InlineData(DatabaseQueryPeriodName.ThisMonth, "2026-10-01", "2026-10-31")]
    [InlineData(DatabaseQueryPeriodName.LastMonth, "2026-09-01", "2026-09-30")]
    [InlineData(DatabaseQueryPeriodName.Last7Days, "2026-09-27", "2026-10-03")]
    [InlineData(DatabaseQueryPeriodName.Last30Days, "2026-09-04", "2026-10-03")]
    public void Named_periods_are_whole_UTC_days_with_Monday_weeks(DatabaseQueryPeriodName name, string from, string to)
    {
        var period = DatabaseFixedQueries.Resolve(name, Today);

        period.From.ShouldBe(DateOnly.Parse(from));
        period.To.ShouldBe(DateOnly.Parse(to));
    }

    [Fact]
    public void A_Sunday_belongs_to_the_week_that_ends_on_it_and_a_Monday_starts_the_next()
    {
        DatabaseFixedQueries.Resolve(DatabaseQueryPeriodName.ThisWeek, new DateOnly(2026, 10, 4)).From.ShouldBe(new DateOnly(2026, 9, 28));
        DatabaseFixedQueries.Resolve(DatabaseQueryPeriodName.ThisWeek, new DateOnly(2026, 10, 5)).From.ShouldBe(new DateOnly(2026, 10, 5));
    }

    [Fact]
    public void The_period_bounds_are_UTC_midnights_so_the_last_day_is_included_and_the_next_is_not()
    {
        var period = new DatabaseQueryPeriod(null, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        period.Start.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        period.EndExclusive.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_previous_period_is_the_full_period_before()
    {
        var thisMonth = DatabaseFixedQueries.Previous(DatabaseFixedQueries.Resolve(DatabaseQueryPeriodName.ThisMonth, new DateOnly(2026, 3, 31)));
        (thisMonth.From, thisMonth.To).ShouldBe((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), "February is shorter");

        var lastMonth = DatabaseFixedQueries.Previous(DatabaseFixedQueries.Resolve(DatabaseQueryPeriodName.LastMonth, Today));
        (lastMonth.From, lastMonth.To).ShouldBe((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)));

        var week = DatabaseFixedQueries.Previous(DatabaseFixedQueries.Resolve(DatabaseQueryPeriodName.ThisWeek, Today));
        (week.From, week.To).ShouldBe((new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 27)));

        var rolling = DatabaseFixedQueries.Previous(DatabaseFixedQueries.Resolve(DatabaseQueryPeriodName.Last30Days, Today));
        (rolling.From, rolling.To).ShouldBe((new DateOnly(2026, 8, 5), new DateOnly(2026, 9, 3)));

        var custom = DatabaseFixedQueries.Previous(new DatabaseQueryPeriod(null, new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 12)));
        (custom.From, custom.To).ShouldBe((new DateOnly(2026, 1, 7), new DateOnly(2026, 1, 9)));
    }

    [Fact]
    public void A_valid_request_becomes_a_spec()
    {
        var subject = Guid.NewGuid();

        var named = DatabaseFixedQueries.Validate(
            DatabaseQueryKind.FieldSum,
            Parameters(("period", "last-week"), ("fieldId", "field-weight"), ("subjectId", subject.ToString())),
            Today, Fields);
        var custom = DatabaseFixedQueries.Validate(
            DatabaseQueryKind.RecordCount, Parameters(("from", "2026-01-01"), ("to", "2026-12-31")), Today, Fields);

        named.IsValid.ShouldBeTrue();
        named.Value.Period!.Name.ShouldBe(DatabaseQueryPeriodName.LastWeek);
        named.Value.FieldId.ShouldBe("field-weight");
        named.Value.SubjectId.ShouldBe(subject);
        custom.IsValid.ShouldBeTrue();
        custom.Value.Period!.Name.ShouldBeNull();
        custom.Value.Period.Days.ShouldBe(365);
    }

    [Theory]
    [InlineData("record-count", "bogus", "period=last-week")]
    [InlineData("record-count", "fieldId", "period=last-week")]
    [InlineData("record-count", "sql", "period=last-week")]
    [InlineData("record-count", "orderBy", "period=last-week")]
    [InlineData("subject-comparison", "period", "")]
    [InlineData("subject-comparison", "from", "")]
    [InlineData("field-sum", "expression", "period=last-week&fieldId=field-weight")]
    [InlineData("period-summary", "fieldId", "period=last-week")]
    public void A_parameter_the_query_does_not_define_is_refused(string kind, string extra, string valid)
    {
        var parameters = valid.Length == 0
            ? new Dictionary<string, string?>()
            : valid.Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => (string?)pair[1]);
        if (kind == "subject-comparison")
        {
            parameters["subjectId"] = Guid.NewGuid().ToString();
        }

        parameters[extra] = "x";

        var result = DatabaseFixedQueries.Validate(WireNames.Parse<DatabaseQueryKind>(kind), parameters, Today, Fields);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == extra && failure.Message == DatabaseFixedQueries.UnknownParameterMessage);
    }

    [Theory]
    [InlineData("", "請指定統計期間，或同時指定起訖日期。")]
    [InlineData("period=this-year", "不支援的統計期間。")]
    [InlineData("period=", "不支援的統計期間。")]
    [InlineData("period=LAST-WEEK", "不支援的統計期間。")]
    [InlineData("period=last-week&from=2026-01-01&to=2026-01-02", "統計期間與起訖日期只能擇一指定。")]
    [InlineData("from=2026-01-01", "起訖日期要同時指定。")]
    [InlineData("to=2026-01-01", "起訖日期要同時指定。")]
    [InlineData("from=2026-02-30&to=2026-03-01", "請輸入 yyyy-MM-dd 格式的日期。")]
    [InlineData("from=2026-1-1&to=2026-01-05", "請輸入 yyyy-MM-dd 格式的日期。")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-01-05", "請輸入 yyyy-MM-dd 格式的日期。")]
    [InlineData("from=1999-12-31&to=2000-01-02", "請輸入 yyyy-MM-dd 格式的日期。")]
    [InlineData("from=2026-03-01&to=2026-02-01", "起始日期不能晚於結束日期。")]
    [InlineData("from=2025-01-01&to=2026-01-02", "統計期間最長 366 天。")]
    public void A_period_must_be_defined_and_bounded(string query, string message)
    {
        var parameters = query.Length == 0
            ? Parameters()
            : query.Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => (string?)pair[1]);

        var result = DatabaseFixedQueries.Validate(DatabaseQueryKind.RecordCount, parameters, Today, Fields);

        result.IsValid.ShouldBeFalse();
        result.Failures.Select(failure => failure.Message).ShouldContain(message);
    }

    [Fact]
    public void A_range_of_exactly_366_days_is_the_longest_allowed()
    {
        DatabaseFixedQueries.Validate(
            DatabaseQueryKind.RecordCount, Parameters(("from", "2024-01-01"), ("to", "2024-12-31")), Today, Fields)
            .IsValid.ShouldBeTrue("2024 is a leap year: 366 days");
    }

    [Theory]
    [InlineData("field-sum", "field-weight", true, "")]
    [InlineData("field-sum", "field-old", true, "")]
    [InlineData("field-sum", "field-score", false, "只有數字欄位可以加總。")]
    [InlineData("field-sum", "field-note", false, "只有數字欄位可以加總。")]
    [InlineData("field-sum", "field-missing", false, "找不到這個欄位。")]
    [InlineData("field-sum", "", false, "找不到這個欄位。")]
    [InlineData("subject-comparison", "field-score", true, "")]
    [InlineData("subject-comparison", "field-weight", true, "")]
    [InlineData("subject-comparison", "field-note", false, "只有數字或量尺欄位可以比較。")]
    [InlineData("subject-comparison", "field-missing", false, "找不到這個欄位。")]
    public void A_field_must_exist_in_some_form_version_and_suit_the_query(string kind, string fieldId, bool valid, string message)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["fieldId"] = fieldId,
            [kind == "field-sum" ? "period" : "subjectId"] = kind == "field-sum" ? "last-week" : Guid.NewGuid().ToString(),
        };

        var result = DatabaseFixedQueries.Validate(WireNames.Parse<DatabaseQueryKind>(kind), parameters, Today, Fields);

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            result.Failures.ShouldContain(failure => failure.Field == "fieldId" && failure.Message == message);
        }
    }

    [Fact]
    public void A_field_resolves_to_its_latest_definition_and_removed_fields_stay_known()
    {
        Fields["field-weight"].Field.Unit.ShouldBe("kg");
        Fields["field-weight"].VersionNumber.ShouldBe(2);
        Fields["field-old"].VersionNumber.ShouldBe(1, "removed in version 2 but still queryable");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{6f9619ff-8b86-d011-b42d-00c04fc964ff}")]
    public void A_subject_that_is_not_an_account_id_is_the_same_failure_as_a_missing_one(string subjectId)
    {
        var result = DatabaseFixedQueries.Validate(
            DatabaseQueryKind.SubjectComparison, Parameters(("subjectId", subjectId)), Today, Fields);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldBe([new SmartAgri.Application.Validation.ValidationFailure("subjectId", DatabaseFixedQueries.SubjectNotFoundMessage)]);
    }

    [Fact]
    public void Required_parameters_are_required()
    {
        var comparison = DatabaseFixedQueries.Validate(DatabaseQueryKind.SubjectComparison, Parameters(), Today, Fields);
        var sum = DatabaseFixedQueries.Validate(DatabaseQueryKind.FieldSum, Parameters(("period", "last-week")), Today, Fields);

        comparison.Failures.Select(failure => failure.Field).ShouldBe(["subjectId"]);
        sum.Failures.Select(failure => failure.Field).ShouldBe(["fieldId"]);
    }

    [Fact]
    public void Every_kind_has_exactly_one_definition_and_none_takes_an_expression()
    {
        Definitions().ShouldBe(Enum.GetValues<DatabaseQueryKind>().Order());
        foreach (var definition in DatabaseFixedQueries.Definitions)
        {
            definition.AllowedParameters.ShouldBeSubsetOf(["period", "from", "to", "fieldId", "subjectId"]);
            definition.Name.ShouldNotBeNullOrEmpty();
        }

        static IEnumerable<DatabaseQueryKind> Definitions() => DatabaseFixedQueries.Definitions.Select(definition => definition.Kind).Order();
    }

    private static class WireNames
    {
        public static T Parse<T>(string name)
            where T : struct, Enum => SmartAgri.Domain.WireNames<T>.Parse(name);
    }
}
