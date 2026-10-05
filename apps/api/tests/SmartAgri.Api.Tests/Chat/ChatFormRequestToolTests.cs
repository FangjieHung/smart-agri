using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Chat;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Chat;

/// <summary>
/// The model-chosen form tool (M4 #164) against real PostgreSQL with the <c>Fake</c> models: with
/// <c>Chat:FormRequests:Trigger = Model</c> the model decides whether a question gets the
/// assistant's form (scripted with <c>#form-request</c>, <c>#form-none</c> and <c>#query:</c>); the
/// server offers only the form target the assistant may use right now, re-checks the id the model
/// names exactly as #148 does, and answers an unoffered id (another organization's, made up, another
/// tool) the same as no form; a model failure falls back to the keyword gate; usage and saving
/// follow #148's rules. Keyword mode (the default) is unchanged and calls no model.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class ChatFormRequestToolTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Chat-Form-Tool-Pass-1!";
    private const string AssistantsPath = "/api/v1/assistants";
    private const string DatabasesPath = "/api/v1/databases";
    private const string CollectionPurpose = "記錄田區異常，方便技術人員追蹤處理。";
    private const string DatabaseName = "田間異常資料庫";
    private const string FormQuestion = "我要回報今天 A 區的病蟲害";
    private const string UnmarkedQuestion = "3 號溫室的番茄葉子出現黃斑";

    private readonly AuthHostFixture _host;

    public ChatFormRequestToolTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string FormCall(object databaseId, string name = AssistantFormRequestRules.ToolName) =>
        FakeChatDirectives.Query + JsonSerializer.Serialize(new { name, arguments = new { databaseId } });

    // --- Keyword mode (default) -------------------------------------------------------------------

    [Fact]
    public async Task Keyword_mode_is_unchanged_and_calls_no_model()
    {
        var setup = await CreateSetupAsync(_host.Factory, keepConversations: true);

        Kind(await RunAsync(setup.Member, setup.AssistantId, FormQuestion)).ShouldBe("form-request");
        // Directives for the model are ignored: keywords alone decide.
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"{UnmarkedQuestion} {FakeChatDirectives.FormRequest}")).ShouldNotBe("form-request");
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"{FormQuestion} {FakeChatDirectives.NoForm}")).ShouldBe("form-request");

        (await FormInvocationsAsync(setup.Org)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_trigger_fails_startup()
    {
        await using var factory = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "Sometimes"));
        var exception = Should.Throw<Exception>(() => factory.CreateClient());
        exception.ToString().ShouldContain("Chat:FormRequests:Trigger must be Keyword or Model");
    }

    // --- Model mode -------------------------------------------------------------------------------

    [Fact]
    public async Task The_model_requests_the_servers_form_saved_and_recorded_like_148()
    {
        await using var model = ModelMode();
        var setup = await CreateSetupAsync(model, keepConversations: true);

        var run = await RunAsync(setup.Member, setup.AssistantId, FormQuestion);
        run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
        var reply = run.Reply!.Value;
        OpenApiContract.AssertKeysMatchSchema(reply, "ChatMessageView");
        reply.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");
        reply.GetProperty("reply").GetProperty("text").GetString().ShouldBe(AssistantFormRequestRules.FormRequestText);
        var form = reply.GetProperty("reply").GetProperty("form");
        OpenApiContract.AssertKeysMatchSchema(form, "ChatFormRequestView");
        form.GetProperty("id").GetGuid().ShouldBe(setup.DatabaseId);
        form.GetProperty("title").GetString().ShouldBe(DatabaseName);
        form.GetProperty("consent").GetProperty("purpose").GetString().ShouldBe(CollectionPurpose);
        var threadId = run.ThreadId!.Value;

        // Read back exactly as streamed (re-authorized on read, as in #148).
        var chat = await BodyJsonAsync(await setup.Member.Spa.GetAsync($"{AssistantsPath}/{setup.AssistantId}/chat?conversation={threadId}", setup.Member.Token));
        JsonNode.DeepEquals(JsonNode.Parse(chat.GetProperty("messages")[1].GetRawText()), JsonNode.Parse(reply.GetRawText())).ShouldBeTrue();

        // The model recognizes a report the keywords miss, and declines one they would catch.
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"{UnmarkedQuestion} {FakeChatDirectives.FormRequest}", threadId.ToString()))
            .ShouldBe("form-request");
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"{FormQuestion} {FakeChatDirectives.NoForm}", threadId.ToString()))
            .ShouldNotBe("form-request");

        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        var messages = await dbContext.ChatMessages.AsNoTracking().Where(message => message.ThreadId == threadId)
            .OrderBy(message => message.Sequence).ToListAsync(CancellationToken);
        messages.Count.ShouldBe(6);
        messages.Count(message => message.ReplyKind == ChatReplyKind.FormRequest && message.FormDatabaseId == setup.DatabaseId).ShouldBe(2);

        // One selection call per question, purpose form-request, attributed, no content; the form
        // itself still calls no model.
        var invocations = await FormInvocationsAsync(setup.Org);
        invocations.Count.ShouldBe(3);
        invocations.ShouldAllBe(invocation => invocation.AccountId == setup.Org.Member.Id
            && invocation.AssistantId == setup.AssistantId
            && invocation.Succeeded);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task An_unoffered_id_or_tool_is_no_form_and_reveals_nothing()
    {
        await using var model = ModelMode();
        var setup = await CreateSetupAsync(model, keepConversations: false);

        // Another organization's database, one of this organization's that is not the form target,
        // a made-up id, a malformed one, another tool: all the same ordinary reply.
        var orgB = await CreateOrganizationAsync("組織 B");
        var foreign = await CreateDatabaseAsync(await SignInAsync(_host.Factory, orgB, "admin"), "他組織的資料庫");
        var unconnected = await CreateDatabaseAsync(setup.Admin, "沒連接的資料庫");
        var replies = new List<string>();
        foreach (var call in new[]
        {
            FormCall(foreign), FormCall(unconnected), FormCall(Guid.NewGuid()), FormCall("not-a-guid"),
            FormCall(setup.DatabaseId, name: "database_record_count"),
        })
        {
            var run = await RunAsync(setup.Member, setup.AssistantId, $"我要回報 {call}");
            run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
            var reply = run.Reply!.Value.GetProperty("reply");
            reply.GetProperty("kind").GetString().ShouldNotBe("form-request");
            reply.GetProperty("form").ValueKind.ShouldBe(JsonValueKind.Null);
            run.Body.ShouldNotContain("他組織的資料庫");
            run.Body.ShouldNotContain("沒連接的資料庫");
            replies.Add(reply.GetRawText());
        }

        replies.Distinct().Count().ShouldBe(1);

        // The offered id itself is a form.
        Kind(await RunAsync(setup.Member, setup.AssistantId, $"我要回報 {FormCall(setup.DatabaseId)}")).ShouldBe("form-request");
    }

    [Fact]
    public async Task Authorization_is_148s_and_takes_effect_on_the_next_request()
    {
        await using var model = ModelMode();
        var setup = await CreateSetupAsync(model, keepConversations: true);

        // Not shared with the caller, or another organization's account: the same refusal as #148.
        var outsider = await SignInAsync(model, setup.Org, "internal");
        var orgB = await CreateOrganizationAsync("組織 C");
        foreach (var caller in new[] { outsider, await SignInAsync(model, orgB, "admin") })
        {
            var refused = await RunAsync(caller, setup.AssistantId, FormQuestion);
            refused.Status.ShouldBe(HttpStatusCode.Forbidden);
            JsonDocument.Parse(refused.Body).RootElement.GetProperty("reason").GetString().ShouldBe("assistant-use");
        }

        Kind(await RunAsync(setup.Member, setup.AssistantId, FormQuestion)).ShouldBe("form-request");
        (await FormInvocationsAsync(setup.Org)).Count.ShouldBe(1);

        // The owner clears the form target: the tool is no longer offered, so no model call and no
        // form, even when the model would ask for it by id.
        var cleared = await setup.Admin.Spa.PatchAsync($"{AssistantsPath}/{setup.AssistantId}/settings", setup.Admin.Token, new
        {
            rules = new { dataWriteDatabaseId = string.Empty },
        });
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(CancellationToken));
        var revoked = await RunAsync(setup.Member, setup.AssistantId, $"我要回報 {FormCall(setup.DatabaseId)}");
        revoked.Status.ShouldBe(HttpStatusCode.OK, revoked.Body);
        Kind(revoked).ShouldNotBe("form-request");
        revoked.Body.ShouldNotContain(DatabaseName);
        (await FormInvocationsAsync(setup.Org)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_model_failure_falls_back_to_the_keyword_gate()
    {
        await using var model = ModelMode();
        var setup = await CreateSetupAsync(model, keepConversations: true);

        // A fill-in word: the form, as keyword mode would.
        var asked = await RunAsync(setup.Member, setup.AssistantId, $"{FormQuestion} {FakeChatDirectives.FailMidway}");
        asked.Status.ShouldBe(HttpStatusCode.OK, asked.Body);
        Kind(asked).ShouldBe("form-request");

        // No fill-in word: no form; the question goes on to the usual answer.
        var other = await RunAsync(setup.Member, setup.AssistantId, $"{UnmarkedQuestion} {FakeChatDirectives.FailMidway}", asked.ThreadId!.Value.ToString());
        other.Status.ShouldBe(HttpStatusCode.OK, other.Body);
        other.Body.ShouldNotContain("\"form-request\"");

        // The failed calls are recorded as failed, like every model call.
        var invocations = await FormInvocationsAsync(setup.Org);
        invocations.Count.ShouldBe(2);
        invocations.ShouldAllBe(invocation => !invocation.Succeeded && invocation.AccountId == setup.Org.Member.Id);
    }

    [Fact]
    public async Task Without_saved_conversations_the_form_is_shown_and_nothing_is_saved()
    {
        await using var model = ModelMode();
        var setup = await CreateSetupAsync(model, keepConversations: false);

        var run = await RunAsync(setup.Member, setup.AssistantId, FormQuestion);
        Kind(run).ShouldBe("form-request");
        run.Reply!.Value.GetProperty("reply").GetProperty("form").GetProperty("id").GetGuid().ShouldBe(setup.DatabaseId);

        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        (await dbContext.ChatMessages.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.ChatThreads.CountAsync(CancellationToken)).ShouldBe(0);
        (await FormInvocationsAsync(setup.Org)).Count.ShouldBe(1);
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private WebApplicationFactory<Program> ModelMode() =>
        _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Chat:FormRequests:Trigger", "model"));

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Member);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private sealed record Setup(TestOrganization Org, SignedIn Admin, SignedIn Member, Guid AssistantId, Guid DatabaseId);

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

    private async Task<List<ModelInvocation>> FormInvocationsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.FormRequest)
            .ToListAsync(CancellationToken);
    }

    /// <summary>The admin's database (customer-profile template) connected to the admin's assistant
    /// as its form target; the assistant is shared with the member (who may not read records).</summary>
    private async Task<Setup> CreateSetupAsync(WebApplicationFactory<Program> factory, bool keepConversations)
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(_host.Factory, org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, DatabaseName);
        var assistantId = await CreateAssistantAsync(org, keepConversations);
        var connected = await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{databaseId}", admin.Token, new { });
        connected.StatusCode.ShouldBe(HttpStatusCode.OK, await connected.Content.ReadAsStringAsync(CancellationToken));
        var target = await admin.Spa.PatchAsync($"{AssistantsPath}/{assistantId}/settings", admin.Token, new
        {
            rules = new { dataWriteDatabaseId = databaseId.ToString(), dataWritePurpose = CollectionPurpose },
        });
        target.StatusCode.ShouldBe(HttpStatusCode.OK, await target.Content.ReadAsStringAsync(CancellationToken));
        return new Setup(org, admin, await SignInAsync(factory, org, "member"), assistantId, databaseId);
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "表單工具農場")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}同仁", AccountPermission.UseSharedAssistants);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}成員", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, internalEmployee, member);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private static async Task<SignedIn> SignInAsync(WebApplicationFactory<Program> factory, TestOrganization org, string loginName)
    {
        var spa = new SpaClient(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name)
    {
        var response = await owner.Spa.PostAsync(DatabasesPath, owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>The admin's assistant, shared with the member only.</summary>
    private async Task<Guid> CreateAssistantAsync(TestOrganization org, bool keepConversations)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Admin.Id, "田間小幫手", "協助回報田間異常", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: keepConversations, now);
        dbContext.Assistants.Add(assistant);
        dbContext.AssistantShares.Add(new AssistantShare(assistant, org.Member.Id));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private static async Task<RecordedRun> RunAsync(SignedIn caller, Guid assistantId, string question, string? threadId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AssistantsPath}/{assistantId}/chat/runs")
        {
            Content = JsonContent.Create(new
            {
                threadId = threadId ?? string.Empty,
                runId = "run-form-tool",
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
