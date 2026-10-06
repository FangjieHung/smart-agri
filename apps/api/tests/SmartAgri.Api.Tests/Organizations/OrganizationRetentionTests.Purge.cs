using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// Deleting an assistant's saved conversations at once (M6 plan §3 H, §5 Slice 5, issue #242): the
/// manager's per-assistant list, one assistant's summary and the purge, against real PostgreSQL.
/// Responses carry numbers only; the purge touches that assistant's threads and nothing else, in any
/// organization.
/// </summary>
public sealed partial class OrganizationRetentionTests
{
    private const string RetentionAssistantsPath = "/api/v1/organization/retention/assistants";

    /// <summary>Words of the seeded threads' titles and messages: no response may contain them.</summary>
    private static readonly string[] ConversationWords = ["保存期限測試", "退貨要多久", "七天內可以退貨", "退貨政策", "成員自己的標題"];

    private static string SummaryPath(Guid assistantId) => $"/api/v1/assistants/{assistantId}/chat/conversations/summary";

    private static string PurgePath(Guid assistantId) => $"/api/v1/assistants/{assistantId}/chat/conversations:purge";

    [Fact]
    public async Task The_list_counts_each_assistants_threads_members_and_last_activity_and_carries_no_content()
    {
        var org = await CreateOrganizationAsync();
        var owned = await CreateMemberAssistantAsync(org, keepConversations: false);
        var now = _host.Clock.GetUtcNow();
        await SeedThreadAsync(org, now.AddDays(-3));
        await SeedThreadAsync(org, now.AddHours(-2));
        await SeedThreadOnAsync(org, org.AssistantId, org.External.Id, now.AddDays(-1));
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.GetAsync(RetentionAssistantsPath, admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var raw = await response.Content.ReadAsStringAsync(CancellationToken);
        ConversationWords.ShouldAllBe(word => !raw.Contains(word));
        var rows = JsonDocument.Parse(raw).RootElement.Clone().EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        foreach (var row in rows)
        {
            OpenApiContract.AssertKeysMatchSchema(row, "OrganizationRetentionAssistantView");
        }

        var first = rows[0];
        (first.GetProperty("assistantId").GetGuid(), first.GetProperty("assistantName").GetString(), first.GetProperty("keepConversations").GetBoolean())
            .ShouldBe((org.AssistantId, "保存助理", true));
        (first.GetProperty("threadCount").GetInt32(), first.GetProperty("accountCount").GetInt32()).ShouldBe((3, 2));
        first.GetProperty("lastActivityAt").GetDateTimeOffset().ShouldBe(now.AddHours(-2), TimeSpan.FromMilliseconds(1));

        var second = rows[1];
        (second.GetProperty("assistantId").GetGuid(), second.GetProperty("assistantName").GetString(), second.GetProperty("keepConversations").GetBoolean())
            .ShouldBe((owned, "同仁助理", false));
        (second.GetProperty("threadCount").GetInt32(), second.GetProperty("accountCount").GetInt32()).ShouldBe((0, 0));
        second.GetProperty("lastActivityAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_purge_deletes_every_members_threads_on_that_assistant_and_nothing_else_and_records_it()
    {
        var org = await CreateOrganizationAsync();
        var owned = await CreateMemberAssistantAsync(org, keepConversations: true);
        var neighbour = await CreateOrganizationAsync();
        var now = _host.Clock.GetUtcNow();
        var purged = new[]
        {
            (await SeedThreadAsync(org, now.AddDays(-2))).ThreadId,
            (await SeedThreadAsync(org, now.AddHours(-1))).ThreadId,
            (await SeedThreadOnAsync(org, org.AssistantId, org.External.Id, now.AddHours(-3))).ThreadId,
        };
        var kept = await SeedThreadOnAsync(org, owned, org.Internal.Id, now.AddHours(-1));
        await SeedOutcomeAsync(org, AnswerOutcomeChannel.Chat, now.AddHours(-1));
        await SeedHandoffIssueAsync(org);
        await SeedOldRowsAsync(neighbour, now.AddHours(-1));
        var neighbourBefore = await CountConversationRowsAsync(neighbour);
        neighbourBefore.ShouldBe([2, 4, 2, 2]);
        var othersBefore = await CountOthersAsync(org);
        var outcomesBefore = (await CountConversationRowsAsync(org))[3];
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.PostAsync(PurgePath(org.AssistantId), admin.Token, new { });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantConversationPurgeView");
        body.GetProperty("deletedThreadCount").GetInt32().ShouldBe(3);
        foreach (var threadId in purged)
        {
            (await ThreadExistsAsync(org, threadId)).ShouldBeFalse();
        }

        // Messages and citations went with their threads; the other assistant's thread stays whole.
        (await ThreadExistsAsync(org, kept.ThreadId)).ShouldBeTrue();
        (await CountConversationRowsAsync(org)).ShouldBe([1, 2, 0, outcomesBefore]);
        // Answer outcomes and the issue's handoff copy (and usage, test runs, reports…) are untouched.
        (await CountOthersAsync(org)).ShouldBe(othersBefore);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var issue = await dbContext.AssistantIssues.AsNoTracking().SingleAsync(CancellationToken);
            (issue.QuestionSnapshot, issue.AnswerSnapshot).ShouldBe(("退貨要多久？", "七天內可以退貨。"));
        }

        // Another organization is never touched.
        (await CountConversationRowsAsync(neighbour)).ShouldBe(neighbourBefore);

        var activity = (await ActivitiesAsync(org)).ShouldHaveSingleItem();
        (activity.Action, activity.ActorAccountId).ShouldBe((OrganizationActivityAction.ConversationsPurged, (Guid?)org.Admin.Id));
        activity.At.ShouldBe(_host.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
        using (var detail = JsonDocument.Parse(activity.Detail!))
        {
            detail.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["assistantId", "assistantName", "threadCount"], ignoreOrder: true);
            detail.RootElement.GetProperty("assistantId").GetGuid().ShouldBe(org.AssistantId);
            detail.RootElement.GetProperty("assistantName").GetString().ShouldBe("保存助理");
            detail.RootElement.GetProperty("threadCount").GetInt32().ShouldBe(3);
        }

        (await ActivitiesAsync(neighbour)).ShouldBeEmpty();

        // The switch off makes no difference, and nothing left to delete is still recorded.
        await SetKeepConversationsAsync(org, org.AssistantId, false);
        var again = await BodyJsonAsync(await admin.Spa.PostAsync(PurgePath(org.AssistantId), admin.Token, new { }));
        again.GetProperty("deletedThreadCount").GetInt32().ShouldBe(0);
        (await ActivitiesAsync(org)).Count.ShouldBe(2);
        (await ThreadExistsAsync(org, kept.ThreadId)).ShouldBeTrue();

        var list = (await BodyJsonAsync(await admin.Spa.GetAsync(RetentionAssistantsPath, admin.Token))).EnumerateArray().ToList();
        list.Select(row => row.GetProperty("threadCount").GetInt32()).ShouldBe([0, 1]);
    }

    [Fact]
    public async Task The_purge_deletes_in_batches_of_the_cleanups_size()
    {
        var org = await CreateOrganizationAsync();
        var now = _host.Clock.GetUtcNow();
        for (var i = 0; i < 5; i++)
        {
            await SeedThreadAsync(org, now.AddHours(-i));
        }

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var service = NewService(dbContext, now, batchSize: 2);

        var deleted = await service.DeleteThreadsInBatchesAsync(
            dbContext.ChatThreads.Where(thread => thread.AssistantId == org.AssistantId), CancellationToken);

        (deleted.Count, deleted.Batches).ShouldBe((5, 3));
        (await CountConversationRowsAsync(org)).Take(3).ShouldBe([0, 0, 0]);
    }

    [Fact]
    public async Task Non_managers_the_owner_included_and_other_organizations_get_the_same_403_for_the_list_and_the_purge()
    {
        var org = await CreateOrganizationAsync();
        var owned = await CreateMemberAssistantAsync(org, keepConversations: true);
        var neighbour = await CreateOrganizationAsync();
        var now = _host.Clock.GetUtcNow();
        await SeedThreadAsync(org, now.AddHours(-1));
        await SeedThreadOnAsync(org, owned, org.Internal.Id, now.AddHours(-1));
        await SeedOldRowsAsync(neighbour, now.AddHours(-1));
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.OrganizationSettings));

        var responses = new List<(string Case, HttpResponseMessage Response)>();
        foreach (var login in new[] { "internal", "external" })
        {
            var caller = await SignInAsync(org, login);
            responses.Add(($"{login} list", await caller.Spa.GetAsync(RetentionAssistantsPath, caller.Token)));
            responses.Add(($"{login} purge the admin's", await caller.Spa.PostAsync(PurgePath(org.AssistantId), caller.Token, new { })));
            // The internal member owns this one, and still is no manager (decision E).
            responses.Add(($"{login} purge the member's", await caller.Spa.PostAsync(PurgePath(owned), caller.Token, new { })));
        }

        var admin = await SignInAsync(org, "admin");
        responses.Add(("admin purge another organization's", await admin.Spa.PostAsync(PurgePath(neighbour.AssistantId), admin.Token, new { })));
        responses.Add(("admin purge an unknown id", await admin.Spa.PostAsync(PurgePath(Guid.CreateVersion7()), admin.Token, new { })));

        foreach (var (name, response) in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, name);
            (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, name);
            response.Content.Headers.ContentType?.ToString().ShouldBe(expected.ContentType, name);
        }

        (await CountConversationRowsAsync(org)).Take(3).ShouldBe([2, 4, 1]);
        (await CountConversationRowsAsync(neighbour)).ShouldBe([2, 4, 2, 2]);
        (await ActivitiesAsync(org)).ShouldBeEmpty();
        (await ActivitiesAsync(neighbour)).ShouldBeEmpty();

        var anonymous = _host.CreateSpaClient().Http;
        (await anonymous.GetAsync(RetentionAssistantsPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(PurgePath(org.AssistantId), null, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(SummaryPath(org.AssistantId), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_owner_and_the_manager_read_the_summary_and_everyone_else_gets_403_assistant_configuration()
    {
        var org = await CreateOrganizationAsync();
        var owned = await CreateMemberAssistantAsync(org, keepConversations: true);
        var neighbour = await CreateOrganizationAsync();
        var now = _host.Clock.GetUtcNow();
        await SeedThreadOnAsync(org, owned, org.Internal.Id, now.AddHours(-2));
        await SeedThreadOnAsync(org, owned, org.Internal.Id, now.AddHours(-1));
        await SeedThreadOnAsync(org, owned, org.External.Id, now.AddHours(-1));
        await SeedThreadAsync(org, now.AddHours(-1));
        var member = await SignInAsync(org, "internal");
        var admin = await SignInAsync(org, "admin");

        // The owner (manage-assistants, not a manager) reads their own assistant's numbers.
        var ownerResponse = await member.Spa.GetAsync(SummaryPath(owned), member.Token);
        ownerResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await ownerResponse.Content.ReadAsStringAsync(CancellationToken));
        var ownerRaw = await ownerResponse.Content.ReadAsStringAsync(CancellationToken);
        ConversationWords.ShouldAllBe(word => !ownerRaw.Contains(word));
        var ownerBody = JsonDocument.Parse(ownerRaw).RootElement.Clone();
        OpenApiContract.AssertKeysMatchSchema(ownerBody, "AssistantConversationSummaryView");
        (ownerBody.GetProperty("threadCount").GetInt32(), ownerBody.GetProperty("accountCount").GetInt32(), ownerBody.GetProperty("canPurge").GetBoolean())
            .ShouldBe((3, 2, false));

        // The manager reads any of the organization's assistants, owner or not, and may purge.
        foreach (var (assistantId, threads, accounts) in new[] { (owned, 3, 2), (org.AssistantId, 1, 1) })
        {
            var body = await BodyJsonAsync(await admin.Spa.GetAsync(SummaryPath(assistantId), admin.Token));
            (body.GetProperty("threadCount").GetInt32(), body.GetProperty("accountCount").GetInt32(), body.GetProperty("canPurge").GetBoolean())
                .ShouldBe((threads, accounts, true));
        }

        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.AssistantConfiguration));
        var external = await SignInAsync(org, "external");
        var denied = new List<(string Case, HttpResponseMessage Response)>
        {
            ("member reads the admin's", await member.Spa.GetAsync(SummaryPath(org.AssistantId), member.Token)),
            ("external reads the member's", await external.Spa.GetAsync(SummaryPath(owned), external.Token)),
            ("member reads an unknown id", await member.Spa.GetAsync(SummaryPath(Guid.CreateVersion7()), member.Token)),
            ("admin reads another organization's", await admin.Spa.GetAsync(SummaryPath(neighbour.AssistantId), admin.Token)),
            ("admin reads an unknown id", await admin.Spa.GetAsync(SummaryPath(Guid.CreateVersion7()), admin.Token)),
        };
        foreach (var (name, response) in denied)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, name);
            (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, name);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>An assistant owned by the internal member (who has <c>manage-assistants</c>).</summary>
    private async Task<Guid> CreateMemberAssistantAsync(TestOrganization org, bool keepConversations)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Internal.Id, "同仁助理", "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: keepConversations, DateTimeOffset.UtcNow.AddSeconds(1));
        dbContext.Assistants.Add(assistant);
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    /// <summary>Like <see cref="SeedThreadAsync"/>, on <paramref name="assistantId"/> for
    /// <paramref name="accountId"/>.</summary>
    private async Task<SeededThread> SeedThreadOnAsync(TestOrganization org, Guid assistantId, Guid accountId, DateTimeOffset at)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(candidate => candidate.Id == assistantId, CancellationToken);
        var thread = new ChatThread(assistant, accountId, "成員自己的標題", at);
        dbContext.ChatThreads.Add(thread);
        var question = ChatMessage.Account(thread, "退貨要多久？", at);
        var answer = ChatMessage.Assistant(thread, "七天內可以退貨。", ChatReplyKind.CompanyData, null, [], at);
        dbContext.ChatMessages.AddRange(question, answer);
        await dbContext.SaveChangesAsync(CancellationToken);
        return new SeededThread(thread.Id, [question.Id, answer.Id]);
    }

    private async Task SeedHandoffIssueAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(candidate => candidate.Id == org.AssistantId, CancellationToken);
        var (issue, created) = AssistantIssue.OpenFromHandoff(
            assistant, org.Internal.Id, "退貨要多久？", "七天內可以退貨。", false, null, DateTimeOffset.UtcNow.AddHours(-1));
        dbContext.AssistantIssues.Add(issue);
        dbContext.AssistantIssueEvents.Add(created);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task SetKeepConversationsAsync(TestOrganization org, Guid assistantId, bool keepConversations)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        await dbContext.Assistants.Where(assistant => assistant.Id == assistantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(assistant => assistant.KeepConversations, keepConversations), CancellationToken);
    }
}
