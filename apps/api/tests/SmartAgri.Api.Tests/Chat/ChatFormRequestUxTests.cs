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
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Chat;

/// <summary>
/// The three API pieces of the in-conversation form experience (M4 #171), against real PostgreSQL
/// with the <c>Fake</c> models: E1 the <c>smartagri.form-check</c> event (model mode with a form
/// target only), E2 <c>GET .../chat/forms</c> (#148's authorization) and E3
/// <c>POST .../chat/forms/{databaseId}/dismissals</c> (no content; only the member in that
/// conversation).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class ChatFormRequestUxTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Chat-Form-Ux-Pass-1!";
    private const string AssistantsPath = "/api/v1/assistants";
    private const string DatabasesPath = "/api/v1/databases";
    private const string CollectionPurpose = "記錄田區異常，方便技術人員追蹤處理。";
    private const string DatabaseName = "田間異常資料庫";
    private const string FormQuestion = "我要回報今天 A 區的病蟲害";
    private const string UnmarkedQuestion = "3 號溫室的番茄葉子出現黃斑";

    private readonly AuthHostFixture _host;

    public ChatFormRequestUxTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- E1: smartagri.form-check -----------------------------------------------------------------

    [Fact]
    public async Task Model_mode_with_a_form_target_sends_form_check_before_the_answer_or_the_form()
    {
        await using var model = ModelMode();
        var setup = await CreateSetupAsync(model, keepConversations: true);

        foreach (var (question, expectForm) in new[]
        {
            (FormQuestion, true),
            ($"{UnmarkedQuestion} {FakeChatDirectives.NoForm}", false),
            // A model failure falls back to the keyword gate: the event was already sent.
            ($"{FormQuestion} {FakeChatDirectives.FailMidway}", true),
        })
        {
            var run = await RunAsync(setup.Member, setup.AssistantId, question);
            run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
            var types = run.Events.Select(Describe).ToList();
            types.Count(type => type == $"CUSTOM {ChatRunEndpoints.FormCheckEventName}").ShouldBe(1, string.Join(", ", types));

            var check = types.IndexOf($"CUSTOM {ChatRunEndpoints.FormCheckEventName}");
            check.ShouldBeGreaterThan(types.IndexOf("TEXT_MESSAGE_START"));
            check.ShouldBeLessThan(types.IndexOf("TEXT_MESSAGE_CONTENT"));
            check.ShouldBeLessThan(types.IndexOf("CUSTOM smartagri.reply"));
            var value = run.Events[check].GetProperty("value");
            value.ValueKind.ShouldBe(JsonValueKind.Object);
            value.EnumerateObject().ShouldBeEmpty();

            (run.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString() == "form-request").ShouldBe(expectForm, question);
        }
    }

    [Fact]
    public async Task Keyword_mode_never_sends_form_check()
    {
        var setup = await CreateSetupAsync(_host.Factory, keepConversations: true);

        foreach (var question in new[] { FormQuestion, UnmarkedQuestion })
        {
            var run = await RunAsync(setup.Member, setup.AssistantId, question);
            run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
            run.Body.ShouldNotContain(ChatRunEndpoints.FormCheckEventName);
        }
    }

    [Fact]
    public async Task An_assistant_without_a_form_target_never_sends_form_check_even_in_model_mode()
    {
        await using var model = ModelMode();
        var setup = await CreateSetupAsync(model, keepConversations: false);
        var cleared = await setup.Admin.Spa.PatchAsync($"{AssistantsPath}/{setup.AssistantId}/settings", setup.Admin.Token, new
        {
            rules = new { dataWriteDatabaseId = string.Empty },
        });
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(CancellationToken));

        foreach (var question in new[] { FormQuestion, $"{UnmarkedQuestion} {FakeChatDirectives.FormRequest}" })
        {
            var run = await RunAsync(setup.Member, setup.AssistantId, question);
            run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
            run.Body.ShouldNotContain(ChatRunEndpoints.FormCheckEventName);
        }
    }

    // --- E2: GET .../chat/forms -------------------------------------------------------------------

    [Fact]
    public async Task The_form_list_is_the_form_a_request_would_show_now()
    {
        var setup = await CreateSetupAsync(_host.Factory, keepConversations: true);

        var listed = await ListAsync(setup.Member, setup.AssistantId);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var forms = await BodyJsonAsync(listed);
        forms.ValueKind.ShouldBe(JsonValueKind.Array);
        forms.GetArrayLength().ShouldBe(1);
        OpenApiContract.AssertKeysMatchSchema(forms[0], "ChatFormRequestView");
        forms[0].GetProperty("id").GetGuid().ShouldBe(setup.DatabaseId);
        forms[0].GetProperty("title").GetString().ShouldBe(DatabaseName);

        // Exactly the form a form request carries.
        var run = await RunAsync(setup.Member, setup.AssistantId, FormQuestion);
        var requested = run.Reply!.Value.GetProperty("reply").GetProperty("form");
        JsonNode.DeepEquals(JsonNode.Parse(forms[0].GetRawText()), JsonNode.Parse(requested.GetRawText())).ShouldBeTrue();

        // Re-authorized on every request: no form target, an empty list.
        var cleared = await setup.Admin.Spa.PatchAsync($"{AssistantsPath}/{setup.AssistantId}/settings", setup.Admin.Token, new
        {
            rules = new { dataWriteDatabaseId = string.Empty },
        });
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(CancellationToken));
        var empty = await ListAsync(setup.Member, setup.AssistantId);
        empty.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await empty.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("[]");
    }

    [Fact]
    public async Task The_form_list_refuses_like_148_and_reveals_nothing()
    {
        var setup = await CreateSetupAsync(_host.Factory, keepConversations: true);

        var unauthenticated = await setup.Member.Spa.Http.GetAsync($"{AssistantsPath}/{setup.AssistantId}/chat/forms", CancellationToken);
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Not shared with the caller, another organization's account, an assistant that does not
        // exist: one byte-identical refusal.
        var outsider = await SignInAsync(_host.Factory, setup.Org, "internal");
        var orgB = await CreateOrganizationAsync("組織 B");
        var foreign = await SignInAsync(_host.Factory, orgB, "admin");
        var bodies = new List<string>();
        foreach (var (caller, assistantId) in new[]
        {
            (outsider, setup.AssistantId), (foreign, setup.AssistantId), (setup.Member, Guid.NewGuid()),
        })
        {
            var refused = await ListAsync(caller, assistantId);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var body = await refused.Content.ReadAsStringAsync(CancellationToken);
            JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString().ShouldBe("assistant-use");
            body.ShouldNotContain(DatabaseName);
            bodies.Add(body);
        }

        bodies.Distinct().Count().ShouldBe(1);
    }

    // --- E3: POST .../chat/forms/{databaseId}/dismissals -----------------------------------------

    [Fact]
    public async Task A_dismissal_records_the_event_assistant_form_and_time_only()
    {
        var setup = await CreateSetupAsync(_host.Factory, keepConversations: true);
        var run = await RunAsync(setup.Member, setup.AssistantId, FormQuestion);
        var threadId = run.ThreadId!.Value;
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var dismissed = await DismissAsync(setup.Member, setup.AssistantId, setup.DatabaseId, new { threadId });
        dismissed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await dismissed.Content.ReadAsStringAsync(CancellationToken));
        // The body is optional (an unsaved conversation has no thread to name).
        var bare = await setup.Member.Spa.Http.SendAsync(Authorized(HttpMethod.Post, DismissalsPath(setup.AssistantId, setup.DatabaseId), setup.Member.Token), CancellationToken);
        bare.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        var rows = await dbContext.ChatFormDismissals.AsNoTracking().ToListAsync(CancellationToken);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => row.AssistantId == setup.AssistantId && row.DatabaseId == setup.DatabaseId
            && row.OrganizationId == setup.Org.Organization.Id && row.At > before);

        // Nothing else was written: no submission, no message.
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.ChatMessages.CountAsync(message => message.ThreadId == threadId, CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Only_the_member_in_that_conversation_may_record_a_dismissal()
    {
        var setup = await CreateSetupAsync(_host.Factory, keepConversations: true);
        var run = await RunAsync(setup.Member, setup.AssistantId, FormQuestion);
        var threadId = run.ThreadId!.Value;

        // Another member who may use the assistant, naming the first member's conversation.
        var colleague = await SignInAsync(_host.Factory, setup.Org, "internal");
        await ShareAsync(setup, setup.Org.Internal.Id);
        var otherThread = await DismissAsync(colleague, setup.AssistantId, setup.DatabaseId, new { threadId });
        otherThread.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReasonAsync(otherThread)).ShouldBe("chat-thread");

        // A caller who may not use the assistant at all, another organization's account.
        var orgB = await CreateOrganizationAsync("組織 D");
        var foreign = await SignInAsync(_host.Factory, orgB, "admin");
        var foreignRefused = await DismissAsync(foreign, setup.AssistantId, setup.DatabaseId, new { threadId });
        foreignRefused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReasonAsync(foreignRefused)).ShouldBe("assistant-use");

        // A database that is not the assistant's form target.
        var notTarget = await DismissAsync(setup.Member, setup.AssistantId, Guid.NewGuid(), new { threadId });
        notTarget.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReasonAsync(notTarget)).ShouldBe("assistant-form");

        var unauthenticated = await setup.Member.Spa.PostAsync(DismissalsPath(setup.AssistantId, setup.DatabaseId), null, new { threadId });
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        (await dbContext.ChatFormDismissals.CountAsync(CancellationToken)).ShouldBe(0);

        // The colleague's own dismissal, without naming a conversation, is fine.
        (await DismissAsync(colleague, setup.AssistantId, setup.DatabaseId, new { })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await dbContext.ChatFormDismissals.CountAsync(CancellationToken)).ShouldBe(1);
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

    private static string Describe(JsonElement e) =>
        e.GetProperty("type").GetString() == "CUSTOM" ? $"CUSTOM {e.GetProperty("name").GetString()}" : e.GetProperty("type").GetString()!;

    private static string DismissalsPath(Guid assistantId, Guid databaseId) =>
        $"{AssistantsPath}/{assistantId}/chat/forms/{databaseId}/dismissals";

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static Task<HttpResponseMessage> ListAsync(SignedIn caller, Guid assistantId) =>
        caller.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat/forms", caller.Token);

    private static Task<HttpResponseMessage> DismissAsync(SignedIn caller, Guid assistantId, Guid databaseId, object body) =>
        caller.Spa.PostAsync(DismissalsPath(assistantId, databaseId), caller.Token, body);

    private static async Task<string?> ReasonAsync(HttpResponseMessage response) =>
        (await BodyJsonAsync(response)).GetProperty("reason").GetString();

    private async Task ShareAsync(Setup setup, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(candidate => candidate.Id == setup.AssistantId, CancellationToken);
        dbContext.AssistantShares.Add(new AssistantShare(assistant, accountId));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>The admin's database (customer-profile template) connected to the admin's assistant
    /// as its form target; the assistant is shared with the member only.</summary>
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

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "表單體驗農場")
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
                runId = "run-form-ux",
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
