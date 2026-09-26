using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// New versions, the document's version history and activity log, and emergency disabling
/// (M2 plan, Slice 8; ticket #42) through the API, against real PostgreSQL. What is retrievable
/// at each step is <see cref="KnowledgeRetrievalEligibilityTests"/>'.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeVersionEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string BasePath = "/api/v1/knowledge-bases";
    private const string Password = "Knowledge-Version-Pass-1!";

    private readonly AuthHostFixture _host;

    public KnowledgeVersionEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- New versions ------------------------------------------------------------------------

    [Fact]
    public async Task A_new_version_keeps_the_document_name_stores_its_own_file_name_and_waits_for_review_while_v1_stays_in_effect()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (documentId, v1) = await UploadAsync(owner, "退貨政策.md", Markdown("第 1 版"));
        await RunJobsAsync();
        (await ApproveAsync(owner, v1)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await owner.PostFileAsync(VersionsPath(owner, documentId), "退貨政策（2026 修訂）.md", Markdown("第 2 版"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        response.Headers.Location!.ToString().ShouldBe(DocumentPath(owner, documentId));
        var row = await JsonAsync(response);
        row.GetProperty("id").GetGuid().ShouldBe(documentId);
        row.GetProperty("name").GetString().ShouldBe("退貨政策.md", "the document keeps its name");
        row.GetProperty("status").GetString().ShouldBe("queued");
        row.GetProperty("latestVersionNumber").GetInt32().ShouldBe(2);
        row.GetProperty("latestVersionState").GetString().ShouldBe("pending-review");
        row.GetProperty("effectiveVersionNumber").GetInt32().ShouldBe(1, "the version in effect keeps serving");
        row.GetProperty("inEffect").GetBoolean().ShouldBeTrue();
        var v2 = row.GetProperty("latestVersionId").GetGuid();

        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(candidate => candidate.Id == v2, CancellationToken);
            (version.DocumentId, version.VersionNumber, version.FileName).ShouldBe((documentId, 2, "退貨政策（2026 修訂）.md"));
            (version.ProcessingStatus, version.ReviewState, version.UploadedByAccountId).ShouldBe((KnowledgeDocumentStatus.Queued, KnowledgeReviewState.PendingReview, owner.AccountId));
            (await dbContext.KnowledgeFileContents.AsNoTracking().SingleAsync(file => file.VersionId == v2, CancellationToken)).Bytes.ShouldBe(Markdown("第 2 版"));
            var job = await dbContext.BackgroundJobs.AsNoTracking()
                .Where(candidate => candidate.Status == BackgroundJobStatus.Queued)
                .SingleAsync(CancellationToken);
            job.Kind.ShouldBe(ProcessKnowledgeVersionJob.Kind);
            job.Payload.ShouldContain(v2.ToString());
            var activity = await dbContext.KnowledgeActivities.AsNoTracking().SingleAsync(candidate => candidate.VersionId == v2, CancellationToken);
            (activity.Action, activity.ActorAccountId, activity.DocumentId, activity.Detail).ShouldBe((KnowledgeActivityAction.VersionUploaded, (Guid?)owner.AccountId, (Guid?)documentId, (string?)null));
        }

        // Processed like any upload.
        await RunJobsAsync();
        var detail = await DocumentDetailAsync(owner, documentId);
        detail.GetProperty("versions").EnumerateArray().Select(version => (version.GetProperty("versionNumber").GetInt32(), version.GetProperty("status").GetString(), version.GetProperty("state").GetString()))
            .ShouldBe([(2, "ready", "pending-review"), (1, "ready", "effective")]);
        detail.GetProperty("document").GetProperty("latestVersionState").GetString().ShouldBe("pending-review");
    }

    [Fact]
    public async Task A_new_version_repeating_any_versions_content_gets_422_duplicate_content_and_nothing_is_written()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (documentId, _) = await UploadAsync(owner, "退貨政策.md", Markdown("第 1 版"));
        await UploadAsync(owner, "產品說明.md", Markdown("產品說明"));

        var cases = new (byte[] Content, string Message)[]
        {
            (Markdown("第 1 版"), "這份檔案的內容與這份文件的第 1 版完全相同，不需要再上傳一次。"),
            (Markdown("產品說明"), "這份檔案的內容與「產品說明.md」的第 1 版完全相同，不能當作這份文件的新版本上傳。"),
        };
        foreach (var (content, message) in cases)
        {
            var refused = await owner.PostFileAsync(VersionsPath(owner, documentId), "新版.md", content);

            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var body = await JsonAsync(refused);
            body.GetProperty("reason").GetString().ShouldBe("duplicate-content");
            body.GetProperty("message").GetString().ShouldBe(message);
            body.GetProperty("errors").EnumerateObject().Select(error => error.Name).ShouldBe(["file"]);
        }

        // The other upload checks apply too.
        var unsupported = await owner.PostFileAsync(VersionsPath(owner, documentId), "安裝程式.exe", TestFiles.Exe());
        unsupported.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await JsonAsync(unsupported)).GetProperty("reason").GetString().ShouldBe("unsupported-file-type");

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeDocumentVersions.CountAsync(version => version.DocumentId == documentId, CancellationToken)).ShouldBe(1);
        (await dbContext.KnowledgeActivities.CountAsync(activity => activity.Action == KnowledgeActivityAction.VersionUploaded, CancellationToken)).ShouldBe(0);
    }

    // --- Document detail and activity log ------------------------------------------------------

    [Fact]
    public async Task The_detail_lists_versions_newest_first_with_uploader_approver_and_times_and_every_operation_names_its_operator()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (documentId, v1) = await UploadAsync(owner, "退貨政策.md", Markdown("第 1 版"));
        await RunJobsAsync();
        (await ApproveAsync(owner, v1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var v2 = (await JsonAsync(await owner.PostFileAsync(VersionsPath(owner, documentId), "退貨政策 v2.md", Markdown("第 2 版"))))
            .GetProperty("latestVersionId").GetGuid();
        await RunJobsAsync();
        var scheduled = _host.Clock.GetUtcNow().AddDays(3);
        (await ApproveAsync(owner, v2, scheduled.ToString("O"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/disable", owner.Token, new { reason = "  條款待法務確認  " })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await DocumentDetailAsync(owner, documentId);

        detail.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["document", "createdAt", "disabledAt", "disabledBy", "disabledReason", "versions", "activities"]);
        var row = detail.GetProperty("document");
        (row.GetProperty("disabled").GetBoolean(), row.GetProperty("inEffect").GetBoolean(), row.GetProperty("effectiveVersionNumber").GetInt32())
            .ShouldBe((true, false, 1));
        detail.GetProperty("disabledReason").GetString().ShouldBe("條款待法務確認");
        AssertAccount(detail.GetProperty("disabledBy"), owner);
        detail.GetProperty("disabledAt").GetDateTimeOffset().ShouldBeGreaterThan(detail.GetProperty("createdAt").GetDateTimeOffset());

        var versions = detail.GetProperty("versions").EnumerateArray().ToList();
        versions[0].EnumerateObject().Select(property => property.Name).ShouldBe(
        [
            "id", "documentId", "versionNumber", "fileName", "contentType", "sizeBytes", "status", "issue", "state",
            "effectiveFrom", "uploadedBy", "uploadedAt", "approvedBy", "approvedAt", "updatedAt",
        ]);
        versions.Select(version => (version.GetProperty("versionNumber").GetInt32(), version.GetProperty("fileName").GetString(), version.GetProperty("state").GetString()))
            .ShouldBe([(2, "退貨政策 v2.md", "scheduled"), (1, "退貨政策.md", "effective")]);
        foreach (var version in versions)
        {
            AssertAccount(version.GetProperty("uploadedBy"), owner);
            AssertAccount(version.GetProperty("approvedBy"), owner);
            version.GetProperty("approvedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(version.GetProperty("uploadedAt").GetDateTimeOffset());
        }

        versions[0].GetProperty("effectiveFrom").GetDateTimeOffset().ShouldBe(scheduled, TimeSpan.FromMilliseconds(1));

        // Every operation, newest first, each naming the owner, none carrying content.
        var activities = detail.GetProperty("activities").EnumerateArray().ToList();
        activities.Select(activity => (activity.GetProperty("action").GetString(), activity.GetProperty("versionNumber").ValueKind == JsonValueKind.Null ? 0 : activity.GetProperty("versionNumber").GetInt32()))
            .ShouldBe(
            [
                ("document-disabled", 0),
                ("version-approved", 2),
                ("version-uploaded", 2),
                ("version-approved", 1),
                ("document-uploaded", 1),
            ]);
        activities.ShouldAllBe(activity => activity.GetProperty("actor").GetProperty("id").GetGuid() == owner.AccountId);
        activities[0].EnumerateObject().Select(property => property.Name).ShouldBe(["id", "action", "actor", "at", "versionId", "versionNumber"]);

        // Enabling is recorded too, and clears who, when and why.
        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/enable", owner.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var enabled = await DocumentDetailAsync(owner, documentId);
        enabled.GetProperty("disabledAt").ValueKind.ShouldBe(JsonValueKind.Null);
        enabled.GetProperty("disabledBy").ValueKind.ShouldBe(JsonValueKind.Null);
        enabled.GetProperty("disabledReason").ValueKind.ShouldBe(JsonValueKind.Null);
        var latest = enabled.GetProperty("activities")[0];
        latest.GetProperty("action").GetString().ShouldBe("document-enabled");
        latest.GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(owner.AccountId);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var rows = await dbContext.KnowledgeActivities.AsNoTracking().Where(activity => activity.DocumentId == documentId).ToListAsync(CancellationToken);
        rows.Count.ShouldBe(6);
        rows.ShouldAllBe(activity => activity.ActorAccountId == owner.AccountId && activity.Detail == null && activity.KnowledgeBaseId == owner.KnowledgeBaseId);
    }

    // --- Emergency disable and enable ------------------------------------------------------------

    [Fact]
    public async Task Disabling_needs_a_reason_and_disabling_or_enabling_twice_is_409_writing_nothing()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (documentId, _) = await UploadAsync(owner, "退貨政策.md", Markdown("第 1 版"));
        var disable = DocumentPath(owner, documentId) + "/disable";
        var enable = DocumentPath(owner, documentId) + "/enable";

        foreach (var (body, message) in new (object? Body, string Message)[]
        {
            (null, KnowledgeReviewRules.ReasonRequiredMessage),
            (new { }, KnowledgeReviewRules.ReasonRequiredMessage),
            (new { reason = " \n " }, KnowledgeReviewRules.ReasonRequiredMessage),
            (new { reason = new string('因', KnowledgeDocument.DisabledReasonMaxLength + 1) }, KnowledgeReviewRules.ReasonTooLongMessage),
        })
        {
            var refused = body is null ? await PostWithoutBodyAsync(owner, disable) : await owner.Spa.PostAsync(disable, owner.Token, body);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await JsonAsync(refused)).GetProperty("errors").GetProperty("reason")[0].GetString().ShouldBe(message);
        }

        var notDisabled = await PostWithoutBodyAsync(owner, enable);
        notDisabled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(notDisabled)).GetProperty("reason").GetString().ShouldBe("document-not-disabled");

        var disabled = await owner.Spa.PostAsync(disable, owner.Token, new { reason = new string('因', KnowledgeDocument.DisabledReasonMaxLength) });
        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(disabled)).GetProperty("disabled").GetBoolean().ShouldBeTrue();

        var again = await owner.Spa.PostAsync(disable, owner.Token, new { reason = "再一次" });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(again)).GetProperty("reason").GetString().ShouldBe("document-already-disabled");

        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var document = await dbContext.KnowledgeDocuments.AsNoTracking().SingleAsync(candidate => candidate.Id == documentId, CancellationToken);
            document.DisabledReason!.Length.ShouldBe(KnowledgeDocument.DisabledReasonMaxLength, "the refused second disable changed nothing");
            document.DisabledByAccountId.ShouldBe(owner.AccountId);
            (await dbContext.KnowledgeActivities.CountAsync(activity => activity.Action == KnowledgeActivityAction.DocumentDisabled, CancellationToken)).ShouldBe(1);
        }

        (await PostWithoutBodyAsync(owner, enable)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostWithoutBodyAsync(owner, enable)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_and_detail_count_documents_in_effect_awaiting_approval_and_disabled_next_to_processing_status()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (_, effective) = await UploadAsync(owner, "已生效.md", Markdown("已生效"));
        var (disabledDocument, disabledVersion) = await UploadAsync(owner, "已停用.md", Markdown("已停用"));
        await UploadAsync(owner, "待確認.md", Markdown("待確認"));
        await RunJobsAsync();
        (await ApproveAsync(owner, effective)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ApproveAsync(owner, disabledVersion)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Spa.PostAsync(DocumentPath(owner, disabledDocument) + "/disable", owner.Token, new { reason = "暫停" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await UploadAsync(owner, "處理中.md", Markdown("處理中"));

        var detail = await JsonAsync(await owner.Spa.GetAsync($"{BasePath}/{owner.KnowledgeBaseId}", owner.Token));

        var summary = detail.GetProperty("summary");
        summary.GetProperty("statusCounts").GetProperty("ready").GetInt32().ShouldBe(3);
        summary.GetProperty("statusCounts").GetProperty("queued").GetInt32().ShouldBe(1);
        (summary.GetProperty("inEffectCount").GetInt32(), summary.GetProperty("awaitingApprovalCount").GetInt32(), summary.GetProperty("disabledCount").GetInt32())
            .ShouldBe((1, 1, 1));
        detail.GetProperty("documents").EnumerateArray()
            .Select(document => (document.GetProperty("name").GetString(), document.GetProperty("status").GetString(), document.GetProperty("latestVersionState").GetString(), document.GetProperty("inEffect").GetBoolean()))
            .ShouldBe(
            [
                ("已生效.md", "ready", "effective", true),
                ("已停用.md", "ready", "effective", false),
                ("待確認.md", "ready", "pending-review", false),
                ("處理中.md", "queued", "pending-review", false),
            ]);
        (await JsonAsync(await owner.Spa.GetAsync(BasePath, owner.Token))).EnumerateArray().ShouldHaveSingleItem().GetRawText().ShouldBe(summary.GetRawText());
    }

    // --- Owner only ------------------------------------------------------------------------------

    [Fact]
    public async Task Other_organizations_non_owners_and_nonexistent_ids_get_identical_403s_on_every_new_endpoint()
    {
        var orgA = await _host.CreateOrganizationAsync("組織 A");
        await _host.CreateAccountAsync(orgA, "admin", Password, AccountRole.SmbAdmin, "A 管理者", AccountPermission.ManageDataSources);
        await _host.CreateAccountAsync(orgA, "admin2", Password, AccountRole.SmbAdmin, "A 第二位管理者", AccountPermission.ManageDataSources);
        await _host.CreateAccountAsync(orgA, "internal", Password, AccountRole.InternalEmployee, "A 客服同仁", AccountPermission.UseSharedAssistants);
        var orgB = await _host.CreateOrganizationAsync("組織 B");
        await _host.CreateAccountAsync(orgB, "admin", Password, AccountRole.SmbAdmin, "B 管理者", AccountPermission.ManageDataSources);

        var ownerA = await SignInAsync(orgA, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(ownerA, "A 的知識庫");
        var otherOwnKnowledgeBaseId = await CreateKnowledgeBaseAsync(ownerA, "A 的另一個知識庫");
        var upload = await PostFileAsync(ownerA, $"{BasePath}/{knowledgeBaseId}/documents", "機密.md", Markdown("機密"));
        upload.StatusCode.ShouldBe(HttpStatusCode.Created);
        var documentId = (await JsonAsync(upload)).GetProperty("id").GetGuid();
        var versionId = (await JsonAsync(upload)).GetProperty("latestVersionId").GetGuid();
        await RunJobsAsync();

        var outsiders = new[]
        {
            ("other organization", await SignInAsync(orgB, "admin")),
            ("same-organization admin", await SignInAsync(orgA, "admin2")),
            ("same-organization employee", await SignInAsync(orgA, "internal")),
        };
        foreach (var (who, caller) in outsiders)
        {
            foreach (var (endpoint, send) in NewEndpoints(caller))
            {
                var toReal = await send(knowledgeBaseId, documentId, versionId);
                var toNonexistent = await send(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

                toReal.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{who} {endpoint}");
                await AssertIdenticalAsync(toReal, toNonexistent);
                (await JsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("knowledge-base");
            }
        }

        // The owner too, when the document is not in the knowledge base named.
        foreach (var (endpoint, send) in NewEndpoints(ownerA).Where(endpoint => endpoint.Name != "POST approve"))
        {
            var reference = await send(knowledgeBaseId, Guid.NewGuid(), versionId);
            reference.StatusCode.ShouldBe(HttpStatusCode.Forbidden, endpoint);
            await AssertIdenticalAsync(await send(otherOwnKnowledgeBaseId, documentId, versionId), reference);
        }

        // Nothing changed in A.
        await using var dbContext = _host.Postgres.CreateDbContext(orgA.Id);
        var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(CancellationToken);
        (version.Id, version.ReviewState).ShouldBe((versionId, KnowledgeReviewState.PendingReview));
        (await dbContext.KnowledgeDocuments.AsNoTracking().SingleAsync(CancellationToken)).DisabledAt.ShouldBeNull();
        (await dbContext.KnowledgeActivities.CountAsync(activity => activity.DocumentId == documentId, CancellationToken)).ShouldBe(1, "only the upload");
    }

    [Fact]
    public async Task Unauthenticated_callers_get_401_on_every_new_endpoint()
    {
        using var anonymous = _host.CreateSpaClient();
        var document = $"{BasePath}/{Guid.NewGuid()}/documents/{Guid.NewGuid()}";

        (await anonymous.Http.GetAsync(document, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.PostAsync($"{document}/versions", new MultipartFormDataContent(), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"{BasePath}/{Guid.NewGuid()}/versions/approve", null, new { versionIds = new[] { Guid.NewGuid() } })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"{document}/disable", null, new { reason = "x" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"{document}/enable", null, new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Helpers -------------------------------------------------------------------------------

    private sealed record Caller(SpaClient Spa, string Token);

    private static byte[] Markdown(string marker) => Encoding.UTF8.GetBytes($"# {marker}\n\n## 退貨期限\n\n收到商品後七天內可以退貨（{marker}）。\n");

    private Task RunJobsAsync() => _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private static string DocumentPath(KnowledgeTestOwner owner, Guid documentId) => $"{BasePath}/{owner.KnowledgeBaseId}/documents/{documentId}";

    private static string VersionsPath(KnowledgeTestOwner owner, Guid documentId) => DocumentPath(owner, documentId) + "/versions";

    private static async Task<(Guid DocumentId, Guid VersionId)> UploadAsync(KnowledgeTestOwner owner, string fileName, byte[] content)
    {
        var response = await owner.PostFileAsync($"{BasePath}/{owner.KnowledgeBaseId}/documents", fileName, content);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var view = await JsonAsync(response);
        return (view.GetProperty("id").GetGuid(), view.GetProperty("latestVersionId").GetGuid());
    }

    private static Task<HttpResponseMessage> ApproveAsync(KnowledgeTestOwner owner, Guid versionId, string? effectiveFrom = null) =>
        owner.Spa.PostAsync($"{BasePath}/{owner.KnowledgeBaseId}/versions/approve", owner.Token, new { versionIds = new[] { versionId }, effectiveFrom });

    private static async Task<JsonElement> DocumentDetailAsync(KnowledgeTestOwner owner, Guid documentId)
    {
        var response = await owner.Spa.GetAsync(DocumentPath(owner, documentId), owner.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> PostWithoutBodyAsync(KnowledgeTestOwner owner, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var response = await owner.Spa.Http.SendAsync(request, CancellationToken);
        await response.Content.LoadIntoBufferAsync(CancellationToken);
        return response;
    }

    private static void AssertAccount(JsonElement account, KnowledgeTestOwner owner)
    {
        account.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "displayName"]);
        account.GetProperty("id").GetGuid().ShouldBe(owner.AccountId);
        account.GetProperty("displayName").GetString().ShouldBe($"{owner.Organization.Name}管理者");
    }

    private async Task<Caller> SignInAsync(SmartAgri.Domain.Organizations.Organization organization, string loginName)
    {
        var spa = _host.CreateSpaClient();
        return new Caller(spa, (await spa.SignInAsync(organization.Code, loginName, Password)).AccessToken);
    }

    private static async Task<Guid> CreateKnowledgeBaseAsync(Caller owner, string name)
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { name, purpose = "" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostFileAsync(Caller caller, string path, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        var response = await caller.Spa.Http.SendAsync(request, CancellationToken);
        await response.Content.LoadIntoBufferAsync(CancellationToken);
        return response;
    }

    /// <summary>Every endpoint of this slice, each with a valid request, so only the ids can make
    /// it fail. Arguments: knowledge base, document, version.</summary>
    private static IEnumerable<(string Name, Func<Guid, Guid, Guid, Task<HttpResponseMessage>> Send)> NewEndpoints(Caller caller) =>
    [
        ("GET document", (knowledgeBase, document, _) =>
            caller.Spa.GetAsync($"{BasePath}/{knowledgeBase}/documents/{document}", caller.Token)),
        ("POST versions", (knowledgeBase, document, _) =>
            PostFileAsync(caller, $"{BasePath}/{knowledgeBase}/documents/{document}/versions", "新版.md", Markdown(Guid.NewGuid().ToString()))),
        ("POST approve", (knowledgeBase, _, version) =>
            caller.Spa.PostAsync($"{BasePath}/{knowledgeBase}/versions/approve", caller.Token, new { versionIds = new[] { version } })),
        ("POST disable", (knowledgeBase, document, _) =>
            caller.Spa.PostAsync($"{BasePath}/{knowledgeBase}/documents/{document}/disable", caller.Token, new { reason = "測試" })),
        ("POST enable", (knowledgeBase, document, _) =>
            caller.Spa.PostAsync($"{BasePath}/{knowledgeBase}/documents/{document}/enable", caller.Token, new { })),
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
