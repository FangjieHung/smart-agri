using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// <c>/api/v1/assistant-drafts</c>, <c>/api/v1/assistants</c> (creation from a draft) and
/// <c>/api/v1/connectable-sources</c> (M3 plan, Slice 2 acceptance; ticket #72).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class AssistantDraftEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Assistant-Draft-Pass-1!";
    private const string DraftsPath = "/api/v1/assistant-drafts";
    private const string AssistantsPath = "/api/v1/assistants";
    private const string ConnectableSourcesPath = "/api/v1/connectable-sources";

    private readonly AuthHostFixture _host;

    public AssistantDraftEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: another account's draft id is indistinguishable from a missing one -

    [Fact]
    public async Task Another_organizations_draft_and_a_nonexistent_id_get_identical_403s_on_every_endpoint()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var adminA = await SignInAsync(orgA, "admin");
        var draftId = await CreateDraftAsync(orgA, orgA.Admin.Id, ValidDraftPayload());

        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        foreach (var (verb, send) in EndpointsById(adminB))
        {
            var toOtherOrganization = await send(draftId);
            var toNonexistent = await send(Guid.NewGuid());

            toOtherOrganization.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            await AssertIdenticalAsync(toOtherOrganization, toNonexistent);
            (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("assistant-draft", verb);
        }

        // B's attempts changed nothing in A.
        var get = await adminA.Spa.GetAsync($"{DraftsPath}/{draftId}", adminA.Token);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_non_owner_in_the_same_organization_gets_the_same_403()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var draftId = await CreateDraftAsync(org, org.Admin.Id, ValidDraftPayload());

        await _host.CreateAccountAsync(
            org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var admin2 = await SignInAsync(org, "admin2");

        foreach (var (verb, send) in EndpointsById(admin2))
        {
            var toSomeoneElses = await send(draftId);
            var toNonexistent = await send(Guid.NewGuid());

            toSomeoneElses.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            await AssertIdenticalAsync(toSomeoneElses, toNonexistent);
        }

        var get = await admin.Spa.GetAsync($"{DraftsPath}/{draftId}", admin.Token);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // --- Acceptance: create, list, save with optimistic concurrency, delete ------------

    [Fact]
    public async Task Creating_a_draft_returns_it_at_revision_1_and_it_appears_in_the_list()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var created = await admin.Spa.PostAsync(DraftsPath, admin.Token, new { payload = new { name = "新草稿" } });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdBody = await BodyJsonAsync(created);
        createdBody.GetProperty("revision").GetInt32().ShouldBe(1);
        createdBody.GetProperty("payload").GetProperty("name").GetString().ShouldBe("新草稿");
        var draftId = createdBody.GetProperty("id").GetGuid();

        var list = await BodyJsonAsync(await admin.Spa.GetAsync(DraftsPath, admin.Token));
        list.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(draftId);
    }

    [Fact]
    public async Task Two_tabs_saving_the_same_revision_the_second_gets_409_and_the_first_ones_save_wins()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var draftId = await CreateDraftAsync(org, org.Admin.Id, """{"name":"原始草稿"}""");

        var firstSave = await admin.Spa.PutAsync(
            $"{DraftsPath}/{draftId}", admin.Token, new { payload = new { name = "分頁一" }, revision = 1 });
        firstSave.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(firstSave)).GetProperty("revision").GetInt32().ShouldBe(2);

        var secondSave = await admin.Spa.PutAsync(
            $"{DraftsPath}/{draftId}", admin.Token, new { payload = new { name = "分頁二" }, revision = 1 });
        secondSave.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(secondSave)).GetProperty("reason").GetString().ShouldBe("draft-revision-conflict");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var draft = await dbContext.AssistantDrafts.SingleAsync(d => d.Id == draftId, CancellationToken);
        draft.Payload.ShouldContain("分頁一");
        draft.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_a_draft_removes_it_and_a_later_read_is_the_same_403_as_missing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var draftId = await CreateDraftAsync(org, org.Admin.Id, ValidDraftPayload());

        var deleted = await admin.Spa.DeleteAsync($"{DraftsPath}/{draftId}", admin.Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var afterDelete = await admin.Spa.GetAsync($"{DraftsPath}/{draftId}", admin.Token);
        afterDelete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(afterDelete)).GetProperty("reason").GetString().ShouldBe("assistant-draft");
    }

    [Fact]
    public async Task An_oversized_or_non_object_payload_is_422_on_both_create_and_save()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var createArray = await admin.Spa.PostAsync(DraftsPath, admin.Token, new { payload = new[] { 1, 2 } });
        createArray.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var draftId = await CreateDraftAsync(org, org.Admin.Id, ValidDraftPayload());
        var saveArray = await admin.Spa.PutAsync(
            $"{DraftsPath}/{draftId}", admin.Token, new { payload = new[] { 1, 2 }, revision = 1 });
        saveArray.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // --- Acceptance: creating an assistant from a draft ---------------------------------

    [Fact]
    public async Task Creating_an_assistant_from_a_valid_draft_deletes_the_draft_and_connects_its_knowledge_base()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(org, org.Admin.Id, "客服知識庫");
        var draftId = await CreateDraftAsync(org, org.Admin.Id, ValidDraftPayload(knowledgeBaseId));

        var response = await admin.Spa.PostAsync(AssistantsPath, admin.Token, new { draftId });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await BodyJsonAsync(response);
        body.GetProperty("name").GetString().ShouldBe("客服助理");
        var assistantId = body.GetProperty("id").GetGuid();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantDrafts.AnyAsync(d => d.Id == draftId, CancellationToken)).ShouldBeFalse();
        (await dbContext.Assistants.AnyAsync(a => a.Id == assistantId, CancellationToken)).ShouldBeTrue();
        (await dbContext.AssistantKnowledgeBases.AnyAsync(
                link => link.AssistantId == assistantId && link.KnowledgeBaseId == knowledgeBaseId, CancellationToken))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Creating_an_assistant_from_an_invalid_draft_is_422_and_the_draft_and_no_assistant_are_left()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(org, org.Admin.Id, "知識庫");
        var draftId = await CreateDraftAsync(org, org.Admin.Id, ValidDraftPayload(knowledgeBaseId, name: ""));

        var response = await admin.Spa.PostAsync(AssistantsPath, admin.Token, new { draftId });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(response)).GetProperty("errors").TryGetProperty("name", out _).ShouldBeTrue();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantDrafts.AnyAsync(d => d.Id == draftId, CancellationToken)).ShouldBeTrue();
        (await dbContext.Assistants.AnyAsync(CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_draft_naming_another_organizations_knowledge_base_id_is_422_when_creating_the_assistant()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var otherOrg = await CreateOrganizationAsync("其他組織");
        var foreignKnowledgeBaseId = await CreateKnowledgeBaseAsync(otherOrg, otherOrg.Admin.Id, "別組織的知識庫");

        var draftId = await CreateDraftAsync(org, org.Admin.Id, ValidDraftPayload(foreignKnowledgeBaseId));

        var response = await admin.Spa.PostAsync(AssistantsPath, admin.Token, new { draftId });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(response)).GetProperty("errors").TryGetProperty("sources", out _).ShouldBeTrue();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Assistants.AnyAsync(CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Creating_an_assistant_from_someone_elses_draft_id_is_403_assistant_draft()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(org, org.Admin.Id, "知識庫");

        await _host.CreateAccountAsync(
            org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var admin2 = await SignInAsync(org, "admin2");
        var draftId = await CreateDraftAsync(org, org.Admin.Id, ValidDraftPayload(knowledgeBaseId));

        var response = await admin2.Spa.PostAsync(AssistantsPath, admin2.Token, new { draftId });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("assistant-draft");
    }

    // --- Acceptance: connectable sources -------------------------------------------------

    [Fact]
    public async Task Connectable_sources_lists_own_and_shared_knowledge_bases_but_not_someone_elses_private_one()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var owned = await CreateKnowledgeBaseAsync(org, org.Admin.Id, "自己的知識庫");
        var privateOfOther = await CreateKnowledgeBaseAsync(org, org.Internal.Id, "同仁的私人知識庫");
        var sharedWithAdmin = await CreateKnowledgeBaseAsync(org, org.Internal.Id, "分享給管理者的知識庫");
        await ShareWithAsync(org, sharedWithAdmin, org.Admin.Id);

        var response = await BodyJsonAsync(await admin.Spa.GetAsync(ConnectableSourcesPath, admin.Token));
        var ids = response.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

        ids.ShouldContain(owned);
        ids.ShouldContain(sharedWithAdmin);
        ids.ShouldNotContain(privateOfOther);
    }

    [Fact]
    public async Task Connectable_sources_status_reflects_the_knowledge_bases_content()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var empty = await CreateKnowledgeBaseAsync(org, org.Admin.Id, "還沒有內容的知識庫");
        var faqOnly = await CreateKnowledgeBaseAsync(org, org.Admin.Id, "只有 FAQ 的知識庫");
        await AddReadyFaqAsync(org, faqOnly, org.Admin.Id, "問題？", "答案。");
        var withProcessingDocument = await CreateKnowledgeBaseAsync(org, org.Admin.Id, "處理中的知識庫");
        await AddQueuedDocumentAsync(org, withProcessingDocument, org.Admin.Id, "退貨政策.pdf");

        var response = await BodyJsonAsync(await admin.Spa.GetAsync(ConnectableSourcesPath, admin.Token));
        var statusById = response.EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("status").GetString());

        statusById[empty].ShouldBe("empty");
        statusById[faqOnly].ShouldBe("ready");
        statusById[withProcessingDocument].ShouldBe("processing");
    }

    [Fact]
    public async Task Unauthenticated_callers_get_401()
    {
        using var anonymous = _host.CreateSpaClient();
        (await anonymous.Http.GetAsync(DraftsPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.GetAsync(ConnectableSourcesPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants,
            AccountPermission.ReadConsentedSubmissions);
        var customer = await _host.CreateAccountAsync(
            organization, "customer", Password, AccountRole.ExternalCustomer, $"{name}外部客戶",
            AccountPermission.SubmitAuthorizedForms,
            AccountPermission.ReadOwnTracking);

        return new TestOrganization(organization, admin, internalEmployee, customer);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private async Task<Guid> CreateDraftAsync(TestOrganization org, Guid ownerAccountId, string payloadJson)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var draft = new AssistantDraft(org.Organization.Id, ownerAccountId, payloadJson, schemaVersion: 1, DateTimeOffset.UtcNow);
        dbContext.AssistantDrafts.Add(draft);
        await dbContext.SaveChangesAsync(CancellationToken);
        return draft.Id;
    }

    private async Task<Guid> CreateKnowledgeBaseAsync(TestOrganization org, Guid ownerAccountId, string name)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = KnowledgeBase.Create(org.Organization.Id, ownerAccountId, name, string.Empty, now);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        await dbContext.SaveChangesAsync(CancellationToken);
        return knowledgeBase.Id;
    }

    private async Task ShareWithAsync(TestOrganization org, Guid knowledgeBaseId, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBaseId, CancellationToken);
        knowledgeBase.ChangeSharing(KnowledgeSharingScope.SpecificAccounts, allowOriginalDownload: false, DateTimeOffset.UtcNow);
        dbContext.KnowledgeBaseShares.Add(new KnowledgeBaseShare(knowledgeBase, accountId));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>Writes an FAQ entry straight to the database, already processed
    /// <see cref="KnowledgeDocumentStatus.Ready"/> (pending review, like a real FAQ's version 1),
    /// so a knowledge base can be given FAQ-only content without going through the queue.</summary>
    private async Task AddReadyFaqAsync(TestOrganization org, Guid knowledgeBaseId, Guid uploadedByAccountId, string question, string answer)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBaseId, CancellationToken);
        var document = KnowledgeDocument.CreateFaq(knowledgeBase, question, now);
        dbContext.KnowledgeDocuments.Add(document);
        var content = new KnowledgeFaqEntry(question, answer).ToContent();
        var version = KnowledgeDocumentVersion.CreateFaq(
            document, versionNumber: 1, question, content, KnowledgeUploadRules.Sha256(content), uploadedByAccountId, now);
        version.StartProcessing(now);
        version.CompleteProcessing(KnowledgeDocumentStatus.Ready, issue: null, now);
        dbContext.KnowledgeDocumentVersions.Add(version);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>Writes an uploaded document straight to the database with a
    /// <see cref="KnowledgeDocumentStatus.Queued"/> version (no content, no queued job — nothing
    /// processes it) so a knowledge base can be given "still processing" content.</summary>
    private async Task AddQueuedDocumentAsync(TestOrganization org, Guid knowledgeBaseId, Guid uploadedByAccountId, string fileName)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBaseId, CancellationToken);
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, fileName, now);
        dbContext.KnowledgeDocuments.Add(document);
        var sha256 = KnowledgeUploadRules.Sha256("內容"u8.ToArray());
        var version = KnowledgeDocumentVersion.Create(
            document, versionNumber: 1, fileName, "application/pdf", sizeBytes: 6, sha256, uploadedByAccountId, uploadBatchId: null, now);
        dbContext.KnowledgeDocumentVersions.Add(version);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>A draft payload with every field "由草稿建立助理" needs, valid by default.
    /// <paramref name="knowledgeBaseId"/> defaults to a random (non-connectable) id when
    /// omitted — enough for tests that never call the create-assistant endpoint.</summary>
    private static string ValidDraftPayload(Guid? knowledgeBaseId = null, string name = "客服助理") =>
        $$"""
        {
          "name": "{{name}}",
          "purpose": "回答退換貨問題",
          "tone": "friendly",
          "audience": "account-members",
          "roleInstructions": "",
          "sources": [{ "id": "{{knowledgeBaseId ?? Guid.NewGuid()}}", "type": "knowledge-base" }],
          "rules": {
            "knowledgeScope": "company-data-only",
            "refusalMessage": "目前的資料中找不到這個問題的答案。",
            "showCitations": true,
            "keepOwnConversations": true
          }
        }
        """;

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Every draft endpoint addressed by a draft id, each with a valid body, so the
    /// only thing that can make it fail is the id.</summary>
    private static IEnumerable<(string Verb, Func<Guid, Task<HttpResponseMessage>> Send)> EndpointsById(SignedIn caller) =>
    [
        ("GET", id => caller.Spa.GetAsync($"{DraftsPath}/{id}", caller.Token)),
        ("PUT", id => caller.Spa.PutAsync($"{DraftsPath}/{id}", caller.Token, new { payload = new { name = "改名" }, revision = 1 })),
        ("DELETE", id => caller.Spa.DeleteAsync($"{DraftsPath}/{id}", caller.Token)),
        ("POST trial-answers", id => caller.Spa.PostAsync($"{DraftsPath}/{id}/trial-answers", caller.Token, new { question = "測試問題" })),
    ];

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
