using Shouldly;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge.Embeddings;

public class KnowledgeEmbeddingTextTests
{
    [Fact]
    public void Heading_paths_and_worksheet_labels_lead_the_text_and_page_numbers_and_faq_labels_do_not()
    {
        KnowledgeEmbeddingText.For(KnowledgeUnitLocationKind.Section, "2 退換貨 › 2.2 運費", "地區 | 運費")
            .ShouldBe("2 退換貨 › 2.2 運費\n地區 | 運費");
        KnowledgeEmbeddingText.For(KnowledgeUnitLocationKind.Sheet, "工作表『配送時間』第 2–30 列", "地區 | 最快到貨")
            .ShouldBe("工作表『配送時間』第 2–30 列\n地區 | 最快到貨");
        KnowledgeEmbeddingText.For(KnowledgeUnitLocationKind.Page, "第 3 頁", "退款將於五個工作天內退回。")
            .ShouldBe("退款將於五個工作天內退回。");
        KnowledgeEmbeddingText.For(KnowledgeUnitLocationKind.Faq, "FAQ", "問：可以退貨嗎？\n答：七天內可以。")
            .ShouldBe("問：可以退貨嗎？\n答：七天內可以。", "an FAQ's text starts with its question already");
        Should.Throw<ArgumentOutOfRangeException>(() => KnowledgeEmbeddingText.For((KnowledgeUnitLocationKind)99, "x", "y"));
    }
}
