using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

public class DatabaseAnswerRulesTests
{
    private static readonly IReadOnlyList<DatabaseFormField> Fields =
    [
        new("field-name", "姓名", DatabaseFieldType.Text, true, [], null, string.Empty),
        new("field-amount", "金額", DatabaseFieldType.Number, false, [], null, "元"),
        new("field-day", "日期", DatabaseFieldType.Date, false, [], null, string.Empty),
        new("field-kind", "類型", DatabaseFieldType.SingleChoice, false, ["個人", "企業"], null, string.Empty),
        new("field-topics", "主題", DatabaseFieldType.MultipleChoice, false, ["品質", "客服", "速度"], null, string.Empty),
        new("field-score", "滿意度", DatabaseFieldType.Scale, false, [], new DatabaseScaleRange(1, 5, "低", "高"), string.Empty),
    ];

    private static Dictionary<string, DatabaseAnswerInput> Answers(params (string Id, DatabaseAnswerInput Answer)[] answers) =>
        answers.ToDictionary(pair => pair.Id, pair => pair.Answer);

    private static DatabaseAnswerInput T(string text) => DatabaseAnswerInput.FromText(text);

    [Fact]
    public void Valid_answers_become_entries_in_form_order_with_the_previews_display_text()
    {
        var result = DatabaseAnswerRules.Validate(Fields, Answers(
            ("field-score", T("4")),
            ("field-topics", DatabaseAnswerInput.FromChoices(["速度", "品質"])),
            ("field-kind", T("企業")),
            ("field-day", T("2026-10-03")),
            ("field-amount", T(" 1234567.891 ")),
            ("field-name", T("  王小明 "))));

        result.IsValid.ShouldBeTrue();
        result.Value.Select(entry => (entry.Field.Id, entry.Display)).ShouldBe(
        [
            ("field-name", "王小明"),
            ("field-amount", "1,234,567.891 元"),
            ("field-day", "2026-10-03"),
            ("field-kind", "企業"),
            ("field-topics", "品質、速度"),
            ("field-score", "4 / 5"),
        ]);
        result.Value[1].Number.ShouldBe(1234567.891);
        result.Value[4].Choices.ShouldBe(["品質", "速度"]);
        result.Value[5].Number.ShouldBe(4);
    }

    [Fact]
    public void Optional_fields_left_empty_show_not_filled_and_are_empty_entries()
    {
        var result = DatabaseAnswerRules.Validate(Fields, Answers(("field-name", T("甲"))));

        result.IsValid.ShouldBeTrue();
        result.Value.Skip(1).ShouldAllBe(entry => entry.Display == "未填寫" && entry.IsEmpty);
        result.Value[0].IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Every_broken_field_is_reported_under_its_field_id_with_the_label_quoted()
    {
        var result = DatabaseAnswerRules.Validate(Fields, Answers(
            ("field-amount", T("abc")),
            ("field-day", T("2026-13-45")),
            ("field-kind", T("政府")),
            ("field-topics", DatabaseAnswerInput.FromChoices(["品質", "不存在"])),
            ("field-score", T("6"))));

        result.Failures.Select(failure => (failure.Field, failure.Message)).ShouldBe(
        [
            ("answers.field-name", "「姓名」為必填。"),
            ("answers.field-amount", "「金額」請輸入數字。"),
            ("answers.field-day", "「日期」請輸入日期。"),
            ("answers.field-kind", "「類型」請從選項中選擇。"),
            ("answers.field-topics", "「主題」請從選項中選擇。"),
            ("answers.field-score", "「滿意度」請選擇 1 到 5 之間的分數。"),
        ]);
    }

    [Theory]
    [InlineData("3.5")]
    [InlineData("0")]
    [InlineData("1e3")]
    [InlineData("NaN")]
    public void A_scale_answer_must_be_an_integer_inside_the_range(string answer)
    {
        DatabaseAnswerRules.Validate(Fields, Answers(("field-name", T("甲")), ("field-score", T(answer)))).Failures
            .ShouldBe([new("answers.field-score", "「滿意度」請選擇 1 到 5 之間的分數。")]);
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("1,000")]
    [InlineData("NaN")]
    public void A_number_must_be_a_plain_finite_number(string answer)
    {
        DatabaseAnswerRules.Validate(Fields, Answers(("field-name", T("甲")), ("field-amount", T(answer)))).Failures
            .ShouldBe([new("answers.field-amount", "「金額」請輸入數字。")]);
    }

    [Fact]
    public void Answers_for_unknown_field_ids_are_ignored()
    {
        DatabaseAnswerRules.Validate(Fields, Answers(("field-name", T("甲")), ("field-removed", T("舊的")))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_required_multiple_choice_with_no_choice_is_required_and_a_list_for_a_text_field_is_empty()
    {
        var fields = new List<DatabaseFormField>
        {
            new("field-a", "甲", DatabaseFieldType.MultipleChoice, true, ["一", "二"], null, string.Empty),
            new("field-b", "乙", DatabaseFieldType.Text, false, [], null, string.Empty),
        };

        var result = DatabaseAnswerRules.Validate(fields, Answers(
            ("field-a", DatabaseAnswerInput.FromChoices([])),
            ("field-b", DatabaseAnswerInput.FromChoices(["x"]))));

        result.Failures.ShouldBe([new("answers.field-a", "「甲」為必填。")]);
    }
}
