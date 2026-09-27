using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

public class KnowledgeBaseDetailsRulesTests
{
    [Fact]
    public void Create_trims_both_fields_and_a_missing_purpose_is_empty()
    {
        KnowledgeBaseDetailsRules.ForCreate("  退換貨政策 ", " 退貨期限 ").Value
            .ShouldBe(new KnowledgeBaseDetails("退換貨政策", "退貨期限"));
        KnowledgeBaseDetailsRules.ForCreate("退換貨政策", null).Value
            .ShouldBe(new KnowledgeBaseDetails("退換貨政策", string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_requires_a_name(string? name)
    {
        var failure = KnowledgeBaseDetailsRules.ForCreate(name, "用途").Failures.ShouldHaveSingleItem();

        failure.ShouldBe(new("name", "請輸入知識庫名稱。"));
    }

    [Fact]
    public void Limits_count_characters_after_trimming_and_every_broken_rule_is_reported()
    {
        var longestName = new string('名', KnowledgeBase.NameMaxLength);
        var longestPurpose = new string('用', KnowledgeBase.PurposeMaxLength);
        KnowledgeBaseDetailsRules.ForCreate($" {longestName} ", $" {longestPurpose} ").IsValid.ShouldBeTrue();

        var result = KnowledgeBaseDetailsRules.ForCreate(longestName + "名", longestPurpose + "用");

        result.Failures.ShouldBe(
        [
            new("name", $"知識庫名稱最多 {KnowledgeBase.NameMaxLength} 個字。"),
            new("purpose", $"用途說明最多 {KnowledgeBase.PurposeMaxLength} 個字。"),
        ]);
    }

    [Fact]
    public void Update_keeps_fields_that_were_not_sent()
    {
        var current = new KnowledgeBaseDetails("舊名稱", "舊用途");

        KnowledgeBaseDetailsRules.ForUpdate(current, " 新名稱 ", null).Value.ShouldBe(new KnowledgeBaseDetails("新名稱", "舊用途"));
        KnowledgeBaseDetailsRules.ForUpdate(current, null, "").Value.ShouldBe(new KnowledgeBaseDetails("舊名稱", string.Empty));
        KnowledgeBaseDetailsRules.ForUpdate(current, null, null).Value.ShouldBe(current);
    }

    [Fact]
    public void Update_cannot_blank_the_name()
    {
        KnowledgeBaseDetailsRules.ForUpdate(new KnowledgeBaseDetails("舊名稱", "舊用途"), " ", null)
            .Failures.ShouldHaveSingleItem().Field.ShouldBe("name");
    }
}
