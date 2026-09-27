using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

public class KnowledgeFaqProcessingTests
{
    [Fact]
    public void An_entry_is_one_ready_unit_and_one_chunk_located_faq_holding_question_and_answer()
    {
        var entry = new KnowledgeFaqEntry("收到商品幾天內可退貨？", "收到商品後七天內可申請退貨。\n請保留發票。");

        var processed = KnowledgeFaqProcessing.Process(entry);

        (processed.Status, processed.Issue).ShouldBe((KnowledgeDocumentStatus.Ready, (string?)null));
        var unit = processed.Units.ShouldHaveSingleItem();
        (unit.Ordinal, unit.Kind, unit.LocationLabel, unit.Readable, unit.IssueCode)
            .ShouldBe((0, KnowledgeUnitLocationKind.Faq, "FAQ", true, (KnowledgeUnitIssue?)null));
        unit.Text.ShouldBe("問：收到商品幾天內可退貨？\n答：收到商品後七天內可申請退貨。\n請保留發票。");
        var chunk = unit.Chunks.ShouldHaveSingleItem();
        (chunk.LocationLabel, chunk.Text).ShouldBe(("FAQ", unit.Text));
    }

    [Fact]
    public void A_long_answer_is_still_one_chunk()
    {
        var entry = new KnowledgeFaqEntry("運費怎麼算？", new string('費', KnowledgeFaqEntry.AnswerMaxLength));

        var chunk = KnowledgeFaqProcessing.Process(entry).Units.ShouldHaveSingleItem().Chunks.ShouldHaveSingleItem();

        chunk.Text.Length.ShouldBeGreaterThan(ChunkingOptions.Default.MaxCharacters);
        chunk.Text.ShouldEndWith(entry.Answer);
    }
}
