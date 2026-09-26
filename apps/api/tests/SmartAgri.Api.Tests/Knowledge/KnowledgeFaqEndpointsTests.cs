using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// FAQ entries (M2 plan Slice 10; ticket #44) against real PostgreSQL, through the real FAQ,
/// processing, approval, disable, extraction and retrieval-preview endpoints. The embedding model
/// is <see cref="KnowledgeRetrievalPreviewTests.ScriptedVectors"/>: against <see cref="ReturnQuestion"/>,
/// a passage with 「十四天內可申請退貨」 scores 0.95, 「十天內…」 0.9, 「七天內…」 0.8 and anything else
/// 0.1. Each test has an organization of its own.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeFaqEndpointsTests : IClassFixture<KnowledgeRetrievalPreviewTests.RetrievalHostFixture>
{
    private const string BasePath = "/api/v1/knowledge-bases";

    /// <summary>The question <see cref="KnowledgeRetrievalPreviewTests.ScriptedVectors"/> knows.</summary>
    private const string ReturnQuestion = "收到商品幾天內可退貨？";
    private const string TenDays = "收到商品後十天內可申請退貨，請保留發票。";
    private const string FourteenDays = "收到商品後十四天內可申請退貨，請保留發票。";
    private const string StaffPassword = "Faq-Staff-Pass-1!";

    private readonly KnowledgeRetrievalPreviewTests.RetrievalHostFixture _host;

    public KnowledgeFaqEndpointsTests(KnowledgeRetrievalPreviewTests.RetrievalHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: a new FAQ is found once approved, located 「FAQ」 -----------------------

    [Fact]
    public async Task A_new_faq_is_version_1_pending_review_and_once_approved_the_retrieval_preview_finds_it_located_FAQ()
    {
        var owner = await OwnerAsync();
        var manual = await UploadMarkdownAsync(owner, "退貨說明.md", "收到商品後七天內可申請退貨。");
        await RunJobsAsync();
        (await ApproveAsync(owner, manual)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, new { question = $"  {ReturnQuestion}\n", answer = TenDays });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var created = await FaqAsync(response);
        response.Headers.Location!.OriginalString.ShouldBe($"{FaqsPath(owner)}/{created.Document.Id}");
        (created.Document.Kind, created.Document.Name, created.Document.Status, created.Document.LatestVersionNumber)
            .ShouldBe(("faq", ReturnQuestion, "queued", 1));
        (created.Document.LatestVersionState, created.Document.EffectiveVersionNumber, created.Document.InEffect)
            .ShouldBe(("pending-review", (int?)null, false));
        created.Latest.ShouldBe(new FaqContent(created.Document.LatestVersionId, 1, ReturnQuestion, TenDays));
        created.Effective.ShouldBeNull();

        // Stored like an upload: the document, version 1, its content, a content-free activity
        // row and a processing job — the embedding goes through the queue.
        var versionId = created.Latest.VersionId;
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var document = await dbContext.KnowledgeDocuments.AsNoTracking().SingleAsync(candidate => candidate.Id == created.Document.Id, CancellationToken);
            document.Kind.ShouldBe(KnowledgeItemKind.Faq);
            var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(candidate => candidate.Id == versionId, CancellationToken);
            (version.ContentType, version.FileName, version.ReviewState, version.UploadedByAccountId)
                .ShouldBe((KnowledgeFaqEntry.ContentType, ReturnQuestion, KnowledgeReviewState.PendingReview, owner.AccountId));
            var content = await dbContext.KnowledgeFileContents.AsNoTracking().SingleAsync(file => file.VersionId == versionId, CancellationToken);
            content.Bytes.ShouldBe(new KnowledgeFaqEntry(ReturnQuestion, TenDays).ToContent());
            version.Sha256.ShouldBe(KnowledgeUploadRules.Sha256(content.Bytes));
            var activity = await dbContext.KnowledgeActivities.AsNoTracking().SingleAsync(row => row.DocumentId == document.Id, CancellationToken);
            (activity.Action, activity.VersionId, activity.ActorAccountId, activity.Detail)
                .ShouldBe((KnowledgeActivityAction.FaqCreated, (Guid?)versionId, (Guid?)owner.AccountId, (string?)null));
            var job = await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(candidate => candidate.Status == BackgroundJobStatus.Queued, CancellationToken);
            job.Kind.ShouldBe(ProcessKnowledgeVersionJob.Kind);
        }

        await RunJobsAsync();

        // One unit and one embedded chunk, located 「FAQ」, holding question and answer.
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(candidate => candidate.Id == versionId, CancellationToken);
            (version.ProcessingStatus, version.Issue).ShouldBe((KnowledgeDocumentStatus.Ready, (string?)null));
            var unit = await dbContext.KnowledgeExtractedUnits.AsNoTracking().SingleAsync(candidate => candidate.VersionId == versionId, CancellationToken);
            (unit.LocationKind, unit.LocationLabel, unit.Readable).ShouldBe((KnowledgeUnitLocationKind.Faq, "FAQ", true));
            var chunk = await dbContext.KnowledgeChunks.AsNoTracking().SingleAsync(candidate => candidate.VersionId == versionId, CancellationToken);
            (chunk.LocationLabel, chunk.Text).ShouldBe(("FAQ", $"問：{ReturnQuestion}\n答：{TenDays}"));
            chunk.Embedding.ShouldNotBeNull();
            chunk.EmbeddingModel.ShouldBe(AuthHostFixture.EmbeddingModel);
            (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedDocument, CancellationToken))
                .ShouldBe(2, "the manual's batch and the FAQ's");
        }

        // Pending: never retrieved, except when the owner asks to see pending versions.
        var pending = await PreviewAsync(owner, ReturnQuestion);
        pending.Passages.ShouldNotContain(passage => passage.DocumentId == created.Document.Id);
        var included = (await PreviewAsync(owner, ReturnQuestion, includePending: true)).Passages[0];
        (included.DocumentId, included.VersionState, included.LocationLabel).ShouldBe((created.Document.Id, "pending-review", "FAQ"));

        (await ApproveAsync(owner, versionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var preview = await PreviewAsync(owner, ReturnQuestion);
        var top = preview.Passages[0];
        (top.DocumentId, top.DocumentName, top.VersionNumber, top.VersionState, top.LocationLabel, top.VersionId)
            .ShouldBe((created.Document.Id, ReturnQuestion, 1, "effective", "FAQ", versionId));
        top.Excerpt.ShouldBe($"問：{ReturnQuestion}\n答：{TenDays}");
        top.Score.ShouldBe(0.9, 1e-4);
        preview.Passages[1].DocumentName.ShouldBe("退貨說明.md", "the approved document still scores 0.8");
        preview.BelowThreshold.ShouldBeFalse();

        // Listed with the documents and counted as an FAQ.
        var detail = await JsonAsync(await owner.Spa.GetAsync($"{BasePath}/{owner.KnowledgeBaseId}", owner.Token));
        detail.GetProperty("documents").EnumerateArray().Select(item => (item.GetProperty("kind").GetString(), item.GetProperty("name").GetString()))
            .ShouldBe([("document", "退貨說明.md"), ("faq", ReturnQuestion)]);
        var summary = detail.GetProperty("summary");
        (summary.GetProperty("documentCount").GetInt32(), summary.GetProperty("faqCount").GetInt32(), summary.GetProperty("inEffectCount").GetInt32())
            .ShouldBe((1, 1, 2));
        var listed = (await JsonAsync(await owner.Spa.GetAsync(BasePath, owner.Token))).EnumerateArray().Single();
        (listed.GetProperty("documentCount").GetInt32(), listed.GetProperty("faqCount").GetInt32()).ShouldBe((1, 1));
    }

    // --- Acceptance: an edit is pending; the old answer keeps serving until it is approved ---

    [Fact]
    public async Task After_an_edit_the_retrieval_preview_returns_the_old_answer_until_the_edit_is_approved()
    {
        var owner = await OwnerAsync();
        var faq = await CreateApprovedFaqAsync(owner, ReturnQuestion, TenDays);

        var response = await owner.Spa.PutAsync($"{FaqsPath(owner)}/{faq.Document.Id}", owner.Token, new { question = ReturnQuestion, answer = FourteenDays });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var edited = await FaqAsync(response);
        (edited.Document.Id, edited.Document.Name, edited.Document.LatestVersionNumber, edited.Document.LatestVersionState)
            .ShouldBe((faq.Document.Id, ReturnQuestion, 2, "pending-review"));
        (edited.Document.EffectiveVersionNumber, edited.Document.InEffect).ShouldBe(((int?)1, true));
        (edited.Latest.VersionNumber, edited.Latest.Answer).ShouldBe((2, FourteenDays));
        edited.Effective.ShouldBe(faq.Latest, "version 1 still answers");
        await RunJobsAsync();

        var effectiveOnly = await PreviewAsync(owner, ReturnQuestion);
        var served = effectiveOnly.Passages.ShouldHaveSingleItem();
        (served.VersionNumber, served.VersionState, served.LocationLabel).ShouldBe((1, "effective", "FAQ"));
        served.Excerpt.ShouldContain(TenDays);
        served.Excerpt.ShouldNotContain("十四天");

        var withPending = await PreviewAsync(owner, ReturnQuestion, includePending: true);
        withPending.Passages.Select(passage => (passage.VersionNumber, passage.VersionState)).ShouldBe([(2, "pending-review"), (1, "effective")]);
        withPending.Passages[0].Excerpt.ShouldContain(FourteenDays);
        withPending.Passages[0].Score.ShouldBe(0.95, 1e-4);

        var beforeApproval = await GetFaqAsync(owner, faq.Document.Id);
        (beforeApproval.Latest.Answer, beforeApproval.Effective!.Answer).ShouldBe((FourteenDays, TenDays));

        (await ApproveAsync(owner, edited.Latest.VersionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var afterApproval = await PreviewAsync(owner, ReturnQuestion, includePending: true);
        var current = afterApproval.Passages.ShouldHaveSingleItem("version 1 is archived");
        (current.VersionNumber, current.VersionState).ShouldBe((2, "effective"));
        current.Excerpt.ShouldContain(FourteenDays);
        var approved = await GetFaqAsync(owner, faq.Document.Id);
        approved.Effective.ShouldBe(approved.Latest);
        approved.Latest.Answer.ShouldBe(FourteenDays);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeActivities.AsNoTracking().Where(row => row.DocumentId == faq.Document.Id)
                .OrderBy(row => row.At).Select(row => row.Action).ToListAsync(CancellationToken))
            .ShouldBe([KnowledgeActivityAction.FaqCreated, KnowledgeActivityAction.VersionApproved, KnowledgeActivityAction.FaqUpdated, KnowledgeActivityAction.VersionApproved]);
    }

    [Fact]
    public async Task Editing_the_question_renames_the_entry_and_a_long_question_is_listed_cut()
    {
        var owner = await OwnerAsync();
        var longQuestion = new string('問', 299) + "？";
        var created = await CreateFaqAsync(owner, longQuestion, "答案。");

        created.Document.Name.ShouldBe(new string('問', KnowledgeDocument.NameMaxLength - 1) + "…");
        created.Latest.Question.ShouldBe(longQuestion);

        var response = await owner.Spa.PutAsync(
            $"{FaqsPath(owner)}/{created.Document.Id}", owner.Token, new { question = "退貨期限是幾天？", answer = "答案。" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var renamed = await FaqAsync(response);
        (renamed.Document.Name, renamed.Latest.Question, renamed.Latest.VersionNumber).ShouldBe(("退貨期限是幾天？", "退貨期限是幾天？", 2));
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeDocumentVersions.AsNoTracking().Where(version => version.DocumentId == created.Document.Id)
                .OrderBy(version => version.VersionNumber).Select(version => version.FileName).ToListAsync(CancellationToken))
            .ShouldBe([created.Document.Name, "退貨期限是幾天？"], "each version keeps the name its question gave");
    }

    // --- Validation and duplicates ----------------------------------------------------------

    [Fact]
    public async Task Missing_blank_or_too_long_fields_get_422_naming_each_field_and_nothing_is_written()
    {
        var owner = await OwnerAsync();

        foreach (var (body, fields) in new (object Body, string[] Fields)[]
        {
            (new { }, ["question", "answer"]),
            (new { question = " \n ", answer = "答" }, ["question"]),
            (new { question = "問", answer = "\r\n\t" }, ["answer"]),
            (new { question = new string('問', KnowledgeFaqEntry.QuestionMaxLength + 1), answer = new string('答', KnowledgeFaqEntry.AnswerMaxLength + 1) }, ["question", "answer"]),
        })
        {
            var response = await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, body);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(error => error.Name).ShouldBe(fields, ignoreOrder: true);
        }

        var tooLong = await JsonAsync(await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, new { question = "問", answer = new string('答', 4001) }));
        tooLong.GetProperty("errors").GetProperty("answer")[0].GetString().ShouldBe(KnowledgeFaqRules.AnswerTooLongMessage);
        (await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, new { question = new string('問', 500), answer = new string('答', 4000) }))
            .StatusCode.ShouldBe(HttpStatusCode.Created, "500 and 4000 characters are fine");

        var faq = await CreateFaqAsync(owner, ReturnQuestion, TenDays);
        var edit = await owner.Spa.PutAsync($"{FaqsPath(owner)}/{faq.Document.Id}", owner.Token, new { question = ReturnQuestion, answer = " " });
        edit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeDocuments.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.KnowledgeDocumentVersions.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.BackgroundJobs.CountAsync(CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task The_same_question_or_the_same_content_twice_gets_422_duplicate_name_or_duplicate_content()
    {
        var owner = await OwnerAsync();
        var first = await CreateFaqAsync(owner, ReturnQuestion, TenDays);
        var second = await CreateFaqAsync(owner, "運費怎麼算？", "滿千免運。");

        await AssertRefusedAsync(
            await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, new { question = ReturnQuestion, answer = "另一個答案。" }),
            "duplicate-name");
        var sameContent = await AssertRefusedAsync(
            await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, new { question = $"{ReturnQuestion} ", answer = $"{TenDays}\n" }),
            "duplicate-content");
        sameContent.GetProperty("existingDocumentName").GetString().ShouldBe(ReturnQuestion);

        var secondPath = $"{FaqsPath(owner)}/{second.Document.Id}";
        await AssertRefusedAsync(await owner.Spa.PutAsync(secondPath, owner.Token, new { question = ReturnQuestion, answer = "滿千免運。" }), "duplicate-name");
        var unchanged = await AssertRefusedAsync(await owner.Spa.PutAsync(secondPath, owner.Token, new { question = "運費怎麼算？", answer = "滿千免運。" }), "duplicate-content");
        unchanged.GetProperty("message").GetString().ShouldBe("內容與這則 FAQ 的第 1 版完全相同，不需要再新增一個版本。");
        var copied = await AssertRefusedAsync(await owner.Spa.PutAsync(secondPath, owner.Token, new { question = ReturnQuestion, answer = TenDays }), "duplicate-content");
        copied.GetProperty("existingDocumentName").GetString().ShouldBe(ReturnQuestion);

        // A file named like a question is the same name.
        await UploadMarkdownAsync(owner, "營業時間.md", "平日九點到六點。");
        await AssertRefusedAsync(
            await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, new { question = "營業時間.md", answer = "平日九點到六點。" }),
            "duplicate-name");

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeDocuments.CountAsync(document => document.Kind == KnowledgeItemKind.Faq, CancellationToken)).ShouldBe(2);
        (await dbContext.KnowledgeDocumentVersions.CountAsync(version => version.DocumentId == first.Document.Id || version.DocumentId == second.Document.Id, CancellationToken))
            .ShouldBe(2, "no refused write left a version");
    }

    // --- The documents' endpoints work on FAQ entries ---------------------------------------

    [Fact]
    public async Task Extraction_preview_exclusion_disable_enable_and_history_work_on_an_faq_through_the_document_endpoints()
    {
        var owner = await OwnerAsync();
        var faq = await CreateFaqAsync(owner, ReturnQuestion, TenDays);
        await RunJobsAsync();
        var documentPath = $"{BasePath}/{owner.KnowledgeBaseId}/documents/{faq.Document.Id}";
        var versionPath = $"{documentPath}/versions/{faq.Latest.VersionId}";

        var extraction = await JsonAsync(await owner.Spa.GetAsync($"{versionPath}/preview", owner.Token));
        extraction.GetProperty("status").GetString().ShouldBe("ready");
        var unit = extraction.GetProperty("units").EnumerateArray().ShouldHaveSingleItem();
        (unit.GetProperty("locationKind").GetString(), unit.GetProperty("locationLabel").GetString(), unit.GetProperty("readable").GetBoolean())
            .ShouldBe(("faq", "FAQ", true));
        var chunkId = unit.GetProperty("chunks").EnumerateArray().ShouldHaveSingleItem().GetProperty("id").GetGuid();

        (await ApproveAsync(owner, faq.Latest.VersionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PreviewAsync(owner, ReturnQuestion)).Passages.ShouldHaveSingleItem().ChunkId.ShouldBe(chunkId);

        (await owner.Spa.PutAsync($"{versionPath}/chunks/{chunkId}/exclusion", owner.Token, new { excluded = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PreviewAsync(owner, ReturnQuestion)).Passages.ShouldBeEmpty("excluded");
        (await owner.Spa.PutAsync($"{versionPath}/chunks/{chunkId}/exclusion", owner.Token, new { excluded = false })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await owner.Spa.PostAsync($"{documentPath}/disable", owner.Token, new { reason = "答案待確認" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PreviewAsync(owner, ReturnQuestion, includePending: true)).Passages.ShouldBeEmpty("disabled");
        (await owner.Spa.PostAsync($"{documentPath}/enable", owner.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PreviewAsync(owner, ReturnQuestion)).Passages.ShouldHaveSingleItem().ChunkId.ShouldBe(chunkId);

        var history = await JsonAsync(await owner.Spa.GetAsync(documentPath, owner.Token));
        history.GetProperty("document").GetProperty("kind").GetString().ShouldBe("faq");
        var version = history.GetProperty("versions").EnumerateArray().ShouldHaveSingleItem();
        (version.GetProperty("contentType").GetString(), version.GetProperty("state").GetString()).ShouldBe((KnowledgeFaqEntry.ContentType, "effective"));
        history.GetProperty("activities").EnumerateArray().Select(activity => activity.GetProperty("action").GetString()).Reverse().ShouldBe(
            ["faq-created", "version-approved", "chunk-excluded", "chunk-included", "document-disabled", "document-enabled"]);
    }

    // --- Deleting ---------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_an_faq_removes_its_versions_content_units_and_chunks_records_faq_deleted_and_updates_faqCount()
    {
        var owner = await OwnerAsync();
        var doomed = await CreateApprovedFaqAsync(owner, ReturnQuestion, TenDays);
        var edit = await owner.Spa.PutAsync($"{FaqsPath(owner)}/{doomed.Document.Id}", owner.Token, new { question = ReturnQuestion, answer = FourteenDays });
        edit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var kept = await CreateFaqAsync(owner, "運費怎麼算？", "滿千免運。");
        await RunJobsAsync();
        (await SummaryAsync(owner)).GetProperty("faqCount").GetInt32().ShouldBe(2);

        var response = await owner.Spa.DeleteAsync($"{FaqsPath(owner)}/{doomed.Document.Id}", owner.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            (await dbContext.KnowledgeDocuments.AnyAsync(document => document.Id == doomed.Document.Id, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeDocumentVersions.AnyAsync(version => version.DocumentId == doomed.Document.Id, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeChunks.AnyAsync(chunk => chunk.DocumentId == doomed.Document.Id, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeFileContents.CountAsync(CancellationToken)).ShouldBe(1);
            (await dbContext.KnowledgeExtractedUnits.Select(unit => unit.VersionId).ToListAsync(CancellationToken)).ShouldBe([kept.Latest.VersionId]);
            var deleted = await dbContext.KnowledgeActivities.AsNoTracking()
                .SingleAsync(row => row.DocumentId == doomed.Document.Id && row.Action == KnowledgeActivityAction.FaqDeleted, CancellationToken);
            (deleted.VersionId, deleted.ActorAccountId, deleted.Detail).ShouldBe(((Guid?)null, (Guid?)owner.AccountId, (string?)null));
            (await dbContext.KnowledgeActivities.CountAsync(row => row.DocumentId == doomed.Document.Id, CancellationToken))
                .ShouldBe(4, "created, approved, updated and deleted: the log outlives the entry");
        }

        (await PreviewAsync(owner, ReturnQuestion, includePending: true)).Passages.ShouldNotContain(passage => passage.DocumentId == doomed.Document.Id);
        var summary = await SummaryAsync(owner);
        (summary.GetProperty("faqCount").GetInt32(), summary.GetProperty("documentCount").GetInt32()).ShouldBe((1, 0));

        // Gone means gone: the same 403 as an id that never existed.
        await AssertIdenticalAsync(
            await owner.Spa.DeleteAsync($"{FaqsPath(owner)}/{doomed.Document.Id}", owner.Token),
            await owner.Spa.DeleteAsync($"{FaqsPath(owner)}/{Guid.NewGuid()}", owner.Token));
        await AssertIdenticalAsync(
            await owner.Spa.GetAsync($"{FaqsPath(owner)}/{doomed.Document.Id}", owner.Token),
            await owner.Spa.GetAsync($"{FaqsPath(owner)}/{Guid.NewGuid()}", owner.Token));
    }

    // --- Owner only -------------------------------------------------------------------------

    [Fact]
    public async Task Other_organizations_non_owners_nonexistent_ids_and_documents_get_identical_403s_on_every_faq_endpoint()
    {
        var owner = await OwnerAsync();
        var stranger = await OwnerAsync("別的組織");
        await _host.CreateAccountAsync(owner.Organization, "staff", StaffPassword, AccountRole.SmbAdmin, "同事", AccountPermission.ManageDataSources);
        var colleague = new SpaClient(_host.Scripted.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var colleagueToken = (await colleague.SignInAsync(owner.Organization.Code, "staff", StaffPassword)).AccessToken;
        var faq = await CreateFaqAsync(owner, ReturnQuestion, TenDays);
        var documentId = await UploadMarkdownAsync(owner, "退貨說明.md", "收到商品後七天內可申請退貨。", returnDocumentId: true);

        var reference = await ResponseFingerprint.FromAsync(await stranger.Spa.GetAsync($"{BasePath}/{Guid.NewGuid()}/faqs/{Guid.NewGuid()}", stranger.Token));
        reference.Status.ShouldBe(HttpStatusCode.Forbidden);
        JsonDocument.Parse(reference.Body).RootElement.GetProperty("reason").GetString().ShouldBe("knowledge-base");

        foreach (var (who, spa, token) in new (string, SpaClient, string)[]
        {
            ("other organization", stranger.Spa, stranger.Token),
            ("same-organization admin", colleague, colleagueToken),
        })
        {
            foreach (var (endpoint, send) in FaqEndpoints(spa, token))
            {
                await AssertSameAsync(reference, await send(owner.KnowledgeBaseId, faq.Document.Id), $"{who} {endpoint}");
                await AssertSameAsync(reference, await send(Guid.NewGuid(), Guid.NewGuid()), $"{who} {endpoint} (nonexistent)");
            }

            // Refused before the body is looked at.
            await AssertSameAsync(reference, await spa.PostAsync(FaqsPath(owner), token, new { question = " " }), $"{who} blank POST");
        }

        // The owner too, for an id that is not an FAQ entry of this knowledge base — an uploaded
        // document included — and a file cannot become a version of an FAQ entry.
        foreach (var (endpoint, send) in FaqEndpoints(owner.Spa, owner.Token).Where(endpoint => endpoint.Name != "POST faqs"))
        {
            await AssertSameAsync(reference, await send(owner.KnowledgeBaseId, documentId), $"owner {endpoint} on a document");
            await AssertSameAsync(reference, await send(owner.KnowledgeBaseId, Guid.NewGuid()), $"owner {endpoint} on a nonexistent id");
        }

        await AssertSameAsync(
            reference,
            await owner.PostFileAsync($"{BasePath}/{owner.KnowledgeBaseId}/documents/{faq.Document.Id}/versions", "新版.md", Encoding.UTF8.GetBytes("# 新版\n\n內容。\n")),
            "a file as a new version of an FAQ entry");

        using var anonymous = _host.CreateSpaClient();
        var path = $"{FaqsPath(owner)}/{faq.Document.Id}";
        (await anonymous.Http.PostAsync(FaqsPath(owner), JsonContent(new { question = "問", answer = "答" }), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.GetAsync(path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.PutAsync(path, JsonContent(new { question = "問", answer = "答" }), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.DeleteAsync(path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Nothing changed.
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeDocuments.Select(document => document.Id).ToListAsync(CancellationToken)).ShouldBe([faq.Document.Id, documentId], ignoreOrder: true);
        (await dbContext.KnowledgeDocumentVersions.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.KnowledgeDocuments.SingleAsync(document => document.Id == faq.Document.Id, CancellationToken)).Name.ShouldBe(ReturnQuestion);
    }

    // --- Helpers ----------------------------------------------------------------------------

    private sealed record FaqView(Item Document, FaqContent Latest, FaqContent? Effective);

    private sealed record Item(
        Guid Id,
        string Kind,
        string Name,
        string Status,
        Guid LatestVersionId,
        int LatestVersionNumber,
        string LatestVersionState,
        int? EffectiveVersionNumber,
        bool InEffect);

    private sealed record FaqContent(Guid VersionId, int VersionNumber, string Question, string Answer);

    private sealed record Preview(IReadOnlyList<Passage> Passages, bool BelowThreshold);

    private sealed record Passage(Guid DocumentId, string DocumentName, int VersionNumber, string VersionState, string LocationLabel, string Excerpt, double Score, Guid VersionId, Guid ChunkId);

    private static string FaqsPath(KnowledgeTestOwner owner) => $"{BasePath}/{owner.KnowledgeBaseId}/faqs";

    private Task<KnowledgeTestOwner> OwnerAsync(string name = "安心商行") => KnowledgeTestOwner.CreateAsync(_host, name, _host.Scripted);

    private Task RunJobsAsync() => _host.Scripted.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private static async Task<FaqView> CreateFaqAsync(KnowledgeTestOwner owner, string question, string answer)
    {
        var response = await owner.Spa.PostAsync(FaqsPath(owner), owner.Token, new { question, answer });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await FaqAsync(response);
    }

    private async Task<FaqView> CreateApprovedFaqAsync(KnowledgeTestOwner owner, string question, string answer)
    {
        var faq = await CreateFaqAsync(owner, question, answer);
        await RunJobsAsync();
        (await ApproveAsync(owner, faq.Latest.VersionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return await GetFaqAsync(owner, faq.Document.Id);
    }

    private static async Task<FaqView> GetFaqAsync(KnowledgeTestOwner owner, Guid documentId)
    {
        var response = await owner.Spa.GetAsync($"{FaqsPath(owner)}/{documentId}", owner.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await FaqAsync(response);
    }

    /// <returns>The version id, or with <paramref name="returnDocumentId"/> the document id.</returns>
    private static async Task<Guid> UploadMarkdownAsync(KnowledgeTestOwner owner, string fileName, string body, bool returnDocumentId = false)
    {
        var response = await owner.PostFileAsync(
            $"{BasePath}/{owner.KnowledgeBaseId}/documents", fileName, Encoding.UTF8.GetBytes($"# {fileName}\n\n{body}\n"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var view = await JsonAsync(response);
        return view.GetProperty(returnDocumentId ? "id" : "latestVersionId").GetGuid();
    }

    private static Task<HttpResponseMessage> ApproveAsync(KnowledgeTestOwner owner, Guid versionId) =>
        owner.Spa.PostAsync($"{BasePath}/{owner.KnowledgeBaseId}/versions/approve", owner.Token, new { versionIds = new[] { versionId.ToString() } });

    private static async Task<Preview> PreviewAsync(KnowledgeTestOwner owner, string question, bool? includePending = null)
    {
        var response = await owner.Spa.PostAsync($"{BasePath}/{owner.KnowledgeBaseId}/retrieval-preview", owner.Token, new { question, includePending });
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<Preview>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task<JsonElement> SummaryAsync(KnowledgeTestOwner owner) =>
        (await JsonAsync(await owner.Spa.GetAsync($"{BasePath}/{owner.KnowledgeBaseId}", owner.Token))).GetProperty("summary");

    /// <summary>Every FAQ endpoint with a valid request, so only the ids can make it fail.
    /// Arguments: knowledge base, FAQ entry.</summary>
    private static IEnumerable<(string Name, Func<Guid, Guid, Task<HttpResponseMessage>> Send)> FaqEndpoints(SpaClient spa, string token) =>
    [
        ("POST faqs", (knowledgeBase, _) => spa.PostAsync($"{BasePath}/{knowledgeBase}/faqs", token, new { question = $"新問題 {Guid.NewGuid()}", answer = "答" })),
        ("GET faq", (knowledgeBase, faq) => spa.GetAsync($"{BasePath}/{knowledgeBase}/faqs/{faq}", token)),
        ("PUT faq", (knowledgeBase, faq) => spa.PutAsync($"{BasePath}/{knowledgeBase}/faqs/{faq}", token, new { question = "改過的問題", answer = "改過的答案" })),
        ("DELETE faq", (knowledgeBase, faq) => spa.DeleteAsync($"{BasePath}/{knowledgeBase}/faqs/{faq}", token)),
    ];

    private static async Task<JsonElement> AssertRefusedAsync(HttpResponseMessage response, string reason)
    {
        var body = await JsonAsync(response);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body.ToString());
        body.GetProperty("reason").GetString().ShouldBe(reason);
        body.GetProperty("errors").EnumerateObject().Select(error => error.Name).ShouldBe(["question"]);
        return body;
    }

    private static async Task AssertSameAsync(ResponseFingerprint expected, HttpResponseMessage response, string because)
    {
        var actual = await ResponseFingerprint.FromAsync(response);
        (actual.Status, actual.ContentType, actual.SetsCookie).ShouldBe((expected.Status, expected.ContentType, expected.SetsCookie), because);
        actual.Body.ShouldBe(expected.Body, because);
    }

    private static async Task AssertIdenticalAsync(HttpResponseMessage first, HttpResponseMessage second)
    {
        var a = await ResponseFingerprint.FromAsync(first);
        a.Status.ShouldBe(HttpStatusCode.Forbidden);
        await AssertSameAsync(a, second, "identical 403s");
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    private static async Task<FaqView> FaqAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<FaqView>(await response.Content.ReadAsStringAsync(CancellationToken), JsonSerializerOptions.Web)!;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
