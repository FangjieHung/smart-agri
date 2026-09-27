using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

public class KnowledgeReadabilityTests
{
    private const KnowledgeUnitLocationKind Page = KnowledgeUnitLocationKind.Page;
    private const KnowledgeUnitLocationKind Section = KnowledgeUnitLocationKind.Section;

    [Theory]
    [InlineData("", KnowledgeUnitIssue.TooLittleText)]
    [InlineData("一二三四五六七八九", KnowledgeUnitIssue.TooLittleText)]
    [InlineData("一 二 三 四 五 六 七 八 九\n\n", KnowledgeUnitIssue.TooLittleText)] // white space does not count
    [InlineData("一二三四五六七八九十", null)]
    [InlineData("Page 1 of 3", KnowledgeUnitIssue.TooLittleText)]
    [InlineData("Page 10 of 30", null)]
    public void A_pdf_page_needs_at_least_10_characters(string text, KnowledgeUnitIssue? expected)
    {
        KnowledgeReadability.Judge(Page, text).ShouldBe(expected);
    }

    [Fact]
    public void A_supplementary_cjk_character_counts_once()
    {
        KnowledgeReadability.Judge(Page, string.Concat(Enumerable.Repeat("\U00020000", 9))).ShouldBe(KnowledgeUnitIssue.TooLittleText);
        KnowledgeReadability.Judge(Page, string.Concat(Enumerable.Repeat("\U00020000", 10))).ShouldBeNull();
    }

    [Fact]
    public void Sections_have_no_minimum_but_must_have_some_text()
    {
        KnowledgeReadability.Judge(Section, "無。").ShouldBeNull();
        KnowledgeReadability.Judge(Section, " \n ").ShouldBe(KnowledgeUnitIssue.TooLittleText);
        KnowledgeReadability.Judge(KnowledgeUnitLocationKind.Sheet, "是").ShouldBeNull();
    }

    [Fact]
    public void Garbage_up_to_30_percent_is_readable_and_more_is_garbled()
    {
        // 7 good characters and 3 replacement characters: exactly 30%.
        KnowledgeReadability.Judge(Page, "退貨條件七天內\uFFFD\uFFFD\uFFFD").ShouldBeNull();

        // 7 and 4: 36%.
        KnowledgeReadability.Judge(Page, "退貨條件七天內\uFFFD\uFFFD\uFFFD\uFFFD").ShouldBe(KnowledgeUnitIssue.GarbledText);
        KnowledgeReadability.Judge(Section, "退貨條件七天內\uFFFD\uFFFD\uFFFD\uFFFD").ShouldBe(KnowledgeUnitIssue.GarbledText);
    }

    [Fact]
    public void Private_use_control_and_broken_surrogate_characters_are_garbage()
    {
        // What a font without ToUnicode typically yields: its own codes in the private-use area.
        KnowledgeReadability.Count("\uE001\uE002\uF8FF\U000F0000").ShouldBe((4, 4));
        KnowledgeReadability.Count("\u0000\u0007\u007F\u0080").ShouldBe((4, 4));
        KnowledgeReadability.Count("\uD800退").ShouldBe((2, 1));
        KnowledgeReadability.Count("退貨 \t\n\u3000條件").ShouldBe((4, 0));

        KnowledgeReadability.Judge(Page, "\uE001\uE002\uE003\uE004\uE005\uE006\uE007\uE008\uE009\uE00A退貨").ShouldBe(KnowledgeUnitIssue.GarbledText);
    }
}
