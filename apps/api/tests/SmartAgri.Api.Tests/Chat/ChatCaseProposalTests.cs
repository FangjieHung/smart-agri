using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Chat;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using Case = SmartAgri.Domain.Cases.Case;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Chat;

/// <summary>
/// The assistant proposing a case (M7 plan §3 H, §5 Slice M7-9; issue #254) against real PostgreSQL
/// with the <c>Fake</c> models: none without proposable types; the keyword rule (decision T) and the
/// model's selection (one counted <c>case-proposal</c> call, keyword fallback); precedence after the
/// database query and the form (decision L), one proposal per reply; never for an external customer or
/// a conversation that is not kept; the snapshot re-checked on read; confirming creates exactly the
/// confirmed title and description linked to the thread, once (<c>409</c> after), and 「不用了」 creates
/// nothing; the assistant settings' case-type list (decision U).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class ChatCaseProposalTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Chat-Case-Proposal-Pass-1!";
    private const string AssistantsPath = "/api/v1/assistants";
    private const string RepairType = "設備故障報修";
    private const string PurchaseType = "採購申請";
    private const string RepairQuestion = "冷藏庫溫度降不下來，需要報修";

    private readonly AuthHostFixture _host;

    public ChatCaseProposalTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Keyword mode (decision T) ----------------------------------------------------------------

    [Fact]
    public async Task There_is_no_proposal_until_the_owner_adds_a_type_then_the_keyword_rule_proposes_it()
    {
        var setup = await CreateSetupAsync(_host.Factory);

        Kind(await RunAsync(setup.Member, setup.AssistantId, RepairQuestion)).ShouldNotBe("case-proposal");

        var settings = await AddTypeAsync(setup, setup.RepairTypeId);
        settings.GetProperty("caseTypeIds").EnumerateArray().Select(id => id.GetGuid()).ShouldBe([setup.RepairTypeId]);

        var run = await RunAsync(setup.Member, setup.AssistantId, RepairQuestion);
        var reply = run.Reply!.Value;
        OpenApiContract.AssertKeysMatchSchema(reply, "ChatMessageView");
        reply.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("case-proposal");
        reply.GetProperty("reply").GetProperty("text").GetString().ShouldBe(CaseProposalRules.ProposalText(RepairType));
        reply.GetProperty("reply").GetProperty("form").ValueKind.ShouldBe(JsonValueKind.Null);
        var proposal = reply.GetProperty("reply").GetProperty("caseProposal");
        OpenApiContract.AssertKeysMatchSchema(proposal, "ChatCaseProposalView");
        proposal.GetProperty("typeId").GetGuid().ShouldBe(setup.RepairTypeId);
        proposal.GetProperty("typeName").GetString().ShouldBe(RepairType);
        proposal.GetProperty("title").GetString().ShouldBe(RepairQuestion);
        proposal.GetProperty("description").GetString().ShouldBe(string.Empty);
        proposal.GetProperty("status").GetString().ShouldBe("proposed");
        proposal.GetProperty("available").GetBoolean().ShouldBeTrue();
        proposal.GetProperty("group").GetProperty("name").GetString().ShouldBe("設備組");
        proposal.GetProperty("dueHours").GetInt32().ShouldBe(72);
        proposal.GetProperty("caseId").ValueKind.ShouldBe(JsonValueKind.Null);

        // Read back exactly as streamed (the same thread as the first question, so the fourth message).
        var chat = await ChatAsync(setup.Member, setup.AssistantId, run.ThreadId!.Value);
        JsonNode.DeepEquals(JsonNode.Parse(chat.GetProperty("messages")[3].GetRawText()), JsonNode.Parse(reply.GetRawText())).ShouldBeTrue();

        // No case word: an ordinary answer. Keyword mode never calls a model for this.
        Kind(await RunAsync(setup.Member, setup.AssistantId, "冷藏庫溫度降不下來", run.ThreadId.ToString())).ShouldNotBe("case-proposal");
        (await InvocationsAsync(setup.Org, ModelInvocationPurpose.CaseProposal)).ShouldBeEmpty();
        (await CaseCountAsync(setup.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task With_several_types_the_question_must_name_one_and_the_title_is_its_first_120_characters()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        await AddTypeAsync(setup, setup.RepairTypeId);
        await AddTypeAsync(setup, setup.PurchaseTypeId);

        // A case word but no type name: no proposal with two types to choose from.
        Kind(await RunAsync(setup.Member, setup.AssistantId, RepairQuestion)).ShouldNotBe("case-proposal");

        var named = await RunAsync(setup.Member, setup.AssistantId, "幫我送一張採購申請，要買兩台除濕機");
        Kind(named).ShouldBe("case-proposal");
        Proposal(named).GetProperty("typeId").GetGuid().ShouldBe(setup.PurchaseTypeId);

        var longQuestion = "請安排設備故障報修：" + new string('冷', 200);
        var truncated = Proposal(await RunAsync(setup.Member, setup.AssistantId, longQuestion));
        truncated.GetProperty("typeId").GetGuid().ShouldBe(setup.RepairTypeId);
        truncated.GetProperty("title").GetString().ShouldBe(longQuestion[..Case.TitleMaxLength]);
    }

    // --- Precedence (decision L) ------------------------------------------------------------------

    [Fact]
    public async Task A_statistics_question_is_a_database_query_a_form_request_comes_first_and_one_reply_has_one_proposal()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        await AddTypeAsync(setup, setup.RepairTypeId);
        var databaseId = await CreateDatabaseAsync(setup.Admin, "報修紀錄資料庫");
        (await setup.Admin.Spa.PutAsync($"{AssistantsPath}/{setup.AssistantId}/sources/database/{databaseId}", setup.Admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var target = await setup.Admin.Spa.PatchAsync($"{AssistantsPath}/{setup.AssistantId}/settings", setup.Admin.Token, new
        {
            rules = new { dataWriteDatabaseId = databaseId.ToString(), dataWritePurpose = "記錄設備問題，方便追蹤。" },
        });
        target.StatusCode.ShouldBe(HttpStatusCode.OK, await target.Content.ReadAsStringAsync(CancellationToken));

        Kind(await RunAsync(setup.Member, setup.AssistantId, "本月一共有幾筆報修？")).ShouldBe("database-query");

        var form = await RunAsync(setup.Member, setup.AssistantId, "我要回報冷藏庫故障，需要報修");
        Kind(form).ShouldBe("form-request");
        form.Reply!.Value.GetProperty("reply").GetProperty("caseProposal").ValueKind.ShouldBe(JsonValueKind.Null);

        var proposed = await RunAsync(setup.Member, setup.AssistantId, RepairQuestion);
        Kind(proposed).ShouldBe("case-proposal");
        proposed.Reply!.Value.GetProperty("reply").GetProperty("form").ValueKind.ShouldBe(JsonValueKind.Null);
        proposed.Events.Count(e => e.GetProperty("type").GetString() == "CUSTOM" && e.GetProperty("name").GetString() == "smartagri.reply")
            .ShouldBe(1);
    }

    // --- Who never gets one -----------------------------------------------------------------------

    [Fact]
    public async Task An_external_customer_and_a_conversation_that_is_not_kept_never_get_a_proposal()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        await AddTypeAsync(setup, setup.RepairTypeId);

        Kind(await RunAsync(setup.External, setup.AssistantId, RepairQuestion)).ShouldNotBe("case-proposal");

        var notKept = await CreateAssistantAsync(setup.Org, keepConversations: false);
        (await setup.Admin.Spa.PutAsync($"{AssistantsPath}/{notKept}/sources/case-type/{setup.RepairTypeId}", setup.Admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Kind(await RunAsync(setup.Member, notKept, RepairQuestion)).ShouldNotBe("case-proposal");

        // The member's proposal cannot be confirmed by the external customer, another internal account
        // or another organization's account; none of them learns whether it exists.
        var run = await RunAsync(setup.Member, setup.AssistantId, RepairQuestion);
        var messageId = run.Reply!.Value.GetProperty("id").GetGuid();
        var externalRefused = await ConfirmAsync(setup.External, setup.AssistantId, messageId, "冷藏庫報修", "");
        await ShouldBeAsync(externalRefused, ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        var colleague = await SignInAsync(_host.Factory, setup.Org, "colleague");
        await ShouldBeAsync(await ConfirmAsync(colleague, setup.AssistantId, messageId, "冷藏庫報修", ""), ApiErrors.NotFound(ForbiddenReason.ChatThread));
        await ShouldBeAsync(await ConfirmAsync(colleague, setup.AssistantId, Guid.CreateVersion7(), "冷藏庫報修", ""), ApiErrors.NotFound(ForbiddenReason.ChatThread));
        var orgB = await CreateOrganizationAsync("別家農場");
        var outsider = await SignInAsync(_host.Factory, orgB, "member");
        await ShouldBeAsync(await ConfirmAsync(outsider, setup.AssistantId, messageId, "冷藏庫報修", ""), ApiErrors.NotFound(ForbiddenReason.AssistantUse));
        (await CaseCountAsync(setup.Org)).ShouldBe(0);
    }

    // --- Confirm and dismiss ----------------------------------------------------------------------

    [Fact]
    public async Task Confirming_creates_only_the_confirmed_title_and_description_linked_to_the_thread_and_only_once()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        await AddTypeAsync(setup, setup.RepairTypeId);
        var run = await RunAsync(setup.Member, setup.AssistantId, RepairQuestion);
        var messageId = run.Reply!.Value.GetProperty("id").GetGuid();
        var threadId = run.ThreadId!.Value;

        var invalid = await ConfirmAsync(setup.Member, setup.AssistantId, messageId, "  ", new string('字', Case.DescriptionMaxLength + 1));
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var errors = (await BodyJsonAsync(invalid)).GetProperty("errors");
        errors.GetProperty("title")[0].GetString().ShouldBe(CaseRules.TitleRequiredMessage);
        errors.GetProperty("description")[0].GetString().ShouldBe(CaseRules.DescriptionTooLongMessage);
        (await CaseCountAsync(setup.Org)).ShouldBe(0);

        var before = _host.Clock.GetUtcNow();
        var confirmed = await ConfirmAsync(setup.Member, setup.AssistantId, messageId, "  二號冷藏庫溫度異常  ", " 請今天派人檢查壓縮機。 ");
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(CancellationToken));
        var view = await BodyJsonAsync(confirmed);
        OpenApiContract.AssertKeysMatchSchema(view, "ChatMessageView");
        var proposal = view.GetProperty("reply").GetProperty("caseProposal");
        proposal.GetProperty("status").GetString().ShouldBe("confirmed");
        proposal.GetProperty("available").GetBoolean().ShouldBeFalse();
        proposal.GetProperty("title").GetString().ShouldBe("二號冷藏庫溫度異常");
        var caseId = proposal.GetProperty("caseId").GetGuid();

        // The chat reads it back the same way.
        var chat = await ChatAsync(setup.Member, setup.AssistantId, threadId);
        JsonNode.DeepEquals(JsonNode.Parse(chat.GetProperty("messages")[1].GetRawText()), JsonNode.Parse(view.GetRawText())).ShouldBeTrue();

        // The case: the confirmed text only, created by the asker, from a chat proposal, linked to the thread.
        var detail = await setup.Member.Spa.GetAsync($"/api/v1/cases/{caseId}", setup.Member.Token);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await detail.Content.ReadAsStringAsync(CancellationToken);
        var item = JsonDocument.Parse(raw).RootElement;
        item.GetProperty("case").GetProperty("title").GetString().ShouldBe("二號冷藏庫溫度異常");
        item.GetProperty("case").GetProperty("description").GetString().ShouldBe("請今天派人檢查壓縮機。");
        item.GetProperty("case").GetProperty("origin").GetString().ShouldBe("chat-proposal");
        item.GetProperty("case").GetProperty("status").GetString().ShouldBe("pending");
        item.GetProperty("case").GetProperty("createdBy").GetProperty("id").GetGuid().ShouldBe(setup.Org.Member.Id);
        item.GetProperty("case").GetProperty("group").GetProperty("id").GetGuid().ShouldBe(setup.EquipmentGroupId);
        item.GetProperty("case").GetProperty("dueAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(before.AddHours(72).AddSeconds(-1));
        var thread = item.GetProperty("links").GetProperty("thread");
        (thread.GetProperty("assistantId").GetGuid(), thread.GetProperty("threadId").GetGuid(), thread.GetProperty("canOpen").GetBoolean())
            .ShouldBe((setup.AssistantId, threadId, true));
        raw.ShouldNotContain("冷藏庫溫度降不下來");

        // Only once: a second confirmation or a dismissal is 409 and creates nothing.
        var again = await ConfirmAsync(setup.Member, setup.AssistantId, messageId, "再建一次", "");
        await ShouldBeReasonAsync(again, HttpStatusCode.Conflict, ChatCaseProposalEndpoints.ClosedReason);
        await ShouldBeReasonAsync(await DismissAsync(setup.Member, setup.AssistantId, messageId), HttpStatusCode.Conflict, ChatCaseProposalEndpoints.ClosedReason);
        (await CaseCountAsync(setup.Org)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_concurrent_confirmations_create_one_case()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        await AddTypeAsync(setup, setup.RepairTypeId);
        var messageId = (await RunAsync(setup.Member, setup.AssistantId, RepairQuestion)).Reply!.Value.GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(index => ConfirmAsync(setup.Member, setup.AssistantId, messageId, $"冷藏庫報修 {index}", "")));
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(3);
        (await CaseCountAsync(setup.Org)).ShouldBe(1);
    }

    [Fact]
    public async Task Dismissing_records_not_needed_and_creates_nothing()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        await AddTypeAsync(setup, setup.RepairTypeId);
        var run = await RunAsync(setup.Member, setup.AssistantId, RepairQuestion);
        var messageId = run.Reply!.Value.GetProperty("id").GetGuid();

        var dismissed = await DismissAsync(setup.Member, setup.AssistantId, messageId);
        dismissed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposal = (await BodyJsonAsync(dismissed)).GetProperty("reply").GetProperty("caseProposal");
        proposal.GetProperty("status").GetString().ShouldBe("dismissed");
        proposal.GetProperty("available").GetBoolean().ShouldBeFalse();
        proposal.GetProperty("caseId").ValueKind.ShouldBe(JsonValueKind.Null);
        Proposal((await ChatAsync(setup.Member, setup.AssistantId, run.ThreadId!.Value)).GetProperty("messages")[1])
            .GetProperty("status").GetString().ShouldBe("dismissed");

        await ShouldBeReasonAsync(
            await ConfirmAsync(setup.Member, setup.AssistantId, messageId, "冷藏庫報修", ""), HttpStatusCode.Conflict, ChatCaseProposalEndpoints.ClosedReason);
        (await CaseCountAsync(setup.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task A_deactivated_or_removed_type_reads_as_unavailable_and_confirming_is_422()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        await AddTypeAsync(setup, setup.RepairTypeId);
        var run = await RunAsync(setup.Member, setup.AssistantId, RepairQuestion);
        var messageId = run.Reply!.Value.GetProperty("id").GetGuid();
        var threadId = run.ThreadId!.Value;

        await SetTypeActiveAsync(setup, setup.RepairTypeId, isActive: false);
        Proposal((await ChatAsync(setup.Member, setup.AssistantId, threadId)).GetProperty("messages")[1])
            .GetProperty("available").GetBoolean().ShouldBeFalse();
        await ShouldBeReasonAsync(
            await ConfirmAsync(setup.Member, setup.AssistantId, messageId, "冷藏庫報修", ""),
            HttpStatusCode.UnprocessableEntity, ChatCaseProposalEndpoints.NotProposableReason);
        // A deactivated type is not proposed either, though it stays on the list.
        Kind(await RunAsync(setup.Member, setup.AssistantId, RepairQuestion, threadId.ToString())).ShouldNotBe("case-proposal");

        await SetTypeActiveAsync(setup, setup.RepairTypeId, isActive: true);
        Proposal((await ChatAsync(setup.Member, setup.AssistantId, threadId)).GetProperty("messages")[1])
            .GetProperty("available").GetBoolean().ShouldBeTrue();

        var removed = await setup.Admin.Spa.DeleteAsync($"{AssistantsPath}/{setup.AssistantId}/sources/case-type/{setup.RepairTypeId}", setup.Admin.Token);
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(removed)).GetProperty("caseTypeIds").GetArrayLength().ShouldBe(0);
        Proposal((await ChatAsync(setup.Member, setup.AssistantId, threadId)).GetProperty("messages")[1])
            .GetProperty("available").GetBoolean().ShouldBeFalse();
        await ShouldBeReasonAsync(
            await ConfirmAsync(setup.Member, setup.AssistantId, messageId, "冷藏庫報修", ""),
            HttpStatusCode.UnprocessableEntity, ChatCaseProposalEndpoints.NotProposableReason);
        (await CaseCountAsync(setup.Org)).ShouldBe(0);
    }

    // --- Model mode -------------------------------------------------------------------------------

    [Fact]
    public async Task Model_mode_makes_one_counted_case_proposal_call_and_falls_back_to_keywords()
    {
        await using var model = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "model"));
        var setup = await CreateSetupAsync(model);
        await AddTypeAsync(setup, setup.RepairTypeId);

        // The model recognizes a case the keywords miss, and drafts the title and description.
        var unmarked = "二號冷藏庫溫度一直降不下來" + new string('冷', 150);
        var drafted = Proposal(await RunAsync(setup.Member, setup.AssistantId, $"{unmarked} {FakeChatDirectives.CaseProposal}"));
        drafted.GetProperty("typeId").GetGuid().ShouldBe(setup.RepairTypeId);
        drafted.GetProperty("title").GetString()!.Length.ShouldBe(Case.TitleMaxLength);
        drafted.GetProperty("title").GetString().ShouldStartWith("模型草擬：二號冷藏庫");
        drafted.GetProperty("description").GetString().ShouldBe("由模型依問題草擬的說明。");

        // It declines one the keywords would catch; an unoffered id or another tool is no proposal.
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {FakeChatDirectives.NoCase}")).ShouldNotBe("case-proposal");
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {CaseCall(setup.PurchaseTypeId)}")).ShouldNotBe("case-proposal");
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {CaseCall(setup.RepairTypeId, name: "propose_something")}"))
            .ShouldNotBe("case-proposal");

        var invocations = await InvocationsAsync(setup.Org, ModelInvocationPurpose.CaseProposal);
        invocations.Count.ShouldBe(4);
        invocations.ShouldAllBe(invocation => invocation.AccountId == setup.Org.Member.Id
            && invocation.AssistantId == setup.AssistantId && invocation.Succeeded);
        OrganizationTokenUsageRulesShouldCount();

        // A model failure: the keyword rule decides, and the failed call is still recorded.
        var fallback = await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {FakeChatDirectives.FailMidway}");
        Kind(fallback).ShouldBe("case-proposal");
        Proposal(fallback).GetProperty("title").GetString().ShouldBe($"{RepairQuestion} {FakeChatDirectives.FailMidway}");
        var after = await InvocationsAsync(setup.Org, ModelInvocationPurpose.CaseProposal);
        after.Count.ShouldBe(5);
        after.Count(invocation => !invocation.Succeeded).ShouldBe(1);
    }

    // --- Model mode with the form and a type: one combined call (#286) ------------------------------

    [Fact]
    public async Task With_the_form_and_a_type_model_mode_makes_one_counted_combined_call_for_the_form_the_case_or_neither()
    {
        await using var model = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "model"));
        var setup = await CreateSetupAsync(model);
        await AddTypeAsync(setup, setup.RepairTypeId);
        await ConnectFormAsync(setup);

        // The model chooses the form (the fake decides the form first, like decision L) …
        var form = await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {FakeChatDirectives.FormRequest}");
        Kind(form).ShouldBe("form-request");
        form.Reply!.Value.GetProperty("reply").GetProperty("caseProposal").ValueKind.ShouldBe(JsonValueKind.Null);

        // … the case (no form words: the fake calls propose_case in the same call) …
        var proposed = await RunAsync(setup.Member, setup.AssistantId, RepairQuestion);
        Proposal(proposed).GetProperty("typeId").GetGuid().ShouldBe(setup.RepairTypeId);
        Proposal(proposed).GetProperty("title").GetString().ShouldBe($"模型草擬：{RepairQuestion}");
        proposed.Reply!.Value.GetProperty("reply").GetProperty("form").ValueKind.ShouldBe(JsonValueKind.Null);

        // … or neither: the answer pipeline answers.
        var neither = await RunAsync(setup.Member, setup.AssistantId, "今天下午適合噴藥嗎？");
        Kind(neither).ShouldNotBeNull().ShouldNotBeOneOf("form-request", "case-proposal");

        // One reply, one proposal at most, and one form-check right before the call (#171's place and value).
        foreach (var run in new[] { form, proposed, neither })
        {
            var types = run.Events.Select(e => e.GetProperty("type").GetString() == "CUSTOM" ? $"CUSTOM {e.GetProperty("name").GetString()}" : e.GetProperty("type").GetString()).ToList();
            types.Count(type => type == $"CUSTOM {ChatRunEndpoints.FormCheckEventName}").ShouldBe(1, string.Join(", ", types));
            types.Count(type => type == "CUSTOM smartagri.reply").ShouldBe(1);
            var check = types.IndexOf($"CUSTOM {ChatRunEndpoints.FormCheckEventName}");
            check.ShouldBeGreaterThan(types.IndexOf("TEXT_MESSAGE_START"));
            check.ShouldBeLessThan(types.IndexOf("TEXT_MESSAGE_CONTENT"));
            run.Events[check].GetProperty("value").EnumerateObject().ShouldBeEmpty();
        }

        // Exactly one model call per reply, as proposal-selection — never form-request or case-proposal.
        var calls = await InvocationsAsync(setup.Org, ModelInvocationPurpose.ProposalSelection);
        calls.Count.ShouldBe(3);
        calls.ShouldAllBe(invocation => invocation.AccountId == setup.Org.Member.Id && invocation.AssistantId == setup.AssistantId && invocation.Succeeded);
        (await InvocationsAsync(setup.Org, ModelInvocationPurpose.FormRequest)).ShouldBeEmpty();
        (await InvocationsAsync(setup.Org, ModelInvocationPurpose.CaseProposal)).ShouldBeEmpty();
        SmartAgri.Application.Organizations.OrganizationTokenUsageRules.CountedPurposes.ShouldContain(ModelInvocationPurpose.ProposalSelection);
    }

    [Fact]
    public async Task The_combined_call_accepts_only_what_was_offered_and_falls_back_to_the_form_gate_then_the_case_rule()
    {
        await using var model = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "model"));
        var setup = await CreateSetupAsync(model);
        await AddTypeAsync(setup, setup.RepairTypeId);
        var databaseId = await ConnectFormAsync(setup);

        // An unoffered type, an unoffered form, another tool: no proposal of either kind.
        foreach (var call in new[]
        {
            CaseCall(setup.PurchaseTypeId),
            FakeChatDirectives.Query + JsonSerializer.Serialize(new { name = AssistantFormRequestRulesToolName, arguments = new { databaseId = Guid.CreateVersion7() } }),
            CaseCall(setup.RepairTypeId, name: "propose_something"),
        })
        {
            Kind(await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {call}")).ShouldNotBeNull().ShouldNotBeOneOf("form-request", "case-proposal");
        }

        // The offered ones, named explicitly, are accepted (the form re-authorized for that id).
        var named = FakeChatDirectives.Query + JsonSerializer.Serialize(new { name = AssistantFormRequestRulesToolName, arguments = new { databaseId } });
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"今天的事 {named}")).ShouldBe("form-request");

        // A model failure: the form gate first, then the case rule — and the failed call is still recorded.
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"我要回報冷藏庫故障，需要報修 {FakeChatDirectives.FailMidway}")).ShouldBe("form-request");
        var fallback = await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {FakeChatDirectives.FailMidway}");
        Proposal(fallback).GetProperty("title").GetString().ShouldBe($"{RepairQuestion} {FakeChatDirectives.FailMidway}");
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"今天下午適合噴藥嗎？ {FakeChatDirectives.FailMidway}")).ShouldNotBeOneOf("form-request", "case-proposal");

        var calls = await InvocationsAsync(setup.Org, ModelInvocationPurpose.ProposalSelection);
        calls.Count.ShouldBe(7);
        calls.Count(invocation => !invocation.Succeeded).ShouldBe(3);
    }

    [Fact]
    public async Task With_only_one_of_the_two_the_single_call_of_before_is_made()
    {
        await using var model = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "model"));

        // Only a type: #254's case-proposal call, and no form-check (no form is offered).
        var caseOnly = await CreateSetupAsync(model);
        await AddTypeAsync(caseOnly, caseOnly.RepairTypeId);
        var proposed = await RunAsync(caseOnly.Member, caseOnly.AssistantId, RepairQuestion);
        Kind(proposed).ShouldBe("case-proposal");
        proposed.Body.ShouldNotContain(ChatRunEndpoints.FormCheckEventName);
        (await InvocationsAsync(caseOnly.Org, ModelInvocationPurpose.CaseProposal)).Count.ShouldBe(1);
        (await InvocationsAsync(caseOnly.Org, ModelInvocationPurpose.ProposalSelection)).ShouldBeEmpty();

        // Only the form (no type on the list): #164's form-request call, after form-check.
        var formOnly = await CreateSetupAsync(model);
        await ConnectFormAsync(formOnly);
        var form = await RunAsync(formOnly.Member, formOnly.AssistantId, "我要回報冷藏庫故障");
        Kind(form).ShouldBe("form-request");
        form.Body.ShouldContain(ChatRunEndpoints.FormCheckEventName);
        Kind(await RunAsync(formOnly.Member, formOnly.AssistantId, RepairQuestion)).ShouldNotBe("case-proposal");
        (await InvocationsAsync(formOnly.Org, ModelInvocationPurpose.FormRequest)).Count.ShouldBe(2);
        (await InvocationsAsync(formOnly.Org, ModelInvocationPurpose.ProposalSelection)).ShouldBeEmpty();

        // Both set up, but the asker is an external customer (never a case): never the combined call.
        await AddTypeAsync(formOnly, formOnly.RepairTypeId);
        Kind(await RunAsync(formOnly.External, formOnly.AssistantId, RepairQuestion)).ShouldNotBe("case-proposal");
        (await InvocationsAsync(formOnly.Org, ModelInvocationPurpose.ProposalSelection)).ShouldBeEmpty();
        (await InvocationsAsync(formOnly.Org, ModelInvocationPurpose.CaseProposal)).ShouldBeEmpty();
    }

    // --- The explicit 「都不符合」 (#297) --------------------------------------------------------------

    [Fact]
    public async Task Choosing_no_match_in_the_case_call_is_no_proposal_and_the_answer_pipeline_answers()
    {
        await using var model = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "model"));
        var setup = await CreateSetupAsync(model);
        await AddTypeAsync(setup, setup.RepairTypeId);

        // The keyword rule would propose this one (a case word, one type): choosing no_matching_type is not a failure,
        // so no keyword fallback either.
        var declined = await RunAsync(setup.Member, setup.AssistantId, $"{RepairQuestion} {FakeChatDirectives.NoMatch}");
        Kind(declined).ShouldNotBeNull().ShouldNotBeOneOf("form-request", "case-proposal");
        declined.Reply!.Value.GetProperty("reply").GetProperty("caseProposal").ValueKind.ShouldBe(JsonValueKind.Null);
        declined.Body.ShouldNotContain(ChatRunEndpoints.FormCheckEventName);
        (await ChatAsync(setup.Member, setup.AssistantId, declined.ThreadId!.Value)).GetRawText().ShouldNotContain("\"case-proposal\"");

        // Still the one counted case-proposal call, with its usage — no other purpose.
        var calls = await InvocationsAsync(setup.Org, ModelInvocationPurpose.CaseProposal);
        calls.Count.ShouldBe(1);
        calls.ShouldAllBe(invocation => invocation.AccountId == setup.Org.Member.Id && invocation.AssistantId == setup.AssistantId
            && invocation.Succeeded && invocation.InputTokens > 0 && invocation.OutputTokens > 0);
        (await InvocationsAsync(setup.Org, ModelInvocationPurpose.ProposalSelection)).ShouldBeEmpty();
        OrganizationTokenUsageRulesShouldCount();
        (await CaseCountAsync(setup.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task Choosing_no_match_in_the_combined_call_is_neither_the_form_nor_a_case()
    {
        await using var model = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "model"));
        var setup = await CreateSetupAsync(model);
        await AddTypeAsync(setup, setup.RepairTypeId);
        await ConnectFormAsync(setup);

        // Both the form gate and the case rule would fire on this question; no_matching_type means neither.
        var declined = await RunAsync(setup.Member, setup.AssistantId, $"我要回報冷藏庫故障，需要報修 {FakeChatDirectives.NoMatch}");
        Kind(declined).ShouldNotBeNull().ShouldNotBeOneOf("form-request", "case-proposal");
        declined.Reply!.Value.GetProperty("reply").GetProperty("form").ValueKind.ShouldBe(JsonValueKind.Null);
        declined.Reply!.Value.GetProperty("reply").GetProperty("caseProposal").ValueKind.ShouldBe(JsonValueKind.Null);

        // form-check unchanged: exactly once, right before the call (the call offers the form).
        var types = declined.Events.Select(e => e.GetProperty("type").GetString() == "CUSTOM" ? $"CUSTOM {e.GetProperty("name").GetString()}" : e.GetProperty("type").GetString()).ToList();
        types.Count(type => type == $"CUSTOM {ChatRunEndpoints.FormCheckEventName}").ShouldBe(1, string.Join(", ", types));
        var check = types.IndexOf($"CUSTOM {ChatRunEndpoints.FormCheckEventName}");
        check.ShouldBeGreaterThan(types.IndexOf("TEXT_MESSAGE_START"));
        check.ShouldBeLessThan(types.IndexOf("TEXT_MESSAGE_CONTENT"));

        // One counted proposal-selection call with its usage; never form-request or case-proposal.
        var calls = await InvocationsAsync(setup.Org, ModelInvocationPurpose.ProposalSelection);
        calls.Count.ShouldBe(1);
        calls.ShouldAllBe(invocation => invocation.AccountId == setup.Org.Member.Id && invocation.AssistantId == setup.AssistantId
            && invocation.Succeeded && invocation.InputTokens > 0 && invocation.OutputTokens > 0);
        (await InvocationsAsync(setup.Org, ModelInvocationPurpose.FormRequest)).ShouldBeEmpty();
        (await InvocationsAsync(setup.Org, ModelInvocationPurpose.CaseProposal)).ShouldBeEmpty();
        (await CaseCountAsync(setup.Org)).ShouldBe(0);
    }

    // --- Settings (decision U) --------------------------------------------------------------------

    [Fact]
    public async Task Only_the_owner_with_manage_assistants_chooses_among_active_types_and_the_rows_go_with_the_assistant()
    {
        var setup = await CreateSetupAsync(_host.Factory);
        var path = $"{AssistantsPath}/{setup.AssistantId}/sources/case-type";

        var settings = await BodyJsonAsync(await setup.Admin.Spa.GetAsync($"{AssistantsPath}/{setup.AssistantId}/settings", setup.Admin.Token));
        OpenApiContract.AssertKeysMatchSchema(settings, "AssistantSettingsView");
        settings.GetProperty("caseTypeIds").GetArrayLength().ShouldBe(0);

        var inactive = await CreateTypeAsync(setup.Admin, "已停用類型", setup.EquipmentGroupId, isActive: false);
        foreach (var id in new[] { inactive.ToString(), Guid.CreateVersion7().ToString(), "not-a-guid" })
        {
            var refused = await setup.Admin.Spa.PutAsync($"{path}/{id}", setup.Admin.Token, new { });
            await ShouldBeReasonAsync(refused, HttpStatusCode.UnprocessableEntity, "case-type-inactive");
        }

        // Another organization's active type is the same 422.
        var orgB = await CreateOrganizationAsync("別家農場");
        var adminB = await SignInAsync(_host.Factory, orgB, "admin");
        var foreignGroup = await CreateGroupAsync(adminB, "別家設備組");
        var foreignType = await CreateTypeAsync(adminB, RepairType, foreignGroup);
        await ShouldBeReasonAsync(await setup.Admin.Spa.PutAsync($"{path}/{foreignType}", setup.Admin.Token, new { }),
            HttpStatusCode.UnprocessableEntity, "case-type-inactive");

        // Not the owner (even with manage-assistants), or without it: the same 403 as every setting.
        var colleague = await SignInAsync(_host.Factory, setup.Org, "colleague");
        foreach (var caller in new[] { colleague, setup.Member })
        {
            var refused = await caller.Spa.PutAsync($"{path}/{setup.RepairTypeId}", caller.Token, new { });
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await BodyJsonAsync(refused)).GetProperty("reason").GetString().ShouldBe("assistant-configuration");
        }

        // Idempotent both ways.
        await AddTypeAsync(setup, setup.RepairTypeId);
        var twice = await AddTypeAsync(setup, setup.RepairTypeId);
        twice.GetProperty("caseTypeIds").GetArrayLength().ShouldBe(1);
        (await setup.Admin.Spa.DeleteAsync($"{path}/{setup.PurchaseTypeId}", setup.Admin.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await setup.Admin.Spa.DeleteAsync($"{path}/not-a-guid", setup.Admin.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await setup.Admin.Spa.DeleteAsync($"{AssistantsPath}/{setup.AssistantId}", setup.Admin.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        (await dbContext.AssistantCaseTypes.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.CaseTypes.CountAsync(CancellationToken)).ShouldBe(3);
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private static void OrganizationTokenUsageRulesShouldCount() =>
        SmartAgri.Application.Organizations.OrganizationTokenUsageRules.CountedPurposes.ShouldContain(ModelInvocationPurpose.CaseProposal);

    private const string AssistantFormRequestRulesToolName = SmartAgri.Application.Assistants.AssistantFormRequestRules.ToolName;

    /// <summary>Makes a new database the assistant's form target (the admin owns both); its id.</summary>
    private static async Task<Guid> ConnectFormAsync(Setup setup)
    {
        var databaseId = await CreateDatabaseAsync(setup.Admin, "設備問題資料庫");
        (await setup.Admin.Spa.PutAsync($"{AssistantsPath}/{setup.AssistantId}/sources/database/{databaseId}", setup.Admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var target = await setup.Admin.Spa.PatchAsync($"{AssistantsPath}/{setup.AssistantId}/settings", setup.Admin.Token, new
        {
            rules = new { dataWriteDatabaseId = databaseId.ToString(), dataWritePurpose = "記錄設備問題，方便追蹤。" },
        });
        target.StatusCode.ShouldBe(HttpStatusCode.OK, await target.Content.ReadAsStringAsync(CancellationToken));
        return databaseId;
    }

    private static string CaseCall(Guid caseTypeId, string name = CaseProposalRules.ToolName) =>
        FakeChatDirectives.Query + JsonSerializer.Serialize(new { name, arguments = new { caseTypeId, title = "標題", description = "說明" } });

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Member);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private sealed record Setup(
        TestOrganization Org, SignedIn Admin, SignedIn Member, SignedIn External, Guid AssistantId, Guid EquipmentGroupId, Guid RepairTypeId, Guid PurchaseTypeId);

    private sealed record RecordedRun(HttpStatusCode Status, string Body, IReadOnlyList<JsonElement> Events)
    {
        public JsonElement? Reply => Custom("smartagri.reply");

        public Guid? ThreadId => Custom("smartagri.thread")?.GetProperty("threadId").GetGuid();

        private JsonElement? Custom(string name) =>
            Events.Where(e => e.GetProperty("type").GetString() == "CUSTOM" && e.GetProperty("name").GetString() == name)
                .Select(e => (JsonElement?)e.GetProperty("value"))
                .SingleOrDefault();
    }

    private static string? Kind(RecordedRun run)
    {
        run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
        return run.Reply?.GetProperty("reply").GetProperty("kind").GetString();
    }

    private static JsonElement Proposal(RecordedRun run)
    {
        Kind(run).ShouldBe("case-proposal", run.Body);
        return Proposal(run.Reply!.Value);
    }

    private static JsonElement Proposal(JsonElement message) => message.GetProperty("reply").GetProperty("caseProposal");

    /// <summary>The admin's assistant (kept conversations) shared with the member and the external customer;
    /// 設備組 with the types 設備故障報修 (72 hours) and 採購申請, neither on the assistant's list yet.</summary>
    private async Task<Setup> CreateSetupAsync(WebApplicationFactory<Program> factory)
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(_host.Factory, org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var repair = await CreateTypeAsync(admin, RepairType, group);
        var purchase = await CreateTypeAsync(admin, PurchaseType, group);
        var assistantId = await CreateAssistantAsync(org, keepConversations: true);
        return new Setup(
            org, admin, await SignInAsync(factory, org, "member"), await SignInAsync(factory, org, "external"), assistantId, group, repair, purchase);
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "開案農場")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ReadConsentedSubmissions);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}成員", AccountPermission.UseSharedAssistants);
        await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, $"{name}同事",
            AccountPermission.UseSharedAssistants, AccountPermission.ManageAssistants);
        await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, $"{name}客戶",
            AccountPermission.UseSharedAssistants, AccountPermission.SubmitAuthorizedForms);
        return new TestOrganization(organization, admin, member);
    }

    /// <summary>The admin's assistant, shared with every other account of the organization.</summary>
    private async Task<Guid> CreateAssistantAsync(TestOrganization org, bool keepConversations)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Admin.Id, "設備小幫手", "協助處理設備問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: keepConversations, DateTimeOffset.UtcNow);
        dbContext.Assistants.Add(assistant);
        var others = await dbContext.Accounts.AsNoTracking().Where(account => account.Id != org.Admin.Id).Select(account => account.Id).ToListAsync(CancellationToken);
        dbContext.AssistantShares.AddRange(others.Select(id => new AssistantShare(assistant, id)));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private static async Task<SignedIn> SignInAsync(WebApplicationFactory<Program> factory, TestOrganization org, string loginName)
    {
        var spa = new SpaClient(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateGroupAsync(SignedIn admin, string name)
    {
        var response = await admin.Spa.PostAsync("/api/v1/case-groups", admin.Token, new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateTypeAsync(SignedIn admin, string name, Guid groupId, bool isActive = true)
    {
        var response = await admin.Spa.PostAsync("/api/v1/case-types", admin.Token, new
        {
            name, description = $"{name}的說明", defaultGroupId = groupId, defaultDueHours = 72, isActive,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task SetTypeActiveAsync(Setup setup, Guid typeId, bool isActive)
    {
        var response = await setup.Admin.Spa.PutAsync($"/api/v1/case-types/{typeId}", setup.Admin.Token, new
        {
            name = RepairType, description = $"{RepairType}的說明", defaultGroupId = setup.EquipmentGroupId, defaultDueHours = 72, isActive,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<JsonElement> AddTypeAsync(Setup setup, Guid typeId)
    {
        var response = await setup.Admin.Spa.PutAsync($"{AssistantsPath}/{setup.AssistantId}/sources/case-type/{typeId}", setup.Admin.Token, new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await BodyJsonAsync(response);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name)
    {
        var response = await owner.Spa.PostAsync("/api/v1/databases", owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> ConfirmAsync(SignedIn caller, Guid assistantId, Guid messageId, string title, string description) =>
        caller.Spa.PostAsync($"{AssistantsPath}/{assistantId}/chat/case-proposals/{messageId}:confirm", caller.Token, new { title, description });

    private static Task<HttpResponseMessage> DismissAsync(SignedIn caller, Guid assistantId, Guid messageId) =>
        caller.Spa.PostAsync($"{AssistantsPath}/{assistantId}/chat/case-proposals/{messageId}:dismiss", caller.Token, new { });

    private static async Task<JsonElement> ChatAsync(SignedIn caller, Guid assistantId, Guid threadId)
    {
        var response = await caller.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    private async Task<List<ModelInvocation>> InvocationsAsync(TestOrganization org, ModelInvocationPurpose purpose)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.ModelInvocations.AsNoTracking().Where(invocation => invocation.Purpose == purpose).ToListAsync(CancellationToken);
    }

    private async Task<int> CaseCountAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.Cases.CountAsync(CancellationToken);
    }

    private static async Task ShouldBeAsync(HttpResponseMessage response, IResult expected)
    {
        var bytes = await ApiErrorsTests.ExecuteAsync(expected);
        response.StatusCode.ShouldBe((HttpStatusCode)bytes.StatusCode);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(bytes.Body);
    }

    private static async Task ShouldBeReasonAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString().ShouldBe(reason);
    }

    private static async Task<RecordedRun> RunAsync(SignedIn caller, Guid assistantId, string question, string? threadId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AssistantsPath}/{assistantId}/chat/runs")
        {
            Content = JsonContent.Create(new
            {
                threadId = threadId ?? string.Empty,
                runId = "run-case-proposal",
                state = new { },
                messages = new object[] { new { id = Guid.NewGuid().ToString(), role = "user", content = question } },
                tools = Array.Empty<object>(),
                context = Array.Empty<object>(),
                forwardedProps = new { },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await caller.Spa.Http.SendAsync(request, CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        IReadOnlyList<JsonElement> events = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ?
            [
                .. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                    .Select(record => string.Concat(record.Split('\n')
                        .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                        .Select(line => line["data: ".Length..])))
                    .Where(data => data.Length > 0)
                    .Select(data => JsonDocument.Parse(data).RootElement.Clone()),
            ]
            : [];
        return new RecordedRun(response.StatusCode, body, events);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
