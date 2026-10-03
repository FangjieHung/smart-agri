using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

public class DatabaseCreationRulesTests
{
    [Fact]
    public void A_valid_request_is_trimmed_and_a_missing_purpose_is_the_templates_description()
    {
        var creation = DatabaseCreationRules.Validate("template-satisfaction", "  門市滿意度 ", null).Value;

        creation.Template.Id.ShouldBe(DatabaseTemplateId.Satisfaction);
        creation.Name.ShouldBe("門市滿意度");
        creation.Purpose.ShouldBe("收集客戶對服務的評分與建議。");

        DatabaseCreationRules.Validate("template-blank", "名", "  自訂用途 ").Value.Purpose.ShouldBe("自訂用途");
        DatabaseCreationRules.Validate("template-blank", "名", "   ").Value.Purpose.ShouldBe("從一個文字欄位開始，自行設計要收集的欄位。");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("template-unknown")]
    [InlineData("Satisfaction")]
    public void An_unknown_template_is_a_templateId_failure(string? templateId)
    {
        DatabaseCreationRules.Validate(templateId, "名", null).Failures
            .ShouldBe([new("templateId", "請選擇一個模板。")]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_is_required(string? name)
    {
        DatabaseCreationRules.Validate("template-blank", name, null).Failures
            .ShouldBe([new("name", "請輸入資料庫名稱。")]);
    }

    [Fact]
    public void Limits_count_characters_after_trimming_and_every_broken_rule_is_reported()
    {
        var longestName = new string('名', Database.NameMaxLength);
        DatabaseCreationRules.Validate("template-blank", $" {longestName} ", null).IsValid.ShouldBeTrue();

        DatabaseCreationRules.Validate(null, longestName + "名", new string('用', Database.PurposeMaxLength + 1)).Failures.ShouldBe(
        [
            new("templateId", "請選擇一個模板。"),
            new("name", "資料庫名稱請在 40 個字以內。"),
            new("purpose", "用途說明最多 500 個字。"),
        ]);
    }
}
