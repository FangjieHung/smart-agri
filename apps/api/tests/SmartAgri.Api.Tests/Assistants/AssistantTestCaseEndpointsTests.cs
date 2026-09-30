using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// <c>/api/v1/assistants/{id}/test-cases[...]</c> against real PostgreSQL (M3.5 plan §4/§5 Slice
/// 1, issue #123).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AssistantTestCaseEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "TestCase-Endpoint-Pass-1!";

    private readonly AuthHostFixture _host;

    public AssistantTestCaseEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string BasePath(Guid assistantId) => $"/api/v1/assistants/{assistantId}/test-cases";

    // --- Acceptance: another organization's assistant and a nonexistent one get identical 403s --

    [Fact]
    public async Task Another_organizations_assistant_and_a_nonexistent_id_get_identical_403s()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var (assistantId, _) = await CreateAssistantWithKnowledgeBaseAsync(orgA, orgA.Admin.Id, "A 的助理");

        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        var onForeignAssistant = await adminB.Spa.GetAsync(BasePath(assistantId), adminB.Token);
        var onMissingAssistant = await adminB.Spa.GetAsync(BasePath(Guid.NewGuid()), adminB.Token);

        onForeignAssistant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(onForeignAssistant, onMissingAssistant);
    }

    [Fact]
    public async Task An_assistant_owned_by_someone_else_in_the_same_organization_is_also_the_same_403()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        await _host.CreateAccountAsync(org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var admin2 = await SignInAsync(org, "admin2");
        var (assistantId, _) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "管理者的助理");

        var byOtherAdmin = await admin2.Spa.GetAsync(BasePath(assistantId), admin2.Token);
        var byMissing = await admin.Spa.GetAsync(BasePath(Guid.NewGuid()), admin.Token);

        // Different callers, so bytes need not match admin's own missing-id response, but the
        // status and reason must be the same "does not exist for you" shape.
        byOtherAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(byOtherAdmin)).GetProperty("reason").GetString().ShouldBe("assistant-configuration");
        byMissing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // --- Acceptance: the 51st test case is 422 -------------------------------------------

    [Fact]
    public async Task The_51st_test_case_is_422_and_the_50th_succeeds()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (assistantId, _) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "助理");

        for (var i = 0; i < 50; i++)
        {
            var response = await CreateTestCaseAsync(admin, assistantId, $"第 {i} 題？", expectedKind: "general-knowledge");
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        }

        var overLimit = await CreateTestCaseAsync(admin, assistantId, "第 51 題？", expectedKind: "general-knowledge");
        overLimit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantTestCases.CountAsync(t => t.AssistantId == assistantId, CancellationToken)).ShouldBe(50);
    }

    // --- Acceptance: a document not connected to the assistant is 422 --------------------

    [Fact]
    public async Task A_document_not_in_the_assistants_connected_knowledge_bases_is_422()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (assistantId, connectedKnowledgeBaseId) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "助理");
        var foreignDocumentId = await SeedDocumentAsync(org, connectedKnowledgeBaseId, "連接的文件.md");
        var (_, unrelatedKnowledgeBaseId) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "另一個助理");
        var unrelatedDocumentId = await SeedDocumentAsync(org, unrelatedKnowledgeBaseId, "沒有連接的文件.md");

        var ok = await CreateTestCaseAsync(admin, assistantId, "連接文件的題目？", expectedDocumentIds: [foreignDocumentId]);
        ok.StatusCode.ShouldBe(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync(CancellationToken));

        var refused = await CreateTestCaseAsync(admin, assistantId, "沒連接文件的題目？", expectedDocumentIds: [unrelatedDocumentId]);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(refused)).GetProperty("errors").TryGetProperty("expectedDocumentIds", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task No_result_with_expected_documents_is_422()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (assistantId, knowledgeBaseId) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "助理");
        var documentId = await SeedDocumentAsync(org, knowledgeBaseId, "文件.md");

        var response = await CreateTestCaseAsync(
            admin, assistantId, "應拒答的問題？", category: "should-refuse", expectedKind: "no-result", expectedDocumentIds: [documentId]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // --- CRUD round trip -------------------------------------------------------------------

    [Fact]
    public async Task Create_list_update_and_delete_round_trip()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (assistantId, knowledgeBaseId) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "助理");
        var documentId = await SeedDocumentAsync(org, knowledgeBaseId, "退換貨辦法.md");

        var created = await CreateTestCaseAsync(admin, assistantId, "退貨期限是幾天？", expectedDocumentIds: [documentId]);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        var createdBody = await BodyJsonAsync(created);
        var caseId = createdBody.GetProperty("id").GetGuid();
        createdBody.GetProperty("ordinal").GetInt32().ShouldBe(1);

        var followUp = await CreateTestCaseAsync(
            admin, assistantId, "那退款要多久？", expectedDocumentIds: [documentId], followUpOfId: caseId);
        followUp.StatusCode.ShouldBe(HttpStatusCode.Created, await followUp.Content.ReadAsStringAsync(CancellationToken));
        var followUpId = (await BodyJsonAsync(followUp)).GetProperty("id").GetGuid();

        var list = await BodyJsonAsync(await admin.Spa.GetAsync(BasePath(assistantId), admin.Token));
        list.GetArrayLength().ShouldBe(2);

        var updated = await admin.Spa.PatchAsync(
            $"{BasePath(assistantId)}/{caseId}", admin.Token, new { question = "退貨期限最晚是幾天？" });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(updated)).GetProperty("question").GetString().ShouldBe("退貨期限最晚是幾天？");

        var deleted = await admin.Spa.DeleteAsync($"{BasePath(assistantId)}/{followUpId}", admin.Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var afterDelete = await BodyJsonAsync(await admin.Spa.GetAsync(BasePath(assistantId), admin.Token));
        afterDelete.GetArrayLength().ShouldBe(1);
    }

    // --- Deleting the assistant cascades ----------------------------------------------------

    [Fact]
    public async Task Deleting_the_assistant_deletes_its_test_cases()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (assistantId, _) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "助理");
        var created = await CreateTestCaseAsync(admin, assistantId, "問題？", expectedKind: "general-knowledge");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var deleted = await admin.Spa.DeleteAsync($"/api/v1/assistants/{assistantId}", admin.Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantTestCases.CountAsync(t => t.AssistantId == assistantId, CancellationToken)).ShouldBe(0);
    }

    // --- Import / export round trip ---------------------------------------------------------

    [Fact]
    public async Task Export_then_import_into_a_fresh_assistant_round_trips()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (sourceAssistantId, knowledgeBaseId) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "來源助理");
        var documentId = await SeedDocumentAsync(org, knowledgeBaseId, "退換貨辦法.md");

        var first = await CreateTestCaseAsync(admin, sourceAssistantId, "退貨期限是幾天？", expectedDocumentIds: [documentId]);
        var firstId = (await BodyJsonAsync(first)).GetProperty("id").GetString();
        var second = await CreateTestCaseAsync(
            admin, sourceAssistantId, "那退款要多久？", expectedDocumentIds: [documentId], followUpOfId: Guid.Parse(firstId!));
        second.StatusCode.ShouldBe(HttpStatusCode.Created);

        var exported = await admin.Spa.GetAsync($"{BasePath(sourceAssistantId)}/export", admin.Token);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var exportedJson = await exported.Content.ReadAsStringAsync(CancellationToken);
        var exportedBody = JsonDocument.Parse(exportedJson).RootElement;
        var questions = exportedBody.GetProperty("questions");
        questions.GetArrayLength().ShouldBe(2);
        questions[0].GetProperty("expectedCitedDocuments")[0].GetString().ShouldBe("退換貨辦法.md");
        questions[1].GetProperty("followUpOf").GetString().ShouldBe(questions[0].GetProperty("id").GetString());

        // Import that exact payload into a second, otherwise identical assistant.
        var (targetAssistantId, targetKnowledgeBaseId) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "目標助理");
        await SeedDocumentAsync(org, targetKnowledgeBaseId, "退換貨辦法.md");

        using var payload = JsonDocument.Parse(exportedJson);
        var imported = await admin.Spa.PostAsync($"{BasePath(targetAssistantId)}/import", admin.Token, payload.RootElement);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync(CancellationToken));
        var importedBody = await BodyJsonAsync(imported);
        importedBody.GetArrayLength().ShouldBe(2);
        importedBody[0].GetProperty("question").GetString().ShouldBe("退貨期限是幾天？");
        importedBody[1].GetProperty("followUpOfId").GetGuid().ShouldBe(importedBody[0].GetProperty("id").GetGuid());
        importedBody[1].GetProperty("expectedDocumentIds")[0].GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Importing_a_question_that_cites_an_unknown_document_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (assistantId, _) = await CreateAssistantWithKnowledgeBaseAsync(org, org.Admin.Id, "助理");

        var payload = new
        {
            questions = new[]
            {
                new { question = "一般知識題？", expectedKind = "general-knowledge", expectedCitedDocuments = Array.Empty<string>() },
                new { question = "找不到文件的題目？", expectedKind = "company-data", expectedCitedDocuments = new[] { "不存在的文件.md" } },
            },
        };

        var response = await admin.Spa.PostAsync($"{BasePath(assistantId)}/import", admin.Token, payload);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantTestCases.CountAsync(t => t.AssistantId == assistantId, CancellationToken)).ShouldBe(0);
    }

    // --- Fixtures ----------------------------------------------------------------------------

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
    ];

    private sealed record TestOrganization(Organization Organization, Account Admin);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        return new TestOrganization(organization, admin);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private async Task<(Guid AssistantId, Guid KnowledgeBaseId)> CreateAssistantWithKnowledgeBaseAsync(
        TestOrganization org, Guid ownerAccountId, string name)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, ownerAccountId, name, "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, now);
        var knowledgeBase = KnowledgeBase.Create(org.Organization.Id, ownerAccountId, $"{name}的知識庫", string.Empty, now);
        dbContext.Assistants.Add(assistant);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);
        return (assistant.Id, knowledgeBase.Id);
    }

    /// <summary>Seeds a document row directly (version content does not matter for these tests,
    /// only that the document exists and belongs to the given knowledge base).</summary>
    private async Task<Guid> SeedDocumentAsync(TestOrganization org, Guid knowledgeBaseId, string fileName)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBaseId, CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, fileName, now);
        dbContext.KnowledgeDocuments.Add(document);
        await dbContext.SaveChangesAsync(CancellationToken);
        return document.Id;
    }

    private static Task<HttpResponseMessage> CreateTestCaseAsync(
        SignedIn caller,
        Guid assistantId,
        string question,
        string category = "common",
        string expectedKind = "company-data",
        IReadOnlyList<Guid>? expectedDocumentIds = null,
        Guid? followUpOfId = null) =>
        caller.Spa.PostAsync(BasePath(assistantId), caller.Token, new
        {
            question,
            category,
            expectedKind,
            expectedDocumentIds = expectedDocumentIds ?? [],
            followUpOfId,
        });

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
