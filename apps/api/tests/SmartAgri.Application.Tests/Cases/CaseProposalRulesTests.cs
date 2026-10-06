using System.Text.Json;
using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Application.Cases;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Application.Tests.Cases;

/// <summary><see cref="CaseProposalRules"/>: when a conversation turn gets a case proposal and its draft
/// (M7-9 #254; decision T for keywords, the server's checks of a model's call).</summary>
public class CaseProposalRulesTests
{
    private static readonly CaseProposalOffer Repair = new(Guid.CreateVersion7(), "設備故障報修", "冷藏、灌溉等設備故障，需要派人處理。");

    private static readonly CaseProposalOffer Purchase = new(Guid.CreateVersion7(), "採購申請", "");

    private static readonly CaseProposalOffer ColdRepair = new(Guid.CreateVersion7(), "冷藏設備故障報修", "");

    [Theory]
    [InlineData("冷藏庫壞了要報修", true)]
    [InlineData("幫我 安排 師傅", true)]
    [InlineData("我想申請 退貨", true)]
    [InlineData("冷藏庫溫度降不下來", false)]
    [InlineData("報 修", true)]
    public void The_case_words_ignore_whitespace(string question, bool expected) =>
        CaseProposalRules.AsksForCase(question).ShouldBe(expected);

    [Fact]
    public void Keywords_need_a_case_word_and_a_named_type_unless_only_one_type_is_offered()
    {
        // One type: a case word is enough.
        CaseProposalRules.KeywordProposal("冷藏庫壞了要報修", [Repair])!.Offer.ShouldBe(Repair);
        CaseProposalRules.KeywordProposal("冷藏庫溫度降不下來", [Repair]).ShouldBeNull();
        CaseProposalRules.KeywordProposal("冷藏庫壞了要報修", []).ShouldBeNull();

        // Several: only one the question names; the longest name wins.
        CaseProposalRules.KeywordProposal("冷藏庫壞了要報修", [Repair, Purchase]).ShouldBeNull();
        CaseProposalRules.KeywordProposal("幫我送一張採購申請", [Repair, Purchase])!.Offer.ShouldBe(Purchase);
        CaseProposalRules.KeywordProposal("請安排冷藏設備故障報修", [Repair, ColdRepair, Purchase])!.Offer.ShouldBe(ColdRepair);
        // No case word: nothing, even with a type's subject in it.
        CaseProposalRules.KeywordProposal("冷藏設備多久保養一次", [Repair, ColdRepair, Purchase]).ShouldBeNull();
        // A question about a type matches too (its name has a case word): the asker still confirms, and
        // M7-12 measures how often this happens.
        CaseProposalRules.KeywordProposal("設備故障報修的流程是什麼", [Repair, Purchase])!.Offer.ShouldBe(Repair);
    }

    [Fact]
    public void A_keyword_draft_has_the_questions_first_120_characters_and_no_description()
    {
        var draft = CaseProposalRules.KeywordProposal("  冷藏庫\n壞了   要報修  ", [Repair])!;
        (draft.Title, draft.Description).ShouldBe(("冷藏庫 壞了 要報修", string.Empty));

        var longQuestion = "要報修" + new string('冷', 200);
        CaseProposalRules.TitleFromQuestion(longQuestion).ShouldBe(longQuestion[..Case.TitleMaxLength]);

        // Never splits a surrogate pair (an emoji at the cut).
        var emoji = new string('冷', Case.TitleMaxLength - 1) + "😀";
        var cut = CaseProposalRules.TitleFromQuestion(emoji + "後面");
        cut.Length.ShouldBe(Case.TitleMaxLength - 1);
        char.IsHighSurrogate(cut[^1]).ShouldBeFalse();
    }

    [Fact]
    public void The_text_names_the_type() =>
        CaseProposalRules.ProposalText("設備報修").ShouldBe("這件事可以開一件「設備報修」案件，請確認內容。");

    [Fact]
    public void The_tool_offers_only_the_types_ids_with_their_names_and_descriptions()
    {
        var declaration = CaseProposalRules.Declaration([Repair, Purchase]);
        declaration.Name.ShouldBe(CaseProposalRules.ToolName);
        declaration.Description.ShouldContain("「設備故障報修」（冷藏、灌溉等設備故障，需要派人處理。）");
        declaration.Description.ShouldContain($"{Purchase.TypeId}＝「採購申請」");
        var properties = declaration.JsonSchema.GetProperty("properties");
        properties.GetProperty(CaseProposalRules.TypeIdParameter).GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([Repair.TypeId.ToString(), Purchase.TypeId.ToString()]);
        declaration.JsonSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        Should.Throw<ArgumentException>(() => CaseProposalRules.Declaration([]));
    }

    [Fact]
    public void A_call_must_name_an_offered_type_and_its_draft_is_truncated()
    {
        IReadOnlyList<CaseProposalOffer> offers = [Repair];
        CaseProposalRules.ParseCall(new ChatResponse(new ChatMessage(ChatRole.Assistant, "不需要開案")), offers, "問題").Match
            .ShouldBe(CaseProposalCallMatch.NoCall);

        foreach (var (name, typeId) in new[]
                 {
                     ("propose_something", Repair.TypeId.ToString()),
                     (CaseProposalRules.ToolName, Purchase.TypeId.ToString()),
                     (CaseProposalRules.ToolName, Guid.CreateVersion7().ToString()),
                     (CaseProposalRules.ToolName, "not-a-guid"),
                 })
        {
            var rejected = CaseProposalRules.ParseCall(Call(name, typeId, "標題", "說明"), offers, "問題");
            rejected.ShouldBe((CaseProposalCallMatch.Rejected, (CaseProposalDraft?)null), $"{name} {typeId}");
        }

        var (match, draft) = CaseProposalRules.ParseCall(
            Call(CaseProposalRules.ToolName, Repair.TypeId.ToString(), "  " + new string('題', 200), new string('說', 2000)), offers, "問題");
        match.ShouldBe(CaseProposalCallMatch.Matched);
        draft!.Offer.ShouldBe(Repair);
        draft.Title.Length.ShouldBe(Case.TitleMaxLength);
        draft.Description.Length.ShouldBe(CaseProposalRules.DraftDescriptionMaxLength);

        // An empty title is the question's.
        CaseProposalRules.ParseCall(Call(CaseProposalRules.ToolName, Repair.TypeId.ToString(), " ", ""), offers, "冷藏庫要報修").Draft!.Title
            .ShouldBe("冷藏庫要報修");
    }

    private static ChatResponse Call(string name, string typeId, string title, string description) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", name, new Dictionary<string, object?>
        {
            [CaseProposalRules.TypeIdParameter] = JsonSerializer.SerializeToElement(typeId),
            [CaseProposalRules.TitleParameter] = JsonSerializer.SerializeToElement(title),
            [CaseProposalRules.DescriptionParameter] = description,
        })]));
}
