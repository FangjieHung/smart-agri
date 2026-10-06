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
/// The website channel's settings and publishing endpoints against real PostgreSQL (M5a plan §5
/// Slice 2, issue #194). Test runs are seeded directly through the database (the job worker is off
/// in test hosts), so each acceptance status is set up exactly.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class AssistantWebsiteChannelEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Website-Channel-Pass-1!";
    private const string BasePath = "/api/v1/assistants";

    private readonly AuthHostFixture _host;

    public AssistantWebsiteChannelEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string WebsitePath(Guid id) => $"{BasePath}/{id}/publishing/website";

    // --- Settings ------------------------------------------------------------------------------

    [Fact]
    public async Task Before_any_save_the_channel_shows_defaults_at_revision_0_and_the_embed_code()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");

        var response = await org.Admin.Spa.GetAsync(WebsitePath(assistantId), org.Admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(view, "WebsiteChannelView");
        view.GetProperty("revision").GetInt32().ShouldBe(0);
        view.GetProperty("displayName").GetString().ShouldBe("客服助理");
        view.GetProperty("welcomeMessage").GetString().ShouldBe("您好，我是客服助理，有什麼可以協助？");
        view.GetProperty("brandColor").GetString().ShouldBe("forest");
        view.GetProperty("position").GetString().ShouldBe("bottom-right");
        view.GetProperty("allowedDomains").GetArrayLength().ShouldBe(0);
        view.GetProperty("state").GetString().ShouldBe("draft");
        view.GetProperty("servingState").GetString().ShouldBe("not-published");
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("not-configured");
        view.GetProperty("channel").GetProperty("id").GetString().ShouldBe($"channel-website:{assistantId}");
        view.GetProperty("embedCode").GetString()
            .ShouldBe($"<script src=\"http://localhost:5153/embed.js\" data-assistant=\"{assistantId}\" async></script>");
    }

    [Fact]
    public async Task Saving_settings_normalizes_domains_bumps_the_revision_and_a_stale_revision_is_409()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");

        var first = await SaveAsync(org, assistantId, 0, "  安心客服 ", ["Shop.Example.com", "www.example.com"]);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(CancellationToken));
        var view = await BodyJsonAsync(first);
        view.GetProperty("revision").GetInt32().ShouldBe(1);
        view.GetProperty("displayName").GetString().ShouldBe("安心客服");
        view.GetProperty("allowedDomains").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["shop.example.com", "www.example.com"], ignoreOrder: true);
        view.GetProperty("domains").EnumerateArray().All(item => item.GetProperty("lastSeenAt").ValueKind == JsonValueKind.Null)
            .ShouldBeTrue();
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("testing");

        var second = await SaveAsync(org, assistantId, 1, "安心客服", ["www.example.com"]);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(second)).GetProperty("revision").GetInt32().ShouldBe(2);

        // Another tab still at revision 1 (or a client that thinks nothing was saved yet).
        foreach (var stale in new[] { 1, 0 })
        {
            var conflict = await SaveAsync(org, assistantId, stale, "改名", ["other.example.com"]);
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await BodyJsonAsync(conflict)).GetProperty("reason").GetString().ShouldBe("website-revision-conflict");
        }

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantWebsiteChannels.SingleAsync(channel => channel.AssistantId == assistantId, CancellationToken))
            .DisplayName.ShouldBe("安心客服");
        (await dbContext.AssistantWebsiteDomains.Where(domain => domain.AssistantId == assistantId).Select(domain => domain.Domain)
                .ToListAsync(CancellationToken))
            .ShouldBe(["www.example.com"]);
    }

    [Fact]
    public async Task Invalid_settings_are_422_per_field_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        (await SaveAsync(org, assistantId, 0, "安心客服", ["shop.example.com"])).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await org.Admin.Spa.PutAsync(WebsitePath(assistantId), org.Admin.Token, new
        {
            displayName = " ",
            welcomeMessage = "歡迎",
            brandColor = "red",
            position = "bottom-right",
            allowedDomains = new[] { "new.example.com", "https://shop.example.com" },
            revision = 1,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var errors = (await BodyJsonAsync(response)).GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).ShouldBe(["displayName", "brandColor", "allowedDomains"], ignoreOrder: true);
        errors.GetProperty("allowedDomains")[0].GetString().ShouldBe("只需要填網域，不要包含 https:// 或路徑，例如 shop.example.com。");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var channel = await dbContext.AssistantWebsiteChannels.SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
        channel.Revision.ShouldBe(1);
        channel.DisplayName.ShouldBe("安心客服");
        (await dbContext.AssistantWebsiteDomains.Where(domain => domain.AssistantId == assistantId).Select(domain => domain.Domain)
                .ToListAsync(CancellationToken))
            .ShouldBe(["shop.example.com"]);
    }

    // --- Acceptance: the publishing gate ------------------------------------------------------

    [Fact]
    public async Task Publishing_is_422_acceptance_while_not_accepted_failed_or_outdated_and_succeeds_when_passed()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        (await SaveAsync(org, assistantId, 0, "安心客服", ["shop.example.com"])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var t0 = _host.Clock.GetUtcNow().AddHours(-1);

        // not-accepted: no test case yet.
        await AssertPublishRefusedAsync(org, assistantId, ["acceptance"], "not-accepted");

        // failed: the latest completed run has a failed case.
        await SeedCompletedRunAsync(org, assistantId, t0, failed: 1, withTestCase: true);
        await AssertPublishRefusedAsync(org, assistantId, ["acceptance"], "failed");

        // outdated: a passing run, then an automatic rerun not completed yet.
        await SeedCompletedRunAsync(org, assistantId, t0.AddMinutes(1), failed: 0);
        var queued = await SeedQueuedRunAsync(org, assistantId, t0.AddMinutes(2), AssistantTestRunTrigger.KnowledgeChanged);
        await AssertPublishRefusedAsync(org, assistantId, ["acceptance"], "outdated");

        // passed: the rerun completes with every case passing.
        await CompleteRunAsync(org, queued, failed: 0);
        var response = await PublishAsync(org, assistantId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var view = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(view, "WebsiteChannelView");
        view.GetProperty("state").GetString().ShouldBe("published");
        view.GetProperty("servingState").GetString().ShouldBe("serving");
        view.GetProperty("acceptanceStatus").GetString().ShouldBe("passed");
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("published");
        view.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.String);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var channel = await dbContext.AssistantWebsiteChannels.SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
        channel.State.ShouldBe(WebsiteChannelState.Published);
        channel.PublishedByAccountId.ShouldBe(org.Admin.AccountId);
    }

    [Fact]
    public async Task Publishing_without_an_allowed_domain_is_422_allowed_domains()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        await SeedCompletedRunAsync(org, assistantId, _host.Clock.GetUtcNow().AddHours(-1), failed: 0, withTestCase: true);

        // Never saved at all.
        await AssertPublishRefusedAsync(org, assistantId, ["allowed-domains"], "never saved");

        // Saved with an empty list.
        (await SaveAsync(org, assistantId, 0, "安心客服", [])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await AssertPublishRefusedAsync(org, assistantId, ["allowed-domains"], "no domain");
    }

    [Fact]
    public async Task Publishing_a_paused_assistant_is_422_assistant_paused()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        await SeedCompletedRunAsync(org, assistantId, _host.Clock.GetUtcNow().AddHours(-1), failed: 0, withTestCase: true);
        (await SaveAsync(org, assistantId, 0, "安心客服", ["shop.example.com"])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.Spa.PutAsync($"{BasePath}/{assistantId}/publishing/platform/paused", org.Admin.Token, new { paused = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await AssertPublishRefusedAsync(org, assistantId, ["assistant-paused"], "assistant paused");
    }

    // --- Acceptance: serving follows reruns without publishing again ---------------------------

    [Fact]
    public async Task After_publishing_a_failed_rerun_suspends_it_and_a_passing_rerun_restores_it()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PublishedAssistantAsync(org);
        var t0 = _host.Clock.GetUtcNow().AddMinutes(-30);

        var failing = await SeedQueuedRunAsync(org, assistantId, t0, AssistantTestRunTrigger.AssistantChanged);
        await CompleteRunAsync(org, failing, failed: 1);
        var suspended = await GetWebsiteAsync(org, assistantId);
        suspended.GetProperty("acceptanceStatus").GetString().ShouldBe("failed");
        suspended.GetProperty("servingState").GetString().ShouldBe("suspended-acceptance");
        suspended.GetProperty("channel").GetProperty("status").GetString().ShouldBe("needs-attention");
        suspended.GetProperty("state").GetString().ShouldBe("published");

        var passing = await SeedQueuedRunAsync(org, assistantId, t0.AddMinutes(1), AssistantTestRunTrigger.Manual);
        await CompleteRunAsync(org, passing, failed: 0);
        var restored = await GetWebsiteAsync(org, assistantId);
        restored.GetProperty("servingState").GetString().ShouldBe("serving");
        restored.GetProperty("state").GetString().ShouldBe("published");
    }

    [Fact]
    public async Task Outdated_keeps_serving_after_a_fully_passed_run_and_is_suspended_after_a_failed_one()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PublishedAssistantAsync(org);
        var t0 = _host.Clock.GetUtcNow().AddMinutes(-30);

        // The latest completed run passed; a knowledge change is waiting for its rerun.
        var pending = await SeedQueuedRunAsync(org, assistantId, t0, AssistantTestRunTrigger.KnowledgeChanged);
        var outdatedPassed = await GetWebsiteAsync(org, assistantId);
        outdatedPassed.GetProperty("acceptanceStatus").GetString().ShouldBe("outdated");
        outdatedPassed.GetProperty("servingState").GetString().ShouldBe("serving");

        // That rerun fails a case, and yet another change is waiting again.
        await CompleteRunAsync(org, pending, failed: 1);
        await SeedQueuedRunAsync(org, assistantId, t0.AddMinutes(1), AssistantTestRunTrigger.AssistantChanged);
        var outdatedFailed = await GetWebsiteAsync(org, assistantId);
        outdatedFailed.GetProperty("acceptanceStatus").GetString().ShouldBe("outdated");
        outdatedFailed.GetProperty("servingState").GetString().ShouldBe("suspended-acceptance");
    }

    // --- Acceptance: knowledge ownership (decision B) ------------------------------------------

    [Fact]
    public async Task Someone_elses_knowledge_base_blocks_publishing_and_suspends_a_published_channel_until_disconnected()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        await SeedCompletedRunAsync(org, assistantId, _host.Clock.GetUtcNow().AddHours(-1), failed: 0, withTestCase: true);
        (await SaveAsync(org, assistantId, 0, "安心客服", ["shop.example.com"])).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Connected directly (no rerun asked for), so only ownership blocks publishing.
        var colleagues = await CreateKnowledgeBaseAsync(org, org.Internal.Id, "同仁的公開知識庫", KnowledgeSharingScope.Public);
        await ConnectDirectlyAsync(org, assistantId, colleagues);
        var refused = await AssertPublishRefusedAsync(org, assistantId, ["knowledge-ownership"], "someone else's knowledge base");
        refused.GetProperty("errors").GetProperty("knowledge-ownership")[0].GetString()!.ShouldContain("同仁的公開知識庫");
        var blocked = await GetWebsiteAsync(org, assistantId);
        blocked.GetProperty("nonOwnedKnowledgeBases").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())
            .ShouldBe([colleagues]);

        (await org.Admin.Spa.DeleteAsync($"{BasePath}/{assistantId}/sources/knowledge-base/{colleagues}", org.Admin.Token))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        // The disconnection queued an automatic rerun (outdated); let it pass, then publish.
        await CompleteActiveRunAsync(org, assistantId, failed: 0);
        (await PublishAsync(org, assistantId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Connecting someone else's knowledge base after publishing: acceptance becomes outdated
        // (still serving on its own), but ownership suspends it.
        (await org.Admin.Spa.PutAsync($"{BasePath}/{assistantId}/sources/knowledge-base/{colleagues}", org.Admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var suspended = await GetWebsiteAsync(org, assistantId);
        suspended.GetProperty("acceptanceStatus").GetString().ShouldBe("outdated");
        suspended.GetProperty("servingState").GetString().ShouldBe("suspended-knowledge");
        suspended.GetProperty("channel").GetProperty("status").GetString().ShouldBe("needs-attention");

        (await org.Admin.Spa.DeleteAsync($"{BasePath}/{assistantId}/sources/knowledge-base/{colleagues}", org.Admin.Token))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var restored = await GetWebsiteAsync(org, assistantId);
        restored.GetProperty("acceptanceStatus").GetString().ShouldBe("outdated");
        restored.GetProperty("servingState").GetString().ShouldBe("serving");
        restored.GetProperty("nonOwnedKnowledgeBases").GetArrayLength().ShouldBe(0);
    }

    // --- Pause, resume, unpublish ---------------------------------------------------------------

    [Fact]
    public async Task Pause_resume_and_unpublish_keep_the_settings()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PublishedAssistantAsync(org);

        var paused = await org.Admin.Spa.PutAsync($"{WebsitePath(assistantId)}/paused", org.Admin.Token, new { paused = true });
        paused.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pausedView = await BodyJsonAsync(paused);
        pausedView.GetProperty("state").GetString().ShouldBe("paused");
        pausedView.GetProperty("servingState").GetString().ShouldBe("paused");
        pausedView.GetProperty("channel").GetProperty("status").GetString().ShouldBe("paused");

        var resumed = await org.Admin.Spa.PutAsync($"{WebsitePath(assistantId)}/paused", org.Admin.Token, new { paused = false });
        (await BodyJsonAsync(resumed)).GetProperty("servingState").GetString().ShouldBe("serving");

        var unpublished = await org.Admin.Spa.PostAsync($"{WebsitePath(assistantId)}:unpublish", org.Admin.Token, new { });
        unpublished.StatusCode.ShouldBe(HttpStatusCode.OK);
        var draft = await BodyJsonAsync(unpublished);
        draft.GetProperty("state").GetString().ShouldBe("draft");
        draft.GetProperty("servingState").GetString().ShouldBe("not-published");
        draft.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        draft.GetProperty("displayName").GetString().ShouldBe("安心客服");
        draft.GetProperty("allowedDomains").EnumerateArray().Select(item => item.GetString()).ShouldBe(["shop.example.com"]);

        var pauseDraft = await org.Admin.Spa.PutAsync($"{WebsitePath(assistantId)}/paused", org.Admin.Token, new { paused = true });
        pauseDraft.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(pauseDraft)).GetProperty("reason").GetString().ShouldBe("website-not-published");
    }

    [Fact]
    public async Task Pausing_the_assistant_pauses_its_published_website_channel()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PublishedAssistantAsync(org);

        (await org.Admin.Spa.PutAsync($"{BasePath}/{assistantId}/publishing/platform/paused", org.Admin.Token, new { paused = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var view = await GetWebsiteAsync(org, assistantId);
        view.GetProperty("state").GetString().ShouldBe("published");
        view.GetProperty("servingState").GetString().ShouldBe("paused");
    }

    [Fact]
    public async Task Get_publishing_returns_the_real_website_view()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PublishedAssistantAsync(org);

        var response = await org.Admin.Spa.GetAsync($"{BasePath}/{assistantId}/publishing", org.Admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantPublishingView");
        var website = body.GetProperty("website");
        website.GetProperty("servingState").GetString().ShouldBe("serving");
        website.GetProperty("allowedDomains").EnumerateArray().Select(item => item.GetString()).ShouldBe(["shop.example.com"]);
        body.GetProperty("line").GetProperty("status").GetString().ShouldBe("not-available");
    }

    // --- Acceptance: another organization's assistant is indistinguishable from missing -------

    [Fact]
    public async Task Another_organizations_or_someone_elses_assistant_gets_the_same_403_publishing_as_a_missing_id()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var assistantId = await CreateAssistantAsync(orgA, "A 的助理");
        (await SaveAsync(orgA, assistantId, 0, "A 的客服", ["a.example.com"])).StatusCode.ShouldBe(HttpStatusCode.OK);

        var orgB = await CreateOrganizationAsync("組織 B");
        await _host.CreateAccountAsync(
            orgA.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var otherAdminA = await SignInAsync(orgA.Organization, "admin2");

        foreach (var caller in new[] { orgB.Admin, otherAdminA })
        {
            foreach (var (verb, send) in EndpointsById(caller))
            {
                var toAssistant = await send(assistantId);
                var toNonexistent = await send(Guid.NewGuid());

                toAssistant.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
                await AssertIdenticalAsync(toAssistant, toNonexistent);
                (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("publishing", verb);
            }
        }

        // Nothing changed in A.
        var view = await GetWebsiteAsync(orgA, assistantId);
        view.GetProperty("displayName").GetString().ShouldBe("A 的客服");
        view.GetProperty("revision").GetInt32().ShouldBe(1);
        view.GetProperty("state").GetString().ShouldBe("draft");
    }

    [Fact]
    public async Task Without_manage_publishing_every_website_endpoint_is_403_publishing()
    {
        var org = await CreateOrganizationAsync();
        var limited = await _host.CreateAccountAsync(
            org.Organization, "limited", Password, AccountRole.SmbAdmin, "沒有發布權限", AccountPermission.ManageAssistants);
        var ownAssistantId = await CreateAssistantAsync(org, "沒有發布權限的助理", limited.Id);

        var caller = await SignInAsync(org.Organization, "limited");
        foreach (var (verb, send) in EndpointsById(caller))
        {
            var response = await send(ownAssistantId);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("publishing", verb);
        }
    }

    // --- Helpers --------------------------------------------------------------------------------

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private sealed record SignedIn(SpaClient Spa, string Token, Guid AccountId);

    private sealed record TestOrganization(Organization Organization, SignedIn Admin, Guid AdminId, Account Internal);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, await SignInAsync(organization, "admin"), admin.Id, internalEmployee);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(Organization organization, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, loginName, Password);
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var accountId = await dbContext.Accounts
            .Where(account => account.LoginName == loginName)
            .Select(account => account.Id)
            .SingleAsync(CancellationToken);
        return new SignedIn(spa, token.AccessToken, accountId);
    }

    /// <summary>An assistant owned by the organization's admin (or <paramref name="ownerId"/>), with
    /// one knowledge base its owner owns.</summary>
    private async Task<Guid> CreateAssistantAsync(TestOrganization org, string name, Guid? ownerId = null)
    {
        var owner = ownerId ?? org.AdminId;
        var now = _host.Clock.GetUtcNow().AddHours(-2);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, owner, name, "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: false, now);
        var knowledgeBase = KnowledgeBase.Create(org.Organization.Id, owner, $"{name}的知識庫", string.Empty, now);
        dbContext.Assistants.Add(assistant);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    /// <summary>An assistant whose acceptance passed, with one allowed domain, published.</summary>
    private async Task<Guid> PublishedAssistantAsync(TestOrganization org)
    {
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        await SeedCompletedRunAsync(org, assistantId, _host.Clock.GetUtcNow().AddHours(-1), failed: 0, withTestCase: true);
        (await SaveAsync(org, assistantId, 0, "安心客服", ["shop.example.com"])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await PublishAsync(org, assistantId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return assistantId;
    }

    private async Task<Guid> CreateKnowledgeBaseAsync(TestOrganization org, Guid ownerAccountId, string name, KnowledgeSharingScope scope)
    {
        var now = _host.Clock.GetUtcNow().AddHours(-2);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = KnowledgeBase.Create(org.Organization.Id, ownerAccountId, name, string.Empty, now);
        knowledgeBase.ChangeSharing(scope, allowOriginalDownload: false, now);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        await dbContext.SaveChangesAsync(CancellationToken);
        return knowledgeBase.Id;
    }

    private async Task ConnectDirectlyAsync(TestOrganization org, Guid assistantId, Guid knowledgeBaseId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(row => row.Id == assistantId, CancellationToken);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(row => row.Id == knowledgeBaseId, CancellationToken);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, _host.Clock.GetUtcNow()));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>A completed run queued at <paramref name="queuedAt"/> (and, the first time, the
    /// test case that makes the assistant "tested").</summary>
    private async Task SeedCompletedRunAsync(TestOrganization org, Guid assistantId, DateTimeOffset queuedAt, int failed, bool withTestCase = false)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        if (withTestCase)
        {
            dbContext.AssistantTestCases.Add(new AssistantTestCase(
                org.Organization.Id, assistantId, "退貨期限是幾天？", AssistantTestCaseCategory.Common,
                AssistantTestExpectedKind.CompanyData, [Guid.NewGuid()], null, ordinal: 1, queuedAt));
        }

        var run = AssistantTestRun.Queue(org.Organization.Id, assistantId, AssistantTestRunTrigger.Manual, queuedAt);
        run.Start("test", AuthHostFixture.ChatModel, 0.3, queuedAt);
        run.Complete(failed == 0 ? 1 : 0, failed, queuedAt);
        dbContext.AssistantTestRuns.Add(run);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task<Guid> SeedQueuedRunAsync(TestOrganization org, Guid assistantId, DateTimeOffset queuedAt, AssistantTestRunTrigger trigger)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var run = AssistantTestRun.Queue(org.Organization.Id, assistantId, trigger, queuedAt);
        dbContext.AssistantTestRuns.Add(run);
        await dbContext.SaveChangesAsync(CancellationToken);
        return run.Id;
    }

    private async Task CompleteRunAsync(TestOrganization org, Guid runId, int failed)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var run = await dbContext.AssistantTestRuns.SingleAsync(row => row.Id == runId, CancellationToken);
        var now = _host.Clock.GetUtcNow();
        run.Start("test", AuthHostFixture.ChatModel, 0.3, now);
        run.Complete(failed == 0 ? 1 : 0, failed, now);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task CompleteActiveRunAsync(TestOrganization org, Guid assistantId, int failed)
    {
        Guid runId;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            runId = await dbContext.AssistantTestRuns
                .Where(run => run.AssistantId == assistantId && run.Status == AssistantTestRunStatus.Queued)
                .Select(run => run.Id)
                .SingleAsync(CancellationToken);
        }

        await CompleteRunAsync(org, runId, failed);
    }

    private Task<HttpResponseMessage> SaveAsync(
        TestOrganization org, Guid assistantId, int revision, string displayName, string[] allowedDomains) =>
        org.Admin.Spa.PutAsync(WebsitePath(assistantId), org.Admin.Token, new
        {
            displayName,
            welcomeMessage = "您好，有什麼可以協助？",
            brandColor = "ocean",
            position = "bottom-right",
            allowedDomains,
            revision,
        });

    private Task<HttpResponseMessage> PublishAsync(TestOrganization org, Guid assistantId) =>
        org.Admin.Spa.PostAsync($"{WebsitePath(assistantId)}:publish", org.Admin.Token, new { });

    private async Task<JsonElement> GetWebsiteAsync(TestOrganization org, Guid assistantId)
    {
        var response = await org.Admin.Spa.GetAsync(WebsitePath(assistantId), org.Admin.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    /// <summary>Publishing is refused with exactly <paramref name="errorKeys"/>, and the stored
    /// channel (state, publication, revision) is unchanged.</summary>
    private async Task<JsonElement> AssertPublishRefusedAsync(TestOrganization org, Guid assistantId, string[] errorKeys, string because)
    {
        var before = await SnapshotAsync(org, assistantId);

        var response = await PublishAsync(org, assistantId);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, because);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("website-publish-refused", because);
        body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace(because);
        body.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(errorKeys, because);
        (await SnapshotAsync(org, assistantId)).ShouldBe(before, because);
        return body;
    }

    private async Task<string> SnapshotAsync(TestOrganization org, Guid assistantId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var channel = await dbContext.AssistantWebsiteChannels.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AssistantId == assistantId, CancellationToken);
        return channel is null
            ? "none"
            : $"{channel.State}|{channel.PublishedAt:O}|{channel.PublishedByAccountId}|{channel.Revision}|{channel.UpdatedAt:O}";
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Every website endpoint addressed by an assistant id, each with a valid body.</summary>
    private static IEnumerable<(string Verb, Func<Guid, Task<HttpResponseMessage>> Send)> EndpointsById(SignedIn caller) =>
    [
        ("GET website", id => caller.Spa.GetAsync(WebsitePath(id), caller.Token)),
        ("PUT website", id => caller.Spa.PutAsync(WebsitePath(id), caller.Token, new
        {
            displayName = "改名",
            welcomeMessage = "歡迎",
            brandColor = "forest",
            position = "bottom-right",
            allowedDomains = new[] { "evil.example.com" },
            revision = 1,
        })),
        ("POST publish", id => caller.Spa.PostAsync($"{WebsitePath(id)}:publish", caller.Token, new { })),
        ("PUT paused", id => caller.Spa.PutAsync($"{WebsitePath(id)}/paused", caller.Token, new { paused = true })),
        ("POST unpublish", id => caller.Spa.PostAsync($"{WebsitePath(id)}:unpublish", caller.Token, new { })),
        ("GET publishing", id => caller.Spa.GetAsync($"{BasePath}/{id}/publishing", caller.Token)),
    ];

    /// <summary>Status, content type, body bytes and cookies all equal.</summary>
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
