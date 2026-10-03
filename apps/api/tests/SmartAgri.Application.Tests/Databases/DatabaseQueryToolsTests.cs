using System.Text.Json;
using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

/// <summary>
/// The fixed queries as conversation tools (M4 #149): only the four fixed queries are offered, each
/// with only its defined parameters and the offered databases and fields as enums; a model's call is
/// matched back without trusting it; the reply's numbers are the result's own strings.
/// </summary>
public class DatabaseQueryToolsTests
{
    private static readonly Guid Reports = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Visits = Guid.Parse("0193a000-0000-7000-8000-000000000002");

    private static readonly DatabaseQueryToolSource ReportSource = new(
        Reports,
        "回報資料庫",
        [
            new("field-report-date", "回報日期", DatabaseFieldType.Date, string.Empty),
            new("field-completed-count", "完成數量", DatabaseFieldType.Number, "件"),
            new("field-satisfaction", "滿意度", DatabaseFieldType.Scale, string.Empty),
        ]);

    private static readonly DatabaseQueryToolSource VisitSource = new(
        Visits,
        "拜訪紀錄",
        [new("field-note", "備註", DatabaseFieldType.Text, string.Empty)]);

    [Fact]
    public void Only_the_four_fixed_queries_are_offered_each_with_only_its_defined_parameters()
    {
        var tools = DatabaseQueryTools.Declarations([ReportSource, VisitSource]);

        tools.Select(tool => tool.Name).ShouldBe(
            ["database_record_count", "database_field_sum", "database_period_summary", "database_subject_comparison"]);
        foreach (var tool in tools)
        {
            var kind = DatabaseQueryTools.KindOf(tool.Name).ShouldNotBeNull();
            var definition = DatabaseFixedQueries.Definition(kind);
            var schema = tool.JsonSchema;
            schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
            var names = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToList();
            names.ShouldBe([DatabaseQueryTools.DatabaseIdParameter, .. definition.AllowedParameters], ignoreOrder: true);
            Strings(schema.GetProperty("required")).ShouldBe([DatabaseQueryTools.DatabaseIdParameter, .. definition.RequiredParameters], ignoreOrder: true);
            Strings(schema.GetProperty("properties").GetProperty("databaseId").GetProperty("enum")).ShouldBe([Reports.ToString(), Visits.ToString()]);

            var text = (tool.Description + schema.GetRawText()).ToLowerInvariant();
            text.ShouldNotContain("\"sql\"");
            text.ShouldNotContain("expression");
        }

        var sum = tools.Single(tool => tool.Name == "database_field_sum").JsonSchema.GetProperty("properties");
        Strings(sum.GetProperty("fieldId").GetProperty("enum")).ShouldBe(["field-completed-count"]);
        Strings(sum.GetProperty("period").GetProperty("enum"))
            .ShouldBe(["this-week", "last-week", "this-month", "last-month", "last-7-days", "last-30-days"]);
        var comparison = tools.Single(tool => tool.Name == "database_subject_comparison").JsonSchema.GetProperty("properties");
        Strings(comparison.GetProperty("fieldId").GetProperty("enum")).ShouldBe(["field-completed-count", "field-satisfaction"]);
    }

    [Fact]
    public void Without_a_number_field_there_is_no_sum_tool()
    {
        var tools = DatabaseQueryTools.Declarations([VisitSource]);

        tools.Select(tool => tool.Name).ShouldBe(["database_record_count", "database_period_summary", "database_subject_comparison"]);
        tools.Last().JsonSchema.GetProperty("properties").TryGetProperty("fieldId", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_call_is_matched_only_to_an_offered_tool_and_database_and_passes_every_other_argument_on()
    {
        var unknown = DatabaseQueryTools.Parse("run_sql", Arguments(("databaseId", Reports.ToString()), ("sql", "SELECT 1")), [ReportSource]);
        unknown.Match.ShouldBe(DatabaseQueryToolCallMatch.UnknownTool);

        foreach (var databaseId in new object?[] { null, "not-a-guid", Guid.NewGuid().ToString(), Visits.ToString(), 42 })
        {
            DatabaseQueryTools.Parse("database_record_count", Arguments(("databaseId", databaseId), ("period", "this-month")), [ReportSource])
                .Match.ShouldBe(DatabaseQueryToolCallMatch.DatabaseNotOffered);
        }

        DatabaseQueryTools.Parse("database_record_count", Arguments(("period", "this-month")), [ReportSource])
            .Match.ShouldBe(DatabaseQueryToolCallMatch.DatabaseNotOffered);

        var matched = DatabaseQueryTools.Parse(
            "database_record_count",
            Arguments(("databaseId", Reports.ToString()), ("period", "this-month"), ("sql", "DROP TABLE x"), ("from", 20260101)),
            [ReportSource]);
        matched.Match.ShouldBe(DatabaseQueryToolCallMatch.Matched);
        var call = matched.Call.ShouldNotBeNull();
        call.Kind.ShouldBe(DatabaseQueryKind.RecordCount);
        call.Source.ShouldBe(ReportSource);
        call.Parameters.ShouldBe(new Dictionary<string, string?> { ["period"] = "this-month", ["sql"] = "DROP TABLE x", ["from"] = "20260101" }, ignoreOrder: true);

        // The undefined ones fail the fixed query's own validation.
        var validated = DatabaseFixedQueries.Validate(call.Kind, call.Parameters, new DateOnly(2026, 10, 3), new Dictionary<string, DatabaseFieldReference>());
        validated.IsValid.ShouldBeFalse();
        validated.Failures.Select(failure => failure.Field).ShouldContain("sql");
    }

    [Theory]
    [InlineData("本月回報了幾筆？", true)]
    [InlineData("這 個 月 的完成數量 加總 是多少", true)]
    [InlineData("上週一共噴藥幾次", true)]
    [InlineData("我要回報今天的用藥", false)]
    [InlineData("皮革商品可以用水清洗嗎？", false)]
    public void Statistics_questions_are_recognized(string question, bool expected) =>
        DatabaseQueryTools.AsksForStatistics(question).ShouldBe(expected);

    [Fact]
    public void A_count_reply_quotes_the_results_own_strings_and_names_period_metric_and_source()
    {
        var call = Call(DatabaseQueryKind.RecordCount);
        var result = new DatabaseRecordCountResult(
            new DatabaseQueryPeriodView("this-month", "2026-10-01", "2026-10-31", "本月（2026-10-01 至 2026-10-31）"),
            new DatabaseQueryPeriodView(null, "2026-09-01", "2026-09-30", "2026-09-01 至 2026-09-30"),
            null,
            1200,
            1197,
            3,
            "+3 筆");

        var answer = DatabaseQueryTools.Compose(call, result);

        answer.View.Status.ShouldBe(ChatDatabaseQueryStatus.Answered);
        answer.View.DatabaseName.ShouldBe("回報資料庫");
        answer.View.Query.ShouldBe("record-count");
        answer.View.QueryLabel.ShouldBe("紀錄筆數");
        answer.View.Period.ShouldBe(result.Period);
        var figure = answer.View.Figures.ShouldHaveSingleItem();
        (figure.Value, figure.Display, figure.PreviousDisplay, figure.ChangeLabel).ShouldBe((1200d, "1,200 筆", "1,197 筆", "+3 筆"));
        answer.Text.ShouldBe("根據「回報資料庫」的紀錄筆數查詢：本月（2026-10-01 至 2026-10-31）共有 1,200 筆有效紀錄；前一期（2026-09-01 至 2026-09-30）為 1,197 筆，變化 +3 筆。");
    }

    [Fact]
    public void Empty_periods_are_no_data_and_too_few_records_are_insufficient_data()
    {
        var period = new DatabaseQueryPeriodView(null, "2026-02-01", "2026-02-28", "2026-02-01 至 2026-02-28");
        var previous = new DatabaseQueryPeriodView(null, "2026-01-04", "2026-01-31", "2026-01-04 至 2026-01-31");
        var empty = DatabaseQueryTools.Compose(Call(DatabaseQueryKind.FieldSum), new DatabaseFieldSumResult(period, previous, null,
            new DatabaseFieldSum("field-completed-count", "完成數量", "件", 0, "0 件", 0, 5, "5 件", 1, -5, "-5 件")));
        empty.View.Status.ShouldBe(ChatDatabaseQueryStatus.NoData);
        empty.Text.ShouldContain("0 件");
        empty.Text.ShouldContain("沒有任何紀錄填寫「完成數量」");

        var insufficient = DatabaseQueryTools.Compose(Call(DatabaseQueryKind.SubjectComparison), new DatabaseSubjectComparisonResult(
            Guid.NewGuid(), DatabaseQueryResults.Insufficient(1, "目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。")));
        insufficient.View.Status.ShouldBe(ChatDatabaseQueryStatus.InsufficientData);
        insufficient.View.Figures.ShouldBeEmpty();
        insufficient.Text.ShouldContain("資料不足");
    }

    [Fact]
    public void Refusals_name_nothing_about_a_database_the_member_may_not_read()
    {
        foreach (var answer in new[] { DatabaseQueryTools.NotAvailable(), DatabaseQueryTools.Failed(), DatabaseQueryTools.Rejected(null) })
        {
            answer.View.DatabaseId.ShouldBeNull();
            answer.View.DatabaseName.ShouldBeNull();
            answer.View.Figures.ShouldBeEmpty();
        }

        var rejected = DatabaseQueryTools.Rejected(Call(DatabaseQueryKind.FieldSum));
        (rejected.View.Status, rejected.View.DatabaseName, rejected.View.Query).ShouldBe((ChatDatabaseQueryStatus.Rejected, "回報資料庫", "field-sum"));
        rejected.View.Figures.ShouldBeEmpty();
    }

    private static DatabaseQueryToolCall Call(DatabaseQueryKind kind) => new(kind, ReportSource, new Dictionary<string, string?>());

    private static Dictionary<string, object?> Arguments(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            string text => (object?)JsonSerializer.SerializeToElement(text),
            int number => JsonSerializer.SerializeToElement(number),
            _ => pair.Value,
        });

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString())];
}
