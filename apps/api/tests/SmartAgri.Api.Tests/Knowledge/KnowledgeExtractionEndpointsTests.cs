using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tenancy;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Knowledge.Extraction;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using UglyToad.PdfPig.Writer;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// Processing uploaded files for real, the extraction preview and chunk exclusion, against real
/// PostgreSQL with the committed fixtures (M2 plan, Slice 6; ticket #40). Each test builds its
/// own organizations; the job worker is off and tests run <see cref="JobRunner"/> themselves.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public partial class KnowledgeExtractionEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Knowledge-Extraction-Pass-1!";
    private const string BasePath = "/api/v1/knowledge-bases";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private readonly AuthHostFixture _host;

    public KnowledgeExtractionEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private JobRunner Runner => _host.Factory.Services.GetRequiredService<JobRunner>();

    // --- Acceptance: a Chinese PDF is ready and page 2 reads exactly as the fixture ----------

    [Fact]
    public async Task A_chinese_pdf_is_ready_and_its_page_2_preview_is_the_fixture_text()
    {
        var (org, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "退換貨政策.pdf", KnowledgeFixtures.ReturnPolicyPdf);
        await Runner.RunUntilIdleAsync(CancellationToken);

        var preview = await PreviewAsync(owner, knowledgeBaseId, document);

        preview.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["documentId", "versionId", "versionNumber", "fileName", "status", "issue", "units"]);
        preview.GetProperty("status").GetString().ShouldBe("ready");
        preview.GetProperty("issue").ValueKind.ShouldBe(JsonValueKind.Null);
        preview.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        preview.GetProperty("fileName").GetString().ShouldBe("退換貨政策.pdf");

        var units = preview.GetProperty("units").EnumerateArray().ToList();
        units.Select(unit => (unit.GetProperty("ordinal").GetInt32(), unit.GetProperty("locationKind").GetString(), unit.GetProperty("locationLabel").GetString()))
            .ShouldBe([(0, "page", "第 1 頁"), (1, "page", "第 2 頁"), (2, "page", "第 3 頁")]);
        units[0].EnumerateObject().Select(property => property.Name)
            .ShouldBe(["ordinal", "locationKind", "locationLabel", "readable", "issueCode", "text", "chunks"]);
        units.ShouldAllBe(unit => unit.GetProperty("readable").GetBoolean() && unit.GetProperty("issueCode").ValueKind == JsonValueKind.Null);

        units[1].GetProperty("text").GetString().ShouldBe(KnowledgeFixtures.ReturnPolicyPage2);
        var chunk = units[1].GetProperty("chunks").EnumerateArray().ShouldHaveSingleItem();
        chunk.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "locationLabel", "text", "excluded"]);
        chunk.GetProperty("locationLabel").GetString().ShouldBe("第 2 頁");
        chunk.GetProperty("text").GetString().ShouldBe(KnowledgeFixtures.ReturnPolicyPage2);
        chunk.GetProperty("excluded").GetBoolean().ShouldBeFalse();

        (await DocumentStatusAsync(owner, knowledgeBaseId, document.DocumentId)).ShouldBe("ready");
        await using var dbContext = _host.Postgres.CreateDbContext(org.Id);
        var chunks = await dbContext.KnowledgeChunks.AsNoTracking().Where(row => row.VersionId == document.VersionId).ToListAsync(CancellationToken);
        chunks.Count.ShouldBe(3);
        chunks.ShouldAllBe(row => row.DocumentId == document.DocumentId && row.KnowledgeBaseId == knowledgeBaseId && !row.Excluded);
    }

    // --- Acceptance: an image-only page makes the PDF partially readable, naming the page ----

    [Fact]
    public async Task A_pdf_with_an_image_only_page_is_partially_readable_and_the_issue_names_that_page()
    {
        var (_, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "配送說明.pdf", KnowledgeFixtures.ScannedPagePdf);
        await Runner.RunUntilIdleAsync(CancellationToken);

        var preview = await PreviewAsync(owner, knowledgeBaseId, document);

        preview.GetProperty("status").GetString().ShouldBe("partially-readable");
        var issue = preview.GetProperty("issue").GetString().ShouldNotBeNull();
        issue.ShouldStartWith("第 2 頁找不到可讀文字");
        issue.ShouldContain("OCR");

        var units = preview.GetProperty("units").EnumerateArray().ToList();
        units.Select(unit => (unit.GetProperty("readable").GetBoolean(), unit.GetProperty("issueCode").GetString(), unit.GetProperty("chunks").GetArrayLength()))
            .ShouldBe([(true, null, 1), (false, "too-little-text", 0), (true, null, 1)]);

        (await DocumentStatusAsync(owner, knowledgeBaseId, document.DocumentId)).ShouldBe("partially-readable");
    }

    // --- Acceptance: an encrypted PDF fails once, asking for the password to be removed ------

    [Fact]
    public async Task An_encrypted_pdf_fails_after_exactly_one_attempt_asking_to_remove_the_password()
    {
        var (org, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "加密.pdf", KnowledgeFixtures.EncryptedPdf);

        await Runner.RunUntilIdleAsync(CancellationToken);

        var job = await JobOfAsync(org, document.VersionId);
        job.Status.ShouldBe(BackgroundJobStatus.Failed);
        job.Attempts.ShouldBe(1);
        job.MaxAttempts.ShouldBeGreaterThan(1, "the single attempt is the handler's choice, not the queue's limit");

        var preview = await PreviewAsync(owner, knowledgeBaseId, document);
        preview.GetProperty("status").GetString().ShouldBe("failed");
        preview.GetProperty("issue").GetString().ShouldBe(KnowledgeProcessingIssues.Encrypted);
        preview.GetProperty("issue").GetString()!.ShouldContain("請解除密碼後重新上傳");
        preview.GetProperty("units").GetArrayLength().ShouldBe(0);
    }

    // --- Acceptance: DOCX chunks carry heading paths; every XLSX chunk repeats its header ----

    [Fact]
    public async Task Docx_chunks_are_located_by_their_heading_paths()
    {
        var (_, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "商品指南.docx", KnowledgeFixtures.ProductGuideDocx);
        await Runner.RunUntilIdleAsync(CancellationToken);

        var preview = await PreviewAsync(owner, knowledgeBaseId, document);

        preview.GetProperty("status").GetString().ShouldBe("ready");
        var units = preview.GetProperty("units").EnumerateArray().ToList();
        units.ShouldAllBe(unit => unit.GetProperty("locationKind").GetString() == "section");
        Chunks(preview).Select(chunk => chunk.GetProperty("locationLabel").GetString()).ShouldBe(
        [
            "文件開頭",
            "1 商品介紹",
            "1 商品介紹 › 1.1 有機蔬菜箱",
            "2 退換貨 › 2.1 退貨條件",
            "2 退換貨 › 2.1 退貨條件 › 2.1.1 退貨流程",
            "2 退換貨 › 2.2 運費",
            "3 聯絡我們",
        ]);
        Chunks(preview).Single(chunk => chunk.GetProperty("locationLabel").GetString() == "2 退換貨 › 2.2 運費")
            .GetProperty("text").GetString().ShouldBe("地區 | 運費 | 免運門檻\n本島 | 100 元 | 1,500 元\n離島 | 150 元 | 3,000 元");
    }

    [Fact]
    public async Task Every_xlsx_chunk_repeats_its_header_row_and_is_located_by_sheet_and_rows()
    {
        var (_, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "配送與價格.xlsx", KnowledgeFixtures.DeliveryAndPricesXlsx);
        await Runner.RunUntilIdleAsync(CancellationToken);

        var preview = await PreviewAsync(owner, knowledgeBaseId, document);

        preview.GetProperty("status").GetString().ShouldBe("ready");
        var headers = new Dictionary<string, string>
        {
            ["配送時間"] = "地區 | 最快到貨 | 單位 | 截單時間 | 生效日期",
            ["商品價格"] = "品項 | 重量 | 單位 | 售價 | 單位 | 折扣",
        };
        var units = preview.GetProperty("units").EnumerateArray().ToList();
        units.Select(unit => (unit.GetProperty("locationKind").GetString(), unit.GetProperty("locationLabel").GetString()))
            .ShouldBe([("sheet", "工作表『配送時間』"), ("sheet", "工作表『商品價格』")]);

        var covered = new Dictionary<string, List<int>> { ["配送時間"] = [], ["商品價格"] = [] };
        foreach (var chunk in Chunks(preview))
        {
            var label = SheetRowsLabel().Match(chunk.GetProperty("locationLabel").GetString()!);
            label.Success.ShouldBeTrue(chunk.GetProperty("locationLabel").GetString());
            var sheet = label.Groups["sheet"].Value;
            var first = int.Parse(label.Groups["first"].Value, CultureInfo.InvariantCulture);
            var last = label.Groups["last"].Success ? int.Parse(label.Groups["last"].Value, CultureInfo.InvariantCulture) : first;

            var lines = chunk.GetProperty("text").GetString()!.Split('\n');
            lines[0].ShouldBe(headers[sheet], "every chunk starts with its sheet's header row");
            lines.Length.ShouldBe(last - first + 2);
            covered[sheet].AddRange(Enumerable.Range(first, last - first + 1));
        }

        covered["配送時間"].ShouldBe([.. Enumerable.Range(2, 22)]);
        covered["商品價格"].ShouldBe([.. Enumerable.Range(2, 50)]);
        Chunks(preview).Count(chunk => chunk.GetProperty("locationLabel").GetString()!.StartsWith("工作表『商品價格』", StringComparison.Ordinal))
            .ShouldBeGreaterThan(1);
        Chunks(preview).First().GetProperty("text").GetString()!.Split('\n')[1].ShouldBe("台北市 | 1 | 天 | 15:00 | 2026/9/1");
    }

    // --- Acceptance: Big5 text fails, asking for UTF-8 ------------------------------------

    [Fact]
    public async Task A_big5_text_file_fails_once_asking_for_utf8_and_markdown_with_a_bom_is_ready()
    {
        var (org, owner, knowledgeBaseId) = await SetUpAsync();
        var big5 = await UploadAsync(owner, knowledgeBaseId, "春節公告.txt", KnowledgeFixtures.Big5Text);
        var markdown = await UploadAsync(owner, knowledgeBaseId, "常見問題.md", KnowledgeFixtures.FaqMarkdown);

        await Runner.RunUntilIdleAsync(CancellationToken);

        var failed = await PreviewAsync(owner, knowledgeBaseId, big5);
        failed.GetProperty("status").GetString().ShouldBe("failed");
        failed.GetProperty("issue").GetString()!.ShouldContain("UTF-8");
        failed.GetProperty("issue").GetString().ShouldBe(KnowledgeProcessingIssues.NotUtf8);
        (await JobOfAsync(org, big5.VersionId)).Attempts.ShouldBe(1);

        var ready = await PreviewAsync(owner, knowledgeBaseId, markdown);
        ready.GetProperty("status").GetString().ShouldBe("ready");
        ready.GetProperty("units").EnumerateArray().Select(unit => unit.GetProperty("locationLabel").GetString())
            .ShouldBe(["常見問題", "常見問題 › 訂購與付款 › 可以使用哪些付款方式？", "常見問題 › 訂購與付款 › 可以開立統一編號嗎？", "常見問題 › 配送 › 多久會收到商品？"]);
    }

    // --- Acceptance: excluding a chunk shows in the preview and adds an activity row --------

    [Fact]
    public async Task Excluding_a_chunk_shows_in_the_preview_and_adds_one_content_free_activity_row()
    {
        var (org, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "退換貨政策.pdf", KnowledgeFixtures.ReturnPolicyPdf);
        await Runner.RunUntilIdleAsync(CancellationToken);
        var cover = Chunks(await PreviewAsync(owner, knowledgeBaseId, document))[0];
        var chunkId = cover.GetProperty("id").GetGuid();
        var exclusionPath = $"{VersionPath(knowledgeBaseId, document)}/chunks/{chunkId}/exclusion";

        var excluded = await owner.Spa.PutAsync(exclusionPath, owner.Token, new { excluded = true });

        excluded.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(excluded);
        view.GetProperty("id").GetGuid().ShouldBe(chunkId);
        view.GetProperty("excluded").GetBoolean().ShouldBeTrue();
        view.GetProperty("text").GetString().ShouldBe(cover.GetProperty("text").GetString());

        var preview = await PreviewAsync(owner, knowledgeBaseId, document);
        Chunks(preview).Select(chunk => chunk.GetProperty("excluded").GetBoolean()).ShouldBe([true, false, false]);

        var activity = (await ChunkActivitiesAsync(org)).ShouldHaveSingleItem();
        activity.Action.ShouldBe(KnowledgeActivityAction.ChunkExcluded);
        activity.KnowledgeBaseId.ShouldBe(knowledgeBaseId);
        activity.DocumentId.ShouldBe(document.DocumentId);
        activity.VersionId.ShouldBe(document.VersionId);
        activity.ActorAccountId.ShouldBe(owner.AccountId);
        activity.At.ShouldBe(_host.Clock.GetUtcNow(), TimeSpan.FromSeconds(1));
        using (var detail = JsonDocument.Parse(activity.Detail!))
        {
            detail.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["chunkId"]);
            detail.RootElement.GetProperty("chunkId").GetGuid().ShouldBe(chunkId);
        }

        // Setting what is already set writes nothing; putting it back is its own row.
        (await owner.Spa.PutAsync(exclusionPath, owner.Token, new { excluded = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ChunkActivitiesAsync(org)).Count.ShouldBe(1);
        var included = await owner.Spa.PutAsync(exclusionPath, owner.Token, new { excluded = false });
        (await BodyJsonAsync(included)).GetProperty("excluded").GetBoolean().ShouldBeFalse();
        (await ChunkActivitiesAsync(org)).Select(row => row.Action)
            .ShouldBe([KnowledgeActivityAction.ChunkExcluded, KnowledgeActivityAction.ChunkIncluded]);

        // The field is required.
        var missing = await owner.Spa.PutAsync(exclusionPath, owner.Token, new { });
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(missing)).GetProperty("errors").EnumerateObject().Select(error => error.Name).ShouldBe(["excluded"]);
    }

    // --- Idempotency: however often the job runs, one set of units and chunks --------------

    [Fact]
    public async Task Running_the_job_again_or_twice_at_once_leaves_exactly_one_set_of_units_and_chunks()
    {
        var (org, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "配送與價格.xlsx", KnowledgeFixtures.DeliveryAndPricesXlsx);
        await Runner.RunUntilIdleAsync(CancellationToken);
        var first = await ExtractionAsync(org, document.VersionId);
        first.Units.Count.ShouldBe(2);
        first.Chunks.Count.ShouldBeGreaterThan(3);

        // Delivered again after it finished: nothing changes, not even the chunk ids (so the
        // owner's exclusions survive).
        await EnqueueAgainAsync(org, document.VersionId);
        await Runner.RunUntilIdleAsync(CancellationToken);
        (await ExtractionAsync(org, document.VersionId)).ShouldBeEquivalentTo(first);

        // Two runs of the same job at once on a version that is processing (a lease expired
        // while the first still ran): exactly one result is committed.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Id))
        {
            await dbContext.KnowledgeDocumentVersions
                .Where(version => version.Id == document.VersionId)
                .ExecuteUpdateAsync(set => set.SetProperty(version => version.ProcessingStatus, KnowledgeDocumentStatus.Processing), CancellationToken);
        }

        var job = new JobContext(Guid.NewGuid(), org.Id, ProcessKnowledgeVersionJob.Kind,
            JsonSerializer.Serialize(new ProcessKnowledgeVersionJob(document.VersionId), JsonSerializerOptions.Web), 1, 3);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => HandleAsync(org, job), CancellationToken)));

        var again = await ExtractionAsync(org, document.VersionId);
        again.Units.ShouldBe(first.Units);
        again.Chunks.Select(chunk => chunk with { Id = Guid.Empty }).ShouldBe(first.Chunks.Select(chunk => chunk with { Id = Guid.Empty }));
        (await VersionAsync(org, document.VersionId)).ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Ready);
    }

    [Fact]
    public async Task Retrying_a_version_that_failed_with_units_replaces_them_instead_of_adding_more()
    {
        var (org, owner, knowledgeBaseId) = await SetUpAsync();
        var document = await UploadAsync(owner, knowledgeBaseId, "空白掃描.pdf", BlankPagesPdf(2));
        await Runner.RunUntilIdleAsync(CancellationToken);

        var failed = await PreviewAsync(owner, knowledgeBaseId, document);
        failed.GetProperty("status").GetString().ShouldBe("failed");
        failed.GetProperty("issue").GetString().ShouldBe("找不到可讀文字，可能是掃描檔；目前不支援 OCR");
        failed.GetProperty("units").EnumerateArray().Select(unit => unit.GetProperty("issueCode").GetString())
            .ShouldBe(["too-little-text", "too-little-text"]);
        (await JobOfAsync(org, document.VersionId)).Status.ShouldBe(BackgroundJobStatus.Succeeded);

        var retried = await owner.Spa.PostAsync($"{VersionPath(knowledgeBaseId, document)}/retry", owner.Token, new { });
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PreviewAsync(owner, knowledgeBaseId, document)).GetProperty("status").GetString().ShouldBe("queued");
        await Runner.RunUntilIdleAsync(CancellationToken);

        // Processed again, from scratch: the same two units, not four.
        var reprocessed = await PreviewAsync(owner, knowledgeBaseId, document);
        reprocessed.GetProperty("status").GetString().ShouldBe("failed");
        reprocessed.GetProperty("units").GetArrayLength().ShouldBe(2);
        (await ExtractionAsync(org, document.VersionId)).Units.Count.ShouldBe(2);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Id);
        var jobs = await dbContext.BackgroundJobs.AsNoTracking().ToListAsync(CancellationToken);
        jobs.Count.ShouldBe(2);
        jobs.ShouldAllBe(job => job.Status == BackgroundJobStatus.Succeeded && job.Attempts == 1);
    }

    // --- Owner only: everyone else, and ids that do not belong together, get one 403 --------

    [Fact]
    public async Task Other_organizations_non_owners_and_mismatched_ids_get_identical_403s_on_preview_and_exclusion()
    {
        var (orgA, ownerA, knowledgeBaseId) = await SetUpAsync("組織 A");
        var otherOwnKnowledgeBaseId = await CreateKnowledgeBaseAsync(ownerA, "A 的另一個知識庫");
        var policy = await UploadAsync(ownerA, knowledgeBaseId, "退換貨政策.pdf", KnowledgeFixtures.ReturnPolicyPdf);
        var faq = await UploadAsync(ownerA, knowledgeBaseId, "常見問題.md", KnowledgeFixtures.FaqMarkdown);
        await Runner.RunUntilIdleAsync(CancellationToken);
        var policyChunk = Chunks(await PreviewAsync(ownerA, knowledgeBaseId, policy))[0].GetProperty("id").GetGuid();
        var faqChunk = Chunks(await PreviewAsync(ownerA, knowledgeBaseId, faq))[0].GetProperty("id").GetGuid();

        await _host.CreateAccountAsync(orgA, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var (_, ownerB, _) = await SetUpAsync("組織 B");
        var outsiders = new[]
        {
            ("other organization", ownerB),
            ("same-organization admin", await SignInAsync(orgA, "admin2")),
            ("same-organization employee", await SignInAsync(orgA, "internal")),
        };

        foreach (var (who, caller) in outsiders)
        {
            foreach (var (endpoint, send) in Endpoints(caller))
            {
                var toReal = await send(knowledgeBaseId, policy.DocumentId, policy.VersionId, policyChunk);
                var toNonexistent = await send(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

                toReal.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{who} {endpoint}");
                await AssertIdenticalAsync(toReal, toNonexistent);
                (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("knowledge-base");
            }
        }

        foreach (var (endpoint, send) in Endpoints(ownerA))
        {
            var reference = await send(knowledgeBaseId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            reference.StatusCode.ShouldBe(HttpStatusCode.Forbidden, endpoint);
            await AssertIdenticalAsync(await send(otherOwnKnowledgeBaseId, policy.DocumentId, policy.VersionId, policyChunk), reference);
            await AssertIdenticalAsync(await send(knowledgeBaseId, faq.DocumentId, policy.VersionId, policyChunk), reference);
            await AssertIdenticalAsync(await send(knowledgeBaseId, policy.DocumentId, faq.VersionId, policyChunk), reference);
        }

        // A chunk of another version, through a path that is otherwise the owner's own.
        await AssertIdenticalAsync(
            await ownerA.Spa.PutAsync($"{VersionPath(knowledgeBaseId, policy)}/chunks/{faqChunk}/exclusion", ownerA.Token, new { excluded = true }),
            await ownerA.Spa.PutAsync($"{VersionPath(knowledgeBaseId, policy)}/chunks/{Guid.NewGuid()}/exclusion", ownerA.Token, new { excluded = true }));

        await using var dbContext = _host.Postgres.CreateDbContext(orgA.Id);
        (await dbContext.KnowledgeChunks.AnyAsync(chunk => chunk.Excluded, CancellationToken)).ShouldBeFalse();
        (await ChunkActivitiesAsync(orgA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unauthenticated_callers_get_401_on_preview_and_exclusion()
    {
        using var anonymous = _host.CreateSpaClient();
        var path = $"{BasePath}/{Guid.NewGuid()}/documents/{Guid.NewGuid()}/versions/{Guid.NewGuid()}";

        (await anonymous.Http.GetAsync($"{path}/preview", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsync($"{path}/chunks/{Guid.NewGuid()}/exclusion", null, new { excluded = true }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Helpers -------------------------------------------------------------------------

    private sealed record SignedIn(SpaClient Spa, string Token, Guid AccountId, Guid OrganizationId);

    private sealed record Uploaded(Guid DocumentId, Guid VersionId);

    private sealed record UnitRow(int Ordinal, KnowledgeUnitLocationKind Kind, string Label, string Text, bool Readable, KnowledgeUnitIssue? Issue);

    private sealed record ChunkRow(Guid Id, int UnitOrdinal, int Ordinal, string Label, string Text, bool Excluded);

    private sealed record Extraction(List<UnitRow> Units, List<ChunkRow> Chunks);

    [GeneratedRegex("^工作表『(?<sheet>[^』]+)』第 (?<first>[0-9]+)(?:–(?<last>[0-9]+))? 列$")]
    private static partial Regex SheetRowsLabel();

    private async Task<(Organization Organization, SignedIn Owner, Guid KnowledgeBaseId)> SetUpAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions);
        var owner = await SignInAsync(organization, "admin");
        return (organization, owner, await CreateKnowledgeBaseAsync(owner));
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler,
    /// which the fixture disposes.</remarks>
    private async Task<SignedIn> SignInAsync(Organization organization, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, loginName, Password);
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var accountId = await dbContext.Accounts.Where(account => account.LoginName == loginName).Select(account => account.Id).SingleAsync(CancellationToken);
        return new SignedIn(spa, token.AccessToken, accountId, organization.Id);
    }

    private static async Task<Guid> CreateKnowledgeBaseAsync(SignedIn owner, string name = "退換貨政策")
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { name, purpose = "" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Uploaded> UploadAsync(SignedIn caller, Guid knowledgeBaseId, string fileName, string fixture) =>
        await UploadAsync(caller, knowledgeBaseId, fileName, KnowledgeFixtures.Read(fixture));

    private async Task<Uploaded> UploadAsync(SignedIn caller, Guid knowledgeBaseId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/{knowledgeBaseId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        var response = await caller.Spa.Http.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var documentId = (await BodyJsonAsync(response)).GetProperty("id").GetGuid();

        await using var dbContext = _host.Postgres.CreateDbContext(caller.OrganizationId);
        var versionId = await dbContext.KnowledgeDocumentVersions
            .Where(version => version.DocumentId == documentId)
            .Select(version => version.Id)
            .SingleAsync(CancellationToken);
        return new Uploaded(documentId, versionId);
    }

    private static string VersionPath(Guid knowledgeBaseId, Uploaded document) =>
        $"{BasePath}/{knowledgeBaseId}/documents/{document.DocumentId}/versions/{document.VersionId}";

    private static async Task<JsonElement> PreviewAsync(SignedIn caller, Guid knowledgeBaseId, Uploaded document)
    {
        var response = await caller.Spa.GetAsync($"{VersionPath(knowledgeBaseId, document)}/preview", caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await BodyJsonAsync(response);
    }

    private static List<JsonElement> Chunks(JsonElement preview) =>
        [.. preview.GetProperty("units").EnumerateArray().SelectMany(unit => unit.GetProperty("chunks").EnumerateArray())];

    private static async Task<string?> DocumentStatusAsync(SignedIn caller, Guid knowledgeBaseId, Guid documentId)
    {
        var detail = await BodyJsonAsync(await caller.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", caller.Token));
        return detail.GetProperty("documents").EnumerateArray()
            .Single(document => document.GetProperty("id").GetGuid() == documentId)
            .GetProperty("status").GetString();
    }

    private async Task<BackgroundJob> JobOfAsync(Organization organization, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var jobs = await dbContext.BackgroundJobs.AsNoTracking().ToListAsync(CancellationToken);
        return jobs.Single(job => JsonDocument.Parse(job.Payload).RootElement.GetProperty("versionId").GetGuid() == versionId);
    }

    private async Task<KnowledgeDocumentVersion> VersionAsync(Organization organization, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        return await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == versionId, CancellationToken);
    }

    private async Task<Extraction> ExtractionAsync(Organization organization, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var units = await dbContext.KnowledgeExtractedUnits.AsNoTracking()
            .Where(unit => unit.VersionId == versionId)
            .OrderBy(unit => unit.Ordinal)
            .Select(unit => new UnitRow(unit.Ordinal, unit.LocationKind, unit.LocationLabel, unit.Text, unit.Readable, unit.IssueCode))
            .ToListAsync(CancellationToken);
        var chunks = await dbContext.KnowledgeChunks.AsNoTracking()
            .Where(chunk => chunk.VersionId == versionId)
            .OrderBy(chunk => chunk.UnitOrdinal).ThenBy(chunk => chunk.Ordinal)
            .Select(chunk => new ChunkRow(chunk.Id, chunk.UnitOrdinal, chunk.Ordinal, chunk.LocationLabel, chunk.Text, chunk.Excluded))
            .ToListAsync(CancellationToken);
        return new Extraction(units, chunks);
    }

    private async Task<List<KnowledgeActivity>> ChunkActivitiesAsync(Organization organization)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        return await dbContext.KnowledgeActivities.AsNoTracking()
            .Where(activity => activity.Action == KnowledgeActivityAction.ChunkExcluded || activity.Action == KnowledgeActivityAction.ChunkIncluded)
            .OrderBy(activity => activity.At).ThenBy(activity => activity.Id)
            .ToListAsync(CancellationToken);
    }

    private async Task EnqueueAgainAsync(Organization organization, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        dbContext.BackgroundJobs.Add(BackgroundJob.Create(
            organization.Id, ProcessKnowledgeVersionJob.Kind, new ProcessKnowledgeVersionJob(versionId), _host.Clock.GetUtcNow()));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>Runs the handler as the job runner would: a fresh scope acting for the job's
    /// organization.</summary>
    private async Task HandleAsync(Organization organization, JobContext job)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<JobOrganizationScope>().Enter(organization.Id);
        await scope.ServiceProvider.GetRequiredService<ProcessKnowledgeVersionHandler>().HandleAsync(job, CancellationToken);
    }

    /// <summary>A PDF of blank pages: no text anywhere, like a scan.</summary>
    private static byte[] BlankPagesPdf(int pages)
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = Guid.NewGuid().ToString();
        for (var i = 0; i < pages; i++)
        {
            builder.AddPage(595, 842);
        }

        return builder.Build();
    }

    /// <summary>Both endpoints, each with a valid request. Arguments: knowledge base,
    /// document, version, chunk.</summary>
    private static IEnumerable<(string Name, Func<Guid, Guid, Guid, Guid, Task<HttpResponseMessage>> Send)> Endpoints(SignedIn caller) =>
    [
        ("GET preview", (knowledgeBase, document, version, _) =>
            caller.Spa.GetAsync($"{BasePath}/{knowledgeBase}/documents/{document}/versions/{version}/preview", caller.Token)),
        ("PUT exclusion", (knowledgeBase, document, version, chunk) =>
            caller.Spa.PutAsync($"{BasePath}/{knowledgeBase}/documents/{document}/versions/{version}/chunks/{chunk}/exclusion", caller.Token, new { excluded = true })),
    ];

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
