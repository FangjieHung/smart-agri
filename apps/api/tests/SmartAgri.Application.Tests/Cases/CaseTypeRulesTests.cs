using Shouldly;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Validation;

namespace SmartAgri.Application.Tests.Cases;

/// <summary><see cref="CaseTypeRules"/>: the request rules of <c>/api/v1/case-types</c> (M7-2, #247).</summary>
public class CaseTypeRulesTests
{
    private static readonly Guid GroupId = Guid.CreateVersion7();

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2160, true)]
    [InlineData(2161, false)]
    [InlineData(-1, false)]
    public void The_handling_time_is_one_to_2160_hours(int hours, bool valid)
    {
        var result = CaseTypeRules.Validate("設備故障報修", "", GroupId, hours, true);

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            result.Failures.ShouldHaveSingleItem().ShouldBe(
                new ValidationFailure("defaultDueHours", "預設處理時限請在 1 到 2,160 小時（90 天）之間。"));
        }
    }

    [Fact]
    public void The_rules_trim_and_report_every_field_at_once()
    {
        var result = CaseTypeRules.Validate("  ", new string('說', 501), null, null, null);

        result.Failures.Select(failure => failure.Field).ShouldBe(["name", "description", "defaultGroupId", "defaultDueHours"]);
        result.Failures.Select(failure => failure.Message).ShouldBe(
            ["請輸入案件類型名稱。", "說明請在 500 個字以內。", "請選擇預設承辦組。", "預設處理時限請在 1 到 2,160 小時（90 天）之間。"]);

        CaseTypeRules.Validate(new string('類', 41), "", GroupId, 24, null).Failures.ShouldHaveSingleItem().Message
            .ShouldBe("案件類型名稱請在 40 個字以內。");
        CaseTypeRules.Validate("x", "", Guid.Empty, 24, null).Failures.ShouldHaveSingleItem().Field.ShouldBe("defaultGroupId");

        var valid = CaseTypeRules.Validate(" 設備故障報修 ", $" {new string('說', 500)} ", GroupId, 72, null);
        valid.IsValid.ShouldBeTrue();
        (valid.Value.Name, valid.Value.Description.Length, valid.Value.IsActive).ShouldBe(("設備故障報修", 500, true), "omitted isActive is active");
        CaseTypeRules.Validate(new string('類', 40), null, GroupId, 72, false).Value.ShouldBe(
            new CaseTypeFields(new string('類', 40), "", GroupId, 72, false));
    }
}
