using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Knowledge.Extraction;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// <c>/api/v1/knowledge-bases/{id}/documents</c> against real PostgreSQL (M2 plan, Slice 5;
/// ticket #39). Each test builds its own organizations, so tests never see each other's
/// data. The job worker is off; tests run <see cref="JobRunner"/> themselves (it may also
/// process jobs earlier tests left queued, which is harmless: they are their own
/// organizations' and end in a final state).
/// </summary>
/// <remarks>
/// <c>Knowledge:MaxFileBytes</c> stays at its 20 MB default: the size tests really send
/// 21 MB (in memory, well under a second), and the boundary itself with 20 MB + 1 byte.
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public class KnowledgeDocumentEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Knowledge-Document-Pass-1!";
    private const string BasePath = "/api/v1/knowledge-bases";

    private readonly AuthHostFixture _host;

    public KnowledgeDocumentEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private JobRunner Runner => _host.Factory.Services.GetRequiredService<JobRunner>();

    // --- Acceptance: PDF, DOCX, XLSX and MD each get 201 ------------------------------

    [Fact]
    public async Task Pdf_docx_xlsx_and_md_each_get_201_queued_with_version_1_original_bytes_activity_and_job_in_one_go()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        var batchId = Guid.NewGuid();
        var files = new (string Name, byte[] Content, string ContentType)[]
        {
            ("退貨政策.pdf", TestFiles.Pdf("退貨"), "application/pdf"),
            ("產品說明.docx", TestFiles.Docx("說明"), "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ("配送時間.xlsx", TestFiles.Xlsx("配送"), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ("常見問題.md", TestFiles.Markdown("常見問題"), "text/markdown"),
        };

        var created = new List<(Guid DocumentId, string Name, byte[] Content, string ContentType)>();
        foreach (var (name, content, contentType) in files)
        {
            // A bearer token and no antiforgery token: the upload endpoint does not require
            // one (DisableAntiforgery), so a plain API client can upload.
            var response = await UploadAsync(admin, knowledgeBaseId, name, content, batchId.ToString());

            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
            var view = await BodyJsonAsync(response);
            view.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "kind", "name", "status", "issue", "updatedAt"]);
            view.GetProperty("kind").GetString().ShouldBe("document");
            view.GetProperty("name").GetString().ShouldBe(name);
            view.GetProperty("status").GetString().ShouldBe("queued");
            view.GetProperty("issue").ValueKind.ShouldBe(JsonValueKind.Null);
            var documentId = view.GetProperty("id").GetGuid();
            response.Headers.Location!.ToString().ShouldBe($"{BasePath}/{knowledgeBaseId}/documents/{documentId}");
            created.Add((documentId, name, content, contentType));
        }

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var jobs = await dbContext.BackgroundJobs.AsNoTracking().ToListAsync(CancellationToken);
        foreach (var (documentId, name, content, contentType) in created)
        {
            var document = await dbContext.KnowledgeDocuments.SingleAsync(candidate => candidate.Id == documentId, CancellationToken);
            document.KnowledgeBaseId.ShouldBe(knowledgeBaseId);
            document.Name.ShouldBe(name);

            var version = await dbContext.KnowledgeDocumentVersions.SingleAsync(candidate => candidate.DocumentId == documentId, CancellationToken);
            version.VersionNumber.ShouldBe(1);
            version.FileName.ShouldBe(name);
            version.ContentType.ShouldBe(contentType);
            version.SizeBytes.ShouldBe(content.Length);
            version.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
            version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Queued);
            version.UploadedByAccountId.ShouldBe(org.Admin.Id);
            version.UploadBatchId.ShouldBe(batchId);

            (await dbContext.KnowledgeFileContents.SingleAsync(file => file.VersionId == version.Id, CancellationToken))
                .Bytes.ShouldBe(content);

            var activity = await dbContext.KnowledgeActivities.SingleAsync(candidate => candidate.DocumentId == documentId, CancellationToken);
            activity.Action.ShouldBe(KnowledgeActivityAction.DocumentUploaded);
            activity.VersionId.ShouldBe(version.Id);
            activity.ActorAccountId.ShouldBe(org.Admin.Id);
            activity.Detail.ShouldBeNull();

            var job = jobs.Where(candidate => PayloadVersionId(candidate) == version.Id).ShouldHaveSingleItem();
            job.Kind.ShouldBe(ProcessKnowledgeVersionJob.Kind);
            job.Status.ShouldBe(BackgroundJobStatus.Queued);
            job.Payload.ShouldNotContain(name, Case.Sensitive, "a job payload carries ids only");
        }

        jobs.Count.ShouldBe(files.Length);
    }

    // --- Acceptance: 415 for a renamed .exe, and other format refusals -----------------

    [Fact]
    public async Task A_renamed_exe_and_unsupported_or_mismatched_files_get_415_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);

        var cases = new (string Name, byte[] Content, string Reason)[]
        {
            ("安裝程式.pdf", TestFiles.Exe(), "file-content-mismatch"),
            ("安裝程式.exe", TestFiles.Exe(), "unsupported-file-type"),
            ("舊版.doc", TestFiles.Pdf("doc"), "unsupported-file-type"),
            ("其實是PDF.docx", TestFiles.Pdf("docx"), "file-content-mismatch"),
            ("其實是試算表.docx", TestFiles.Xlsx("xlsx"), "file-content-mismatch"),
            ("其實是文件.xlsx", TestFiles.Docx("docx"), "file-content-mismatch"),
        };

        foreach (var (name, content, reason) in cases)
        {
            var response = await UploadAsync(admin, knowledgeBaseId, name, content);

            response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, name);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            var body = await BodyJsonAsync(response);
            body.GetProperty("status").GetInt32().ShouldBe(415);
            body.GetProperty("reason").GetString().ShouldBe(reason, name);
            body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        await AssertNothingWrittenAsync(org);
    }

    // --- Acceptance: 413 for a 21 MB file ----------------------------------------------

    [Fact]
    public async Task A_21_MB_file_and_one_byte_over_the_limit_get_413_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        var limit = KnowledgeOptions.DefaultMaxFileBytes;

        // 21 MB: refused from Content-Length, before the body is read.
        var huge = await UploadAsync(admin, knowledgeBaseId, "掃描檔.pdf", TestFiles.PdfOfSize(21L * 1024 * 1024));
        huge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var body = await BodyJsonAsync(huge);
        body.GetProperty("reason").GetString().ShouldBe("file-too-large");
        body.GetProperty("message").GetString().ShouldBe("檔案超過 20 MB 的上限，請分割或壓縮後再上傳。");

        // One byte over: the request fits the multipart allowance, the file does not.
        var justOver = await UploadAsync(admin, knowledgeBaseId, "剛好超過.pdf", TestFiles.PdfOfSize(limit + 1));
        justOver.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await BodyJsonAsync(justOver)).GetProperty("reason").GetString().ShouldBe("file-too-large");

        // Exactly the limit passes the size check (and then fails the content check, so no
        // 20 MB row is written).
        var atLimit = await UploadAsync(admin, knowledgeBaseId, "剛好上限.pdf", TestFiles.ExeOfSize(limit));
        atLimit.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        await AssertNothingWrittenAsync(org);
    }

    // --- Acceptance: duplicate content and duplicate name --------------------------------

    [Fact]
    public async Task The_same_content_under_another_name_gets_422_duplicate_content_naming_the_existing_document()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        var content = TestFiles.Pdf("退貨政策");
        (await UploadAsync(admin, knowledgeBaseId, "退貨政策.pdf", content)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await UploadAsync(admin, knowledgeBaseId, "退貨政策（最終版）.pdf", content);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("duplicate-content");
        body.GetProperty("existingDocumentName").GetString().ShouldBe("退貨政策.pdf");
        body.GetProperty("message").GetString().ShouldBe("這份檔案的內容與「退貨政策.pdf」完全相同，不需要重複上傳。");
        body.GetProperty("errors").GetProperty("file")[0].GetString().ShouldBe(body.GetProperty("message").GetString());

        // Another knowledge base may hold the same file.
        var otherKnowledgeBaseId = await CreateKnowledgeBaseAsync(admin, "另一個知識庫");
        (await UploadAsync(admin, otherKnowledgeBaseId, "退貨政策.pdf", content)).StatusCode.ShouldBe(HttpStatusCode.Created);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeDocuments.CountAsync(document => document.KnowledgeBaseId == knowledgeBaseId, CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_name_gets_422_duplicate_name_suggesting_a_new_version()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        (await UploadAsync(admin, knowledgeBaseId, "退貨政策.pdf", TestFiles.Pdf("第一版"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await UploadAsync(admin, knowledgeBaseId, "退貨政策.pdf", TestFiles.Pdf("第二版"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("duplicate-name");
        body.GetProperty("message").GetString()!.ShouldContain("上傳新版本");
        body.TryGetProperty("existingDocumentName", out _).ShouldBeFalse();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeDocumentVersions.CountAsync(CancellationToken)).ShouldBe(1);
        (await dbContext.KnowledgeFileContents.CountAsync(CancellationToken)).ShouldBe(1);
        (await dbContext.BackgroundJobs.CountAsync(CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_uploads_of_the_same_content_or_name_let_exactly_one_through()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);

        var content = TestFiles.Pdf("同時上傳");
        var sameContent = await Task.WhenAll(Enumerable.Range(1, 5)
            .Select(index => UploadAsync(admin, knowledgeBaseId, $"同時上傳-{index}.pdf", content)));
        var sameName = await Task.WhenAll(Enumerable.Range(1, 5)
            .Select(index => UploadAsync(admin, knowledgeBaseId, "同名.md", TestFiles.Markdown($"內容 {index}"))));

        foreach (var (responses, reason) in new[] { (sameContent, "duplicate-content"), (sameName, "duplicate-name") })
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
            foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await BodyJsonAsync(refused)).GetProperty("reason").GetString().ShouldBe(reason);
            }
        }

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeDocuments.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.KnowledgeFileContents.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.BackgroundJobs.CountAsync(CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Requests_without_exactly_one_file_or_with_a_bad_batch_id_get_422_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        var path = $"{BasePath}/{knowledgeBaseId}/documents";

        using (var noFile = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "batchId" } })
        {
            await AssertRefusedAsync(await SendAsync(admin, HttpMethod.Post, path, noFile), "file-missing", "file");
        }

        using (var twoFiles = new MultipartFormDataContent())
        {
            twoFiles.Add(new ByteArrayContent(TestFiles.Markdown("一")), "file", "一.md");
            twoFiles.Add(new ByteArrayContent(TestFiles.Markdown("二")), "file", "二.md");
            await AssertRefusedAsync(await SendAsync(admin, HttpMethod.Post, path, twoFiles), "too-many-files", "file");
        }

        await AssertRefusedAsync(
            await UploadAsync(admin, knowledgeBaseId, "說明.md", TestFiles.Markdown("批次"), batchId: "batch-1"), "invalid-batch-id", "batchId");

        // Not multipart at all: refused by routing, because the endpoint declares that it
        // accepts multipart/form-data only (the status HTTP defines for a wrong request
        // content type; the frontend always sends multipart).
        using (var json = new StringContent("""{"file":"說明.md"}""", Encoding.UTF8, "application/json"))
        {
            (await SendAsync(admin, HttpMethod.Post, path, json)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        }

        await AssertNothingWrittenAsync(org);
    }

    // --- Acceptance: the download is byte-identical --------------------------------------

    [Fact]
    public async Task The_downloaded_original_has_the_uploaded_sha256_stored_type_and_a_safe_chinese_file_name()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        const string name = "退貨政策 2026（修訂）.docx";
        var content = TestFiles.Docx("下載");

        // Sent the way browsers send it: the raw UTF-8 name in filename="...", no filename*.
        var uploaded = await UploadAsync(admin, knowledgeBaseId, name, content, browserStyleFileName: true);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created);
        var documentId = (await BodyJsonAsync(uploaded)).GetProperty("id").GetGuid();
        var versionId = await VersionIdAsync(org, documentId);

        var response = await admin.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}/documents/{documentId}/versions/{versionId}/file", admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var downloaded = await response.Content.ReadAsByteArrayAsync(CancellationToken);
        Convert.ToHexStringLower(SHA256.HashData(downloaded)).ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
        response.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        var disposition = response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        disposition.DispositionType.ShouldBe("attachment");
        disposition.FileNameStar.ShouldBe(name);
        var header = string.Join(",", response.Content.Headers.GetValues("Content-Disposition"));
        header.ShouldAllBe(character => character < 128, "the header itself is ASCII; the real name is percent-encoded in filename*");
        header.ShouldContain("filename*=UTF-8''");

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    // --- Retry ---------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_failed_version_can_be_retried_and_a_repeated_job_changes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);

        // Big5 text fails processing (not UTF-8), so there is a failed version to retry.
        var uploaded = await UploadAsync(admin, knowledgeBaseId, "公告.txt", KnowledgeFixtures.Read(KnowledgeFixtures.Big5Text));
        var documentId = (await BodyJsonAsync(uploaded)).GetProperty("id").GetGuid();
        var versionId = await VersionIdAsync(org, documentId);
        var retryPath = $"{BasePath}/{knowledgeBaseId}/documents/{documentId}/versions/{versionId}/retry";

        // Queued: 409, nothing enqueued.
        var whileQueued = await admin.Spa.PostAsync(retryPath, admin.Token, new { });
        whileQueued.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var conflict = await BodyJsonAsync(whileQueued);
        conflict.GetProperty("reason").GetString().ShouldBe("version-not-retryable");
        conflict.GetProperty("message").GetString().ShouldBe(KnowledgeVersionRules.StillProcessingMessage);

        await Runner.RunUntilIdleAsync(CancellationToken);

        var failed = await ReloadVersionAsync(org, versionId);
        failed.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);
        failed.Issue.ShouldBe(KnowledgeProcessingIssues.NotUtf8);
        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", admin.Token));
        var listed = detail.GetProperty("documents").EnumerateArray().ShouldHaveSingleItem();
        listed.GetProperty("status").GetString().ShouldBe("failed");
        listed.GetProperty("issue").GetString().ShouldBe(KnowledgeProcessingIssues.NotUtf8);

        // Failed: 200, back to queued with a new job and an activity row.
        var retried = await admin.Spa.PostAsync(retryPath, admin.Token, new { });
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(retried);
        view.GetProperty("id").GetGuid().ShouldBe(documentId);
        view.GetProperty("status").GetString().ShouldBe("queued");
        view.GetProperty("issue").ValueKind.ShouldBe(JsonValueKind.Null);

        // Queued again: 409 again.
        (await admin.Spa.PostAsync(retryPath, admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var jobs = (await dbContext.BackgroundJobs.AsNoTracking().ToListAsync(CancellationToken))
                .Where(job => PayloadVersionId(job) == versionId).ToList();
            jobs.Count.ShouldBe(2);
            jobs.Count(job => job.Status == BackgroundJobStatus.Queued).ShouldBe(1);
            var retry = await dbContext.KnowledgeActivities
                .SingleAsync(activity => activity.Action == KnowledgeActivityAction.VersionRetried, CancellationToken);
            retry.VersionId.ShouldBe(versionId);
            retry.ActorAccountId.ShouldBe(org.Admin.Id);
        }

        await Runner.RunUntilIdleAsync(CancellationToken);
        (await ReloadVersionAsync(org, versionId)).ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);

        // At-least-once delivery: the same job again changes nothing.
        var before = await ReloadVersionAsync(org, versionId);
        var duplicate = BackgroundJob.Create(
            org.Organization.Id, ProcessKnowledgeVersionJob.Kind, new ProcessKnowledgeVersionJob(versionId), _host.Clock.GetUtcNow());
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            dbContext.BackgroundJobs.Add(duplicate);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await Runner.RunUntilIdleAsync(CancellationToken);
        var after = await ReloadVersionAsync(org, versionId);
        after.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        (await ReloadJobAsync(org, duplicate.Id)).Status.ShouldBe(BackgroundJobStatus.Succeeded);
    }

    // --- List and detail count real documents -------------------------------------------

    [Fact]
    public async Task List_and_detail_show_documents_and_count_them_by_their_latest_versions_status()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        var first = await UploadAsync(admin, knowledgeBaseId, "第一份.md", TestFiles.Markdown("第一份"));
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        await Runner.RunUntilIdleAsync(CancellationToken);
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await UploadAsync(admin, knowledgeBaseId, "第二份.pdf", TestFiles.Pdf("第二份"));
        second.StatusCode.ShouldBe(HttpStatusCode.Created);

        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", admin.Token));

        var documents = detail.GetProperty("documents").EnumerateArray().ToList();
        documents.Select(document => (document.GetProperty("name").GetString(), document.GetProperty("status").GetString()))
            .ShouldBe([("第一份.md", "ready"), ("第二份.pdf", "queued")]);
        documents[1].GetRawText().ShouldBe((await BodyJsonAsync(second)).GetRawText());

        var summary = detail.GetProperty("summary");
        summary.GetProperty("documentCount").GetInt32().ShouldBe(2);
        summary.GetProperty("faqCount").GetInt32().ShouldBe(0);
        summary.GetProperty("statusCounts").EnumerateObject().Select(count => (count.Name, count.Value.GetInt32()))
            .ShouldBe([("queued", 1), ("processing", 0), ("ready", 1), ("partially-readable", 0), ("failed", 0)]);
        summary.GetProperty("updatedAt").GetDateTimeOffset().ShouldBe(documents[1].GetProperty("updatedAt").GetDateTimeOffset());

        var listed = (await BodyJsonAsync(await admin.Spa.GetAsync(BasePath, admin.Token))).EnumerateArray().ShouldHaveSingleItem();
        listed.GetRawText().ShouldBe(summary.GetRawText());

        // PATCH returns the same counts.
        var renamed = await BodyJsonAsync(await admin.Spa.PatchAsync($"{BasePath}/{knowledgeBaseId}", admin.Token, new { name = "改名" }));
        renamed.GetProperty("documentCount").GetInt32().ShouldBe(2);
    }

    // --- Acceptance: delete removes everything about the document ------------------------

    [Fact]
    public async Task Deleting_a_document_removes_it_its_versions_files_units_and_chunks_keeps_a_content_free_record_and_a_late_job_is_harmless()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin);
        var doomedId = (await BodyJsonAsync(await UploadAsync(admin, knowledgeBaseId, "要刪除.pdf", KnowledgeFixtures.Read(KnowledgeFixtures.ReturnPolicyPdf))))
            .GetProperty("id").GetGuid();
        var keptId = (await BodyJsonAsync(await UploadAsync(admin, knowledgeBaseId, "要保留.md", KnowledgeFixtures.Read(KnowledgeFixtures.FaqMarkdown))))
            .GetProperty("id").GetGuid();
        var doomedVersionId = await VersionIdAsync(org, doomedId);
        var keptVersionId = await VersionIdAsync(org, keptId);
        await Runner.RunUntilIdleAsync(CancellationToken);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.KnowledgeExtractedUnits.CountAsync(unit => unit.VersionId == doomedVersionId, CancellationToken)).ShouldBe(3);
            (await dbContext.KnowledgeChunks.CountAsync(chunk => chunk.VersionId == doomedVersionId, CancellationToken)).ShouldBe(3);

            // At-least-once delivery: the same job again, still queued when the document goes.
            dbContext.BackgroundJobs.Add(BackgroundJob.Create(
                org.Organization.Id, ProcessKnowledgeVersionJob.Kind, new ProcessKnowledgeVersionJob(doomedVersionId), _host.Clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var response = await admin.Spa.DeleteAsync($"{BasePath}/{knowledgeBaseId}/documents/{doomedId}", admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.KnowledgeDocuments.AnyAsync(document => document.Id == doomedId, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeDocumentVersions.AnyAsync(version => version.DocumentId == doomedId, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeFileContents.AnyAsync(file => file.VersionId == doomedVersionId, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeExtractedUnits.AnyAsync(unit => unit.VersionId == doomedVersionId, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeChunks.AnyAsync(chunk => chunk.DocumentId == doomedId, CancellationToken)).ShouldBeFalse();

            var activities = await dbContext.KnowledgeActivities
                .Where(activity => activity.DocumentId == doomedId)
                .OrderBy(activity => activity.At)
                .ToListAsync(CancellationToken);
            activities.Select(activity => activity.Action)
                .ShouldBe([KnowledgeActivityAction.DocumentUploaded, KnowledgeActivityAction.DocumentDeleted]);
            activities.ShouldAllBe(activity => activity.Detail == null);
            activities[1].ActorAccountId.ShouldBe(org.Admin.Id);

            // The other document is untouched.
            (await dbContext.KnowledgeDocuments.AnyAsync(document => document.Id == keptId, CancellationToken)).ShouldBeTrue();
            (await dbContext.KnowledgeFileContents.CountAsync(CancellationToken)).ShouldBe(1);
            (await dbContext.KnowledgeExtractedUnits.Select(unit => unit.VersionId).Distinct().ToListAsync(CancellationToken)).ShouldBe([keptVersionId]);
            (await dbContext.KnowledgeChunks.Select(chunk => chunk.VersionId).Distinct().ToListAsync(CancellationToken)).ShouldBe([keptVersionId]);
        }

        // The deleted version's late job still runs, finds nothing, and succeeds.
        await Runner.RunUntilIdleAsync(CancellationToken);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var jobs = await dbContext.BackgroundJobs.AsNoTracking().ToListAsync(CancellationToken);
            jobs.Count(job => PayloadVersionId(job) == doomedVersionId).ShouldBe(2);
            jobs.ShouldAllBe(job => job.Status == BackgroundJobStatus.Succeeded && job.LastError == null);
        }

        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", admin.Token));
        detail.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("id").GetGuid()).ShouldBe([keptId]);

        // Gone means gone: deleting again is the same 403 as an id that never existed.
        await AssertIdenticalAsync(
            await admin.Spa.DeleteAsync($"{BasePath}/{knowledgeBaseId}/documents/{doomedId}", admin.Token),
            await admin.Spa.DeleteAsync($"{BasePath}/{knowledgeBaseId}/documents/{Guid.NewGuid()}", admin.Token));
    }

    [Fact]
    public async Task Deleting_the_knowledge_base_cascades_to_its_documents_versions_files_units_and_chunks()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var doomed = await CreateKnowledgeBaseAsync(admin, "要刪除的知識庫");
        var kept = await CreateKnowledgeBaseAsync(admin, "要保留的知識庫");
        (await UploadAsync(admin, doomed, "一.pdf", KnowledgeFixtures.Read(KnowledgeFixtures.ReturnPolicyPdf))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await UploadAsync(admin, doomed, "二.md", TestFiles.Markdown("二"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await UploadAsync(admin, kept, "保留.md", TestFiles.Markdown("保留"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        await Runner.RunUntilIdleAsync(CancellationToken);

        (await admin.Spa.DeleteAsync($"{BasePath}/{doomed}", admin.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.KnowledgeDocuments.AnyAsync(document => document.KnowledgeBaseId == doomed, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeDocumentVersions.AnyAsync(version => version.KnowledgeBaseId == doomed, CancellationToken)).ShouldBeFalse();
            var keptVersion = await dbContext.KnowledgeDocumentVersions.SingleAsync(CancellationToken);
            keptVersion.KnowledgeBaseId.ShouldBe(kept);
            (await dbContext.KnowledgeFileContents.Select(file => file.VersionId).ToListAsync(CancellationToken))
                .ShouldBe([keptVersion.Id]);
            (await dbContext.KnowledgeExtractedUnits.Select(unit => unit.VersionId).Distinct().ToListAsync(CancellationToken))
                .ShouldBe([keptVersion.Id]);
            (await dbContext.KnowledgeChunks.Select(chunk => chunk.KnowledgeBaseId).Distinct().ToListAsync(CancellationToken))
                .ShouldBe([kept]);
            (await dbContext.KnowledgeActivities.Where(activity => activity.KnowledgeBaseId == doomed)
                    .Select(activity => activity.Action).ToListAsync(CancellationToken))
                .ShouldBe([KnowledgeActivityAction.KnowledgeBaseDeleted]);
        }

        await Runner.RunUntilIdleAsync(CancellationToken);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.BackgroundJobs.Select(job => job.Status).ToListAsync(CancellationToken))
                .ShouldAllBe(status => status == BackgroundJobStatus.Succeeded);
        }
    }

    // --- Owner only: cross-organization, non-owner and nonexistent ids are one 403 --------

    [Fact]
    public async Task Other_organizations_non_owners_and_nonexistent_ids_get_identical_403s_on_every_document_endpoint()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var adminA = await SignInAsync(orgA, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(adminA, "A 的知識庫");
        var otherOwnKnowledgeBaseId = await CreateKnowledgeBaseAsync(adminA, "A 的另一個知識庫");
        var documentId = (await BodyJsonAsync(await UploadAsync(adminA, knowledgeBaseId, "機密.pdf", TestFiles.Pdf("機密"))))
            .GetProperty("id").GetGuid();
        var versionId = await VersionIdAsync(orgA, documentId);

        await _host.CreateAccountAsync(orgA.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var orgB = await CreateOrganizationAsync("組織 B");

        var outsiders = new[]
        {
            ("other organization", await SignInAsync(orgB, "admin")),
            ("same-organization admin", await SignInAsync(orgA, "admin2")),
            ("same-organization employee", await SignInAsync(orgA, "internal")),
        };
        foreach (var (who, caller) in outsiders)
        {
            foreach (var (endpoint, send) in DocumentEndpoints(caller))
            {
                var toReal = await send(knowledgeBaseId, documentId, versionId);
                var toNonexistent = await send(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

                toReal.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{who} {endpoint}");
                await AssertIdenticalAsync(toReal, toNonexistent);
                (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("knowledge-base");
            }
        }

        // The owner too, when the ids do not belong together.
        foreach (var (endpoint, send) in DocumentEndpoints(adminA).Where(endpoint => endpoint.Name != "POST documents"))
        {
            var reference = await send(knowledgeBaseId, Guid.NewGuid(), Guid.NewGuid());
            reference.StatusCode.ShouldBe(HttpStatusCode.Forbidden, endpoint);
            await AssertIdenticalAsync(await send(otherOwnKnowledgeBaseId, documentId, versionId), reference);
            if (endpoint != "DELETE document")
            {
                await AssertIdenticalAsync(await send(knowledgeBaseId, documentId, Guid.NewGuid()), reference);
                await AssertIdenticalAsync(await send(knowledgeBaseId, Guid.NewGuid(), versionId), reference);
            }
        }

        // Nothing changed in A.
        await using var dbContext = _host.Postgres.CreateDbContext(orgA.Organization.Id);
        (await dbContext.KnowledgeDocuments.Select(document => document.Id).ToListAsync(CancellationToken)).ShouldBe([documentId]);
        (await ReloadVersionAsync(orgA, versionId)).ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Queued);
        (await dbContext.BackgroundJobs.CountAsync(CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Unauthenticated_callers_get_401_on_every_document_endpoint()
    {
        using var anonymous = _host.CreateSpaClient();
        var path = $"{BasePath}/{Guid.NewGuid()}/documents/{Guid.NewGuid()}";

        (await anonymous.Http.PostAsync($"{BasePath}/{Guid.NewGuid()}/documents", new MultipartFormDataContent(), CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.DeleteAsync(path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.PostAsync($"{path}/versions/{Guid.NewGuid()}/retry", null, CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.GetAsync($"{path}/versions/{Guid.NewGuid()}/file", CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Endpoint limits -----------------------------------------------------------------

    [Fact]
    public void The_upload_endpoint_needs_no_antiforgery_token_and_limits_the_body_to_the_file_limit_plus_multipart_overhead()
    {
        var upload = _host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .Single(endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == "/api/v1/knowledge-bases/{id:guid}/documents");
        var expected = KnowledgeOptions.DefaultMaxFileBytes + KnowledgeOptions.MultipartOverheadBytes;

        upload.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation.ShouldBeFalse();
        upload.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize.ShouldBe(expected);
        upload.Metadata.GetMetadata<IFormOptionsMetadata>()!.MultipartBodyLengthLimit.ShouldBe(expected);
    }

    // --- Helpers -------------------------------------------------------------------------

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal);

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
        return new TestOrganization(organization, admin, internalEmployee);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory
    /// handler, which the fixture disposes.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateKnowledgeBaseAsync(SignedIn owner, string name = "退換貨政策")
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { name, purpose = "" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <param name="browserStyleFileName">Send the name as browsers do — raw UTF-8 in
    /// <c>filename="..."</c> — instead of HttpClient's MIME-encoded <c>filename</c> plus
    /// RFC 5987 <c>filename*</c>.</param>
    private static async Task<HttpResponseMessage> UploadAsync(
        SignedIn caller,
        Guid knowledgeBaseId,
        string fileName,
        byte[] content,
        string? batchId = null,
        bool browserStyleFileName = false)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (browserStyleFileName)
        {
            form.HeaderEncodingSelector = (_, _) => Encoding.UTF8;
            file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{fileName}\"");
            form.Add(file);
        }
        else
        {
            form.Add(file, "file", fileName);
        }

        if (batchId is not null)
        {
            form.Add(new StringContent(batchId), "batchId");
        }

        return await SendAsync(caller, HttpMethod.Post, $"{BasePath}/{knowledgeBaseId}/documents", form);
    }

    private static async Task<HttpResponseMessage> SendAsync(SignedIn caller, HttpMethod method, string path, HttpContent content)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        var response = await caller.Spa.Http.SendAsync(request, CancellationToken);
        await response.Content.LoadIntoBufferAsync(CancellationToken);
        return response;
    }

    /// <summary>Every new endpoint, each with a valid request, so only the ids can make it
    /// fail. Arguments: knowledge base, document, version.</summary>
    private static IEnumerable<(string Name, Func<Guid, Guid, Guid, Task<HttpResponseMessage>> Send)> DocumentEndpoints(SignedIn caller) =>
    [
        ("POST documents", (knowledgeBase, _, _) =>
            UploadAsync(caller, knowledgeBase, "新檔案.md", TestFiles.Markdown(Guid.NewGuid().ToString()))),
        ("POST retry", (knowledgeBase, document, version) =>
            caller.Spa.PostAsync($"{BasePath}/{knowledgeBase}/documents/{document}/versions/{version}/retry", caller.Token, new { })),
        ("GET file", (knowledgeBase, document, version) =>
            caller.Spa.GetAsync($"{BasePath}/{knowledgeBase}/documents/{document}/versions/{version}/file", caller.Token)),
        ("DELETE document", (knowledgeBase, document, _) =>
            caller.Spa.DeleteAsync($"{BasePath}/{knowledgeBase}/documents/{document}", caller.Token)),
    ];

    private async Task<Guid> VersionIdAsync(TestOrganization org, Guid documentId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.KnowledgeDocumentVersions
            .Where(version => version.DocumentId == documentId)
            .Select(version => version.Id)
            .SingleAsync(CancellationToken);
    }

    private async Task<KnowledgeDocumentVersion> ReloadVersionAsync(TestOrganization org, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == versionId, CancellationToken);
    }

    private async Task<BackgroundJob> ReloadJobAsync(TestOrganization org, Guid jobId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(job => job.Id == jobId, CancellationToken);
    }

    private async Task AssertNothingWrittenAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeDocuments.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.KnowledgeDocumentVersions.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.KnowledgeFileContents.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.BackgroundJobs.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.KnowledgeActivities.CountAsync(activity => activity.DocumentId != null, CancellationToken)).ShouldBe(0);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string reason, string field)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, reason);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe(reason);
        body.GetProperty("errors").EnumerateObject().Select(error => error.Name).ShouldBe([field]);
    }

    private static Guid? PayloadVersionId(BackgroundJob job)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        return payload.RootElement.TryGetProperty("versionId", out var versionId) ? versionId.GetGuid() : null;
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

/// <summary>
/// Minimal files of each accepted format, generated here rather than checked in: just
/// enough structure for the upload checks. Only the Markdown one can really be processed
/// (the PDF, DOCX and XLSX fail processing as damaged); tests about what processing reads
/// use the committed <see cref="KnowledgeFixtures"/>. Each takes a marker so different calls
/// have different SHA-256s.
/// </summary>
internal static class TestFiles
{
    public static byte[] Pdf(string marker) =>
        Encoding.UTF8.GetBytes($"%PDF-1.4\n% {marker}\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    /// <summary>A PDF header followed by padding, <paramref name="size"/> bytes in all.</summary>
    public static byte[] PdfOfSize(long size)
    {
        var content = new byte[size];
        "%PDF-1.4\n"u8.CopyTo(content);
        return content;
    }

    public static byte[] Docx(string marker) =>
        Package(
            ("word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{marker}</w:t></w:r></w:p></w:body></w:document>"),
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"));

    public static byte[] Xlsx(string marker) =>
        Package(
            ("xl/workbook.xml", $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets><sheet name=\"{marker}\" sheetId=\"1\"/></sheets></workbook>"),
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"));

    public static byte[] Markdown(string marker) => Encoding.UTF8.GetBytes($"# {marker}\n\n退貨期限為七天。\n");

    /// <summary>The start of a Windows executable (<c>MZ</c> header).</summary>
    public static byte[] Exe() => [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];

    public static byte[] ExeOfSize(long size)
    {
        var content = new byte[size];
        Exe().CopyTo(content, 0);
        return content;
    }

    private static byte[] Package(params (string Name, string Xml)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(xml);
            }
        }

        return buffer.ToArray();
    }
}
