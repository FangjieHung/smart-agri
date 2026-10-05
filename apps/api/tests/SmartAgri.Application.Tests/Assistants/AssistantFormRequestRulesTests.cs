using System.Text.Json;
using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Application.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

public sealed class AssistantFormRequestRulesTests
{
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly AssistantFormTarget None = new(null, string.Empty);

    [Theory]
    [InlineData("我要填寫客戶資料", true)]
    [InlineData("我 要 回 報 訂單問題", true)]
    [InlineData("請給我表單", true)]
    [InlineData("營業時間是幾點？", false)]
    public void Asks_for_a_form_only_when_the_question_says_so(string question, bool expected) =>
        AssistantFormRequestRules.AsksForForm(question).ShouldBe(expected);

    [Fact]
    public void A_target_must_be_a_usable_connected_database_with_a_purpose()
    {
        var valid = AssistantFormRequestRules.ForUpdate(None, A.ToString(), "  回訪紀錄  ", [A, B]);
        valid.Value.ShouldBe(new AssistantFormTarget(A, "回訪紀錄"));

        AssistantFormRequestRules.ForUpdate(None, A.ToString(), " ", [A]).Failures.Single().Field.ShouldBe("dataWritePurpose");
        AssistantFormRequestRules.ForUpdate(None, A.ToString(), new string('字', 501), [A]).Failures.Single().Field.ShouldBe("dataWritePurpose");
        AssistantFormRequestRules.ForUpdate(None, B.ToString(), "目的", [A]).Failures.Single().Field.ShouldBe("sources");
        AssistantFormRequestRules.ForUpdate(None, "not-a-guid", "目的", [A]).Failures.Single().Field.ShouldBe("sources");
    }

    [Fact]
    public void Absent_values_keep_the_current_ones_and_an_empty_id_clears_the_target()
    {
        var current = new AssistantFormTarget(A, "目的");

        AssistantFormRequestRules.ForUpdate(current, null, "新的目的", [A]).Value.ShouldBe(new AssistantFormTarget(A, "新的目的"));
        AssistantFormRequestRules.ForUpdate(current, B.ToString(), null, [A, B]).Value.ShouldBe(new AssistantFormTarget(B, "目的"));
        AssistantFormRequestRules.ForUpdate(current, string.Empty, "被忽略", [A]).Value.ShouldBe(None);
    }

    [Fact]
    public void The_receipt_text_names_the_recipient_and_number_but_no_answer() =>
        AssistantFormRequestRules.ReceiptText("安心商行（客戶資料庫）", "R-20261003-0000000001")
            .ShouldBe("已送出。資料只會交給 安心商行（客戶資料庫），回執編號 R-20261003-0000000001。");

    // --- #164: the model-chosen form tool ------------------------------------------------------

    private static readonly AssistantFormToolOffer Offer = new(A, "田間異常回報", "記錄田區異常，方便技術人員追蹤。");

    private static ChatResponse Reply(params AIContent[] contents) => new(new ChatMessage(ChatRole.Assistant, [.. contents]));

    private static ChatResponse Call(string name, IDictionary<string, object?>? arguments) =>
        Reply(new FunctionCallContent("call-1", name, arguments));

    [Fact]
    public void The_form_tool_offers_only_the_listed_forms_and_nothing_else()
    {
        var tool = AssistantFormRequestRules.Declaration([Offer]);

        tool.Name.ShouldBe("request_database_form");
        tool.Description.ShouldContain(A.ToString());
        tool.Description.ShouldContain("田間異常回報");
        var schema = tool.JsonSchema;
        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        var properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(property => property.Name).ShouldBe(["databaseId"]);
        properties.GetProperty("databaseId").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ShouldBe([A.ToString()]);
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(["databaseId"]);
        Should.Throw<ArgumentException>(() => AssistantFormRequestRules.Declaration([]));
    }

    [Fact]
    public void Only_the_form_tool_with_an_offered_id_matches()
    {
        IReadOnlyList<AssistantFormToolOffer> offers = [Offer];

        AssistantFormRequestRules.ParseCall(Call("request_database_form", new Dictionary<string, object?> { ["databaseId"] = A.ToString() }), offers)
            .ShouldBe((AssistantFormToolCallMatch.Matched, (Guid?)A));
        AssistantFormRequestRules.ParseCall(
                Call("request_database_form", new Dictionary<string, object?> { ["databaseId"] = JsonSerializer.SerializeToElement(A.ToString()) }), offers)
            .ShouldBe((AssistantFormToolCallMatch.Matched, (Guid?)A));

        AssistantFormRequestRules.ParseCall(Reply(new TextContent("不需要表單")), offers).ShouldBe((AssistantFormToolCallMatch.NoCall, (Guid?)null));
        foreach (var rejected in new[]
        {
            Call("request_database_form", new Dictionary<string, object?> { ["databaseId"] = B.ToString() }),
            Call("request_database_form", new Dictionary<string, object?> { ["databaseId"] = "not-a-guid" }),
            Call("request_database_form", new Dictionary<string, object?> { ["databaseId"] = 42 }),
            Call("request_database_form", new Dictionary<string, object?>()),
            Call("request_database_form", null),
            Call("database_record_count", new Dictionary<string, object?> { ["databaseId"] = A.ToString() }),
        })
        {
            AssistantFormRequestRules.ParseCall(rejected, offers).ShouldBe((AssistantFormToolCallMatch.Rejected, (Guid?)null));
        }
    }

    [Fact]
    public void The_selection_prompt_holds_only_the_role_and_the_question()
    {
        var messages = AssistantFormRequestRules.SelectionPrompt("  我要回報  ");

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe(ChatRole.System);
        messages[0].Text.ShouldContain("request_database_form");
        (messages[1].Role, messages[1].Text).ShouldBe((ChatRole.User, "我要回報"));
    }
}
