using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// <c>/api/v1/knowledge-bases</c> against real PostgreSQL (M2 plan, Slice 3 acceptance;
/// ticket #37). Each test builds its own organizations with the seed's three roles
/// (<c>demo-seed.ts</c>'s initial permissions), so tests never see each other's data even
/// though they share one database.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class KnowledgeBaseEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Knowledge-Endpoint-Pass-1!";
    private const string BasePath = "/api/v1/knowledge-bases";

    private readonly AuthHostFixture _host;

    public KnowledgeBaseEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: customer (no manage-data-sources) gets 403 on create ---------------

    [Fact]
    public async Task Customer_without_manage_data_sources_gets_403_knowledge_base_on_create_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var customer = await SignInAsync(org, "customer");

        var response = await customer.Spa.PostAsync(BasePath, customer.Token, new { name = "客戶的知識庫", purpose = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("knowledge-base");
        body.GetProperty("message").GetString().ShouldBe("只有可管理資料來源的帳號可以建立知識庫。");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeBases.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.KnowledgeActivities.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Admin_creates_a_private_knowledge_base_they_own_and_it_appears_in_their_list()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.PostAsync(BasePath, admin.Token, new { name = "  退換貨政策 ", purpose = " 退貨期限與流程 " });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await BodyJsonAsync(response);
        var id = created.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"{BasePath}/{id}");
        created.GetProperty("name").GetString().ShouldBe("退換貨政策");
        created.GetProperty("purpose").GetString().ShouldBe("退貨期限與流程");
        created.GetProperty("sharingScope").GetString().ShouldBe("private");
        created.GetProperty("viewerCanManage").GetBoolean().ShouldBeTrue();
        created.GetProperty("documentCount").GetInt32().ShouldBe(0);
        created.GetProperty("faqCount").GetInt32().ShouldBe(0);
        created.GetProperty("statusCounts").EnumerateObject().Select(count => (count.Name, count.Value.GetInt32()))
            .ShouldBe([("queued", 0), ("processing", 0), ("ready", 0), ("partially-readable", 0), ("failed", 0)]);

        var list = await BodyJsonAsync(await admin.Spa.GetAsync(BasePath, admin.Token));
        list.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([id]);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeBases.SingleAsync(CancellationToken)).OwnerAccountId.ShouldBe(org.Admin.Id);
        var activity = await dbContext.KnowledgeActivities.SingleAsync(CancellationToken);
        activity.Action.ShouldBe(KnowledgeActivityAction.KnowledgeBaseCreated);
        activity.KnowledgeBaseId.ShouldBe(id);
        activity.ActorAccountId.ShouldBe(org.Admin.Id);
    }

    [Fact]
    public async Task Invalid_create_is_422_naming_every_bad_field_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.PostAsync(
            BasePath, admin.Token, new { name = "   ", purpose = new string('用', KnowledgeBase.PurposeMaxLength + 1) });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("message").GetString().ShouldBe("請輸入知識庫名稱。");
        body.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["name", "purpose"]);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeBases.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- Acceptance: another organization's id is indistinguishable from a missing one --

    [Fact]
    public async Task Another_organizations_knowledge_base_and_a_nonexistent_id_get_identical_403s_on_every_endpoint()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var adminA = await SignInAsync(orgA, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(adminA, "A 的知識庫");

        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        foreach (var (verb, send) in EndpointsById(adminB))
        {
            var toOtherOrganization = await send(knowledgeBaseId);
            var toNonexistent = await send(Guid.NewGuid());

            toOtherOrganization.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            await AssertIdenticalAsync(toOtherOrganization, toNonexistent);
            (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("knowledge-base", verb);
        }

        // B's attempts changed nothing in A.
        var detail = await BodyJsonAsync(await adminA.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", adminA.Token));
        detail.GetProperty("summary").GetProperty("name").GetString().ShouldBe("A 的知識庫");
        detail.GetProperty("sharing").GetProperty("scope").GetString().ShouldBe("private");
    }

    // --- Owner only: a same-organization non-owner gets the same 403 -------------------

    [Fact]
    public async Task A_non_owner_in_the_same_organization_gets_the_same_403_for_get_patch_delete_and_sharing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin, "管理者的知識庫");

        // A second administrator with every permission: ownership, not permissions, is what
        // is missing. Also a plain employee, to whom the knowledge base is even shared.
        await _host.CreateAccountAsync(
            org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        (await admin.Spa.PutAsync($"{BasePath}/{knowledgeBaseId}/sharing", admin.Token, new
        {
            scope = "specific-accounts",
            sharedWithAccountIds = new[] { org.Internal.Id.ToString() },
            allowOriginalDownload = false,
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var loginName in new[] { "admin2", "internal" })
        {
            var nonOwner = await SignInAsync(org, loginName);
            foreach (var (verb, send) in EndpointsById(nonOwner))
            {
                var toSomeoneElses = await send(knowledgeBaseId);
                var toNonexistent = await send(Guid.NewGuid());

                toSomeoneElses.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{loginName} {verb}");
                await AssertIdenticalAsync(toSomeoneElses, toNonexistent);
            }
        }

        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", admin.Token));
        detail.GetProperty("summary").GetProperty("name").GetString().ShouldBe("管理者的知識庫");
        detail.GetProperty("sharing").GetProperty("sharedWithAccountIds").EnumerateArray()
            .Select(id => id.GetGuid()).ShouldBe([org.Internal.Id]);
    }

    // --- Visibility rule ported from listKnowledgeBaseSummaries ------------------------

    [Fact]
    public async Task The_list_shows_only_the_callers_own_knowledge_bases_even_those_shared_with_others()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var sharedWithInternal = await CreateKnowledgeBaseAsync(admin, "指定分享");
        var publicOne = await CreateKnowledgeBaseAsync(admin, "公開分享");
        await ShareAsync(admin, sharedWithInternal, "specific-accounts", [org.Internal.Id.ToString()]);
        await ShareAsync(admin, publicOne, "public", []);

        await _host.CreateAccountAsync(
            org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var admin2 = await SignInAsync(org, "admin2");
        var admin2s = await CreateKnowledgeBaseAsync(admin2, "第二位管理者的");

        var otherOrg = await CreateOrganizationAsync("其他組織");
        var otherAdmin = await SignInAsync(otherOrg, "admin");
        await CreateKnowledgeBaseAsync(otherAdmin, "其他組織的");

        (await ListIdsAsync(admin)).ShouldBe([sharedWithInternal, publicOne]);
        (await ListIdsAsync(admin2)).ShouldBe([admin2s]);
        (await ListIdsAsync(await SignInAsync(org, "internal"))).ShouldBeEmpty();
        (await ListIdsAsync(await SignInAsync(org, "customer"))).ShouldBeEmpty();

        var adminList = await BodyJsonAsync(await admin.Spa.GetAsync(BasePath, admin.Token));
        adminList.EnumerateArray().ShouldAllBe(item => item.GetProperty("viewerCanManage").GetBoolean());
    }

    // --- Acceptance: sharing validation -----------------------------------------------

    [Fact]
    public async Task Specific_accounts_naming_only_another_organizations_accounts_is_422_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin, "知識庫");
        var otherOrg = await CreateOrganizationAsync("其他組織");

        var response = await admin.Spa.PutAsync($"{BasePath}/{knowledgeBaseId}/sharing", admin.Token, new
        {
            scope = "specific-accounts",
            sharedWithAccountIds = new[] { otherOrg.Admin.Id.ToString(), otherOrg.Internal.Id.ToString() },
            allowOriginalDownload = false,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("message").GetString().ShouldBe("請至少選擇一個帳號或團隊。");
        body.GetProperty("errors").GetProperty("sharedWithAccountIds").EnumerateArray()
            .Select(error => error.GetString()).ShouldBe(["請至少選擇一個帳號或團隊。"]);

        var unknownScope = await admin.Spa.PutAsync($"{BasePath}/{knowledgeBaseId}/sharing", admin.Token, new
        {
            scope = "everyone",
            sharedWithAccountIds = Array.Empty<string>(),
            allowOriginalDownload = false,
        });
        unknownScope.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(unknownScope)).GetProperty("errors").TryGetProperty("scope", out _).ShouldBeTrue();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(CancellationToken);
        knowledgeBase.SharingScope.ShouldBe(KnowledgeSharingScope.Private);
        (await dbContext.KnowledgeBaseShares.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.KnowledgeActivities.Select(activity => activity.Action).ToListAsync(CancellationToken))
            .ShouldBe([KnowledgeActivityAction.KnowledgeBaseCreated]);
    }

    [Fact]
    public async Task Mixed_share_targets_keep_only_this_organizations_other_accounts()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin, "知識庫");
        var otherOrg = await CreateOrganizationAsync("其他組織");

        var response = await admin.Spa.PutAsync($"{BasePath}/{knowledgeBaseId}/sharing", admin.Token, new
        {
            scope = "specific-accounts",
            sharedWithAccountIds = new[]
            {
                org.Customer.Id.ToString(),
                otherOrg.Internal.Id.ToString(), // another organization's account
                org.Admin.Id.ToString(),         // the owner
                Guid.NewGuid().ToString(),       // does not exist
                "account-internal-employee",     // a demo id, not a GUID
                org.Internal.Id.ToString(),
                org.Customer.Id.ToString(),      // duplicate
            },
            allowOriginalDownload = true,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = new[] { org.Internal.Id, org.Customer.Id }.OrderBy(id => id.ToString("N")).ToList();
        var saved = await BodyJsonAsync(response);
        saved.GetProperty("scope").GetString().ShouldBe("specific-accounts");
        saved.GetProperty("sharedWithAccountIds").EnumerateArray().Select(id => id.GetGuid()).ShouldBe(expected);
        saved.GetProperty("allowOriginalDownload").GetBoolean().ShouldBeFalse();

        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", admin.Token));
        detail.GetProperty("sharing").GetRawText().ShouldBe(saved.GetRawText());
        detail.GetProperty("summary").GetProperty("sharingScope").GetString().ShouldBe("specific-accounts");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.KnowledgeBaseShares.Select(share => share.AccountId).ToListAsync(CancellationToken))
            .ShouldBe(expected, ignoreOrder: true);
        var activity = await dbContext.KnowledgeActivities
            .SingleAsync(candidate => candidate.Action == KnowledgeActivityAction.SharingChanged, CancellationToken);
        activity.ActorAccountId.ShouldBe(org.Admin.Id);
        activity.Detail.ShouldNotBeNull();
        using var detailJson = JsonDocument.Parse(activity.Detail);
        detailJson.RootElement.GetProperty("sharedWithAccountIds").EnumerateArray()
            .Select(id => id.GetGuid()).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public async Task Allow_original_download_is_kept_only_for_public_sharing_and_unchanged_sharing_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin, "知識庫");

        var asPublic = await ShareAsync(admin, knowledgeBaseId, "public", [org.Internal.Id.ToString()], allowOriginalDownload: true);
        asPublic.GetProperty("allowOriginalDownload").GetBoolean().ShouldBeTrue();
        asPublic.GetProperty("sharedWithAccountIds").EnumerateArray().ShouldBeEmpty();

        // Sending the same thing again changes nothing and records nothing.
        await ShareAsync(admin, knowledgeBaseId, "public", [], allowOriginalDownload: true);

        var asPrivate = await ShareAsync(admin, knowledgeBaseId, "private", [org.Internal.Id.ToString()], allowOriginalDownload: true);
        asPrivate.GetProperty("allowOriginalDownload").GetBoolean().ShouldBeFalse();
        asPrivate.GetProperty("sharedWithAccountIds").EnumerateArray().ShouldBeEmpty();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(CancellationToken);
        knowledgeBase.SharingScope.ShouldBe(KnowledgeSharingScope.Private);
        knowledgeBase.AllowOriginalDownload.ShouldBeFalse();
        (await dbContext.KnowledgeActivities.CountAsync(
            activity => activity.Action == KnowledgeActivityAction.SharingChanged, CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Detail_share_targets_are_the_organizations_other_accounts_with_id_and_display_name_only()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin, "知識庫");
        await CreateOrganizationAsync("其他組織");

        var response = await admin.Spa.GetAsync($"{BasePath}/{knowledgeBaseId}", admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await BodyJsonAsync(response);
        detail.EnumerateObject().Select(property => property.Name).ShouldBe(["summary", "documents", "sharing", "shareTargets"]);
        detail.GetProperty("documents").EnumerateArray().ShouldBeEmpty();
        detail.GetProperty("summary").GetProperty("viewerCanManage").GetBoolean().ShouldBeTrue();

        var targets = detail.GetProperty("shareTargets").EnumerateArray().ToList();
        targets.Select(target => target.GetProperty("id").GetGuid())
            .ShouldBe(new[] { org.Internal.Id, org.Customer.Id }.OrderBy(id => id.ToString("N")));
        targets.Select(target => target.GetProperty("displayName").GetString()).ShouldBe(
            [org.Internal.DisplayName, org.Customer.DisplayName],
            ignoreOrder: true);
        targets.ShouldAllBe(target => target.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "id", "displayName" }));
    }

    // --- Rename ------------------------------------------------------------------------

    [Fact]
    public async Task Patch_changes_only_the_fields_sent_records_which_changed_and_rejects_a_blank_name()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var knowledgeBaseId = await CreateKnowledgeBaseAsync(admin, "舊名稱", "原本的用途");

        var renamed = await admin.Spa.PatchAsync($"{BasePath}/{knowledgeBaseId}", admin.Token, new { name = " 新名稱 " });
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var summary = await BodyJsonAsync(renamed);
        summary.GetProperty("name").GetString().ShouldBe("新名稱");
        summary.GetProperty("purpose").GetString().ShouldBe("原本的用途");

        var blank = await admin.Spa.PatchAsync($"{BasePath}/{knowledgeBaseId}", admin.Token, new { name = "", purpose = "新用途" });
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(blank)).GetProperty("errors").GetProperty("name").EnumerateArray()
            .Select(error => error.GetString()).ShouldBe(["請輸入知識庫名稱。"]);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(CancellationToken);
        knowledgeBase.Name.ShouldBe("新名稱");
        knowledgeBase.Purpose.ShouldBe("原本的用途");
        var updated = await dbContext.KnowledgeActivities
            .SingleAsync(activity => activity.Action == KnowledgeActivityAction.KnowledgeBaseUpdated, CancellationToken);
        updated.Detail.ShouldNotBeNull();
        updated.Detail.ShouldNotContain("新名稱");
        using var detailJson = JsonDocument.Parse(updated.Detail);
        detailJson.RootElement.GetProperty("changed").EnumerateArray().Select(field => field.GetString()).ShouldBe(["name"]);
    }

    // --- Acceptance: delete removes everything under the knowledge base ---------------

    [Fact]
    public async Task Deleting_removes_the_knowledge_base_its_shares_and_its_activity_leaving_only_the_deletion_record()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var doomed = await CreateKnowledgeBaseAsync(admin, "要刪除的");
        (await admin.Spa.PatchAsync($"{BasePath}/{doomed}", admin.Token, new { purpose = "改過用途" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShareAsync(admin, doomed, "specific-accounts", [org.Internal.Id.ToString(), org.Customer.Id.ToString()]);

        var kept = await CreateKnowledgeBaseAsync(admin, "要保留的");
        await ShareAsync(admin, kept, "specific-accounts", [org.Internal.Id.ToString()]);

        var response = await admin.Spa.DeleteAsync($"{BasePath}/{doomed}", admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.KnowledgeBases.AnyAsync(knowledgeBase => knowledgeBase.Id == doomed, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeBaseShares.AnyAsync(share => share.KnowledgeBaseId == doomed, CancellationToken)).ShouldBeFalse();

            var remaining = await dbContext.KnowledgeActivities
                .Where(activity => activity.KnowledgeBaseId == doomed)
                .ToListAsync(CancellationToken);
            var deletion = remaining.ShouldHaveSingleItem();
            deletion.Action.ShouldBe(KnowledgeActivityAction.KnowledgeBaseDeleted);
            deletion.ActorAccountId.ShouldBe(org.Admin.Id);
            deletion.Detail.ShouldBeNull();

            // The other knowledge base is untouched.
            (await dbContext.KnowledgeBases.AnyAsync(knowledgeBase => knowledgeBase.Id == kept, CancellationToken)).ShouldBeTrue();
            (await dbContext.KnowledgeBaseShares.CountAsync(share => share.KnowledgeBaseId == kept, CancellationToken)).ShouldBe(1);
            (await dbContext.KnowledgeActivities.CountAsync(activity => activity.KnowledgeBaseId == kept, CancellationToken)).ShouldBe(2);
        }

        // Gone means gone: the same 403 as an id that never existed.
        await AssertIdenticalAsync(
            await admin.Spa.GetAsync($"{BasePath}/{doomed}", admin.Token),
            await admin.Spa.GetAsync($"{BasePath}/{Guid.NewGuid()}", admin.Token));
        (await ListIdsAsync(admin)).ShouldBe([kept]);
    }

    // --- Cross-cutting gates ------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_callers_get_401_and_the_password_change_gate_still_applies()
    {
        using var anonymous = _host.CreateSpaClient();
        (await anonymous.Http.GetAsync(BasePath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var org = await CreateOrganizationAsync();
        await _host.RequirePasswordChangeAsync(org.Admin);
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.PostAsync(BasePath, admin.Token, new { name = "知識庫" });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("password-change-required");
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

    /// <summary>An organization with the seed's three accounts and initial permissions.</summary>
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

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory
    /// handler, which the fixture disposes.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateKnowledgeBaseAsync(SignedIn owner, string name, string purpose = "")
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { name, purpose });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ShareAsync(
        SignedIn owner,
        Guid knowledgeBaseId,
        string scope,
        string[] sharedWithAccountIds,
        bool allowOriginalDownload = false)
    {
        var response = await owner.Spa.PutAsync(
            $"{BasePath}/{knowledgeBaseId}/sharing",
            owner.Token,
            new { scope, sharedWithAccountIds, allowOriginalDownload });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    private static async Task<List<Guid>> ListIdsAsync(SignedIn viewer)
    {
        var response = await viewer.Spa.GetAsync(BasePath, viewer.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. (await BodyJsonAsync(response)).EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];
    }

    /// <summary>Every endpoint addressed by a knowledge base id, each with a valid body, so
    /// the only thing that can make it fail is the id.</summary>
    private static IEnumerable<(string Verb, Func<Guid, Task<HttpResponseMessage>> Send)> EndpointsById(SignedIn caller) =>
    [
        ("GET", id => caller.Spa.GetAsync($"{BasePath}/{id}", caller.Token)),
        ("PATCH", id => caller.Spa.PatchAsync($"{BasePath}/{id}", caller.Token, new { name = "改名" })),
        ("DELETE", id => caller.Spa.DeleteAsync($"{BasePath}/{id}", caller.Token)),
        ("PUT sharing", id => caller.Spa.PutAsync($"{BasePath}/{id}/sharing", caller.Token, new
        {
            scope = "public",
            sharedWithAccountIds = Array.Empty<string>(),
            allowOriginalDownload = true,
        })),
    ];

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Status, content type, body bytes and cookies all equal (as in
    /// <c>TeamEndpointsTests</c>).</summary>
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
