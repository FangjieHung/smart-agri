using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

public class DatabaseFormRulesTests
{
    private static readonly IReadOnlySet<string> NoRetired = new HashSet<string>();

    private static DatabaseFieldDraft Text(string? id, string? label, bool required = false) =>
        new(id, label, "text", required, null, null, null);

    private static DatabaseFieldDraft Choice(string? id, string? label, params string?[] options) =>
        new(id, label, "single-choice", false, options, null, null);

    private static IReadOnlyList<ValidationFailure> Failures(params DatabaseFieldDraft?[] drafts) =>
        DatabaseFormRules.Validate(drafts, NoRetired).Failures;

    [Fact]
    public void Every_template_form_passes_unchanged_when_sent_back_as_drafts()
    {
        foreach (var template in DatabaseTemplates.All)
        {
            var drafts = template.Fields.Select(field => new DatabaseFieldDraft(
                field.Id,
                field.Label,
                SmartAgri.Domain.WireNames<DatabaseFieldType>.ToWire(field.Type),
                field.Required,
                field.Options.Cast<string?>().ToList(),
                field.Scale is { } scale ? new DatabaseScaleDraft(scale.Min, scale.Max, scale.MinLabel, scale.MaxLabel) : null,
                field.Unit)).ToList();

            var result = DatabaseFormRules.Validate(drafts, NoRetired);

            result.IsValid.ShouldBeTrue(template.Name);
            DatabaseFormRules.AreSame(template.Fields, result.Value).ShouldBeTrue(template.Name);
            DatabaseFormVersion.EnsureValid(result.Value);
        }
    }

    [Fact]
    public void Text_options_unit_and_unused_settings_are_normalized()
    {
        var fields = DatabaseFormRules.Validate(
        [
            new("field-a", "  姓名 ", "text", true, ["leftover"], new DatabaseScaleDraft(1, 9, "x", "y"), "元"),
            new("field-b", "分類", "multiple-choice", false, [" 甲 ", "", "  ", "乙"], null, "元"),
            new("field-c", "金額", "number", false, null, null, " 元 "),
            new("field-d", "評分", "scale", false, null, new DatabaseScaleDraft(0, 10, " 差 ", " 好 "), null),
        ], NoRetired).Value;

        DatabaseFormRules.AreSame(
            [fields[0]], [new DatabaseFormField("field-a", "姓名", DatabaseFieldType.Text, true, [], null, string.Empty)]).ShouldBeTrue();
        fields[1].Options.ShouldBe(["甲", "乙"]);
        fields[1].Unit.ShouldBe(string.Empty);
        fields[2].Unit.ShouldBe("元");
        fields[3].Scale.ShouldBe(new DatabaseScaleRange(0, 10, "差", "好"));
    }

    [Fact]
    public void A_scale_without_a_range_gets_the_editors_default()
    {
        var field = DatabaseFormRules.Validate([new("field-s", "分數", "scale", false, null, null, null)], NoRetired).Value.Single();

        field.Scale.ShouldBe(new DatabaseScaleRange(1, 5, string.Empty, string.Empty));
    }

    [Fact]
    public void A_field_without_an_id_gets_a_new_valid_one_that_is_not_taken()
    {
        var fields = DatabaseFormRules.Validate([Text(null, "甲"), Text("", "乙"), Text("field-x", "丙")], NoRetired).Value;

        fields.Select(field => field.Id).Distinct().Count().ShouldBe(3);
        fields[0].Id.ShouldStartWith("field-");
        fields[2].Id.ShouldBe("field-x");
        DatabaseFormVersion.EnsureValid(fields);
    }

    [Fact]
    public void Nothing_or_too_many_fields_is_a_form_level_failure()
    {
        DatabaseFormRules.Validate(null, NoRetired).Failures.ShouldBe([new("fields", "表單至少需要一個欄位。")]);
        DatabaseFormRules.Validate([], NoRetired).Failures.ShouldBe([new("fields", "表單至少需要一個欄位。")]);

        var tooMany = Enumerable.Range(0, DatabaseFormVersion.MaxFields + 1).Select(index => Text($"field-{index}", $"欄位{index}")).ToList();
        DatabaseFormRules.Validate(tooMany, NoRetired).Failures.ShouldBe([new("fields", "表單最多 50 個欄位。")]);
    }

    [Fact]
    public void Each_broken_field_reports_its_first_failure_under_its_own_index_and_member()
    {
        var failures = Failures(
            Text("field-ok", "正常"),
            Text("field-blank", "   "),
            Text("field-dup", "正常"),
            Choice("field-few", "選擇", "只有一個", " "),
            new("field-badtype", "類型", "radio", false, null, null, null),
            new("field-scale", "量尺", "scale", false, null, new DatabaseScaleDraft(5, 5, "", ""), null),
            new("field-steps", "太多刻度", "scale", false, null, new DatabaseScaleDraft(0, 11, "", ""), null),
            new("field-frac", "小數量尺", "scale", false, null, new DatabaseScaleDraft(1.5, 5, "", ""), null),
            null);

        failures.ShouldBe(
        [
            new("fields[1].label", "請填寫欄位名稱。"),
            new("fields[2].label", "欄位名稱不可重複。"),
            new("fields[3].options", "單選或多選至少需要 2 個選項。"),
            new("fields[4].type", "不支援的欄位類型。"),
            new("fields[5].scale", "量尺的最小值必須小於最大值。"),
            new("fields[6].scale", "量尺最多 11 個刻度。"),
            new("fields[7].scale", "量尺的最小值必須小於最大值。"),
            new("fields[8]", "欄位內容不可為空。"),
        ]);
    }

    [Fact]
    public void Limits_that_the_domain_guard_enforces_are_messages_not_exceptions()
    {
        Failures(Text("field-a", new string('名', DatabaseFormField.LabelMaxLength + 1))).Single().Key().ShouldBe("fields[0].label");
        Failures(Choice("field-a", "選", "甲", "甲")).ShouldBe([new("fields[0].options", "選項不可重複。")]);
        Failures(Choice("field-a", "選", "甲", new string('乙', DatabaseFormField.OptionMaxLength + 1))).Single().Key().ShouldBe("fields[0].options");
        Failures(Choice("field-a", "選", [.. Enumerable.Range(0, DatabaseFormField.MaxOptions + 1).Select(n => $"選項{n}")])).Single().Key()
            .ShouldBe("fields[0].options");
        Failures(new DatabaseFieldDraft("field-a", "金額", "number", false, null, null, new string('元', DatabaseFormField.UnitMaxLength + 1)))
            .Single().Key().ShouldBe("fields[0].unit");
        Failures(new DatabaseFieldDraft("field-a", "量", "scale", false, null, new DatabaseScaleDraft(1, 5, new string('x', 21), ""), null))
            .Single().Key().ShouldBe("fields[0].scale");
    }

    [Theory]
    [InlineData("Field-A")]
    [InlineData("field-")]
    [InlineData("field_a")]
    [InlineData("not-a-field")]
    public void A_malformed_id_is_an_id_failure(string id)
    {
        Failures(Text(id, "名")).ShouldBe([new("fields[0].id", "欄位編號格式不正確。")]);
    }

    [Fact]
    public void A_repeated_id_and_an_id_retired_by_an_earlier_version_are_id_failures()
    {
        Failures(Text("field-a", "甲"), Text("field-a", "乙")).ShouldBe([new("fields[1].id", "欄位編號重複。")]);

        DatabaseFormRules.Validate([Text("field-gone", "回來了")], new HashSet<string> { "field-gone" }).Failures
            .ShouldBe([new("fields[0].id", "這個欄位編號屬於先前已移除的欄位，請改用新的欄位。")]);
    }

    [Fact]
    public void AreSame_notices_any_changed_setting_or_order()
    {
        var original = DatabaseTemplates.Get(DatabaseTemplateId.Satisfaction).Fields;
        DatabaseFormRules.AreSame(original, [.. original]).ShouldBeTrue();
        DatabaseFormRules.AreSame(original, [.. original.Reverse()]).ShouldBeFalse();
        DatabaseFormRules.AreSame(original, [original[0] with { Required = !original[0].Required }, .. original.Skip(1)]).ShouldBeFalse();
        DatabaseFormRules.AreSame(original, [.. original.Skip(1)]).ShouldBeFalse();
    }
}

internal static class ValidationFailureExtensions
{
    public static string Key(this ValidationFailure failure) => failure.Field;
}
