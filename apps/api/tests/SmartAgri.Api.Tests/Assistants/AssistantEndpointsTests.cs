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
/// <c>/api/v1/assistants</c> against real PostgreSQL (M3 plan, Slice 1 acceptance; ticket
/// #71). There is no create endpoint yet (#72), so tests seed <see cref="Assistant"/> rows
/// directly through the database, exactly as the plan's scope note for this ticket says to.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class AssistantEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Assistant-Endpoint-Pass-1!";
    private const string BasePath = "/api/v1/assistants";

    private readonly AuthHostFixture _host;

    public AssistantEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: another organization's id is indistinguishable from a missing one --

    [Fact]
    public async Task Another_organizations_assistant_and_a_nonexistent_id_get_identical_403s_on_every_endpoint()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var adminA = await SignInAsync(orgA, "admin");
        var assistantId = await CreateAssistantAsync(orgA, orgA.Admin.Id, "A 的助理");

        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        foreach (var (verb, send) in EndpointsById(adminB))
        {
            var toOtherOrganization = await send(assistantId);
            var toNonexistent = await send(Guid.NewGuid());

            toOtherOrganization.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            await AssertIdenticalAsync(toOtherOrganization, toNonexistent);
            (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("assistant-configuration", verb);
        }

        // B's attempts changed nothing in A.
        var settings = await BodyJsonAsync(await adminA.Spa.GetAsync($"{BasePath}/{assistantId}/settings", adminA.Token));
        settings.GetProperty("configuration").GetProperty("name").GetString().ShouldBe("A 的助理");
    }

    // --- Owner only: a same-organization non-owner gets the same 403 -------------------

    [Fact]
    public async Task A_non_owner_in_the_same_organization_gets_the_same_403_for_settings_sources_and_delete()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "管理者的助理");

        await _host.CreateAccountAsync(
            org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var admin2 = await SignInAsync(org, "admin2");

        foreach (var (verb, send) in EndpointsById(admin2))
        {
            var toSomeoneElses = await send(assistantId);
            var toNonexistent = await send(Guid.NewGuid());

            toSomeoneElses.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            await AssertIdenticalAsync(toSomeoneElses, toNonexistent);
        }

        var settings = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{assistantId}/settings", admin.Token));
        settings.GetProperty("configuration").GetProperty("name").GetString().ShouldBe("管理者的助理");
    }

    // --- Acceptance: connecting knowledge bases ----------------------------------------

    [Fact]
    public async Task Connecting_someone_elses_private_knowledge_base_is_422_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理", withKnowledgeBase: true);
        var privateKnowledgeBase = await CreateKnowledgeBaseAsync(org, org.Internal.Id, "同仁的私人知識庫");

        var response = await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/sources/knowledge-base/{privateKnowledgeBase}", admin.Token, new { });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(response)).GetProperty("errors").TryGetProperty("sources", out _).ShouldBeTrue();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantKnowledgeBases
                .AnyAsync(link => link.AssistantId == assistantId && link.KnowledgeBaseId == privateKnowledgeBase, CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Connecting_a_knowledge_base_shared_with_the_owner_succeeds()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理", withKnowledgeBase: true);
        var sharedKnowledgeBase = await CreateKnowledgeBaseAsync(org, org.Internal.Id, "分享給管理者的知識庫");
        await ShareWithAsync(org, sharedKnowledgeBase, org.Admin.Id);

        var response = await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/sources/knowledge-base/{sharedKnowledgeBase}", admin.Token, new { });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = await BodyJsonAsync(response);
        settings.GetProperty("knowledgeBaseIds").EnumerateArray().Select(id => id.GetGuid())
            .ShouldContain(sharedKnowledgeBase);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantKnowledgeBases
                .AnyAsync(link => link.AssistantId == assistantId && link.KnowledgeBaseId == sharedKnowledgeBase, CancellationToken))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Connecting_a_public_knowledge_base_succeeds_and_reconnecting_is_a_no_op()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理", withKnowledgeBase: true);
        var publicKnowledgeBase = await CreateKnowledgeBaseAsync(org, org.Internal.Id, "公開知識庫", KnowledgeSharingScope.Public);

        var first = await admin.Spa.PutAsync($"{BasePath}/{assistantId}/sources/knowledge-base/{publicKnowledgeBase}", admin.Token, new { });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await admin.Spa.PutAsync($"{BasePath}/{assistantId}/sources/knowledge-base/{publicKnowledgeBase}", admin.Token, new { });
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantKnowledgeBases
                .CountAsync(link => link.AssistantId == assistantId && link.KnowledgeBaseId == publicKnowledgeBase, CancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Disconnecting_the_last_source_is_422_and_disconnecting_an_unconnected_one_is_a_no_op()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (assistantId, onlyKnowledgeBaseId) = await CreateAssistantWithOneKnowledgeBaseAsync(org, org.Admin.Id, "唯一來源的助理");

        var lastSource = await admin.Spa.DeleteAsync($"{BasePath}/{assistantId}/sources/knowledge-base/{onlyKnowledgeBaseId}", admin.Token);
        lastSource.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(lastSource)).GetProperty("reason").GetString().ShouldBe("last-source");

        var unconnected = await admin.Spa.DeleteAsync($"{BasePath}/{assistantId}/sources/knowledge-base/{Guid.NewGuid()}", admin.Token);
        unconnected.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantKnowledgeBases.CountAsync(link => link.AssistantId == assistantId, CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_database_sources_are_refused_with_422_after_the_ownership_check()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理", withKnowledgeBase: true);

        // M4 #148: databases are connectable now; an unknown or malformed id is the same 422.
        var unknown = await admin.Spa.PutAsync($"{BasePath}/{assistantId}/sources/database/{Guid.NewGuid()}", admin.Token, new { });
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(unknown)).GetProperty("reason").GetString().ShouldBe("source-not-connectable");
        var malformed = await admin.Spa.PutAsync($"{BasePath}/{assistantId}/sources/database/not-a-guid", admin.Token, new { });
        malformed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Disconnecting what is not connected is a no-op.
        var disconnect = await admin.Spa.DeleteAsync($"{BasePath}/{assistantId}/sources/database/{Guid.NewGuid()}", admin.Token);
        disconnect.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Still gated by ownership: another organization's admin gets the configuration 403,
        // not the database refusal, so this cannot be used to probe assistant ids either.
        var otherOrg = await CreateOrganizationAsync("其他組織");
        var otherAdmin = await SignInAsync(otherOrg, "admin");
        var probe = await otherAdmin.Spa.PutAsync($"{BasePath}/{assistantId}/sources/database/{Guid.NewGuid()}", otherAdmin.Token, new { });
        probe.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(probe)).GetProperty("reason").GetString().ShouldBe("assistant-configuration");
    }

    // --- Acceptance: usable=true does not leak other accounts' assistants --------------

    [Fact]
    public async Task Usable_true_lists_only_the_callers_own_assistants_not_someone_elses()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var adminsAssistant = await CreateAssistantAsync(org, org.Admin.Id, "管理者的助理");

        await _host.CreateAccountAsync(
            org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var admin2Assistant = await CreateAssistantAsync(org, org.Admin.Id, "另一個管理者名下的助理");
        _ = admin2Assistant;

        var internalEmployee = await SignInAsync(org, "internal");

        var ownList = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}?usable=true", admin.Token));
        ownList.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(adminsAssistant);

        var internalList = await BodyJsonAsync(await internalEmployee.Spa.GetAsync($"{BasePath}?usable=true", internalEmployee.Token));
        internalList.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldNotContain(adminsAssistant);
    }

    [Fact]
    public async Task Usable_true_needs_only_a_signed_in_account_but_the_owner_list_needs_manage_assistants()
    {
        var org = await CreateOrganizationAsync();
        var internalEmployee = await SignInAsync(org, "internal"); // no manage-assistants

        var usable = await internalEmployee.Spa.GetAsync($"{BasePath}?usable=true", internalEmployee.Token);
        usable.StatusCode.ShouldBe(HttpStatusCode.OK);

        var ownList = await internalEmployee.Spa.GetAsync(BasePath, internalEmployee.Token);
        ownList.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(ownList)).GetProperty("reason").GetString().ShouldBe("assistant-configuration");
    }

    // --- Acceptance: settings validation is all-or-nothing ------------------------------

    [Fact]
    public async Task Patch_with_one_valid_and_one_invalid_field_is_422_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "原始名稱", withKnowledgeBase: true);

        var response = await admin.Spa.PatchAsync($"{BasePath}/{assistantId}/settings", admin.Token, new
        {
            name = "新名稱",
            tone = "not-a-real-tone",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(response)).GetProperty("errors").TryGetProperty("tone", out _).ShouldBeTrue();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(a => a.Id == assistantId, CancellationToken);
        assistant.Name.ShouldBe("原始名稱");
    }

    [Fact]
    public async Task Patch_changes_only_the_fields_sent_and_rejects_a_blank_name()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "舊名稱", withKnowledgeBase: true);

        var renamed = await admin.Spa.PatchAsync($"{BasePath}/{assistantId}/settings", admin.Token, new { name = " 新名稱 " });
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = await BodyJsonAsync(renamed);
        settings.GetProperty("configuration").GetProperty("name").GetString().ShouldBe("新名稱");

        var blank = await admin.Spa.PatchAsync($"{BasePath}/{assistantId}/settings", admin.Token, new { name = "" });
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var rulesPatch = await admin.Spa.PatchAsync($"{BasePath}/{assistantId}/settings", admin.Token, new
        {
            rules = new { knowledgeScope = "allow-general-knowledge", showCitations = false },
        });
        rulesPatch.StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterRules = await BodyJsonAsync(rulesPatch);
        afterRules.GetProperty("rules").GetProperty("knowledgeScope").GetString().ShouldBe("allow-general-knowledge");
        afterRules.GetProperty("rules").GetProperty("showCitations").GetBoolean().ShouldBeFalse();
        afterRules.GetProperty("configuration").GetProperty("name").GetString().ShouldBe("新名稱");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(a => a.Id == assistantId, CancellationToken);
        assistant.Name.ShouldBe("新名稱");
        assistant.KnowledgeScope.ShouldBe(AssistantKnowledgeScope.AllowGeneralKnowledge);
        assistant.ShowCitations.ShouldBeFalse();
    }

    // --- Acceptance: delete removes the assistant and its connections ------------------

    [Fact]
    public async Task Deleting_removes_the_assistant_and_its_knowledge_base_connections()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var (doomed, knowledgeBaseId) = await CreateAssistantWithOneKnowledgeBaseAsync(org, org.Admin.Id, "要刪除的助理");
        var kept = await CreateAssistantAsync(org, org.Admin.Id, "要保留的助理", withKnowledgeBase: true);

        var response = await admin.Spa.DeleteAsync($"{BasePath}/{doomed}", admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.Assistants.AnyAsync(a => a.Id == doomed, CancellationToken)).ShouldBeFalse();
            (await dbContext.AssistantKnowledgeBases.AnyAsync(link => link.AssistantId == doomed, CancellationToken)).ShouldBeFalse();
            (await dbContext.KnowledgeBases.AnyAsync(kb => kb.Id == knowledgeBaseId, CancellationToken))
                .ShouldBeTrue("the knowledge base itself is not deleted, only the connection");

            (await dbContext.Assistants.AnyAsync(a => a.Id == kept, CancellationToken)).ShouldBeTrue();
        }

        await AssertIdenticalAsync(
            await admin.Spa.GetAsync($"{BasePath}/{doomed}/settings", admin.Token),
            await admin.Spa.GetAsync($"{BasePath}/{Guid.NewGuid()}/settings", admin.Token));
    }

    // --- Cross-cutting gates ------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_callers_get_401()
    {
        using var anonymous = _host.CreateSpaClient();
        (await anonymous.Http.GetAsync(BasePath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Http.GetAsync($"{BasePath}?usable=true", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // === Platform sharing (#73, M3 plan §5 Slice 3) =====================================

    // --- Acceptance: unsharing revokes use; resharing restores it -----------------------

    [Fact]
    public async Task Unsharing_makes_the_assistant_unusable_and_resharing_restores_it()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var internalEmployee = await SignInAsync(org, "internal"); // seeded with use-shared-assistants
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "分享的助理");

        // Not shared yet: does not appear in the internal employee's usable list.
        (await UsableIdsAsync(internalEmployee)).ShouldNotContain(assistantId);

        // Share it.
        var shared = await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform", admin.Token, new { accountIds = new[] { org.Internal.Id.ToString() } });
        shared.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(shared)).GetProperty("allowedAccountIds").EnumerateArray()
            .Select(item => item.GetGuid()).ShouldContain(org.Internal.Id);
        (await UsableIdsAsync(internalEmployee)).ShouldContain(assistantId, "shared, and the account holds use-shared-assistants");

        // Unshare it (acceptance: 取消分享後，被取消的帳號無法使用).
        var unshared = await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform", admin.Token, new { accountIds = Array.Empty<string>() });
        unshared.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(unshared)).GetProperty("allowedAccountIds").GetArrayLength().ShouldBe(0);
        (await UsableIdsAsync(internalEmployee)).ShouldNotContain(assistantId);

        // Reshare it (acceptance: 重新分享後，原本的存取再次出現).
        var reshared = await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform", admin.Token, new { accountIds = new[] { org.Internal.Id.ToString() } });
        reshared.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UsableIdsAsync(internalEmployee)).ShouldContain(assistantId);
    }

    [Fact]
    public async Task Sharing_without_use_shared_assistants_does_not_grant_use()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");

        // The external customer has neither manage-assistants nor use-shared-assistants.
        var customer = await SignInAsync(org, "customer");
        await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform", admin.Token, new { accountIds = new[] { org.Customer.Id.ToString() } });

        (await UsableIdsAsync(customer)).ShouldNotContain(assistantId, "shared, but lacks use-shared-assistants");
    }

    [Fact]
    public async Task Updating_platform_sharing_drops_the_owner_unknown_ids_and_other_organizations_accounts()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");

        var otherOrg = await CreateOrganizationAsync("其他組織");

        var response = await admin.Spa.PutAsync($"{BasePath}/{assistantId}/publishing/platform", admin.Token, new
        {
            accountIds = new[]
            {
                org.Admin.Id.ToString(), // the owner — dropped
                org.Internal.Id.ToString(), // valid
                otherOrg.Admin.Id.ToString(), // another organization — dropped
                Guid.NewGuid().ToString(), // unknown — dropped
                "not-a-guid", // malformed — dropped
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var allowed = (await BodyJsonAsync(response)).GetProperty("allowedAccountIds")
            .EnumerateArray().Select(item => item.GetGuid()).ToList();
        allowed.ShouldBe([org.Internal.Id]);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var persisted = await dbContext.AssistantShares
            .Where(share => share.AssistantId == assistantId)
            .Select(share => share.AccountId)
            .ToListAsync(CancellationToken);
        persisted.ShouldBe([org.Internal.Id]);
    }

    // --- Acceptance: pausing blocks non-owners, never the owner --------------------------

    [Fact]
    public async Task Pausing_the_assistant_blocks_non_owners_but_the_owner_can_still_use_it()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var internalEmployee = await SignInAsync(org, "internal");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");

        await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform", admin.Token, new { accountIds = new[] { org.Internal.Id.ToString() } });
        (await UsableIdsAsync(internalEmployee)).ShouldContain(assistantId);

        var paused = await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform/paused", admin.Token, new { paused = true });
        paused.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(paused)).GetProperty("status").GetString().ShouldBe("paused");

        (await UsableIdsAsync(internalEmployee)).ShouldNotContain(assistantId, "acceptance: 助理暫停後，非擁有者無法使用");
        (await UsableIdsAsync(admin)).ShouldContain(assistantId, "acceptance: 擁有者可以");

        var resumed = await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform/paused", admin.Token, new { paused = false });
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(resumed)).GetProperty("status").GetString().ShouldBe("published");
        (await UsableIdsAsync(internalEmployee)).ShouldContain(assistantId, "resumed");
    }

    // --- GET publishing: real platform, website and line data ----------------------------

    [Fact]
    public async Task Get_publishing_returns_real_platform_website_and_line_data()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, org.Admin.Id, "助理");
        await admin.Spa.PutAsync(
            $"{BasePath}/{assistantId}/publishing/platform", admin.Token, new { accountIds = new[] { org.Internal.Id.ToString() } });

        var response = await admin.Spa.GetAsync($"{BasePath}/{assistantId}/publishing", admin.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await BodyJsonAsync(response);
        body.GetProperty("assistantId").GetGuid().ShouldBe(assistantId);

        var platform = body.GetProperty("platform");
        platform.GetProperty("allowedAccountIds").EnumerateArray().Select(item => item.GetGuid()).ShouldContain(org.Internal.Id);
        platform.GetProperty("channel").GetProperty("type").GetString().ShouldBe("platform");
        platform.GetProperty("channel").GetProperty("status").GetString().ShouldBe("published");
        platform.GetProperty("candidates").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())
            .ShouldNotContain(org.Admin.Id, "the owner is never their own share candidate");

        // The website channel's own behaviour: AssistantWebsiteChannelEndpointsTests (#194).
        var website = body.GetProperty("website");
        website.GetProperty("servingState").GetString().ShouldBe("not-published");
        website.GetProperty("channel").GetProperty("status").GetString().ShouldBe("not-configured");
        // The LINE channel's own behaviour: AssistantLineChannelEndpointsTests (#229).
        var line = body.GetProperty("line");
        line.GetProperty("servingState").GetString().ShouldBe("not-published");
        line.GetProperty("channel").GetProperty("status").GetString().ShouldBe("not-configured");
    }

    // --- Acceptance: another organization's assistant is indistinguishable from missing --

    [Fact]
    public async Task Publishing_endpoints_treat_another_organizations_assistant_like_a_missing_one()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var assistantId = await CreateAssistantAsync(orgA, orgA.Admin.Id, "A 的助理");

        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        (string Verb, Func<Guid, Task<HttpResponseMessage>> Send)[] endpoints =
        [
            ("GET publishing", id => adminB.Spa.GetAsync($"{BasePath}/{id}/publishing", adminB.Token)),
            ("PUT platform", id => adminB.Spa.PutAsync(
                $"{BasePath}/{id}/publishing/platform", adminB.Token, new { accountIds = Array.Empty<string>() })),
            ("PUT platform paused", id => adminB.Spa.PutAsync(
                $"{BasePath}/{id}/publishing/platform/paused", adminB.Token, new { paused = true })),
        ];

        foreach (var (verb, send) in endpoints)
        {
            var toOtherOrganization = await send(assistantId);
            var toNonexistent = await send(Guid.NewGuid());

            toOtherOrganization.StatusCode.ShouldBe(HttpStatusCode.Forbidden, verb);
            await AssertIdenticalAsync(toOtherOrganization, toNonexistent);
            (await BodyJsonAsync(toNonexistent)).GetProperty("reason").GetString().ShouldBe("publishing", verb);
        }
    }

    private static async Task<List<Guid>> UsableIdsAsync(SignedIn caller)
    {
        var body = await BodyJsonAsync(await caller.Spa.GetAsync($"{BasePath}?usable=true", caller.Token));
        return [.. body.EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];
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

    /// <summary>Seeds an <see cref="Assistant"/> row directly (there is no create endpoint
    /// yet; that is #72). Optionally connects one freshly seeded knowledge base, since real
    /// settings updates only need at least one source to exist for other tests to disconnect
    /// from — this flag is for tests that do not care which knowledge base it is.</summary>
    private async Task<Guid> CreateAssistantAsync(
        TestOrganization org, Guid ownerAccountId, string name, bool withKnowledgeBase = false)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, ownerAccountId, name, "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, now);
        dbContext.Assistants.Add(assistant);

        if (withKnowledgeBase)
        {
            var knowledgeBase = KnowledgeBase.Create(org.Organization.Id, ownerAccountId, $"{name}的知識庫", string.Empty, now);
            dbContext.KnowledgeBases.Add(knowledgeBase);
            dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        }

        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private async Task<(Guid AssistantId, Guid KnowledgeBaseId)> CreateAssistantWithOneKnowledgeBaseAsync(
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

    private async Task<Guid> CreateKnowledgeBaseAsync(
        TestOrganization org, Guid ownerAccountId, string name, KnowledgeSharingScope scope = KnowledgeSharingScope.Private)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var knowledgeBase = KnowledgeBase.Create(org.Organization.Id, ownerAccountId, name, string.Empty, now);
        if (scope != KnowledgeSharingScope.Private)
        {
            knowledgeBase.ChangeSharing(scope, allowOriginalDownload: false, now);
        }

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

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Every endpoint addressed by an assistant id, each with a valid body, so the
    /// only thing that can make it fail is the id.</summary>
    private static IEnumerable<(string Verb, Func<Guid, Task<HttpResponseMessage>> Send)> EndpointsById(SignedIn caller) =>
    [
        ("GET settings", id => caller.Spa.GetAsync($"{BasePath}/{id}/settings", caller.Token)),
        ("PATCH settings", id => caller.Spa.PatchAsync($"{BasePath}/{id}/settings", caller.Token, new { name = "改名" })),
        ("PUT knowledge-base source", id => caller.Spa.PutAsync(
            $"{BasePath}/{id}/sources/knowledge-base/{Guid.NewGuid()}", caller.Token, new { })),
        ("DELETE knowledge-base source", id => caller.Spa.DeleteAsync(
            $"{BasePath}/{id}/sources/knowledge-base/{Guid.NewGuid()}", caller.Token)),
        ("DELETE", id => caller.Spa.DeleteAsync($"{BasePath}/{id}", caller.Token)),
    ];

    /// <summary>Status, content type, body bytes and cookies all equal (as in
    /// <c>KnowledgeBaseEndpointsTests</c>).</summary>
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
