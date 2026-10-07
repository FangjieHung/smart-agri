using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Line;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// 「測試連線」 and 「啟用」 (M5b plan §5 Slice 2, issue #230) against real PostgreSQL and the host's fake
/// LINE server (<see cref="AuthHostFixture.Line"/>). Every test adds its own fake bot, so its token,
/// scripts and recorded requests are its own.
/// </summary>
public partial class AssistantLineChannelEndpointsTests
{
    private FakeLineServer Line => _host.Line;

    // --- Testing the connection -------------------------------------------------------------------

    [Fact]
    public async Task Testing_the_connection_runs_the_three_checks_in_order_and_stores_the_results_and_the_bot_user_id()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var bot = Line.AddBot("@anxin-demo", "安心農場");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);
        var webhookUrl = $"http://localhost:5153/api/v1/line/webhook/{assistantId}";

        var response = await TestConnectionAsync(org, assistantId);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var view = JsonDocument.Parse(body).RootElement;
        OpenApiContract.AssertKeysMatchSchema(view, "LineChannelView");
        AssertChecks(view, "passed", "passed", "passed");
        var checks = view.GetProperty("checks");
        checks[0].GetProperty("message").GetString().ShouldBe("Token 有效，屬於官方帳號 @anxin-demo（安心農場）。");
        checks[1].GetProperty("message").GetString().ShouldBe($"已將 LINE 的 Webhook 網址設為 {webhookUrl}。");
        checks[2].GetProperty("message").GetString()!.ShouldContain("驗證簽章通過");
        view.GetProperty("connectionCheckedAt").ValueKind.ShouldBe(JsonValueKind.String);
        view.GetProperty("state").GetString().ShouldBe("draft");
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("testing");
        view.GetProperty("revision").GetInt32().ShouldBe(1, "testing does not change the settings revision");
        body.ShouldNotContain(bot.AccessToken);

        var requests = Line.RequestsWith(bot.AccessToken);
        requests.Select(request => request.Endpoint).ShouldBe(
            [FakeLineServer.BotInfo, FakeLineServer.SetWebhookEndpoint, FakeLineServer.TestWebhookEndpoint]);
        requests[1].Json.GetProperty("endpoint").GetString().ShouldBe(webhookUrl);
        requests[2].Json.GetProperty("endpoint").GetString().ShouldBe(webhookUrl);
        Line.WebhookEndpointOf(bot.AccessToken).ShouldBe(webhookUrl);

        var channel = await StoredChannelAsync(org, assistantId);
        channel.BotUserId.ShouldBe(bot.UserId);
        channel.ConnectionChecksPassed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("forbidden")]
    [InlineData("timeout")]
    public async Task A_failing_token_check_is_200_and_skips_the_other_two(string failure)
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var bot = Line.AddBot("@anxin-demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);
        Line.Script(bot.AccessToken, FakeLineServer.BotInfo, failure switch
        {
            "unauthorized" => LineBehavior.Unauthorized,
            "forbidden" => LineBehavior.Forbidden,
            _ => LineBehavior.TimeOut,
        });

        var response = await TestConnectionAsync(org, assistantId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(response);
        AssertChecks(view, "failed", "skipped", "skipped");
        view.GetProperty("checks")[0].GetProperty("message").GetString()!.ShouldContain(failure == "timeout" ? "逾時" : "Channel access token");
        view.GetProperty("checks")[1].GetProperty("message").GetString().ShouldBe(LineConnectionTester.SkippedMessage);
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("needs-attention");
        Line.RequestsWith(bot.AccessToken).Select(request => request.Endpoint).ShouldBe([FakeLineServer.BotInfo]);
        (await StoredChannelAsync(org, assistantId)).BotUserId.ShouldBeNull();
    }

    [Fact]
    public async Task A_token_of_another_official_account_fails_the_first_check_and_explains_the_mismatch()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var bot = Line.AddBot("@123abcde", "別家商店");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);

        var view = await BodyJsonAsync(await TestConnectionAsync(org, assistantId));

        AssertChecks(view, "failed", "skipped", "skipped");
        view.GetProperty("checks")[0].GetProperty("message").GetString().ShouldBe(
            "這個 Channel access token 屬於官方帳號 @123abcde（別家商店），與填寫的官方帳號 ID @anxin-demo 不符；" +
            "請確認填寫的官方帳號 ID，或改用這個官方帳號的 Messaging API channel 的 Token。");
        Line.RequestsWith(bot.AccessToken).Count.ShouldBe(1);
        (await StoredChannelAsync(org, assistantId)).BotUserId.ShouldBeNull();

        // The same id in other letter case is the same account.
        var sameAccount = Line.AddBot("@AnXin-Demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", sameAccount.AccessToken, revision: 1);
        AssertChecks(await BodyJsonAsync(await TestConnectionAsync(org, assistantId)), "passed", "passed", "passed");
    }

    [Fact]
    public async Task Each_later_check_failing_is_200_with_the_earlier_ones_passed()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var bot = Line.AddBot("@anxin-demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);

        var cases = new (Action Script, string[] States, int Check, string Expected)[]
        {
            (() => Line.Script(bot.AccessToken, FakeLineServer.SetWebhookEndpoint, LineBehavior.ServerError),
                ["passed", "failed", "skipped"], 1, "HTTP 500"),
            (() => Line.Script(bot.AccessToken, FakeLineServer.SetWebhookEndpoint, LineBehavior.Answer(HttpStatusCode.BadRequest, """{"message":"Invalid webhook endpoint URL"}""")),
                ["passed", "failed", "skipped"], 1, "LINE 不接受這個 Webhook 網址"),
            (() => Line.ScriptWebhookTest(bot.AccessToken, success: false, statusCode: 401, reason: "ERROR_STATUS_CODE", detail: "401"),
                ["passed", "passed", "failed"], 2, "Channel secret"),
            (() => Line.ScriptWebhookTest(bot.AccessToken, success: false, statusCode: 0, reason: "COULD_NOT_CONNECT", detail: "Connection refused"),
                ["passed", "passed", "failed"], 2, "LINE 無法連線到 Webhook 網址"),
            (() => Line.Script(bot.AccessToken, FakeLineServer.TestWebhookEndpoint, LineBehavior.RateLimited),
                ["passed", "passed", "failed"], 2, "每小時最多測試 Webhook 60 次"),
            (() => Line.Script(bot.AccessToken, FakeLineServer.TestWebhookEndpoint, LineBehavior.NetworkError),
                ["passed", "passed", "failed"], 2, "無法連線到 LINE"),
        };

        foreach (var (script, states, check, expected) in cases)
        {
            Line.ResetScripts(bot.AccessToken);
            script();

            var response = await TestConnectionAsync(org, assistantId);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, expected);
            var view = await BodyJsonAsync(response);
            AssertChecks(view, states);
            view.GetProperty("checks")[check].GetProperty("message").GetString()!.ShouldContain(expected);
            (await StoredChannelAsync(org, assistantId)).BotUserId.ShouldBe(bot.UserId, "the token check passed");
        }
    }

    [Fact]
    public async Task A_failed_test_of_an_enabled_channel_keeps_it_enabled_but_stops_serving_until_a_test_passes()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var bot = Line.AddBot("@anxin-demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);
        await TestConnectionAsync(org, assistantId);
        (await PublishLineAsync(org, assistantId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Line.Script(bot.AccessToken, FakeLineServer.BotInfo, LineBehavior.Unauthorized);
        var failed = await BodyJsonAsync(await TestConnectionAsync(org, assistantId));
        failed.GetProperty("state").GetString().ShouldBe("published");
        failed.GetProperty("servingState").GetString().ShouldBe("not-published");
        failed.GetProperty("channel").GetProperty("status").GetString().ShouldBe("needs-attention");

        Line.ResetScripts(bot.AccessToken);
        var passed = await BodyJsonAsync(await TestConnectionAsync(org, assistantId));
        passed.GetProperty("servingState").GetString().ShouldBe("serving");
    }

    [Fact]
    public async Task Testing_without_saved_settings_is_422_settings_and_calls_nothing()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var before = Line.Requests.Count;

        var response = await TestConnectionAsync(org, assistantId);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("line-test-refused");
        body.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(["settings"]);
        Line.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_token_that_no_longer_decrypts_fails_the_first_check_without_calling_line()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var bot = Line.AddBot("@anxin-demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            // A ciphertext protected for another purpose cannot be unprotected as an access token,
            // like one written with a key ring that is gone.
            var channel = await dbContext.AssistantLineChannels.SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
            var foreign = _host.Factory.Services.GetRequiredService<ISecretProtector>()
                .Protect("some.other-purpose", bot.AccessToken, _host.Clock.GetUtcNow());
            channel.TryApplySettings(
                    channel.OfficialAccountId, channel.ChannelId, null, foreign, channel.WelcomeMessage, channel.NonTextReply,
                    channel.Revision, _host.Clock.GetUtcNow())
                .ShouldBeTrue();
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var response = await TestConnectionAsync(org, assistantId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(response);
        AssertChecks(view, "failed", "skipped", "skipped");
        view.GetProperty("checks")[0].GetProperty("message").GetString().ShouldBe(LineConnectionTester.TokenUnreadableMessage);
        Line.RequestsWith(bot.AccessToken).ShouldBeEmpty();
    }

    [Fact]
    public async Task Settings_saved_while_line_is_being_called_make_the_test_409_and_nothing_is_stored()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var bot = Line.AddBot("@anxin-demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);

        Line.OnRequest = async request =>
        {
            if (request.Endpoint == FakeLineServer.TestWebhookEndpoint && request.Authorization == "Bearer " + bot.AccessToken)
            {
                // Another tab saves the settings (only the welcome message) meanwhile.
                await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
                var channel = await dbContext.AssistantLineChannels.SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
                channel.TryApplySettings(
                        channel.OfficialAccountId, channel.ChannelId, null, null, "另一個分頁的歡迎訊息", channel.NonTextReply,
                        channel.Revision, _host.Clock.GetUtcNow())
                    .ShouldBeTrue();
                await dbContext.SaveChangesAsync(CancellationToken);
            }
        };
        HttpResponseMessage response;
        try
        {
            response = await TestConnectionAsync(org, assistantId);
        }
        finally
        {
            Line.OnRequest = null;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("line-revision-conflict");
        var stored = await StoredChannelAsync(org, assistantId);
        stored.ConnectionChecks.ShouldBeEmpty();
        stored.BotUserId.ShouldBeNull();
        stored.WelcomeMessage.ShouldBe("另一個分頁的歡迎訊息");
    }

    // --- Enabling ---------------------------------------------------------------------------------

    [Fact]
    public async Task Enabling_needs_every_connection_check_passed_and_then_publishes()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);

        await AssertLinePublishRefusedAsync(org, assistantId, ["connection"], "never saved");

        var bot = Line.AddBot("@anxin-demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);
        await AssertLinePublishRefusedAsync(org, assistantId, ["connection"], "never tested");

        Line.ScriptWebhookTest(bot.AccessToken, success: false, statusCode: 401, reason: "ERROR_STATUS_CODE", detail: "401");
        await TestConnectionAsync(org, assistantId);
        var refused = await AssertLinePublishRefusedAsync(org, assistantId, ["connection"], "a failed test");
        refused.GetProperty("message").GetString().ShouldBe("目前還不能啟用 LINE 頻道，請先處理下列項目。");

        Line.ResetScripts(bot.AccessToken);
        await TestConnectionAsync(org, assistantId);
        var response = await PublishLineAsync(org, assistantId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var view = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(view, "LineChannelView");
        view.GetProperty("state").GetString().ShouldBe("published");
        view.GetProperty("servingState").GetString().ShouldBe("serving");
        view.GetProperty("channel").GetProperty("status").GetString().ShouldBe("published");
        view.GetProperty("publishedAt").ValueKind.ShouldBe(JsonValueKind.String);
        var channel = await StoredChannelAsync(org, assistantId);
        channel.State.ShouldBe(LineChannelState.Published);
        channel.PublishedByAccountId.ShouldBe(org.Admin.AccountId);

        // Enabling again changes nothing.
        var again = await BodyJsonAsync(await PublishLineAsync(org, assistantId));
        again.GetProperty("publishedAt").GetString().ShouldBe(view.GetProperty("publishedAt").GetString());
    }

    [Fact]
    public async Task Enabling_is_422_acceptance_while_acceptance_has_not_passed()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await CreateAssistantAsync(org, "沒有驗收的助理");
        await TestedChannelAsync(org, assistantId);

        await AssertLinePublishRefusedAsync(org, assistantId, ["acceptance"], "not accepted");
    }

    [Fact]
    public async Task Enabling_is_422_assistant_paused_while_the_assistant_is_paused()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        await TestedChannelAsync(org, assistantId);
        (await org.Admin.Spa.PutAsync($"{BasePath}/{assistantId}/publishing/platform/paused", org.Admin.Token, new { paused = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await AssertLinePublishRefusedAsync(org, assistantId, ["assistant-paused"], "assistant paused");
    }

    [Fact]
    public async Task Enabling_is_422_knowledge_ownership_naming_each_knowledge_base_that_is_not_the_owners()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        await TestedChannelAsync(org, assistantId);
        var colleague = await _host.CreateAccountAsync(
            org.Organization, "colleague", Password, AccountRole.SmbAdmin, "同仁", AllAdminPermissions);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var now = _host.Clock.GetUtcNow();
            var assistant = await dbContext.Assistants.SingleAsync(row => row.Id == assistantId, CancellationToken);
            var knowledgeBase = KnowledgeBase.Create(org.Organization.Id, colleague.Id, "同仁的知識庫", string.Empty, now);
            dbContext.KnowledgeBases.Add(knowledgeBase);
            dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var refused = await AssertLinePublishRefusedAsync(org, assistantId, ["knowledge-ownership"], "someone else's knowledge base");

        refused.GetProperty("errors").GetProperty("knowledge-ownership")[0].GetString()!.ShouldContain("同仁的知識庫");
    }

    [Fact]
    public async Task Without_a_public_base_url_testing_and_enabling_are_422_public_base_url()
    {
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        await TestedChannelAsync(org, assistantId);
        await using var factory = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("PublicChannels:PublicBaseUrl", ""));
        var caller = await SignInOnAsync(factory, org.Organization, "admin");
        var before = Line.Requests.Count;

        var test = await caller.Spa.PostAsync($"{LinePath(assistantId)}:test", caller.Token, new { });
        var publish = await caller.Spa.PostAsync($"{LinePath(assistantId)}:publish", caller.Token, new { });

        test.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var testBody = await BodyJsonAsync(test);
        testBody.GetProperty("reason").GetString().ShouldBe("line-test-refused");
        testBody.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(["public-base-url"]);
        publish.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var publishBody = await BodyJsonAsync(publish);
        publishBody.GetProperty("reason").GetString().ShouldBe("line-publish-refused");
        publishBody.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(["public-base-url"]);
        publishBody.GetProperty("errors").GetProperty("public-base-url")[0].GetString()!.ShouldContain("PublicChannels:PublicBaseUrl");
        Line.Requests.Count.ShouldBe(before);
        (await StoredChannelAsync(org, assistantId)).State.ShouldBe(LineChannelState.Draft);
    }

    // --- Logs -------------------------------------------------------------------------------------

    [Fact]
    public async Task No_log_line_ever_contains_the_access_token_or_the_channel_secret()
    {
        var log = new EverythingLog();
        await using var factory = _host.Factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(log);
            logging.AddFilter<EverythingLog>(null, LogLevel.Trace);
        }));
        var org = await CreateOrganizationAsync();
        var assistantId = await PassedAcceptanceAssistantAsync(org);
        var caller = await SignInOnAsync(factory, org.Organization, "admin");

        // Sentinels: values that appear nowhere else.
        var sentinelToken = "SENTINELtoken" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var sentinelSecret = Guid.NewGuid().ToString("N");
        var bot = new LineBot(sentinelToken, "U" + Guid.NewGuid().ToString("N"), "@anxin-demo", "安心農場");
        Line.AddBot(bot);

        var save = await caller.Spa.PutAsync(LinePath(assistantId), caller.Token, new
        {
            officialAccountId = "@anxin-demo",
            channelId = "1650000000",
            channelSecret = sentinelSecret,
            accessToken = sentinelToken,
            welcomeMessage = "您好！",
            nonTextReply = "只收文字",
            revision = 0,
        });
        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await caller.Spa.PostAsync($"{LinePath(assistantId)}:test", caller.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await caller.Spa.PostAsync($"{LinePath(assistantId)}:publish", caller.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var behavior in new[] { LineBehavior.Unauthorized, LineBehavior.TimeOut, LineBehavior.NetworkError })
        {
            Line.Script(sentinelToken, FakeLineServer.BotInfo, behavior);
            (await caller.Spa.PostAsync($"{LinePath(assistantId)}:test", caller.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var lines = log.Lines;
        lines.ShouldContain(line => line.Contains("LINE GET /v2/bot/info answered 200", StringComparison.Ordinal), "the capture works");
        lines.ShouldContain(line => line.Contains("LINE GET /v2/bot/info answered 401", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("LINE GET /v2/bot/info timed out", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            line.ShouldNotContain(sentinelToken);
            line.ShouldNotContain(sentinelToken[..24]);
            line.ShouldNotContain(sentinelToken[^24..]);
            line.ShouldNotContain(sentinelSecret);
            line.ShouldNotContain(sentinelSecret[..16]);
        }
    }

    // --- Helpers ----------------------------------------------------------------------------------

    /// <summary>The channel saved with the fake bot's token, every connection check passed through
    /// <c>:test</c>.</summary>
    private async Task<LineBot> TestedChannelAsync(TestOrganization org, Guid assistantId)
    {
        var bot = Line.AddBot("@anxin-demo");
        await SaveWithTokenAsync(org, assistantId, "@anxin-demo", bot.AccessToken);
        AssertChecks(await BodyJsonAsync(await TestConnectionAsync(org, assistantId)), "passed", "passed", "passed");
        return bot;
    }

    private async Task SaveWithTokenAsync(TestOrganization org, Guid assistantId, string officialAccountId, string accessToken, int revision = 0)
    {
        var response = await SaveAsync(org, assistantId, revision, officialAccountId, "1650000000", revision == 0 ? Secret1 : null, accessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static Task<HttpResponseMessage> TestConnectionAsync(TestOrganization org, Guid assistantId) =>
        org.Admin.Spa.PostAsync($"{LinePath(assistantId)}:test", org.Admin.Token, new { });

    private static Task<HttpResponseMessage> PublishLineAsync(TestOrganization org, Guid assistantId) =>
        org.Admin.Spa.PostAsync($"{LinePath(assistantId)}:publish", org.Admin.Token, new { });

    private async Task<JsonElement> AssertLinePublishRefusedAsync(TestOrganization org, Guid assistantId, string[] errorKeys, string because)
    {
        var response = await PublishLineAsync(org, assistantId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, because);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("line-publish-refused", because);
        body.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(errorKeys, because);
        (await StoredChannelStateAsync(org, assistantId)).ShouldNotBe(LineChannelState.Published, because);
        return body;
    }

    private async Task<AssistantLineChannel> StoredChannelAsync(TestOrganization org, Guid assistantId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
    }

    private async Task<LineChannelState?> StoredChannelStateAsync(TestOrganization org, Guid assistantId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.AssistantLineChannels.AsNoTracking()
            .Where(row => row.AssistantId == assistantId)
            .Select(row => (LineChannelState?)row.State)
            .SingleOrDefaultAsync(CancellationToken);
    }

    /// <summary>Signs in on another host over the same database (its own token and cookies).</summary>
    private static async Task<SignedIn> SignInOnAsync(WebApplicationFactory<Program> factory, Organization organization, string loginName)
    {
        var spa = new SpaClient(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = await spa.SignInAsync(organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken, Guid.Empty);
    }

    /// <summary>Every log entry of every category at every level: its message, its structured values
    /// and its exception.</summary>
    private sealed class EverythingLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<string> _lines;

            public Logger(string category, ConcurrentQueue<string> lines)
            {
                _category = category;
                _lines = lines;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                _lines.Enqueue($"{_category} scope: {Describe(state)}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _lines.Enqueue($"{_category} {logLevel}: {formatter(state, exception)} | {Describe(state)} | {exception}");

            private static string Describe<TState>(TState state) =>
                state is IEnumerable<KeyValuePair<string, object?>> values
                    ? string.Join("; ", values.Select(pair => $"{pair.Key}={pair.Value}"))
                    : state?.ToString() ?? string.Empty;
        }
    }
}
