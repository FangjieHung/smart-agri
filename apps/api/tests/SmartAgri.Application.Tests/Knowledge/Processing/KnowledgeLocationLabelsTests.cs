using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

public class KnowledgeLocationLabelsTests
{
    [Fact]
    public void Pages_sections_and_sheets_are_labelled_as_the_plan_says()
    {
        KnowledgeLocationLabels.Page(3).ShouldBe("第 3 頁");
        KnowledgeLocationLabels.Section(["2 退換貨", "2.1 退貨條件"]).ShouldBe("2 退換貨 › 2.1 退貨條件");
        KnowledgeLocationLabels.Section([]).ShouldBe("文件開頭");
        KnowledgeLocationLabels.Sheet("配送時間").ShouldBe("工作表『配送時間』");
        KnowledgeLocationLabels.SheetRows("配送時間", 2, 30).ShouldBe("工作表『配送時間』第 2–30 列");
        KnowledgeLocationLabels.SheetRows("配送時間", 5, 5).ShouldBe("工作表『配送時間』第 5 列");
    }

    [Fact]
    public void Headings_are_cleaned_to_one_line_and_long_ones_are_cut()
    {
        KnowledgeLocationLabels.Section(["  2.1\n退貨條件  ", " "]).ShouldBe("2.1 退貨條件");

        var label = KnowledgeLocationLabels.Section([new string('章', 200), new string('節', 200), new string('段', 200)]);

        label.Length.ShouldBeLessThanOrEqualTo(KnowledgeExtractedUnit.LocationLabelMaxLength);
        label.Split(KnowledgeLocationLabels.HeadingSeparator).ShouldAllBe(heading => heading.Length == 80 && heading.EndsWith('…'));
    }

    [Fact]
    public void Page_lists_are_ascending_with_consecutive_pages_as_ranges()
    {
        KnowledgeLocationLabels.PageList([9, 2, 5, 6, 7, 2]).ShouldBe("2、5–7、9");
        KnowledgeLocationLabels.PageList([4]).ShouldBe("4");
    }
}
