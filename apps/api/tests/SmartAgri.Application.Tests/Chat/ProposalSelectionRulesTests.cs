using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Chat;

namespace SmartAgri.Application.Tests.Chat;

/// <summary><see cref="ProposalSelectionRules"/> (#286): one call offering the form tool and the case tool
/// unchanged; only an offered form or type is accepted; the keyword fallback keeps decision L's order.</summary>
public class ProposalSelectionRulesTests
{
    private static readonly AssistantFormToolOffer Form = new(Guid.CreateVersion7(), "田間異常回報", "記錄病蟲害。");

    private static readonly CaseProposalOffer Repair = new(Guid.CreateVersion7(), "設備報修", "設備故障，需要派人。");

    private static readonly CaseProposalOffer Purchase = new(Guid.CreateVersion7(), "採購申請", "");

    private static IReadOnlyList<AssistantFormToolOffer> Forms => [Form];

    private static IReadOnlyList<CaseProposalOffer> Cases => [Repair];

    [Fact]
    public void Both_tools_are_offered_unchanged_the_form_first()
    {
        var tools = ProposalSelectionRules.Declarations(Forms, Cases).OfType<AIFunctionDeclaration>().ToList();

        // #297 added the third tool, the explicit 「都不符合」 (see the no-match tests below); the first two are unchanged.
        tools.Select(tool => tool.Name).ShouldBe([AssistantFormRequestRules.ToolName, CaseProposalRules.ToolName, CaseProposalRules.NoMatchToolName]);
        tools[0].Description.ShouldBe(AssistantFormRequestRules.Declaration(Forms).Description);
        tools[1].Description.ShouldBe(CaseProposalRules.Declaration(Cases).Description);
        Should.Throw<ArgumentException>(() => ProposalSelectionRules.Declarations(Forms, []));
        Should.Throw<ArgumentException>(() => ProposalSelectionRules.Declarations([], Cases));
    }

    [Fact]
    public void The_prompt_names_both_tools_prefers_the_form_on_a_tie_and_has_the_case_boundaries()
    {
        var prompt = ProposalSelectionRules.SelectionPrompt(" 冷藏庫壞了 ");

        prompt.Count.ShouldBe(2);
        prompt[0].Role.ShouldBe(ChatRole.System);
        prompt[0].Text.ShouldContain(AssistantFormRequestRules.ToolName);
        prompt[0].Text.ShouldContain(CaseProposalRules.ToolName);
        prompt[0].Text.ShouldContain("兩者都同樣符合時，用表單");
        prompt[0].Text.ShouldContain(CaseProposalRules.SelectionBoundaries);
        prompt[1].Text.ShouldBe("冷藏庫壞了");
        Should.Throw<ArgumentException>(() => ProposalSelectionRules.SelectionPrompt(" "));
    }

    [Fact]
    public void The_first_call_decides_and_only_an_offered_form_or_type_is_accepted()
    {
        ProposalSelectionRules.ParseCall(new ChatResponse(new ChatMessage(ChatRole.Assistant, "不需要表單或案件")), Forms, Cases, "問題")
            .ShouldBe(ProposalSelectionCall.NoCall);

        var form = ProposalSelectionRules.ParseCall(Call(AssistantFormRequestRules.ToolName, new() { ["databaseId"] = Form.DatabaseId.ToString() }), Forms, Cases, "問題");
        form.ShouldBe(new ProposalSelectionCall(ProposalSelectionCallMatch.Form, Form.DatabaseId, null));

        var proposed = ProposalSelectionRules.ParseCall(
            Call(CaseProposalRules.ToolName, new() { ["caseTypeId"] = Repair.TypeId.ToString(), ["title"] = " 冷藏庫故障 ", ["description"] = "說明" }), Forms, Cases, "問題");
        proposed.Match.ShouldBe(ProposalSelectionCallMatch.Case);
        proposed.FormDatabaseId.ShouldBeNull();
        proposed.CaseDraft.ShouldBe(new CaseProposalDraft(Repair, "冷藏庫故障", "說明"));

        foreach (var (name, arguments) in new (string, Dictionary<string, object?>)[]
                 {
                     (AssistantFormRequestRules.ToolName, new() { ["databaseId"] = Guid.CreateVersion7().ToString() }),
                     (CaseProposalRules.ToolName, new() { ["caseTypeId"] = Purchase.TypeId.ToString() }),
                     // The form's id given to the case tool, the type's to the form tool.
                     (CaseProposalRules.ToolName, new() { ["caseTypeId"] = Form.DatabaseId.ToString() }),
                     (AssistantFormRequestRules.ToolName, new() { ["databaseId"] = Repair.TypeId.ToString() }),
                     ("database_record_count", new() { ["databaseId"] = Form.DatabaseId.ToString() }),
                 })
        {
            ProposalSelectionRules.ParseCall(Call(name, arguments), Forms, Cases, "問題").ShouldBe(ProposalSelectionCall.Rejected, name);
        }

        // At most one proposal: only the first call counts.
        var both = new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("1", CaseProposalRules.ToolName, new Dictionary<string, object?> { ["caseTypeId"] = Repair.TypeId.ToString(), ["title"] = "t", ["description"] = "" }),
            new FunctionCallContent("2", AssistantFormRequestRules.ToolName, new Dictionary<string, object?> { ["databaseId"] = Form.DatabaseId.ToString() }),
        ]));
        ProposalSelectionRules.ParseCall(both, Forms, Cases, "問題").Match.ShouldBe(ProposalSelectionCallMatch.Case);
    }

    [Fact]
    public void The_combined_call_offers_no_match_for_neither_the_form_nor_a_case()
    {
        var noMatch = ProposalSelectionRules.Declarations(Forms, Cases).OfType<AIFunctionDeclaration>().Last();
        noMatch.Name.ShouldBe(CaseProposalRules.NoMatchToolName);
        noMatch.Description.ShouldBe(ProposalSelectionRules.NoMatchToolDescription);
        noMatch.Description.ShouldContain(AssistantFormRequestRules.ToolName);
        noMatch.Description.ShouldContain(CaseProposalRules.ToolName);
        noMatch.JsonSchema.GetProperty("properties").EnumerateObject().ShouldBeEmpty();
        noMatch.JsonSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();

        var system = ProposalSelectionRules.SelectionPrompt("葉子黃了")[0].Text;
        system.ShouldContain($"請呼叫 {CaseProposalRules.NoMatchToolName}，表單與案件都不提議");
        system.ShouldContain("每次只呼叫其中一個");
    }

    [Fact]
    public void Choosing_no_match_in_the_combined_call_is_neither_the_form_nor_a_case()
    {
        ProposalSelectionRules.ParseCall(Call(CaseProposalRules.NoMatchToolName, new()), Forms, Cases, "葉子黃了")
            .ShouldBe(ProposalSelectionCall.NoMatch);
        ProposalSelectionCall.NoMatch.ShouldBe(new ProposalSelectionCall(ProposalSelectionCallMatch.NoMatch, null, null));
        // Whatever it is sent with, and only the first call counts.
        ProposalSelectionRules.ParseCall(Call(CaseProposalRules.NoMatchToolName, new() { ["databaseId"] = Form.DatabaseId.ToString() }), Forms, Cases, "問題")
            .ShouldBe(ProposalSelectionCall.NoMatch);
        var noMatchFirst = new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("1", CaseProposalRules.NoMatchToolName, new Dictionary<string, object?>()),
            new FunctionCallContent("2", AssistantFormRequestRules.ToolName, new Dictionary<string, object?> { ["databaseId"] = Form.DatabaseId.ToString() }),
        ]));
        ProposalSelectionRules.ParseCall(noMatchFirst, Forms, Cases, "問題").ShouldBe(ProposalSelectionCall.NoMatch);
    }

    [Fact]
    public void The_keyword_fallback_checks_the_form_gate_then_the_case_rule()
    {
        ProposalSelectionRules.KeywordFallback("我要回報冷藏庫壞了要報修", Forms, Cases)
            .ShouldBe(new ProposalSelectionCall(ProposalSelectionCallMatch.Form, Form.DatabaseId, null));

        var proposed = ProposalSelectionRules.KeywordFallback("冷藏庫壞了要報修", Forms, Cases);
        proposed.Match.ShouldBe(ProposalSelectionCallMatch.Case);
        proposed.CaseDraft!.Offer.ShouldBe(Repair);
        proposed.CaseDraft.Title.ShouldBe("冷藏庫壞了要報修");

        ProposalSelectionRules.KeywordFallback("番茄葉子黃了是什麼原因？", Forms, Cases).ShouldBe(ProposalSelectionCall.NoCall);
    }

    private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", name, arguments)]));
}
