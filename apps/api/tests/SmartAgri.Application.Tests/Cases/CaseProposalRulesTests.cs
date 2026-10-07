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
    public void The_selection_prompt_has_286s_boundaries()
    {
        var prompt = CaseProposalRules.SelectionPrompt("  冷藏庫壞了  ");
        prompt.Count.ShouldBe(2);
        prompt[0].Text.ShouldContain(CaseProposalRules.SelectionBoundaries);
        foreach (var boundary in new[] { "進度", "取消已經送出的申請", "統計數字", "只是抱怨或陳述狀況", "作物病蟲害", "不要套用最接近的類型" })
        {
            CaseProposalRules.SelectionBoundaries.ShouldContain(boundary);
        }

        prompt[1].Text.ShouldBe("冷藏庫壞了");
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

    [Fact]
    public void The_case_call_also_offers_an_explicit_no_match_tool_with_no_parameters()
    {
        // #297: propose_case unchanged, then no_matching_type (an object with no properties).
        var tools = CaseProposalRules.Declarations([Repair, Purchase]).OfType<AIFunctionDeclaration>().ToList();
        tools.Select(tool => tool.Name).ShouldBe([CaseProposalRules.ToolName, CaseProposalRules.NoMatchToolName]);
        tools[0].Description.ShouldBe(CaseProposalRules.Declaration([Repair, Purchase]).Description);
        tools[0].JsonSchema.GetRawText().ShouldBe(CaseProposalRules.Declaration([Repair, Purchase]).JsonSchema.GetRawText());
        tools[1].Description.ShouldBe(CaseProposalRules.NoMatchToolDescription);
        tools[1].JsonSchema.GetProperty("properties").EnumerateObject().ShouldBeEmpty();
        tools[1].JsonSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        Should.Throw<ArgumentException>(() => CaseProposalRules.Declarations([]));
        Should.Throw<ArgumentException>(() => CaseProposalRules.NoMatchDeclaration(" "));

        // The prompt tells the model to call it, not propose_case, when no type's description fits.
        var system = CaseProposalRules.SelectionPrompt("冷藏庫壞了")[0].Text;
        system.ShouldContain($"請呼叫 {CaseProposalRules.NoMatchToolName}，不要呼叫 propose_case");
        system.ShouldContain("說明寫明不包括的事");
    }

    [Fact]
    public void Choosing_no_match_is_no_proposal_whatever_its_arguments_and_only_the_first_call_counts()
    {
        IReadOnlyList<CaseProposalOffer> offers = [Repair];
        CaseProposalRules.ParseCall(NoMatch(), offers, "葉子黃了").ShouldBe((CaseProposalCallMatch.NoMatch, (CaseProposalDraft?)null));
        // Arguments it does not define (it has none) change nothing: it proposes nothing either way.
        CaseProposalRules.ParseCall(NoMatch(new Dictionary<string, object?> { [CaseProposalRules.TypeIdParameter] = Repair.TypeId.ToString() }), offers, "問題")
            .ShouldBe((CaseProposalCallMatch.NoMatch, (CaseProposalDraft?)null));

        // The first call decides: no_matching_type then propose_case is still no proposal, and the other way round a proposal.
        var noMatchFirst = new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("1", CaseProposalRules.NoMatchToolName, new Dictionary<string, object?>()),
            new FunctionCallContent("2", CaseProposalRules.ToolName, new Dictionary<string, object?> { [CaseProposalRules.TypeIdParameter] = Repair.TypeId.ToString() }),
        ]));
        CaseProposalRules.ParseCall(noMatchFirst, offers, "問題").Match.ShouldBe(CaseProposalCallMatch.NoMatch);
        var proposalFirst = new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("1", CaseProposalRules.ToolName, new Dictionary<string, object?> { [CaseProposalRules.TypeIdParameter] = Repair.TypeId.ToString() }),
            new FunctionCallContent("2", CaseProposalRules.NoMatchToolName, new Dictionary<string, object?>()),
        ]));
        CaseProposalRules.ParseCall(proposalFirst, offers, "問題").Match.ShouldBe(CaseProposalCallMatch.Matched);

        // A near-miss name is another tool: rejected, as before.
        CaseProposalRules.ParseCall(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("1", "no_matching_types", new Dictionary<string, object?>())])), offers, "問題").Match
            .ShouldBe(CaseProposalCallMatch.Rejected);
    }

    private static ChatResponse NoMatch(Dictionary<string, object?>? arguments = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", CaseProposalRules.NoMatchToolName, arguments ?? new Dictionary<string, object?>())]));

    private static ChatResponse Call(string name, string typeId, string title, string description) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", name, new Dictionary<string, object?>
        {
            [CaseProposalRules.TypeIdParameter] = JsonSerializer.SerializeToElement(typeId),
            [CaseProposalRules.TitleParameter] = JsonSerializer.SerializeToElement(title),
            [CaseProposalRules.DescriptionParameter] = description,
        })]));
}
