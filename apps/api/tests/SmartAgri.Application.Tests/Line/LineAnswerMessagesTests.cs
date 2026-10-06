using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Line;

/// <summary>The LINE messages of an answer (M5b plan §3 G, #232): plain text with readable citation
/// numbers, cut at LINE's 5,000 UTF-16 code units, and the sources as a Flex carousel within LINE's
/// size limits.</summary>
public class LineAnswerMessagesTests
{
    private static readonly Guid PolicyDocument = Guid.NewGuid();
    private static readonly Guid ShippingDocument = Guid.NewGuid();

    // --- The answer text ------------------------------------------------------------------------------

    [Fact]
    public void A_company_data_answer_is_a_text_with_readable_sources_and_a_carousel_of_the_cited_documents()
    {
        var reply = CompanyData(
            "收到商品後七天內可以退貨 [1]，運費由買家負擔 [2][1]。",
            Citation(1, PolicyDocument, "退貨政策.md", "收到商品後七天內可申請退貨。"),
            Citation(2, ShippingDocument, "運費說明.pdf", "退貨運費由買家負擔。"));

        var messages = LineAnswerMessages.Build(reply, showCitations: true);

        messages.Count.ShouldBe(2);
        messages[0]["type"]!.GetValue<string>().ShouldBe("text");
        messages[0]["text"]!.GetValue<string>().ShouldBe("收到商品後七天內可以退貨（來源 1），運費由買家負擔（來源 2、1）。");
        var flex = messages[1];
        flex["type"]!.GetValue<string>().ShouldBe("flex");
        flex["altText"]!.GetValue<string>().ShouldBe("參考來源：退貨政策.md、運費說明.pdf");
        var bubbles = flex["contents"]!["contents"]!.AsArray();
        flex["contents"]!["type"]!.GetValue<string>().ShouldBe("carousel");
        bubbles.Count.ShouldBe(2);
        Texts(bubbles[0]!).ShouldBe(["來源 1", "客服知識庫", "退貨政策.md", "收到商品後七天內可申請退貨。"]);
        Texts(bubbles[1]!).ShouldBe(["來源 2", "客服知識庫", "運費說明.pdf", "退貨運費由買家負擔。"]);
    }

    [Fact]
    public void Several_citations_of_one_document_share_its_bubble_and_at_most_five_bubbles_are_sent()
    {
        var citations = new List<GroundedCitation>
        {
            Citation(1, PolicyDocument, "退貨政策.md", "第一段。"),
            Citation(2, PolicyDocument, "退貨政策.md", "第二段。"),
        };
        for (var ordinal = 3; ordinal <= 8; ordinal++)
        {
            citations.Add(Citation(ordinal, Guid.NewGuid(), $"文件{ordinal}.md", $"摘錄{ordinal}。"));
        }

        var bubbles = LineAnswerMessages.Build(CompanyData("答案 [1][2][3]。", [.. citations]), showCitations: true)[1]["contents"]!["contents"]!.AsArray();

        bubbles.Count.ShouldBe(LineAnswerMessages.MaxBubbles);
        Texts(bubbles[0]!).ShouldBe(["來源 1、2", "客服知識庫", "退貨政策.md", "第一段。"]);
        Texts(bubbles[4]!)[2].ShouldBe("文件6.md");
    }

    [Fact]
    public void An_assistant_that_hides_its_sources_sends_only_the_text_without_source_numbers()
    {
        var reply = CompanyData("七天內可以退貨 [1]。", Citation(1, PolicyDocument, "退貨政策.md", "七天內可退貨。"));

        var messages = LineAnswerMessages.Build(reply, showCitations: false);

        var text = messages.ShouldHaveSingleItem();
        text["text"]!.GetValue<string>().ShouldBe("七天內可以退貨。");
        text.ToJsonString().ShouldNotContain("退貨政策");
    }

    [Fact]
    public void A_no_result_reply_is_the_refusal_and_the_visitors_next_step_never_the_managers()
    {
        var reply = new GroundedReply(
            GroundedReplyKind.NoResult, "目前的資料中找不到這個問題的答案。請來電 02-1234-5678。", [], null,
            GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold);

        var messages = LineAnswerMessages.Build(reply, showCitations: true);

        var text = messages.ShouldHaveSingleItem()["text"]!.GetValue<string>();
        text.ShouldBe("目前的資料中找不到這個問題的答案。請來電 02-1234-5678。\n\n" + GroundedReply.VisitorNoResultNextSteps.Single());
        text.ShouldNotContain("管理者");
    }

    [Fact]
    public void A_general_knowledge_reply_carries_its_notice_and_no_markdown()
    {
        var reply = new GroundedReply(
            GroundedReplyKind.GeneralKnowledge, "## 小提醒\n番茄**適合**在 `春季` 種植。", [], GroundedReply.GeneralKnowledgeNotice, [], null);

        var text = LineAnswerMessages.Build(reply, showCitations: true).ShouldHaveSingleItem()["text"]!.GetValue<string>();

        text.ShouldBe("小提醒\n番茄適合在 春季 種植。\n\n" + GroundedReply.GeneralKnowledgeNotice);
    }

    // --- 5,000 UTF-16 code units ------------------------------------------------------------------------

    [Theory]
    [InlineData(4_999)]
    [InlineData(5_000)]
    public void A_text_up_to_5000_units_is_sent_whole(int length)
    {
        var answer = new string('答', length);

        var text = LineAnswerMessages.Build(CompanyData(answer), showCitations: false)[0]["text"]!.GetValue<string>();

        text.ShouldBe(answer);
    }

    [Fact]
    public void One_unit_over_the_limit_loses_two_characters_to_the_ellipsis()
    {
        var text = LineAnswerMessages.Build(CompanyData(new string('答', 5_001)), showCitations: false)[0]["text"]!.GetValue<string>();

        text.ShouldBe(new string('答', 4_999) + "…");
    }

    [Theory]
    [InlineData(5_001)]
    [InlineData(12_345)]
    public void A_longer_text_is_cut_to_exactly_5000_units_ending_with_an_ellipsis(int length)
    {
        var answer = CompanyData("[1]" + new string('答', length - 3), Citation(1, PolicyDocument, "退貨政策.md", "摘錄。"));

        // Shown as 「（來源 1）」, the text grows past the limit; the sources still follow.
        var messages = LineAnswerMessages.Build(answer, showCitations: true);
        var text = messages[0]["text"]!.GetValue<string>();
        messages.Count.ShouldBe(2);

        text.Length.ShouldBe(LineAnswerMessages.TextMaxLength);
        text.ShouldStartWith("（來源 1）答");
        text.ShouldEndWith("答…");
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // 4,998 units, then an emoji (2 units) across the 4,999th/5,000th: it does not fit with the ellipsis.
        var text = new string('答', 4_998) + "🍎🍎";

        var cut = LineAnswerMessages.Truncate(text, LineAnswerMessages.TextMaxLength);

        cut.ShouldBe(new string('答', 4_998) + "…");
        cut.Length.ShouldBe(4_999);
        char.IsHighSurrogate(cut[^2]).ShouldBeFalse();
    }

    // --- Flex sizes ------------------------------------------------------------------------------------

    [Fact]
    public void Worst_case_sources_stay_within_lines_bubble_carousel_and_alt_text_limits()
    {
        // Five documents with long names, a long knowledge base name and 200-character excerpts whose every
        // character is escaped on the wire (\uXXXX, 6 bytes).
        var citations = Enumerable.Range(1, 5)
            .Select(ordinal => Citation(
                ordinal, Guid.NewGuid(), new string('檔', 400) + ordinal, GroundedCitation.ExcerptOf(new string('摘', 600)), knowledgeBase: new string('庫', 400)))
            .ToArray();

        var flex = LineAnswerMessages.Build(CompanyData("答 [1][2][3][4][5]", citations), showCitations: true)[1];

        var bubbles = flex["contents"]!["contents"]!.AsArray();
        bubbles.Count.ShouldBe(5);
        bubbles.ShouldAllBe(bubble => Bytes(bubble!) <= LineAnswerMessages.BubbleMaxBytes);
        Bytes(flex["contents"]!).ShouldBeLessThanOrEqualTo(LineAnswerMessages.CarouselMaxBytes);
        flex["altText"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(LineAnswerMessages.AltTextMaxLength);
        // Names are shortened to 100 characters, excerpts kept at 200 (+ the ellipsis).
        Texts(bubbles[0]!)[2].ShouldBe(new string('檔', LineAnswerMessages.NameMaxLength) + "…");
        Texts(bubbles[0]!)[3].ShouldBe(new string('摘', LineAnswerMessages.ExcerptMaxLength) + "…");
    }

    [Fact]
    public void An_alt_text_over_1500_characters_is_cut_with_an_ellipsis()
    {
        var citations = Enumerable.Range(1, 5)
            .Select(ordinal => Citation(ordinal, Guid.NewGuid(), new string((char)('甲' + ordinal), 400), "摘錄。"))
            .ToArray();

        var altText = LineAnswerMessages.Build(CompanyData("答 [1]", citations), showCitations: true)[1]["altText"]!.GetValue<string>();

        // "參考來源：" + 5 × (100 + "…") + 4 × "、" = 514: within the limit, so not cut.
        altText.Length.ShouldBe(5 + (5 * 101) + 4);
        LineAnswerMessages.Truncate("參考來源：" + new string('名', 2_000), LineAnswerMessages.AltTextMaxLength).Length
            .ShouldBe(LineAnswerMessages.AltTextMaxLength);
    }

    [Fact]
    public void Over_the_bubble_limit_the_excerpt_is_shortened_first()
    {
        var reply = CompanyData("答 [1]", Citation(1, PolicyDocument, "退貨政策.md", GroundedCitation.ExcerptOf(new string('摘', 300))));
        var full = Bytes(LineAnswerMessages.Build(reply, showCitations: true)[1]["contents"]!["contents"]![0]!);

        // One byte short of the full bubble: the 200-character excerpt no longer fits, 120 does.
        var flex = LineAnswerMessages.Build(reply, showCitations: true, bubbleMaxBytes: full - 1, carouselMaxBytes: LineAnswerMessages.CarouselMaxBytes)[1];

        var bubble = flex["contents"]!["contents"]![0]!;
        Texts(bubble)[3].ShouldBe(new string('摘', 120) + "…");
        Bytes(bubble).ShouldBeLessThanOrEqualTo(full - 1);
    }

    [Fact]
    public void Over_the_carousel_limit_excerpts_go_then_bubbles_are_dropped_from_the_end()
    {
        var citations = Enumerable.Range(1, 5)
            .Select(ordinal => Citation(ordinal, Guid.NewGuid(), $"文件{ordinal}.md", GroundedCitation.ExcerptOf(new string('摘', 300))))
            .ToArray();
        var reply = CompanyData("答 [1][2][3][4][5]", citations);
        var withoutExcerpts = Enumerable.Range(1, 5)
            .Select(ordinal => Citation(ordinal, citations[ordinal - 1].DocumentId, $"文件{ordinal}.md", string.Empty))
            .ToArray();
        var bare = Bytes(LineAnswerMessages.Build(CompanyData("答", withoutExcerpts), showCitations: true)[1]["contents"]!);

        // Exactly the five bubbles without excerpts: they all stay, with no excerpt.
        var exact = LineAnswerMessages.Build(reply, showCitations: true, LineAnswerMessages.BubbleMaxBytes, carouselMaxBytes: bare)[1];
        var kept = exact["contents"]!["contents"]!.AsArray();
        kept.Count.ShouldBe(5);
        kept.ShouldAllBe(bubble => Texts(bubble!).Count == 3);
        Bytes(exact["contents"]!).ShouldBe(bare);

        // One byte less: the last bubble goes, and the alt text names only the documents still shown.
        var smaller = LineAnswerMessages.Build(reply, showCitations: true, LineAnswerMessages.BubbleMaxBytes, carouselMaxBytes: bare - 1)[1];
        smaller["contents"]!["contents"]!.AsArray().Count.ShouldBe(4);
        Bytes(smaller["contents"]!).ShouldBeLessThanOrEqualTo(bare - 1);
        smaller["altText"]!.GetValue<string>().ShouldBe("參考來源：文件1.md、文件2.md、文件3.md、文件4.md");
    }

    [Fact]
    public void Sources_that_cannot_fit_at_all_are_left_out_and_the_answer_still_goes()
    {
        var reply = CompanyData("答 [1]", Citation(1, PolicyDocument, "退貨政策.md", "摘錄。"));

        var messages = LineAnswerMessages.Build(reply, showCitations: true, bubbleMaxBytes: 10, carouselMaxBytes: 10);

        messages.ShouldHaveSingleItem()["text"]!.GetValue<string>().ShouldBe("答（來源 1）");
    }

    // --- Helpers ---------------------------------------------------------------------------------------

    private static GroundedReply CompanyData(string text, params GroundedCitation[] citations) =>
        new(GroundedReplyKind.CompanyData, text, citations, null, [], null);

    private static GroundedReply General(string text) =>
        new(GroundedReplyKind.GeneralKnowledge, text, [], GroundedReply.GeneralKnowledgeNotice, [], null);

    private static GroundedCitation Citation(int ordinal, Guid documentId, string documentName, string excerpt, string knowledgeBase = "客服知識庫") =>
        new(ordinal, Guid.NewGuid(), Guid.NewGuid(), knowledgeBase, documentId, documentName, Guid.NewGuid(), 1, "第 1 頁", excerpt, excerpt, 0.9, null);

    private static List<string> Texts(JsonNode bubble) =>
        [.. bubble["body"]!["contents"]!.AsArray().Select(node => node!["text"]!.GetValue<string>())];

    private static int Bytes(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString());
}
