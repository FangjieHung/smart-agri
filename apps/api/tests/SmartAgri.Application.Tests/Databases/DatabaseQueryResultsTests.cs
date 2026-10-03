using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

/// <summary>
/// The arithmetic of the fixed queries (M4 #147): first / previous / current comparison across form
/// versions by field id, "not enough records" instead of a fake trend, sums that never mix units,
/// and number texts equal to the frontend mock's.
/// </summary>
public class DatabaseQueryResultsTests
{
    private static readonly IReadOnlyDictionary<string, DatabaseFieldReference> Fields = DatabaseFixedQueries.FieldReferences(
    [
        (1, [Number("field-spend", "消費金額", "元"), Scale("field-mood", "心情")]),
        (2, [Number("field-spend", "每次消費", "元"), Scale("field-mood", "心情"), Number("field-count", "件數", "件")]),
    ]);

    private static DatabaseFormField Number(string id, string label, string unit) =>
        new(id, label, DatabaseFieldType.Number, false, [], null, unit);

    private static DatabaseFormField Scale(string id, string label) =>
        new(id, label, DatabaseFieldType.Scale, false, [], new DatabaseScaleRange(1, 5, "低", "高"), string.Empty);

    private static DatabaseQueryEntry Spend(double value, string label = "消費金額", string unit = "元") =>
        new("field-spend", label, DatabaseFieldType.Number, unit, DatabaseAnswerRules.FormatNumber(value, unit), value);

    private static DatabaseQueryEntry Mood(int value) =>
        new("field-mood", "心情", DatabaseFieldType.Scale, string.Empty, $"{value} / 5", value);

    private static DatabaseQueryRecord Record(string at, params DatabaseQueryEntry[] entries) =>
        new(Guid.NewGuid(), DateTimeOffset.Parse(at), entries);

    [Fact]
    public void No_records_and_one_record_are_not_enough_and_draw_no_trend()
    {
        var none = DatabaseQueryResults.Compare([], Fields);
        var one = DatabaseQueryResults.Compare([Record("2026-09-01T02:00:00Z", Spend(100))], Fields);

        none.Status.ShouldBe(DatabaseComparisonStatus.InsufficientRecords);
        none.RecordCount.ShouldBe(0);
        none.Message.ShouldBe("目前只有 0 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。");
        none.Metrics.ShouldBeEmpty();
        none.Summary.ShouldBeNull();
        one.Status.ShouldBe(DatabaseComparisonStatus.InsufficientRecords);
        one.Message.ShouldBe("目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。");
        one.Metrics.ShouldBeEmpty();
    }

    [Fact]
    public void First_previous_and_current_come_from_the_oldest_the_second_newest_and_the_newest()
    {
        var records = new[]
        {
            Record("2026-08-01T02:00:00Z", Spend(1000), Mood(2)),
            Record("2026-08-15T02:00:00Z", Spend(1500), Mood(4)),
            Record("2026-09-01T23:30:00Z", Spend(1200), Mood(4)),
        };

        var comparison = DatabaseQueryResults.Compare(records, Fields);

        comparison.Status.ShouldBe(DatabaseComparisonStatus.Available);
        comparison.RecordCount.ShouldBe(3);
        comparison.Summary.ShouldBe("比較 3 筆已同意提交的紀錄（2026-08-01 至 2026-09-01）。");
        var spend = comparison.Metrics.Single(metric => metric.FieldId == "field-spend");
        spend.First.Display.ShouldBe("1,000 元");
        spend.Previous.Display.ShouldBe("1,500 元");
        spend.Current.Display.ShouldBe("1,200 元");
        spend.Current.Date.ShouldBe("2026-09-01", "the UTC day of 23:30Z");
        spend.ChangeFromPrevious.ShouldBe(-300);
        spend.ChangeFromFirst.ShouldBe(200);
        spend.ChangeFromPreviousLabel.ShouldBe("-300 元");
        spend.ChangeFromFirstLabel.ShouldBe("+200 元");
        spend.Direction.ShouldBe(DatabaseTrendDirection.Down);
        spend.Points.Select(point => point.Value).ShouldBe([1000, 1500, 1200]);
        spend.Axis.ShouldBe(new DatabaseComparisonAxis(1000, 1500));
        spend.Summary.ShouldBe("每次消費：本次 1,200 元，較上次 -300 元，較首次 +200 元。".Replace("每次消費", "消費金額"));

        var mood = comparison.Metrics.Single(metric => metric.FieldId == "field-mood");
        mood.Current.Display.ShouldBe("4 / 5");
        mood.ChangeFromPreviousLabel.ShouldBe("持平");
        mood.Direction.ShouldBe(DatabaseTrendDirection.Flat);
        mood.ChangeFromFirstLabel.ShouldBe("+2 分");
        mood.Axis.ShouldBe(new DatabaseComparisonAxis(1, 5), "a scale chart spans the scale");
        mood.Unit.ShouldBe("分");
    }

    [Fact]
    public void A_field_is_compared_by_its_id_even_when_its_label_changed_between_form_versions()
    {
        var records = new[]
        {
            Record("2026-08-01T02:00:00Z", Spend(100, label: "消費金額")),
            Record("2026-09-01T02:00:00Z", Spend(150, label: "每次消費")),
        };

        var metric = DatabaseQueryResults.Compare(records, Fields).Metrics.Single();

        metric.Label.ShouldBe("每次消費", "the latest record's label");
        metric.Points.Count.ShouldBe(2);
        metric.ChangeFromPreviousLabel.ShouldBe("+50 元");
    }

    [Fact]
    public void A_field_that_only_the_older_records_have_is_not_compared_and_one_with_a_single_value_is_dropped()
    {
        var records = new[]
        {
            Record("2026-08-01T02:00:00Z", Spend(100), Mood(2)),
            Record("2026-09-01T02:00:00Z", Spend(120)),
            Record("2026-09-02T02:00:00Z", Spend(130), new DatabaseQueryEntry("field-count", "件數", DatabaseFieldType.Number, "件", "3 件", 3)),
        };

        var comparison = DatabaseQueryResults.Compare(records, Fields);

        comparison.Metrics.Select(metric => metric.FieldId).ShouldBe(["field-spend"]);
    }

    [Fact]
    public void Values_stored_under_another_unit_are_not_mixed_into_the_trend()
    {
        var records = new[]
        {
            Record("2026-08-01T02:00:00Z", Spend(100, unit: "公斤")),
            Record("2026-09-01T02:00:00Z", Spend(200, unit: "元")),
            Record("2026-09-02T02:00:00Z", Spend(260, unit: "元")),
        };

        var metric = DatabaseQueryResults.Compare(records, Fields).Metrics.Single();

        metric.Points.Count.ShouldBe(2);
        metric.First.Value.ShouldBe(200);
    }

    [Fact]
    public void Two_records_without_any_comparable_number_say_so_instead_of_an_empty_trend()
    {
        var comparison = DatabaseQueryResults.Compare(
            [Record("2026-08-01T02:00:00Z"), Record("2026-09-01T02:00:00Z")], Fields);

        comparison.Status.ShouldBe(DatabaseComparisonStatus.InsufficientRecords);
        comparison.RecordCount.ShouldBe(2);
        comparison.Metrics.ShouldBeEmpty();
        comparison.Message.ShouldBe("目前有 2 筆紀錄，但沒有任何數字或量尺欄位累積 2 筆以上的數值，無法比較。");
    }

    [Fact]
    public void A_comparison_can_be_narrowed_to_one_field()
    {
        var records = new[]
        {
            Record("2026-08-01T02:00:00Z", Spend(100), Mood(2)),
            Record("2026-09-01T02:00:00Z", Spend(120), Mood(3)),
        };

        DatabaseQueryResults.Compare(records, Fields, "field-mood").Metrics.Select(metric => metric.FieldId).ShouldBe(["field-mood"]);
    }

    [Fact]
    public void Sums_cover_the_period_and_the_one_before_and_never_add_a_value_stored_under_an_old_unit()
    {
        var current = new[] { new DatabaseSumRow("field-spend", "元", 2500.5, 2), new DatabaseSumRow("field-spend", "公斤", 9, 1) };
        var previous = new[] { new DatabaseSumRow("field-spend", "元", 3000, 3) };

        var sums = DatabaseQueryResults.Sums(current, previous, Fields, 2);

        sums.Select(line => line.FieldId).ShouldBe(["field-spend", "field-count"], "current form order; the empty current field is still listed");
        var spend = sums[0];
        spend.Sum.ShouldBe(2500.5);
        spend.Display.ShouldBe("2,500.5 元");
        spend.RecordCount.ShouldBe(2, "the 公斤 row is not counted");
        spend.PreviousSum.ShouldBe(3000);
        spend.PreviousDisplay.ShouldBe("3,000 元");
        spend.Change.ShouldBe(-499.5);
        spend.ChangeLabel.ShouldBe("-499.5 元");
        sums[1].Sum.ShouldBe(0);
        sums[1].Display.ShouldBe("0 件");
        sums[1].RecordCount.ShouldBe(0);
        sums[1].ChangeLabel.ShouldBe("持平");
    }

    [Fact]
    public void Only_number_fields_are_summed()
    {
        DatabaseQueryResults.Sums([], [], Fields, 2).Select(line => line.FieldId).ShouldNotContain("field-mood");
    }

    [Theory]
    [InlineData(3, "+3 筆")]
    [InlineData(-2, "-2 筆")]
    [InlineData(0, "持平")]
    public void Count_changes_read_like_the_mocks_changes(int change, string label) =>
        DatabaseQueryResults.CountChangeLabel(change).ShouldBe(label);

    [Theory]
    [InlineData(1234.5678, "元", "+1,234.568 元")]
    [InlineData(-0.5, "", "-0.5")]
    [InlineData(0, "元", "持平")]
    public void Signed_numbers_use_three_decimals_and_thousands_separators(double value, string unit, string expected) =>
        DatabaseQueryResults.FormatSigned(value, unit).ShouldBe(expected);
}
