using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Chat;

/// <summary>
/// Conversation threads and history (M3 plan, Slice 6 acceptance; ticket #76), plus the two
/// acceptance criteria moved here from #73 (PR #88's note): unsharing blocks the conversation
/// endpoints with <c>403 assistant-use</c>, and the thread reappears once re-shared. Sending a
/// message and streaming are #77, so tests seed <see cref="ChatMessage"/>/<see cref="ChatMessageCitation"/>
/// rows directly, exactly as the M3 plan's scope note for this ticket allows.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class ChatEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Chat-Endpoint-Pass-1!";

    private readonly AuthHostFixture _host;

    public ChatEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: the owner cannot read another account's threads --------------------

    [Fact]
    public async Task Owner_reading_a_members_thread_id_gets_the_same_bytes_as_a_nonexistent_one()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "internal");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        await ShareWithAsync(org, assistantId, org.Internal.Id);

        var threadId = await CreateThreadAsync(org, assistantId, org.Internal.Id, "同仁的對話");

        var basePath = $"/api/v1/assistants/{assistantId}/chat";
        (string Verb, Func<Guid, Task<HttpResponseMessage>> Send)[] endpoints =
        [
            ("GET chat?conversation=", id => admin.Spa.GetAsync($"{basePath}?conversation={id}", admin.Token)),
            ("PATCH conversation", id => admin.Spa.PatchAsync($"{basePath}/conversations/{id}", admin.Token, new { title = "改名" })),
            ("DELETE conversation", id => admin.Spa.DeleteAsync($"{basePath}/conversations/{id}", admin.Token)),
        ];

        foreach (var (verb, send) in endpoints)
        {
            var toMembers = await send(threadId);
            var toNonexistent = await send(Guid.NewGuid());

            toMembers.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            await AssertIdenticalAsync(toMembers, toNonexistent);
            (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("chat-thread", verb);
        }

        // The owner's meddling changed nothing: the member's thread is untouched.
        var list = await BodyJsonAsync(await member.Spa.GetAsync($"{basePath}/conversations", member.Token));
        list.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("title").GetString())
            .ShouldContain("同仁的對話");
    }

    [Fact]
    public async Task The_owner_also_cannot_list_or_see_that_a_members_thread_exists()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        await ShareWithAsync(org, assistantId, org.Internal.Id);
        await CreateThreadAsync(org, assistantId, org.Internal.Id, "同仁的私人對話");

        var list = await BodyJsonAsync(
            await admin.Spa.GetAsync($"/api/v1/assistants/{assistantId}/chat/conversations", admin.Token));
        list.GetProperty("threads").GetArrayLength().ShouldBe(0, "the owner has no threads of their own here");
    }

    // --- Acceptance: unsharing blocks chat endpoints; the thread survives (moved from #73) --

    [Fact]
    public async Task Unsharing_blocks_conversation_endpoints_and_resharing_makes_the_thread_reappear()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "internal");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        await ShareWithAsync(org, assistantId, org.Internal.Id);
        var threadId = await CreateThreadAsync(org, assistantId, org.Internal.Id, "同仁的對話");

        var basePath = $"/api/v1/assistants/{assistantId}/chat";
        (await member.Spa.GetAsync($"{basePath}/conversations", member.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Unshare (empty platform sharing list).
        await admin.Spa.PutAsync($"/api/v1/assistants/{assistantId}/publishing/platform", admin.Token, new { accountIds = Array.Empty<string>() });

        var afterUnshare = await member.Spa.GetAsync($"{basePath}/conversations", member.Token);
        afterUnshare.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(afterUnshare)).GetProperty("reason").GetString().ShouldBe("assistant-use");

        // The thread is untouched in the database.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.ChatThreads.AnyAsync(t => t.Id == threadId, CancellationToken)).ShouldBeTrue();
        }

        // Reshare: the thread reappears.
        await ShareWithAsync(org, assistantId, org.Internal.Id);
        var afterReshare = await BodyJsonAsync(await member.Spa.GetAsync($"{basePath}/conversations", member.Token));
        afterReshare.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ShouldContain(threadId);
    }

    // --- Acceptance: KeepConversations = false ------------------------------------------

    [Fact]
    public async Task KeepConversations_false_refuses_creating_a_thread_and_get_chat_has_no_thread()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "不保存對話的助理", keepConversations: false);

        var basePath = $"/api/v1/assistants/{assistantId}/chat";
        var created = await admin.Spa.PostAsync($"{basePath}/conversations", admin.Token, new { });
        created.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(created)).GetProperty("message").GetString().ShouldNotBeNullOrEmpty();

        var chat = await BodyJsonAsync(await admin.Spa.GetAsync(basePath, admin.Token));
        chat.GetProperty("threadId").ValueKind.ShouldBe(JsonValueKind.Null);
        chat.GetProperty("historyMode").GetString().ShouldBe("not-saved");
        chat.GetProperty("messages").GetArrayLength().ShouldBe(0);

        var list = await BodyJsonAsync(await admin.Spa.GetAsync($"{basePath}/conversations", admin.Token));
        list.GetProperty("historyMode").GetString().ShouldBe("not-saved");
        list.GetProperty("threads").GetArrayLength().ShouldBe(0);
    }

    // --- Acceptance: title validation ----------------------------------------------------

    [Fact]
    public async Task Renaming_with_a_blank_or_too_long_title_is_422_with_only_a_message()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        var threadId = await CreateThreadAsync(org, assistantId, org.Admin.Id, "原始標題");
        var basePath = $"/api/v1/assistants/{assistantId}/chat/conversations/{threadId}";

        var blank = await admin.Spa.PatchAsync(basePath, admin.Token, new { title = "   " });
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(blank)).GetProperty("message").GetString().ShouldNotBeNullOrEmpty();

        var tooLong = await admin.Spa.PatchAsync(basePath, admin.Token, new { title = new string('長', 61) });
        tooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.ChatThreads.SingleAsync(t => t.Id == threadId, CancellationToken)).Title.ShouldBe("原始標題");
    }

    [Fact]
    public async Task Renaming_with_a_valid_title_succeeds_and_deleting_returns_the_remaining_list()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        var threadId1 = await CreateThreadAsync(org, assistantId, org.Admin.Id, "第一段對話");
        var threadId2 = await CreateThreadAsync(org, assistantId, org.Admin.Id, "第二段對話");
        var basePath = $"/api/v1/assistants/{assistantId}/chat";

        var renamed = await admin.Spa.PatchAsync($"{basePath}/conversations/{threadId1}", admin.Token, new { title = "改過的標題" });
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(renamed)).GetProperty("title").GetString().ShouldBe("改過的標題");

        var deleted = await admin.Spa.DeleteAsync($"{basePath}/conversations/{threadId1}", admin.Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var remaining = await BodyJsonAsync(deleted);
        var ids = remaining.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        ids.ShouldNotContain(threadId1);
        ids.ShouldContain(threadId2);
    }

    // --- GET chat: messages and citations round-trip --------------------------------------

    [Fact]
    public async Task Get_chat_returns_seeded_messages_with_citations_and_defaults_to_the_most_recent_thread()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        var olderThreadId = await CreateThreadAsync(org, assistantId, org.Admin.Id, "較舊的對話");
        var newerThreadId = await CreateThreadAsync(org, assistantId, org.Admin.Id, "較新的對話", secondsAfterOlder: 60);

        var effectiveFrom = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        await SeedCompanyDataExchangeAsync(org, newerThreadId, "退貨期限是幾天？", "七天內可以退貨 [1]。", effectiveFrom);

        // No ?conversation= given: opens the most recently active thread.
        var chat = await BodyJsonAsync(await admin.Spa.GetAsync($"/api/v1/assistants/{assistantId}/chat", admin.Token));
        chat.GetProperty("threadId").GetGuid().ShouldBe(newerThreadId);
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        messages.Count.ShouldBe(2);
        messages[0].GetProperty("author").GetString().ShouldBe("account");
        messages[0].GetProperty("text").GetString().ShouldBe("退貨期限是幾天？");
        messages[1].GetProperty("author").GetString().ShouldBe("assistant");
        var reply = messages[1].GetProperty("reply");
        reply.GetProperty("kind").GetString().ShouldBe("company-data");
        reply.GetProperty("text").GetString().ShouldBe("七天內可以退貨 [1]。");
        var citation = reply.GetProperty("citations").EnumerateArray().Single();
        citation.GetProperty("documentName").GetString().ShouldBe("退貨政策");
        citation.GetProperty("updatedLabel").GetString().ShouldBe("2026-01-15");

        // Explicit ?conversation= for the older, empty thread.
        var olderChat = await BodyJsonAsync(
            await admin.Spa.GetAsync($"/api/v1/assistants/{assistantId}/chat?conversation={olderThreadId}", admin.Token));
        olderChat.GetProperty("threadId").GetGuid().ShouldBe(olderThreadId);
        olderChat.GetProperty("messages").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Citation_detail_endpoint_returns_the_full_snapshot_and_rejects_another_accounts_message()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "internal");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        await ShareWithAsync(org, assistantId, org.Internal.Id);
        var threadId = await CreateThreadAsync(org, assistantId, org.Admin.Id, "管理者的對話");
        var effectiveFrom = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var messageId = await SeedCompanyDataExchangeAsync(org, threadId, "問題", "答案 [1]。", effectiveFrom);

        var detail = await BodyJsonAsync(await admin.Spa.GetAsync(
            $"/api/v1/assistants/{assistantId}/chat/citations/{messageId}/1", admin.Token));
        detail.GetProperty("text").GetString().ShouldBe("完整的引用原文內容。");
        detail.GetProperty("updatedLabel").GetString().ShouldBe("2026-02-01");

        // A different account (not the thread's owner) gets the same 403 as an unknown message id.
        var toMembers = await member.Spa.GetAsync($"/api/v1/assistants/{assistantId}/chat/citations/{messageId}/1", member.Token);
        var toNonexistent = await member.Spa.GetAsync($"/api/v1/assistants/{assistantId}/chat/citations/{Guid.NewGuid()}/1", member.Token);
        toMembers.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(toMembers, toNonexistent);
    }

    // --- Acceptance: deleting the assistant deletes everyone's threads -------------------

    [Fact]
    public async Task Deleting_the_assistant_deletes_every_accounts_chat_threads()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        await ShareWithAsync(org, assistantId, org.Internal.Id);
        var ownerThreadId = await CreateThreadAsync(org, assistantId, org.Admin.Id, "擁有者的對話");
        var memberThreadId = await CreateThreadAsync(org, assistantId, org.Internal.Id, "同仁的對話");

        var response = await admin.Spa.DeleteAsync($"/api/v1/assistants/{assistantId}", admin.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.ChatThreads.AnyAsync(t => t.Id == ownerThreadId || t.Id == memberThreadId, CancellationToken))
            .ShouldBeFalse();
    }

    // --- Recent conversations across assistants (sidebar) ---------------------------------

    [Fact]
    public async Task Recent_conversations_lists_up_to_ten_across_usable_assistants_only()
    {
        var org = await CreateOrganizationAsync();
        var member = await SignInAsync(org, "internal");
        var usableAssistantId = await CreateAssistantAsync(org, org.Admin.Id, "可使用的助理");
        await ShareWithAsync(org, usableAssistantId, org.Internal.Id);
        var unsharedAssistantId = await CreateAssistantAsync(org, org.Admin.Id, "沒有分享的助理");

        var usableThreadId = await CreateThreadAsync(org, usableAssistantId, org.Internal.Id, "可看到的對話");
        await CreateThreadAsync(org, unsharedAssistantId, org.Admin.Id, "不該出現的對話（別的助理、也不是同仁的）");

        var recent = await BodyJsonAsync(await member.Spa.GetAsync("/api/v1/chat/recent-conversations", member.Token));
        var ids = recent.EnumerateArray().Select(item => item.GetProperty("threadId").GetGuid()).ToList();
        ids.ShouldContain(usableThreadId);
        ids.Count.ShouldBeLessThanOrEqualTo(10);
    }

    // --- Helpers --------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManagePublishing, AccountPermission.ManageDataSources);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants);

        return new TestOrganization(organization, admin, internalEmployee);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private async Task<Guid> CreateAssistantAsync(
        TestOrganization org, Guid ownerAccountId, string name, bool keepConversations = true)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, ownerAccountId, name, "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: keepConversations, now);
        dbContext.Assistants.Add(assistant);
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private async Task ShareWithAsync(TestOrganization org, Guid assistantId, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(a => a.Id == assistantId, CancellationToken);
        dbContext.AssistantShares.Add(new AssistantShare(assistant, accountId));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task<Guid> CreateThreadAsync(
        TestOrganization org, Guid assistantId, Guid accountId, string title, int secondsAfterOlder = 0)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(secondsAfterOlder);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(a => a.Id == assistantId, CancellationToken);
        var thread = new ChatThread(assistant, accountId, title, now);
        dbContext.ChatThreads.Add(thread);
        await dbContext.SaveChangesAsync(CancellationToken);
        return thread.Id;
    }

    /// <summary>Seeds one account question and one <c>company-data</c> assistant reply citing
    /// one passage, exactly the shape #77 will write.</summary>
    private async Task<Guid> SeedCompanyDataExchangeAsync(
        TestOrganization org, Guid threadId, string question, string answerText, DateTimeOffset versionEffectiveFrom)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var thread = await dbContext.ChatThreads.SingleAsync(t => t.Id == threadId, CancellationToken);

        var accountMessage = ChatMessage.Account(thread, question, now);
        dbContext.ChatMessages.Add(accountMessage);
        thread.RegisterMessage(now);

        var assistantMessage = ChatMessage.Assistant(thread, answerText, ChatReplyKind.CompanyData, null, [], now);
        dbContext.ChatMessages.Add(assistantMessage);
        thread.RegisterMessage(now);

        // Not-yet-real chunk/knowledge-base/document/version ids: #77 will populate real ones
        // when it saves a genuine retrieval; the optional back-reference columns tolerate null
        // exactly as they do once a source is later deleted (see
        // ChatMessageCitationConfiguration's remarks) — only the snapshot columns are load-bearing here.
        dbContext.ChatMessageCitations.Add(new ChatMessageCitation(
            assistantMessage,
            ordinal: 1,
            chunkId: null,
            knowledgeBaseId: null,
            knowledgeBaseName: "退貨知識庫",
            documentId: null,
            documentName: "退貨政策",
            versionId: null,
            versionNumber: 1,
            versionEffectiveFrom: versionEffectiveFrom,
            locationLabel: "第 1 頁",
            excerpt: "完整的引用原文內容。",
            text: "完整的引用原文內容。"));

        await dbContext.SaveChangesAsync(CancellationToken);
        return assistantMessage.Id;
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertIdenticalAsync(HttpResponseMessage first, HttpResponseMessage second)
    {
        var a = await ResponseFingerprint.FromAsync(first);
        var b = await ResponseFingerprint.FromAsync(second);

        b.Status.ShouldBe(a.Status);
        b.ContentType.ShouldBe(a.ContentType);
        b.Body.ShouldBe(a.Body);
        b.SetsCookie.ShouldBe(a.SetsCookie);
    }
}
