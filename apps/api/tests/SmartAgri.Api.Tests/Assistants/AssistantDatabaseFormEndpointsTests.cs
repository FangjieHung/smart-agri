using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// An assistant connected to databases and asking for forms in a conversation (M4, issue #148)
/// against real PostgreSQL with the <c>Fake</c> models: connecting and disconnecting several
/// databases the owner may use (and nothing else, without leaking), the form request with the
/// server's form and consent terms, the review/consent/submission through #145's service (same
/// validation, consent and idempotency, source <c>assistant-conversation</c>), revocation taking
/// effect on the next request, the handler seeing only the consented fields, and submissions working
/// the same when conversations are not kept.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class AssistantDatabaseFormEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Assistant-Form-Pass-1!";
    private const string AssistantsPath = "/api/v1/assistants";
    private const string DatabasesPath = "/api/v1/databases";
    private const string CollectionPurpose = "記錄客戶聯絡方式，方便客服回電。";
    private const string FormQuestion = "我想要填寫客戶資料";
    private const string FormForbiddenMessage = "這份表單目前無法使用：助理已不再連接這個資料庫，或你沒有填寫它的權限。";

    private readonly AuthHostFixture _host;

    public AssistantDatabaseFormEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static Dictionary<string, object> ValidAnswers(string name = "王小明") => new()
    {
        ["field-customer-name"] = name,
        ["field-phone"] = "0912-345-678",
        ["field-first-visit"] = "2026-09-21",
        ["field-customer-type"] = "企業",
    };

    // --- Connections ------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_connects_and_disconnects_several_databases_and_sets_the_form_target()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var owner2 = await SignInAsync(org, "owner2");
        var own = await CreateDatabaseAsync(admin, "客戶資料庫");
        var shared = await CreateDatabaseAsync(owner2, "合作夥伴資料庫");
        (await PutAccessAsync(owner2, shared, [org.Owner2.Id, org.Admin.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var assistantId = await CreateAssistantAsync(org, keepConversations: true);

        // Both are connectable: the own one as owner, the designated one read-only.
        var sources = await BodyJsonAsync(await admin.Spa.GetAsync("/api/v1/connectable-sources", admin.Token));
        var databases = sources.EnumerateArray().Where(source => source.GetProperty("type").GetString() == "database").ToList();
        databases.Select(source => (source.GetProperty("id").GetGuid(), source.GetProperty("permission").GetString()))
            .ShouldBe([(own, "owner"), (shared, "read-only")]);
        databases[0].GetProperty("summary").GetString().ShouldBe("4 個欄位");

        var first = await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{own}", admin.Token, new { });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(CancellationToken));
        var settings = await BodyJsonAsync(await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{shared}", admin.Token, new { }));
        OpenApiContract.AssertKeysMatchSchema(settings, "AssistantSettingsView");
        Guids(settings.GetProperty("databaseIds")).ShouldBe([own, shared]);
        settings.GetProperty("rules").GetProperty("dataWriteDatabaseId").ValueKind.ShouldBe(JsonValueKind.Null);

        // Idempotent.
        (await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{own}", admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // A target needs a purpose: 422, nothing written.
        var noPurpose = await PatchRulesAsync(admin, assistantId, new { dataWriteDatabaseId = own.ToString(), dataWritePurpose = "  " });
        noPurpose.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(noPurpose)).GetProperty("errors").GetProperty("dataWritePurpose")[0].GetString()
            .ShouldBe("寫入資料庫前，請說明收集目的，使用者同意前會看到這段說明。");
        var notConnected = await PatchRulesAsync(admin, assistantId, new { dataWriteDatabaseId = Guid.NewGuid().ToString(), dataWritePurpose = CollectionPurpose });
        notConnected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(notConnected)).GetProperty("errors").TryGetProperty("sources", out _).ShouldBeTrue();
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.AssistantDatabases.CountAsync(link => link.CollectsForms, CancellationToken)).ShouldBe(0);
        }

        var rules = (await BodyJsonAsync(await PatchRulesAsync(
            admin, assistantId, new { dataWriteDatabaseId = own.ToString(), dataWritePurpose = CollectionPurpose }))).GetProperty("rules");
        (rules.GetProperty("dataWriteDatabaseId").GetGuid(), rules.GetProperty("dataWritePurpose").GetString()).ShouldBe((own, CollectionPurpose));

        // Switching the target keeps the purpose; only one row ever collects forms.
        rules = (await BodyJsonAsync(await PatchRulesAsync(admin, assistantId, new { dataWriteDatabaseId = shared.ToString() }))).GetProperty("rules");
        (rules.GetProperty("dataWriteDatabaseId").GetGuid(), rules.GetProperty("dataWritePurpose").GetString()).ShouldBe((shared, CollectionPurpose));

        // Disconnecting the target drops it; the last source cannot be disconnected.
        settings = await BodyJsonAsync(await admin.Spa.DeleteAsync($"{AssistantsPath}/{assistantId}/sources/database/{shared}", admin.Token));
        Guids(settings.GetProperty("databaseIds")).ShouldBe([own]);
        settings.GetProperty("rules").GetProperty("dataWriteDatabaseId").ValueKind.ShouldBe(JsonValueKind.Null);
        settings.GetProperty("rules").GetProperty("dataWritePurpose").GetString().ShouldBe(string.Empty);
        var last = await admin.Spa.DeleteAsync($"{AssistantsPath}/{assistantId}/sources/database/{own}", admin.Token);
        last.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(last)).GetProperty("reason").GetString().ShouldBe("last-source");

        // The database's detail names the owner's own connected assistants.
        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{own}", admin.Token));
        detail.GetProperty("connectedAssistants")[0].GetProperty("id").GetGuid().ShouldBe(assistantId);
        Strings(detail.GetProperty("summary").GetProperty("connectedAssistantNames")).ShouldBe(["客服小幫手"]);
    }

    [Fact]
    public async Task Databases_the_owner_may_not_use_cannot_be_connected_and_are_not_revealed()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var owner2 = await SignInAsync(org, "owner2");
        var notShared = await CreateDatabaseAsync(owner2, "另一位的資料庫");
        var orgB = await CreateOrganizationAsync("組織 B");
        var foreign = await CreateDatabaseAsync(await SignInAsync(orgB, "admin"), "他組織資料庫");
        var assistantId = await CreateAssistantAsync(org, keepConversations: true, withSource: true);

        var unknown = await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{Guid.NewGuid()}", admin.Token, new { });
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        foreach (var id in new[] { notShared, foreign })
        {
            var refused = await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{id}", admin.Token, new { });
            await AssertIdenticalAsync(refused, await admin.Spa.PutAsync(
                $"{AssistantsPath}/{assistantId}/sources/database/{Guid.NewGuid()}", admin.Token, new { }));
            (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("資料庫」");
        }

        var sources = await admin.Spa.GetAsync("/api/v1/connectable-sources", admin.Token);
        var body = await sources.Content.ReadAsStringAsync(CancellationToken);
        body.ShouldNotContain(notShared.ToString());
        body.ShouldNotContain(foreign.ToString());

        // Designated but without the read permission is still not usable.
        await PutAccessAsync(owner2, notShared, [org.Owner2.Id, org.Admin.Id]);
        await SetPermissionsAsync(admin, org.Admin.Id, [AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing]);
        (await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{notShared}", admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantDatabases.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- The form request --------------------------------------------------------------------

    [Fact]
    public async Task A_form_request_carries_the_servers_form_and_terms_and_nothing_is_recorded_without_consent()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "member");
        var (assistantId, databaseId) = await CreateFormAssistantAsync(org, admin, keepConversations: true);

        var run = await RunAsync(member, assistantId, FormQuestion);
        run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
        var reply = run.Reply!.Value;
        OpenApiContract.AssertKeysMatchSchema(reply, "ChatMessageView");
        reply.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");
        var form = reply.GetProperty("reply").GetProperty("form");
        OpenApiContract.AssertKeysMatchSchema(form, "ChatFormRequestView");
        form.GetProperty("id").GetGuid().ShouldBe(databaseId);
        form.GetProperty("title").GetString().ShouldBe("客戶資料庫");
        form.GetProperty("formVersion").GetInt32().ShouldBe(1);
        form.GetProperty("fields").GetArrayLength().ShouldBe(4);
        var consent = form.GetProperty("consent");
        consent.GetProperty("purpose").GetString().ShouldBe(CollectionPurpose);
        consent.GetProperty("recipient").GetString().ShouldBe("表單商行（客戶資料庫）");
        Strings(consent.GetProperty("viewers")).ShouldBe(["表單商行管理者"]);
        consent.GetProperty("sensitiveNotice").GetString()!.ShouldContain("敏感");
        reply.GetProperty("reply").GetProperty("receipt").ValueKind.ShouldBe(JsonValueKind.Null);
        var threadId = run.ThreadId!.Value;

        // GET chat shows the same form request (re-read and re-authorized).
        var chat = await BodyJsonAsync(await member.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", member.Token));
        JsonNode.DeepEquals(JsonNode.Parse(chat.GetProperty("messages")[1].GetRawText()), JsonNode.Parse(reply.GetRawText())).ShouldBeTrue();

        // Reviewing writes nothing; not consenting (or cancelling: no request at all) records nothing.
        var review = await member.Spa.PostAsync(FormPath(assistantId, databaseId, "review"), member.Token, new { formVersionNumber = 1, answers = ValidAnswers() });
        review.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(review)).GetProperty("saved").GetBoolean().ShouldBeFalse();
        var invalid = await member.Spa.PostAsync(FormPath(assistantId, databaseId, "review"), member.Token, new { formVersionNumber = 1, answers = new { } });
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        foreach (var refusal in new object?[] { false, null })
        {
            var response = await SubmitAsync(member, assistantId, databaseId, Guid.NewGuid(), refusal, threadId);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("consent-required");
        }

        await AssertNothingRecordedAsync(org);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.ChatMessages.CountAsync(CancellationToken)).ShouldBe(2);
        // The form tool called no model.
        (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateAnswer, CancellationToken))
            .ShouldBe(0);

        // A question that does not ask for a form is answered as usual.
        var other = await RunAsync(member, assistantId, "營業時間是幾點？", threadId.ToString());
        other.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldNotBe("form-request");
    }

    [Fact]
    public async Task A_consented_submission_returns_the_real_receipt_once_and_shows_it_in_the_conversation()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "member");
        var (assistantId, databaseId) = await CreateFormAssistantAsync(org, admin, keepConversations: true);
        var threadId = (await RunAsync(member, assistantId, FormQuestion)).ThreadId!.Value;
        var key = Guid.NewGuid();

        var created = await SubmitAsync(member, assistantId, databaseId, key, true, threadId);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(created);
        OpenApiContract.AssertKeysMatchSchema(body, "ChatFormSubmissionView");
        var receipt = body.GetProperty("receipt");
        receipt.GetProperty("source").GetString().ShouldBe("assistant-conversation");
        receipt.GetProperty("purpose").GetString().ShouldBe(CollectionPurpose);
        receipt.GetProperty("recipient").GetString().ShouldBe("表單商行（客戶資料庫）");
        receipt.GetProperty("entries")[0].GetProperty("display").GetString().ShouldBe("王小明");
        var message = body.GetProperty("message");
        message.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("submission-receipt");
        message.GetProperty("reply").GetProperty("receipt").GetProperty("id").GetGuid().ShouldBe(receipt.GetProperty("id").GetGuid());
        message.GetProperty("reply").GetProperty("text").GetString()!.ShouldContain(receipt.GetProperty("receiptNumber").GetString()!);
        message.GetProperty("reply").GetProperty("text").GetString()!.ShouldNotContain("王小明");

        // A retry with the same key: the same receipt and message, nothing new written.
        var retried = await SubmitAsync(member, assistantId, databaseId, key, true, threadId);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await BodyJsonAsync(retried);
        again.GetProperty("receipt").GetProperty("id").GetGuid().ShouldBe(receipt.GetProperty("id").GetGuid());
        again.GetProperty("message").GetProperty("id").GetGuid().ShouldBe(message.GetProperty("id").GetGuid());

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var submission = await dbContext.DatabaseSubmissions.AsNoTracking().SingleAsync(CancellationToken);
            submission.Source.ShouldBe(DatabaseSubmissionSource.AssistantConversation);
            submission.ConsentTerms.Purpose.ShouldBe(CollectionPurpose);
            (await dbContext.ChatMessages.CountAsync(row => row.SubmissionId == submission.Id, CancellationToken)).ShouldBe(1);

            // The saved conversation holds only the submission id (and a receipt number in the
            // text), never an answer: read straight from the table.
            var saved = await dbContext.ChatMessages.AsNoTracking().ToListAsync(CancellationToken);
            foreach (var row in saved)
            {
                var stored = $"{row.Text}|{row.Notice}|{string.Join('|', row.NextSteps)}";
                foreach (var value in new[] { "王小明", "0912-345-678", "2026-09-21", "企業" })
                {
                    stored.ShouldNotContain(value);
                }
            }
        }

        // The data manager's records show it, from the conversation.
        var records = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/records", admin.Token));
        records.GetProperty("records")[0].GetProperty("source").GetString().ShouldBe("assistant-conversation");

        // The conversation shows the receipt again.
        var chat = await BodyJsonAsync(await member.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", member.Token));
        var last = chat.GetProperty("messages")[2];
        JsonNode.DeepEquals(JsonNode.Parse(last.GetRawText()), JsonNode.Parse(message.GetRawText())).ShouldBeTrue();

        // A form changed in between: 409 with a new key, nothing recorded.
        await SaveNewFormVersionAsync(admin, databaseId);
        var stale = await SubmitAsync(member, assistantId, databaseId, Guid.NewGuid(), true, threadId);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(stale)).GetProperty("reason").GetString().ShouldBe("form-version-changed");
        await using var after = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await after.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(1);
    }

    // --- Withdrawal (#146 with #148) ---------------------------------------------------------

    [Fact]
    public async Task The_submitter_withdraws_an_in_chat_submission_and_the_conversation_reads_it_back_withdrawn()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "member");
        var (assistantId, databaseId) = await CreateFormAssistantAsync(org, admin, keepConversations: true);
        var threadId = (await RunAsync(member, assistantId, FormQuestion)).ThreadId!.Value;
        var key = Guid.NewGuid();
        var created = await SubmitAsync(member, assistantId, databaseId, key, true, threadId);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        var submitted = await BodyJsonAsync(created);
        var submissionId = submitted.GetProperty("receipt").GetProperty("id").GetGuid();
        var messageId = submitted.GetProperty("message").GetProperty("id").GetGuid();
        var withdrawalPath = $"/api/v1/submissions/{submissionId}/withdrawal";

        // The data manager (the admin here) cannot withdraw it for the member.
        var byManager = await admin.Spa.PostAsync(withdrawalPath, admin.Token, new { });
        byManager.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(byManager)).GetProperty("reason").GetString().ShouldBe("submission-withdrawal");

        var withdrawn = await member.Spa.PostAsync(withdrawalPath, member.Token, new { });
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, await withdrawn.Content.ReadAsStringAsync(CancellationToken));
        var receipt = await BodyJsonAsync(withdrawn);
        receipt.GetProperty("source").GetString().ShouldBe("assistant-conversation");
        receipt.GetProperty("entries").GetArrayLength().ShouldBe(0);
        receipt.GetProperty("withdrawnAt").ValueKind.ShouldBe(JsonValueKind.String);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.DatabaseSubmissionEntries.CountAsync(entry => entry.SubmissionId == submissionId, CancellationToken)).ShouldBe(0);
            var submission = await dbContext.DatabaseSubmissions.AsNoTracking().SingleAsync(CancellationToken);
            submission.WithdrawnAt.ShouldNotBeNull();

            // The conversation keeps its receipt message (it only ever held the submission id) and
            // still holds no answer anywhere.
            (await dbContext.ChatMessages.CountAsync(row => row.SubmissionId == submissionId, CancellationToken)).ShouldBe(1);
            foreach (var row in await dbContext.ChatMessages.AsNoTracking().ToListAsync(CancellationToken))
            {
                var stored = $"{row.Text}|{row.Notice}|{string.Join('|', row.NextSteps)}";
                foreach (var value in new[] { "王小明", "0912-345-678", "2026-09-21", "企業" })
                {
                    stored.ShouldNotContain(value);
                }
            }
        }

        // Read back, the conversation's receipt is the withdrawn one: no content, the withdrawal time.
        var chatResponse = await member.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", member.Token);
        var chatText = await chatResponse.Content.ReadAsStringAsync(CancellationToken);
        var chat = JsonDocument.Parse(chatText).RootElement;
        var readBack = chat.GetProperty("messages")[2];
        readBack.GetProperty("id").GetGuid().ShouldBe(messageId);
        var readReceipt = readBack.GetProperty("reply").GetProperty("receipt");
        readReceipt.GetProperty("id").GetGuid().ShouldBe(submissionId);
        readReceipt.GetProperty("entries").GetArrayLength().ShouldBe(0);
        readReceipt.GetProperty("withdrawnAt").GetString().ShouldBe(receipt.GetProperty("withdrawnAt").GetString());
        // (Only the free-text answers: "企業" is also one of the form's options, shown on the form request.)
        foreach (var value in new[] { "王小明", "0912-345-678" })
        {
            chatText.ShouldNotContain(value);
        }

        // A retry of the original fill learns the withdrawn state; nothing is recorded again.
        var retried = await SubmitAsync(member, assistantId, databaseId, key, true, threadId);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync(CancellationToken));
        var replay = await BodyJsonAsync(retried);
        replay.GetProperty("receipt").GetProperty("withdrawnAt").ValueKind.ShouldBe(JsonValueKind.String);
        replay.GetProperty("receipt").GetProperty("entries").GetArrayLength().ShouldBe(0);
        replay.GetProperty("message").GetProperty("id").GetGuid().ShouldBe(messageId);

        // The member's own list shows it with its source; the data manager sees only the trail.
        var own = await BodyJsonAsync(await member.Spa.GetAsync("/api/v1/submissions", member.Token));
        var listed = own.GetProperty("submissions").EnumerateArray().Single();
        listed.GetProperty("source").GetString().ShouldBe("assistant-conversation");
        listed.GetProperty("withdrawnAt").ValueKind.ShouldBe(JsonValueKind.String);
        var tracking = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/tracking", admin.Token));
        var subject = tracking.GetProperty("subjects").EnumerateArray().Single();
        subject.GetProperty("records").GetArrayLength().ShouldBe(0);
        subject.GetProperty("withdrawals")[0].GetProperty("source").GetString().ShouldBe("assistant-conversation");

        await using var after = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await after.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(1);
        (await after.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(0);
        (await after.ChatMessages.CountAsync(row => row.ReplyKind == ChatReplyKind.SubmissionReceipt, CancellationToken)).ShouldBe(1);
    }

    // --- Revocation --------------------------------------------------------------------------

    [Fact]
    public async Task Revoking_the_designation_or_the_permission_stops_form_requests_and_submissions_at_once()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var owner2 = await SignInAsync(org, "owner2");
        var member = await SignInAsync(org, "member");
        var databaseId = await CreateDatabaseAsync(owner2, "合作夥伴資料庫");
        await PutAccessAsync(owner2, databaseId, [org.Owner2.Id, org.Admin.Id]);
        var assistantId = await CreateAssistantAsync(org, keepConversations: true);
        await ConnectAsTargetAsync(admin, assistantId, databaseId);

        var offered = await RunAsync(member, assistantId, FormQuestion);
        offered.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");
        var threadId = offered.ThreadId!.Value;

        // The database's owner removes the assistant owner's designation: the very next requests.
        await PutAccessAsync(owner2, databaseId, [org.Owner2.Id]);
        var after = await RunAsync(member, assistantId, FormQuestion, threadId.ToString());
        after.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldNotBe("form-request");
        await AssertFormRefusedAsync(member, assistantId, databaseId, threadId);
        var chat = await BodyJsonAsync(await member.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", member.Token));
        chat.GetProperty("messages")[1].GetProperty("reply").GetProperty("form").ValueKind.ShouldBe(JsonValueKind.Null);

        // Designated again, but the assistant owner's permission is revoked: refused too.
        await PutAccessAsync(owner2, databaseId, [org.Owner2.Id, org.Admin.Id]);
        (await RunAsync(member, assistantId, FormQuestion, threadId.ToString())).Reply!.Value
            .GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");
        await SetPermissionsAsync(admin, org.Admin.Id, [AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing]);
        await AssertFormRefusedAsync(member, assistantId, databaseId, threadId);

        await AssertNothingRecordedAsync(org);
    }

    [Fact]
    public async Task Only_members_who_may_use_the_assistant_reach_its_form()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var outsider = await SignInAsync(org, "internal");
        var (assistantId, databaseId) = await CreateFormAssistantAsync(org, admin, keepConversations: true);
        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        foreach (var caller in new[] { outsider, adminB })
        {
            var refused = await SubmitAsync(caller, assistantId, databaseId, Guid.NewGuid(), true, null);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await BodyJsonAsync(refused)).GetProperty("reason").GetString().ShouldBe("assistant-use");
            var review = await caller.Spa.PostAsync(FormPath(assistantId, databaseId, "review"), caller.Token, new { formVersionNumber = 1, answers = ValidAnswers() });
            review.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The owner may use their own assistant, but the database must be the connected form.
        var notTheForm = await SubmitAsync(admin, assistantId, Guid.NewGuid(), Guid.NewGuid(), true, null);
        notTheForm.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(notTheForm)).GetProperty("message").GetString().ShouldBe(FormForbiddenMessage);

        await AssertNothingRecordedAsync(org);
    }

    // --- Privacy -----------------------------------------------------------------------------

    [Fact]
    public async Task The_handler_sees_only_the_consented_fields_and_never_the_private_conversation()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "member");
        var handler = await SignInAsync(org, "internal");
        var (assistantId, databaseId) = await CreateFormAssistantAsync(org, admin, keepConversations: true);
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);
        await ShareWithAsync(org, assistantId, org.Internal.Id);
        const string privateQuestion = "我想要填寫客戶資料，順便說我的私事：下週要搬家";
        var run = await RunAsync(member, assistantId, privateQuestion);
        var threadId = run.ThreadId!.Value;
        (await SubmitAsync(member, assistantId, databaseId, Guid.NewGuid(), true, threadId)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The handler reads the consented record: the fields, nothing of the conversation.
        var records = await handler.Spa.GetAsync($"{DatabasesPath}/{databaseId}/records", handler.Token);
        records.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await records.Content.ReadAsStringAsync(CancellationToken);
        text.ShouldContain("王小明");
        text.ShouldNotContain("搬家");
        text.ShouldNotContain(threadId.ToString());

        // The member's thread stays private, even to a handler who may use the assistant.
        var thread = await handler.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", handler.Token);
        thread.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(thread)).GetProperty("reason").GetString().ShouldBe("chat-thread");

        // A form request or receipt cannot be handed off as an "answer".
        var chat = await BodyJsonAsync(await member.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", member.Token));
        var messages = chat.GetProperty("messages");
        var handoff = await member.Spa.PostAsync($"{AssistantsPath}/{assistantId}/chat/handoffs", member.Token, new
        {
            threadId,
            questionMessageId = messages[0].GetProperty("id").GetGuid(),
            answerMessageId = messages[1].GetProperty("id").GetGuid(),
            confirmed = true,
        });
        handoff.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantIssues.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Without_saved_conversations_the_submission_still_follows_the_consent_rule()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "member");
        var (assistantId, databaseId) = await CreateFormAssistantAsync(org, admin, keepConversations: false);

        var run = await RunAsync(member, assistantId, FormQuestion, "client-thread");
        run.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");
        run.ThreadId.ShouldBeNull();

        var refused = await SubmitAsync(member, assistantId, databaseId, Guid.NewGuid(), false, null);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await AssertNothingRecordedAsync(org);

        // A thread id sent to an assistant that keeps nothing is ignored.
        var created = await SubmitAsync(member, assistantId, databaseId, Guid.NewGuid(), true, Guid.NewGuid());
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await BodyJsonAsync(created);
        body.GetProperty("message").GetProperty("reply").GetProperty("kind").GetString().ShouldBe("submission-receipt");
        body.GetProperty("receipt").GetProperty("source").GetString().ShouldBe("assistant-conversation");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(1);
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(4);
        (await dbContext.ChatThreads.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.ChatMessages.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- Helpers ---------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Owner2, Account Internal, Account Member);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private sealed record RecordedRun(HttpStatusCode Status, string Body, IReadOnlyList<JsonElement> Events)
    {
        public JsonElement? Reply => Custom("smartagri.reply");

        public Guid? ThreadId => Custom("smartagri.thread")?.GetProperty("threadId").GetGuid();

        private JsonElement? Custom(string name) =>
            Events.Where(e => e.GetProperty("type").GetString() == "CUSTOM" && e.GetProperty("name").GetString() == name)
                .Select(e => (JsonElement?)e.GetProperty("value"))
                .SingleOrDefault();
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "表單商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var owner2 = await _host.CreateAccountAsync(
            organization, "owner2", Password, AccountRole.SmbAdmin, $"{name}第二位管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ReadConsentedSubmissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions, AccountPermission.HandleAssistantIssues);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}成員", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, owner2, internalEmployee, member);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name)
    {
        var response = await owner.Spa.PostAsync(DatabasesPath, owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>The admin's assistant, shared with the member. With <paramref name="withSource"/>
    /// it gets a knowledge base so that the database is not its only source.</summary>
    private async Task<Guid> CreateAssistantAsync(TestOrganization org, bool keepConversations, bool withSource = false)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Admin.Id, "客服小幫手", "協助客戶留下聯絡資料", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: keepConversations, now);
        dbContext.Assistants.Add(assistant);
        dbContext.AssistantShares.Add(new AssistantShare(assistant, org.Member.Id));
        if (withSource)
        {
            var knowledgeBase = SmartAgri.Domain.Knowledge.KnowledgeBase.Create(org.Organization.Id, org.Admin.Id, "知識庫", string.Empty, now);
            dbContext.KnowledgeBases.Add(knowledgeBase);
            dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        }

        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private async Task<(Guid AssistantId, Guid DatabaseId)> CreateFormAssistantAsync(TestOrganization org, SignedIn admin, bool keepConversations)
    {
        var databaseId = await CreateDatabaseAsync(admin, "客戶資料庫");
        var assistantId = await CreateAssistantAsync(org, keepConversations);
        await ConnectAsTargetAsync(admin, assistantId, databaseId);
        return (assistantId, databaseId);
    }

    private static async Task ConnectAsTargetAsync(SignedIn admin, Guid assistantId, Guid databaseId)
    {
        var connected = await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{databaseId}", admin.Token, new { });
        connected.StatusCode.ShouldBe(HttpStatusCode.OK, await connected.Content.ReadAsStringAsync(CancellationToken));
        var target = await PatchRulesAsync(admin, assistantId, new { dataWriteDatabaseId = databaseId.ToString(), dataWritePurpose = CollectionPurpose });
        target.StatusCode.ShouldBe(HttpStatusCode.OK, await target.Content.ReadAsStringAsync(CancellationToken));
    }

    private async Task ShareWithAsync(TestOrganization org, Guid assistantId, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(a => a.Id == assistantId, CancellationToken);
        dbContext.AssistantShares.Add(new AssistantShare(assistant, accountId));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static Task<HttpResponseMessage> PatchRulesAsync(SignedIn admin, Guid assistantId, object rules) =>
        admin.Spa.PatchAsync($"{AssistantsPath}/{assistantId}/settings", admin.Token, new { rules });

    private static Task<HttpResponseMessage> PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds) =>
        caller.Spa.PutAsync($"{DatabasesPath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });

    private static async Task SetPermissionsAsync(SignedIn admin, Guid accountId, AccountPermission[] permissions)
    {
        var wire = permissions.Select(SmartAgri.Domain.WireNames<AccountPermission>.ToWire).ToArray();
        var response = await admin.Spa.PutAsync($"/api/v1/team/members/{accountId}/permissions", admin.Token, new { permissions = wire });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task SaveNewFormVersionAsync(SignedIn owner, Guid databaseId)
    {
        var detail = await BodyJsonAsync(await owner.Spa.GetAsync($"{DatabasesPath}/{databaseId}", owner.Token));
        var form = detail.GetProperty("form");
        var fields = JsonNode.Parse(form.GetProperty("fields").GetRawText())!.AsArray();
        fields.Add(new JsonObject { ["label"] = "備註", ["type"] = "text", ["required"] = false, ["options"] = new JsonArray(), ["unit"] = "" });
        var response = await owner.Spa.PutAsync($"{DatabasesPath}/{databaseId}/form", owner.Token, new
        {
            baseVersionNumber = form.GetProperty("versionNumber").GetInt32(),
            fields,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static string FormPath(Guid assistantId, Guid databaseId, string action) =>
        $"{AssistantsPath}/{assistantId}/chat/forms/{databaseId}/{action}";

    private static Task<HttpResponseMessage> SubmitAsync(
        SignedIn caller, Guid assistantId, Guid databaseId, Guid submissionId, object? consent, Guid? threadId) =>
        caller.Spa.PostAsync(FormPath(assistantId, databaseId, "submissions"), caller.Token, new
        {
            submissionId,
            formVersionNumber = 1,
            consent,
            answers = ValidAnswers(),
            threadId,
        });

    /// <summary>Review and submission both get the same <c>403 assistant-form</c> as a made-up database id.</summary>
    private static async Task AssertFormRefusedAsync(SignedIn member, Guid assistantId, Guid databaseId, Guid threadId)
    {
        var submit = await SubmitAsync(member, assistantId, databaseId, Guid.NewGuid(), true, threadId);
        submit.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(submit)).GetProperty("reason").GetString().ShouldBe("assistant-form");
        await AssertIdenticalAsync(submit, await SubmitAsync(member, assistantId, Guid.NewGuid(), Guid.NewGuid(), true, threadId));

        var review = await member.Spa.PostAsync(FormPath(assistantId, databaseId, "review"), member.Token, new { formVersionNumber = 1, answers = ValidAnswers() });
        review.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(review)).GetProperty("reason").GetString().ShouldBe("assistant-form");
    }

    private async Task AssertNothingRecordedAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.ChatMessages.CountAsync(message => message.ReplyKind == ChatReplyKind.SubmissionReceipt, CancellationToken)).ShouldBe(0);
    }

    private static async Task<RecordedRun> RunAsync(SignedIn caller, Guid assistantId, string question, string? threadId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AssistantsPath}/{assistantId}/chat/runs")
        {
            Content = JsonContent.Create(new
            {
                threadId = threadId ?? string.Empty,
                runId = "run-form",
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

    private static List<Guid> Guids(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetGuid())];

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString())];

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
