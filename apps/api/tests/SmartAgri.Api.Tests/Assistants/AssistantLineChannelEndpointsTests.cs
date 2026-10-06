using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// The LINE channel's settings API against real PostgreSQL (M5b plan §5 Slice 1, issue #229). Testing
/// the connection and enabling are #230, so a tested or enabled channel is set up directly through
/// the database with the entity's own methods.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class AssistantLineChannelEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Line-Channel-Pass-1!";
    private const string BasePath = "/api/v1/assistants";

    // Low-entropy, obviously fake credentials of the right shapes.
    private const string Secret1 = "0000000000000000000000000000aaa1";
    private const string Secret2 = "0000000000000000000000000000bbb2";
    private static readonly string Token1 = new string('T', 36) + "tok1";
    private static readonly string Token2 = new string('U', 36) + "tok2";

    private readonly AuthHostFixture _host;

    public AssistantLineChannelEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string LinePath(Guid id) => $"{BasePath}/{id}/publishing/line";

    // --- Settings ------------------------------------------------------------------------------

    [Fact]
    public async Task Before_any_save_the_channel_is_empty_with_the_default_welcome_message_pending_checks_and_the_webhook_url()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");

        var response = await org.Admin.Spa.GetAsync(LinePath(assistantId), org.Admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(view, "LineChannelView");
        view.GetProperty("revision").GetInt32().ShouldBe(0);
        view.GetProperty("officialAccountId").GetString().ShouldBe(string.Empty);
        view.GetProperty("channelId").GetString().ShouldBe(string.Empty);
        view.GetProperty("welcomeMessage").GetString().ShouldBe("您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。");
        AssertSecretStatus(view.GetProperty("channelSecret"), configured: false, lastFour: null);
        AssertSecretStatus(view.GetProperty("accessToken"), configured: false, lastFour: null);
        view.GetProperty("webhookUrl").GetString().ShouldBe($"http://localhost:5153/api/v1/line/webhook/{assistantId}");
        AssertChecks(view, "pending", "pending", "pending");
        view.GetProperty("connectionCheckedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("state").GetString().ShouldBe("draft");
        view.GetProperty("servingState").GetString().ShouldBe("not-published");
        view.GetProperty("pushFallbackCount").GetInt32().ShouldBe(0);
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("not-configured");
        view.GetProperty("channel").GetProperty("type").GetString().ShouldBe("line");
        view.GetProperty("channel").GetProperty("id").GetString().ShouldBe($"channel-line:{assistantId}");
    }

    [Fact]
    public async Task The_first_save_protects_both_credentials_and_answers_only_configured_and_the_last_four()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");

        var response = await SaveAsync(org, assistantId, 0, " @anxin-demo ", "1650000000", $" {Secret1} ", Token1);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var view = JsonDocument.Parse(body).RootElement;
        OpenApiContract.AssertKeysMatchSchema(view, "LineChannelView");
        view.GetProperty("revision").GetInt32().ShouldBe(1);
        view.GetProperty("officialAccountId").GetString().ShouldBe("@anxin-demo");
        AssertSecretStatus(view.GetProperty("channelSecret"), configured: true, lastFour: "aaa1");
        AssertSecretStatus(view.GetProperty("accessToken"), configured: true, lastFour: "tok1");
        view.GetProperty("channelSecret").EnumerateObject().Select(property => property.Name)
            .ShouldBe(["configured", "lastFour", "updatedAt"]);
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("testing");

        // Stored encrypted under each field's own purpose; neither plaintext is in the row.
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var channel = await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
        channel.ChannelSecret.Ciphertext.ShouldNotContain(Secret1);
        channel.AccessToken.Ciphertext.ShouldNotContain(Token1);
        var protector = _host.Factory.Services.GetRequiredService<ISecretProtector>();
        protector.Unprotect(AssistantLineChannel.ChannelSecretPurpose, channel.ChannelSecret).ShouldBe(Secret1);
        protector.Unprotect(AssistantLineChannel.AccessTokenPurpose, channel.AccessToken).ShouldBe(Token1);
        Should.Throw<SecretUnprotectException>(() => protector.Unprotect(AssistantLineChannel.AccessTokenPurpose, channel.ChannelSecret));

        AssertNoCredential(body, channel.ChannelSecret.Ciphertext, channel.AccessToken.Ciphertext);
    }

    [Fact]
    public async Task No_response_ever_contains_a_credential_or_its_ciphertext()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EnableDirectlyAsync(org, assistantId);
        (await SaveAsync(org, assistantId, 1, "@anxin-demo", "1650000000", Secret2, Token2)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EnableDirectlyAsync(org, assistantId);

        string[] ciphertexts;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var channel = await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
            ciphertexts = [channel.ChannelSecret.Ciphertext, channel.AccessToken.Ciphertext];
        }

        var responses = new List<HttpResponseMessage>
        {
            await org.Admin.Spa.GetAsync(LinePath(assistantId), org.Admin.Token),
            await org.Admin.Spa.GetAsync($"{BasePath}/{assistantId}/publishing", org.Admin.Token),
            await org.Admin.Spa.PutAsync($"{LinePath(assistantId)}/paused", org.Admin.Token, new { paused = true }),
            await org.Admin.Spa.PutAsync($"{LinePath(assistantId)}/paused", org.Admin.Token, new { paused = false }),
            await org.Admin.Spa.PostAsync($"{LinePath(assistantId)}:unpublish", org.Admin.Token, new { }),
            // A refused save does not echo what was sent.
            await SaveAsync(org, assistantId, 2, "@anxin-demo", "1650000000", Secret1 + "zz", Token1 + " x"),
            await SaveAsync(org, assistantId, 1, "@anxin-demo", "1650000000", Secret1, Token1),
            await SaveAsync(org, assistantId, 2, "@anxin-demo", "1650000000", null, null),
        };

        responses.Select(response => (int)response.StatusCode).ShouldBe([200, 200, 200, 200, 200, 422, 409, 200]);
        foreach (var response in responses)
        {
            AssertNoCredential(await response.Content.ReadAsStringAsync(CancellationToken), ciphertexts);
        }
    }

    [Fact]
    public async Task Updating_only_the_general_fields_keeps_both_credentials_and_an_enabled_channel()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EnableDirectlyAsync(org, assistantId);
        var before = await StoredCredentialsAsync(org, assistantId);
        (await GetLineAsync(org, assistantId)).GetProperty("servingState").GetString().ShouldBe("serving");

        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        foreach (var (revision, empty) in new[] { (1, (string?)null), (2, string.Empty) })
        {
            var response = await SaveAsync(org, assistantId, revision, "@anxin-demo", "1650000000", empty, empty, "新的歡迎訊息");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
            var view = await BodyJsonAsync(response);
            view.GetProperty("welcomeMessage").GetString().ShouldBe("新的歡迎訊息");
            view.GetProperty("state").GetString().ShouldBe("published");
            view.GetProperty("servingState").GetString().ShouldBe("serving");
            AssertChecks(view, "passed", "passed", "passed");
            AssertSecretStatus(view.GetProperty("channelSecret"), configured: true, lastFour: "aaa1");
            AssertSecretStatus(view.GetProperty("accessToken"), configured: true, lastFour: "tok1");
        }

        (await StoredCredentialsAsync(org, assistantId)).ShouldBe(before);
    }

    [Fact]
    public async Task Replacing_the_token_clears_the_connection_test_and_an_enabled_channel_needs_a_new_test()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EnableDirectlyAsync(org, assistantId);
        var before = await StoredCredentialsAsync(org, assistantId);
        var enabled = await GetLineAsync(org, assistantId);
        enabled.GetProperty("servingState").GetString().ShouldBe("serving");
        enabled.GetProperty("channel").GetProperty("status").GetString().ShouldBe("published");

        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        var response = await SaveAsync(org, assistantId, 1, "@anxin-demo", "1650000000", null, Token2);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(response);
        view.GetProperty("state").GetString().ShouldBe("draft");
        view.GetProperty("servingState").GetString().ShouldBe("not-published");
        view.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("connectionCheckedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        AssertChecks(view, "pending", "pending", "pending");
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("testing");
        AssertSecretStatus(view.GetProperty("accessToken"), configured: true, lastFour: "tok2");
        AssertSecretStatus(view.GetProperty("channelSecret"), configured: true, lastFour: "aaa1");

        var after = await StoredCredentialsAsync(org, assistantId);
        after.Secret.ShouldBe(before.Secret);
        after.Token.ShouldNotBe(before.Token);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var channel = await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
        channel.BotUserId.ShouldBeNull();
        channel.ConnectionChecks.ShouldBeEmpty();
        channel.PublishedByAccountId.ShouldBeNull();
    }

    [Fact]
    public async Task Replacing_the_secret_or_changing_either_id_also_needs_a_new_test()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var revision = 1;
        foreach (var (because, officialAccountId, channelId, secret) in new[]
                 {
                     ("secret", "@anxin-demo", "1650000000", Secret2),
                     ("official account id", "@anxin-shop", "1650000000", (string?)null),
                     ("channel id", "@anxin-shop", "1650000009", (string?)null),
                 })
        {
            await EnableDirectlyAsync(org, assistantId);
            (await GetLineAsync(org, assistantId)).GetProperty("state").GetString().ShouldBe("published", because);

            var view = await BodyJsonAsync(await SaveAsync(org, assistantId, revision++, officialAccountId, channelId, secret, null));

            view.GetProperty("state").GetString().ShouldBe("draft", because);
            AssertChecks(view, "pending", "pending", "pending");
        }
    }

    [Fact]
    public async Task Invalid_fields_are_422_with_the_mocks_messages_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");

        // The first save needs both credentials.
        var missing = await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", null, " ");
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var missingErrors = (await BodyJsonAsync(missing)).GetProperty("errors");
        missingErrors.EnumerateObject().Select(property => property.Name).ShouldBe(["channelSecret", "accessToken"], ignoreOrder: true);
        missingErrors.GetProperty("channelSecret")[0].GetString().ShouldBe("請填寫 Channel secret。");

        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await StoredCredentialsAsync(org, assistantId);

        var response = await SaveAsync(org, assistantId, 1, "anxin-demo", "165000000a", "xyz", "short token", new string('歡', 121));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var errors = (await BodyJsonAsync(response)).GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["officialAccountId", "channelId", "channelSecret", "accessToken", "welcomeMessage"], ignoreOrder: true);
        errors.GetProperty("officialAccountId")[0].GetString().ShouldBe("官方帳號 ID需以 @ 開頭，接 3–20 個英數字，例如 @anxin-demo。");
        errors.GetProperty("channelId")[0].GetString().ShouldBe("Channel ID應為 10 位數字。");
        errors.GetProperty("channelSecret")[0].GetString().ShouldBe("Channel secret應為 32 個英數字（0–9、a–f）。");
        errors.GetProperty("accessToken")[0].GetString().ShouldBe("Channel access token至少 40 個字元且不含空白。");
        errors.GetProperty("welcomeMessage")[0].GetString().ShouldBe("歡迎訊息請在 120 個字以內。");

        var view = await GetLineAsync(org, assistantId);
        view.GetProperty("revision").GetInt32().ShouldBe(1);
        view.GetProperty("channelId").GetString().ShouldBe("1650000000");
        (await StoredCredentialsAsync(org, assistantId)).ShouldBe(before);
    }

    [Fact]
    public async Task A_stale_revision_is_409_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        (await SaveAsync(org, assistantId, 1, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await StoredCredentialsAsync(org, assistantId);

        foreach (var stale in new[] { 0, 2 })
        {
            var conflict = await SaveAsync(org, assistantId, stale, "@other", "1650000001", Secret2, Token2);
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await BodyJsonAsync(conflict)).GetProperty("reason").GetString().ShouldBe("line-revision-conflict");
        }

        (await GetLineAsync(org, assistantId)).GetProperty("officialAccountId").GetString().ShouldBe("@anxin-demo");
        (await StoredCredentialsAsync(org, assistantId)).ShouldBe(before);
    }

    // --- Pause, resume, unpublish ---------------------------------------------------------------

    [Fact]
    public async Task Pause_resume_and_unpublish_keep_the_settings_credentials_and_test_results()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);

        var neverSaved = await org.Admin.Spa.PutAsync($"{LinePath(assistantId)}/paused", org.Admin.Token, new { paused = true });
        neverSaved.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(neverSaved)).GetProperty("reason").GetString().ShouldBe("line-not-published");

        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.Spa.PutAsync($"{LinePath(assistantId)}/paused", org.Admin.Token, new { paused = true }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await EnableDirectlyAsync(org, assistantId);

        var paused = await org.Admin.Spa.PutAsync($"{LinePath(assistantId)}/paused", org.Admin.Token, new { paused = true });
        paused.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pausedView = await BodyJsonAsync(paused);
        pausedView.GetProperty("state").GetString().ShouldBe("paused");
        pausedView.GetProperty("servingState").GetString().ShouldBe("paused");
        pausedView.GetProperty("channel").GetProperty("status").GetString().ShouldBe("paused");

        var resumed = await org.Admin.Spa.PutAsync($"{LinePath(assistantId)}/paused", org.Admin.Token, new { paused = false });
        (await BodyJsonAsync(resumed)).GetProperty("servingState").GetString().ShouldBe("serving");

        var unpublished = await org.Admin.Spa.PostAsync($"{LinePath(assistantId)}:unpublish", org.Admin.Token, new { });
        unpublished.StatusCode.ShouldBe(HttpStatusCode.OK);
        var draft = await BodyJsonAsync(unpublished);
        draft.GetProperty("state").GetString().ShouldBe("draft");
        draft.GetProperty("servingState").GetString().ShouldBe("not-published");
        draft.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        draft.GetProperty("revision").GetInt32().ShouldBe(1);
        draft.GetProperty("officialAccountId").GetString().ShouldBe("@anxin-demo");
        AssertSecretStatus(draft.GetProperty("accessToken"), configured: true, lastFour: "tok1");
        AssertChecks(draft, "passed", "passed", "passed");
        draft.GetProperty("channel").GetProperty("status").GetString().ShouldBe("testing");
    }

    [Fact]
    public async Task Get_publishing_returns_the_real_line_view()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        (await SaveAsync(org, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await org.Admin.Spa.GetAsync($"{BasePath}/{assistantId}/publishing", org.Admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantPublishingView");
        var line = body.GetProperty("line");
        line.ShouldBe(await GetLineAsync(org, assistantId), new JsonElementEquality());
        line.GetProperty("officialAccountId").GetString().ShouldBe("@anxin-demo");
        line.GetProperty("servingState").GetString().ShouldBe("not-published");
    }

    // --- Another organization's assistant is indistinguishable from a missing one --------------

    [Fact]
    public async Task Another_organizations_or_someone_elses_assistant_gets_the_same_403_publishing_as_a_missing_id()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var assistantId = await PassedAcceptanceAssistantAsync(orgA);
        (await SaveAsync(orgA, assistantId, 0, "@anxin-demo", "1650000000", Secret1, Token1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EnableDirectlyAsync(orgA, assistantId);
        var before = await StoredCredentialsAsync(orgA, assistantId);

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
        var view = await GetLineAsync(orgA, assistantId);
        view.GetProperty("officialAccountId").GetString().ShouldBe("@anxin-demo");
        view.GetProperty("revision").GetInt32().ShouldBe(1);
        view.GetProperty("state").GetString().ShouldBe("published");
        (await StoredCredentialsAsync(orgA, assistantId)).ShouldBe(before);
    }

    [Fact]
    public async Task Without_manage_publishing_every_line_endpoint_is_403_publishing()
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

    private sealed record TestOrganization(Organization Organization, SignedIn Admin, Guid AdminId);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        return new TestOrganization(organization, await SignInAsync(organization, "admin"), admin.Id);
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

    /// <summary>An assistant whose acceptance passed (a test case and a completed run without failures).</summary>
    private async Task<Guid> PassedAcceptanceAssistantAsync(TestOrganization org)
    {
        var assistantId = await CreateAssistantAsync(org, "客服助理");
        var queuedAt = _host.Clock.GetUtcNow().AddHours(-1);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        dbContext.AssistantTestCases.Add(new AssistantTestCase(
            org.Organization.Id, assistantId, "退貨期限是幾天？", AssistantTestCaseCategory.Common,
            AssistantTestExpectedKind.CompanyData, [Guid.NewGuid()], null, ordinal: 1, queuedAt));
        var run = AssistantTestRun.Queue(org.Organization.Id, assistantId, AssistantTestRunTrigger.Manual, queuedAt);
        run.Start("test", AuthHostFixture.ChatModel, 0.3, queuedAt);
        run.Complete(1, 0, queuedAt);
        dbContext.AssistantTestRuns.Add(run);
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistantId;
    }

    /// <summary>What #230's 「測試連線」 and 「啟用」 will do, done directly: every check passed, then
    /// enabled by the owner.</summary>
    private async Task EnableDirectlyAsync(TestOrganization org, Guid assistantId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var channel = await dbContext.AssistantLineChannels.SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
        var now = _host.Clock.GetUtcNow();
        channel.RecordConnectionChecks(
            [.. LineConnectionCheck.All.Select(kind => new LineConnectionCheck(kind, LineConnectionCheckState.Passed, "通過。"))],
            "U" + new string('0', 32),
            now);
        channel.Publish(org.AdminId, now);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task<(string Secret, string Token)> StoredCredentialsAsync(TestOrganization org, Guid assistantId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var channel = await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
        return ($"{channel.ChannelSecret.Ciphertext}|{channel.ChannelSecret.SetAt:O}", $"{channel.AccessToken.Ciphertext}|{channel.AccessToken.SetAt:O}");
    }

    private Task<HttpResponseMessage> SaveAsync(
        TestOrganization org,
        Guid assistantId,
        int revision,
        string officialAccountId,
        string channelId,
        string? channelSecret,
        string? accessToken,
        string welcomeMessage = "您好！歡迎加入。") =>
        org.Admin.Spa.PutAsync(LinePath(assistantId), org.Admin.Token, new
        {
            officialAccountId,
            channelId,
            channelSecret,
            accessToken,
            welcomeMessage,
            revision,
        });

    private async Task<JsonElement> GetLineAsync(TestOrganization org, Guid assistantId)
    {
        var response = await org.Admin.Spa.GetAsync(LinePath(assistantId), org.Admin.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    private static void AssertSecretStatus(JsonElement status, bool configured, string? lastFour)
    {
        status.GetProperty("configured").GetBoolean().ShouldBe(configured);
        if (lastFour is null)
        {
            status.GetProperty("lastFour").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        else
        {
            status.GetProperty("lastFour").GetString().ShouldBe(lastFour);
        }

        (status.GetProperty("updatedAt").ValueKind == JsonValueKind.Null).ShouldBe(!configured);
    }

    private static void AssertChecks(JsonElement view, params string[] states)
    {
        var checks = view.GetProperty("checks").EnumerateArray().ToList();
        checks.Select(check => check.GetProperty("check").GetString()).ShouldBe(["access-token", "webhook-endpoint", "webhook-test"]);
        checks.Select(check => check.GetProperty("state").GetString()).ShouldBe(states);
    }

    /// <summary>The raw JSON contains none of the plaintexts used in these tests, nor
    /// <paramref name="ciphertexts"/>, nor any key that could carry one.</summary>
    private static void AssertNoCredential(string json, params string[] ciphertexts)
    {
        foreach (var plaintext in new[] { Secret1, Secret2, Token1, Token2 })
        {
            json.ShouldNotContain(plaintext);
            json.ShouldNotContain(plaintext[..20]);
        }

        foreach (var ciphertext in ciphertexts)
        {
            json.ShouldNotContain(ciphertext);
            json.ShouldNotContain(ciphertext[..24]);
        }

        json.ShouldNotContain("ciphertext", Case.Insensitive);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Every LINE endpoint addressed by an assistant id, each with a valid body.</summary>
    private static IEnumerable<(string Verb, Func<Guid, Task<HttpResponseMessage>> Send)> EndpointsById(SignedIn caller) =>
    [
        ("GET line", id => caller.Spa.GetAsync(LinePath(id), caller.Token)),
        ("PUT line", id => caller.Spa.PutAsync(LinePath(id), caller.Token, new
        {
            officialAccountId = "@evil-account",
            channelId = "1650000009",
            channelSecret = Secret2,
            accessToken = Token2,
            welcomeMessage = "歡迎",
            revision = 1,
        })),
        ("PUT paused", id => caller.Spa.PutAsync($"{LinePath(id)}/paused", caller.Token, new { paused = true })),
        ("POST unpublish", id => caller.Spa.PostAsync($"{LinePath(id)}:unpublish", caller.Token, new { })),
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

    /// <summary>Compares two JSON values by their raw text.</summary>
    private sealed class JsonElementEquality : IEqualityComparer<JsonElement>
    {
        public bool Equals(JsonElement x, JsonElement y) => x.GetRawText() == y.GetRawText();

        public int GetHashCode(JsonElement obj) => obj.GetRawText().GetHashCode(StringComparison.Ordinal);
    }
}
