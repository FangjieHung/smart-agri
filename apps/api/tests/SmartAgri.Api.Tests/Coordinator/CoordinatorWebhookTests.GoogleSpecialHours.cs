using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// Google 特殊營業時間 (store-coordinator design §5 Slice 7, #378) through <c>coordinator-shop auth-link</c>, the real
/// authorization endpoints, the real Google provider and executor, PostgreSQL, the webhook and the background jobs, with
/// <see cref="FakeGoogleServer"/> standing in for Google: connecting with one, several (see the authorization links'
/// tests) or no location; the day closed, cut short, opened late — from the shop's own weekly hours, a split day and an
/// overnight period included — with every other date's special hours written back byte for byte, an existing entry of
/// the day replaced, and a reopen removing only that day; a location without regular hours, a project not yet approved
/// (quota 0), a revoked authorization, rate limits and Google being unavailable. 「Now」 is Monday 2026-11-02 10:00 in
/// Taipei.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    private const string ChristmasClosed = """{"startDate": {"year": 2026, "month": 12, "day": 25}, "closed": true}""";

    private const string MidnightOpening = """{"startDate": {"year": 2026, "month": 11, "day": 20}, "openTime": {}, "closeTime": {"hours": 24}}""";

    [Fact]
    public async Task An_owner_with_one_Google_location_is_connected_at_once_and_only_the_refresh_token_is_stored_encrypted()
    {
        var shop = await _host.CreateOrganizationAsync("單店授權");
        var location = GoogleApi.AddLocation("王記麵店 大安店");
        var user = GoogleApi.AddUser([location]);
        using var browser = Browser();
        Consent consent;
        string code, done;
        _host.Log.Start();
        try
        {
            var issued = await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "google");
            issued.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, issued.Error);
            issued.Output.ShouldStartWith($"組織 {shop.Code}（單店授權）的Google 商家授權連結：");
            consent = await OpenLinkAsync(browser, TokenIn(issued.Output));
            code = GoogleApi.Code(user, consent.Query["code_challenge"]);
            done = await PageAsync(await CallbackAsync(browser, "google", consent.Nonce, ("state", consent.State), ("code", code)), HttpStatusCode.OK);
        }
        finally
        {
            _host.Log.Stop();
        }

        // Google's consent screen: the one scope, offline access, the consent prompt, PKCE S256 and our callback.
        consent.Location.GetLeftPart(UriPartial.Path).ShouldBe("https://accounts.google.com/o/oauth2/v2/auth");
        consent.Query.ShouldSatisfyAllConditions(
            query => query["client_id"].ShouldBe(FakeGoogleServer.ClientId),
            query => query["redirect_uri"].ShouldBe($"{PublicBase}/api/v1/coordinator/oauth/google/callback"),
            query => query["response_type"].ShouldBe("code"),
            query => query["scope"].ShouldBe("https://www.googleapis.com/auth/business.manage"),
            query => query["access_type"].ShouldBe("offline"),
            query => query["prompt"].ShouldBe("consent"),
            query => query["code_challenge_method"].ShouldBe("S256"),
            query => query.ShouldNotContainKey("client_secret"));

        // The code exchanged once with the verifier and the platform's client; the owner's one location connected.
        var token = GoogleApi.Requests.Where(request => request.Endpoint == FakeGoogleServer.Token && request.Form.GetValueOrDefault("code") == code)
            .ShouldHaveSingleItem();
        token.Form.ShouldSatisfyAllConditions(
            form => form["grant_type"].ShouldBe("authorization_code"),
            form => form["client_id"].ShouldBe(FakeGoogleServer.ClientId),
            form => form["client_secret"].ShouldBe(GoogleApi.ClientSecret),
            form => CoordinatorOAuthStates.Challenge(form["code_verifier"]).ShouldBe(consent.Query["code_challenge"]));
        var listed = GoogleApi.Requests.Where(request => request.Endpoint is FakeGoogleServer.Accounts or FakeGoogleServer.Locations
            && GoogleApi.AccessTokens.Contains(request.AccessToken!)).ToList();
        listed.Count(request => request.Subject == user.Accounts[0].Name).ShouldBe(1);
        listed.ShouldAllBe(request => request.ErrorFormatVersion == "2");
        done.ShouldContain("<h1>授權完成</h1>");
        done.ShouldContain("單店授權 已連結Google 商家：王記麵店 大安店。");

        var connection = (await ConnectionOfAsync(shop, CoordinatorService.Google)).ShouldNotBeNull();
        connection.ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorConnectionStatus.Active),
            row => row.ExternalId.ShouldBe(location.Name),
            row => row.DisplayName.ShouldBe("王記麵店 大安店"),
            row => row.Secret.Ciphertext.ShouldNotContain(user.RefreshToken));
        _host.Factory.Services.GetRequiredService<ISecretProtector>()
            .Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Google), connection.Secret)
            .ShouldBe(user.RefreshToken);

        var forbidden = new[] { user.RefreshToken, code, token.Form["code_verifier"], GoogleApi.ClientSecret, consent.Token, consent.State, consent.Nonce }
            .Concat(GoogleApi.AccessTokens)
            .ToList();
        forbidden.ShouldAllBe(value => !done.Contains(value, StringComparison.Ordinal));
        var logged = ProductionLogLines();
        logged.ShouldContain(line => line.Contains("Google accounts.list answered 200", StringComparison.Ordinal));
        logged.ShouldContain(line => line.Contains("connected google", StringComparison.Ordinal));
        logged.ShouldAllBe(line => forbidden.All(value => !line.Contains(value, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task No_location_no_API_access_or_the_permission_unticked_each_get_their_own_page_and_nothing_is_stored()
    {
        var shop = await _host.CreateOrganizationAsync("無地點店");
        using var browser = Browser();
        var nobody = GoogleApi.AddUser();
        var someone = GoogleApi.AddUser([GoogleApi.AddLocation("有地點")]);

        var none = await AuthorizeGoogleAsync(browser, shop, nobody);
        GoogleApi.Script(FakeGoogleServer.Accounts, FakeGoogleServer.AnySubject, FakeGoogleServer.QuotaZero("mybusinessaccountmanagement.googleapis.com"));
        var pending = await AuthorizeGoogleAsync(browser, shop, someone);
        var unticked = await AuthorizeGoogleAsync(browser, shop, someone, scope: "openid");
        var noRefresh = await AuthorizeGoogleAsync(browser, shop, someone, withRefreshToken: false);

        (await PageAsync(none, HttpStatusCode.BadRequest)).ShouldSatisfyAllConditions(
            page => page.ShouldContain("這個 Google 帳號沒有可管理的商家"),
            page => page.ShouldContain("（代碼：no-locations）"));
        (await PageAsync(pending, HttpStatusCode.BadRequest)).ShouldSatisfyAllConditions(
            page => page.ShouldContain(CoordinatorAuthorizationPages.GoogleAccessPendingMessage),
            page => page.ShouldContain("（代碼：google-access-pending）"));
        (await PageAsync(unticked, HttpStatusCode.BadRequest)).ShouldContain(CoordinatorAuthorizationPages.ScopeNotGrantedMessage);
        (await PageAsync(noRefresh, HttpStatusCode.BadRequest)).ShouldSatisfyAllConditions(
            page => page.ShouldContain(CoordinatorAuthorizationPages.FailedMessage),
            page => page.ShouldContain("（代碼：no-refresh-token）"));
        (await ConnectionOfAsync(shop, CoordinatorService.Google)).ShouldBeNull();
        (await ConnectionActivitiesAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_closure_closes_the_day_on_Google_keeping_other_dates_byte_for_byte_and_a_reopen_removes_only_that_day()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("谷歌休息店", GoogleShopFile("谷歌休息店"));
        var location = GoogleApi.AddLocation(
            "谷歌休息店",
            specialHours: $$$"""{"specialHourPeriods": [{{{ChristmasClosed}}}, {"startDate": {"year": 2026, "month": 11, "day": 15}, "openTime": {"hours": 9}, "endDate": {"year": 2026, "month": 11, "day": 15}, "closeTime": {"hours": 12}}, {{{MidnightOpening}}}]}""");
        var user = GoogleApi.AddUser([location]);
        await ConnectGoogleAsync(shop, user);

        var card = Card(await SendForMessageAsync(owner, Scripted("11/15 店休", "temporary-closure", "2026-11-15")));
        card.Body.ShouldContain("・Google 特殊營業時間：11/15（日） 休息（自動）");
        (await PressConfirmAsync(owner, card.Button("確認"))).ShouldStartWith("已確認：");
        await RunJobsAsync();

        Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem()).Body
            .ShouldBe(["執行結果：", "✅ Google 特殊營業時間：11/15（日） 休息"]);
        location.SpecialHourPeriods().ShouldBe([ChristmasClosed, MidnightOpening, Closed(2026, 11, 15)], "other dates as Google sent them; the day's old entry replaced");
        var patch = location.Patches.ShouldHaveSingleItem();
        GoogleApi.RequestsFor(FakeGoogleServer.PatchLocation, location).ShouldHaveSingleItem().Query["updateMask"].ShouldBe("specialHours");
        GoogleApi.RequestsFor(FakeGoogleServer.GetLocation, location).ShouldHaveSingleItem().Query["readMask"].ShouldContain("specialHours");
        patch.ShouldContain(ChristmasClosed);
        var applied = (await ActionsAsync(shop)).ShouldHaveSingleItem();
        (applied.Status, applied.ExternalRef).ShouldBe((CoordinatorActionStatus.Succeeded, $"2026-11-15|{location.Name}"));

        // Reopen: the day's entry removed through the executor; the others untouched.
        var reopen = Card(await SendForMessageAsync(owner, Scripted("11/15 恢復營業", "reopen", "2026-11-15")));
        reopen.Body.ShouldContain("・Google 特殊營業時間：移除 11/15（日） 的設定（自動）");
        await PostbackForMessagesAsync(owner, reopen.Button("確認"));
        await RunJobsAsync();
        Report(PushedMessages(Pushes(owner)[1]).ShouldHaveSingleItem()).Body.ShouldContain("✅ Google 特殊營業時間：移除 11/15（日） 的設定");
        location.SpecialHourPeriods().ShouldBe([ChristmasClosed, MidnightOpening]);

        // A second closure and its undo when the owner already removed the day by hand: nothing to take back.
        await ConfirmAsync(owner, Scripted("11/16 店休", "temporary-closure", "2026-11-16"));
        await RunJobsAsync();
        location.SpecialHourPeriods().ShouldBe([ChristmasClosed, MidnightOpening, Closed(2026, 11, 16)]);
        location.SpecialHours = $$"""{"specialHourPeriods": [{{ChristmasClosed}}]}""";
        var patches = location.Patches.Count;
        await PostbackForMessagesAsync(owner, Card(await SendForMessageAsync(owner, Scripted("11/16 恢復營業", "reopen", "2026-11-16"))).Button("確認"));
        await RunJobsAsync();
        location.Patches.Count.ShouldBe(patches, "nothing to remove, nothing written");
        (await ActionsAsync(shop)).Last().Status.ShouldBe(CoordinatorActionStatus.Succeeded);

        GoogleApi.RequestsFor(FakeGoogleServer.PatchLocation, location).ShouldAllBe(request => request.AccessToken != null && request.ErrorFormatVersion == "2");
        var refreshes = GoogleApi.Requests.Where(request => request.Endpoint == FakeGoogleServer.Token && request.Subject == user.RefreshToken).ToList();
        refreshes.Count.ShouldBe(4, "a fresh access token for each run");
        refreshes.ShouldAllBe(request => request.Form["grant_type"] == "refresh_token" && request.Form["client_secret"] == GoogleApi.ClientSecret);
    }

    [Fact]
    public async Task Close_early_and_open_late_write_the_shops_own_periods_cut_at_the_new_time_split_days_and_overnight_ones_included()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("谷歌時段店", GoogleShopFile("谷歌時段店"));
        var location = GoogleApi.AddLocation("谷歌時段店", specialHours: $$"""{"specialHourPeriods": [{{ChristmasClosed}}]}""");
        await ConnectGoogleAsync(shop, GoogleApi.AddUser([location]));

        // Monday: lunch kept, dinner cut at 19:00 (Google's own regular hours, 11:00–21:00, are not what counts: Q22).
        var card = Card(await SendForMessageAsync(owner, Scripted("今天 19 點打烊", "close-early", "2026-11-02", time: "19:00")));
        card.Body.ShouldContain("・Google 特殊營業時間：11/2（一） 19:00 打烊（自動）");
        (await PressConfirmAsync(owner, card.Button("確認"))).ShouldStartWith("已確認：");
        await RunJobsAsync();
        Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem()).Body
            .ShouldBe(["執行結果：", "✅ Google 特殊營業時間：11/2（一） 19:00 打烊"]);

        // Tuesday: opening at 12:00 shortens lunch; dinner as usual. Saturday: an overnight dinner opening late ends on
        // Sunday at 01:00; another Saturday cut in its small hours ends on Sunday at 00:30.
        await ConfirmAsync(owner, Scripted("明天 12 點開", "open-late", "2026-11-03", time: "12:00"));
        await ConfirmAsync(owner, Scripted("週六晚點開", "open-late", "2026-11-07", time: "18:00"));
        await ConfirmAsync(owner, Scripted("11/14 半夜提早收", "close-early", "2026-11-14", time: "00:30"));
        await RunJobsAsync();

        location.SpecialHourPeriods().ShouldBe(
        [
            ChristmasClosed,
            Open(2026, 11, 2, "11:00", "14:00"), Open(2026, 11, 2, "17:00", "19:00"),
            Open(2026, 11, 3, "12:00", "14:00"), Open(2026, 11, 3, "17:00", "21:00"),
            Open(2026, 11, 7, "18:00", "01:00", nextDay: true),
            Open(2026, 11, 14, "11:00", "14:00"), Open(2026, 11, 14, "17:00", "00:30", nextDay: true),
        ]);
        location.Patches.Count.ShouldBe(4);
        (await ActionsAsync(shop)).ShouldAllBe(action => action.Status == CoordinatorActionStatus.Succeeded);

        // The same command applied again (a repeated job, 〔重試〕) writes the same.
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<CoordinatorGoogleSpecialHoursExecutor>();
        var settings = (await ShopSettingsAsync(shop)).ShouldNotBeNull().Document;
        (await executor.ExecuteAsync(
            new CoordinatorActionRequest(shop.Id, Guid.CreateVersion7(), ShopActionKind.GoogleSpecialHours, CoordinatorActionStep.Apply,
                ShopScenario.OpenLate, new DateOnly(2026, 11, 7), new TimeOnly(18, 0), null, null, null, settings),
            CancellationToken)).Outcome.ShouldBe(CoordinatorActionOutcome.Succeeded);
        location.SpecialHourPeriods().Count(period => period.Contains("\"day\":7", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_location_without_regular_hours_fails_with_the_reason_and_a_retry_works_once_they_are_set()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("無營業時間店", GoogleShopFile("無營業時間店"));
        var location = GoogleApi.AddLocation("無營業時間店", regularHours: null);
        await ConnectGoogleAsync(shop, GoogleApi.AddUser([location]));

        await ConfirmAsync(owner, Scripted("今天店休", "temporary-closure", "2026-11-02"));
        await RunJobsAsync();

        var report = Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem());
        report.Body.ShouldBe(["執行結果：", "❌ Google 特殊營業時間：11/2（一） 休息：Google 商家沒有設定一般營業時間，無法設定特殊營業時間"]);
        location.Patches.ShouldBeEmpty("nothing Google would refuse is sent");
        var row = (await ActionsAsync(shop)).ShouldHaveSingleItem();
        (row.Status, row.FailureCode).ShouldBe((CoordinatorActionStatus.Failed, CoordinatorFailureCodes.GoogleNoRegularHours));

        // Google refusing it for that reason after all (regular hours removed meanwhile) reads the same.
        var reason = await RunGoogleAsync(shop, location, FakeGoogleServer.PatchLocation, GoogleBehavior.Answer(
            HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"Request contains an invalid argument.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SPECIAL_HOURS_SET_WITHOUT_REGULAR_HOURS","domain":"mybusinessbusinessinformation.googleapis.com"}]}}"""),
            regularHours: FakeGoogleServer.WeekdayHours);
        (reason.Outcome, reason.FailureCode).ShouldBe((CoordinatorActionOutcome.Failed, CoordinatorFailureCodes.GoogleNoRegularHours));

        // The owner sets them in Google and presses 〔重試〕.
        location.RegularHours = FakeGoogleServer.WeekdayHours;
        var retried = "rt-google-retry-" + Guid.NewGuid().ToString("N");
        await PressSilentlyAsync(owner, report.Button("重試"), retried);
        await RunJobsAsync();
        Report((await ReplyMessagesAsync(retried)).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "✅ Google 特殊營業時間：11/2（一） 休息"]);
        location.SpecialHourPeriods().ShouldBe([Closed(2026, 11, 2)]);
    }

    [Fact]
    public async Task Before_Google_approves_API_access_the_action_becomes_a_reminder_that_says_why()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("待審核店", GoogleShopFile("待審核店"));
        var location = GoogleApi.AddLocation("待審核店");
        await ConnectGoogleAsync(shop, GoogleApi.AddUser([location]));
        GoogleApi.Script(FakeGoogleServer.GetLocation, location.Name, FakeGoogleServer.QuotaZero());

        await ConfirmAsync(owner, Scripted("今天店休", "temporary-closure", "2026-11-02"));
        await RunJobsAsync();

        var report = Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem());
        report.Body.ShouldBe(
        [
            "執行結果：",
            "⏳ Google 特殊營業時間：11/2（一） 休息",
            "　Google 商家 API 尚未開通，先以提醒處理",
            "　Google 設為休息。",
        ]);
        report.Buttons.Select(button => button.Label).ShouldBe(["已完成"]);
        var row = (await ActionsAsync(shop)).ShouldHaveSingleItem();
        (row.Status, row.FailureCode).ShouldBe((CoordinatorActionStatus.AwaitingManual, CoordinatorFailureCodes.GoogleAccessPending));
        (await ConnectionOfAsync(shop, CoordinatorService.Google)).ShouldNotBeNull().Status.ShouldBe(CoordinatorConnectionStatus.Active, "the authorization is fine");
        ReauthorizationPushes(owner).ShouldBeEmpty();
        location.Patches.ShouldBeEmpty();

        // The API not enabled in the project (403 SERVICE_DISABLED) reads the same.
        var disabled = await RunGoogleAsync(shop, location, FakeGoogleServer.GetLocation, GoogleBehavior.Answer(
            HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"My Business Business Information API has not been used in project 123456789012 before or it is disabled.","status":"PERMISSION_DENIED","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SERVICE_DISABLED","domain":"googleapis.com","metadata":{"consumer":"projects/123456789012","service":"mybusinessbusinessinformation.googleapis.com"}}]}}"""));
        (disabled.Outcome, disabled.FailureCode).ShouldBe((CoordinatorActionOutcome.Unavailable, CoordinatorFailureCodes.GoogleAccessPending));
    }

    [Fact]
    public async Task A_revoked_Google_authorization_becomes_a_reminder_and_asks_the_owners_to_authorize_again()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("撤銷授權店", GoogleShopFile("撤銷授權店"));
        var location = GoogleApi.AddLocation("撤銷授權店");
        var user = GoogleApi.AddUser([location]);
        await ConnectGoogleAsync(shop, user);
        GoogleApi.Revoke(user);

        await ConfirmAsync(owner, Scripted("今天店休", "temporary-closure", "2026-11-02"));
        await RunJobsAsync();

        var connection = (await ConnectionOfAsync(shop, CoordinatorService.Google)).ShouldNotBeNull();
        (connection.Status, connection.ReauthorizationRequestedAt.HasValue).ShouldBe((CoordinatorConnectionStatus.Revoked, true));
        ReauthorizationPushes(owner).ShouldHaveSingleItem()
            .ShouldStartWith($"撤銷授權店 的 Google 商家 授權已失效，相關動作先改為提醒。請點此重新授權：{PublicBase}/api/v1/coordinator/connect?token=");
        var followUp = Pushes(owner).Select(PushedMessages).First(messages => messages[0].GetProperty("type").GetString() == "flex");
        Report(followUp[0]).Body.ShouldContain("　Google 商家授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權");
        var row = (await ActionsAsync(shop)).ShouldHaveSingleItem();
        (row.Status, row.FailureCode).ShouldBe((CoordinatorActionStatus.AwaitingManual, CoordinatorFailureCodes.TokenRevoked));
        GoogleApi.Requests.Where(request => request.Endpoint == FakeGoogleServer.Token && request.Subject == user.RefreshToken)
            .ShouldHaveSingleItem("refused at the refresh");
        location.Patches.ShouldBeEmpty();

        // From now on the card says 提醒 (no active connection), until the owner authorizes again.
        Card(await SendForMessageAsync(owner, Scripted("明天店休", "temporary-closure", "2026-11-03"))).Body
            .ShouldContain("・Google 特殊營業時間：11/3（二） 休息（提醒）");

        // A 401 on the location (the access token refused) also means authorizing again.
        await ConnectGoogleAsync(shop, user);
        var refused = await RunGoogleAsync(shop, location, FakeGoogleServer.GetLocation, FakeGoogleServer.Unauthenticated());
        (refused.Outcome, refused.FailureCode).ShouldBe((CoordinatorActionOutcome.Unavailable, CoordinatorFailureCodes.TokenRevoked));
    }

    [Fact]
    public async Task Rate_limits_and_Google_being_unavailable_fail_with_their_reason_and_a_retry_applies_it()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("忙碌谷歌店", GoogleShopFile("忙碌谷歌店"));
        var location = GoogleApi.AddLocation("忙碌谷歌店");
        var user = GoogleApi.AddUser([location]);
        await ConnectGoogleAsync(shop, user);
        GoogleApi.Script(FakeGoogleServer.PatchLocation, location.Name, FakeGoogleServer.RateLimited());

        await ConfirmAsync(owner, Scripted("今天店休", "temporary-closure", "2026-11-02"));
        await RunJobsAsync();
        var report = Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem());
        report.Body.ShouldBe(["執行結果：", "❌ Google 特殊營業時間：11/2（一） 休息：服務忙碌中"]);
        location.SpecialHourPeriods().ShouldBeEmpty();

        // 〔重試〕: Google is unavailable this time (503), then it works.
        GoogleApi.Script(FakeGoogleServer.GetLocation, location.Name, FakeGoogleServer.Unavailable());
        var unavailable = "rt-google-503-" + Guid.NewGuid().ToString("N");
        await PressSilentlyAsync(owner, report.Button("重試"), unavailable);
        await RunJobsAsync();
        Report((await ReplyMessagesAsync(unavailable)).ShouldHaveSingleItem()).Body
            .ShouldBe(["執行結果：", "❌ Google 特殊營業時間：11/2（一） 休息：Google 暫時無法使用"]);

        var applied = "rt-google-ok-" + Guid.NewGuid().ToString("N");
        await PressSilentlyAsync(owner, report.Button("重試"), applied);
        await RunJobsAsync();
        Report((await ReplyMessagesAsync(applied)).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "✅ Google 特殊營業時間：11/2（一） 休息"]);
        location.SpecialHourPeriods().ShouldBe([Closed(2026, 11, 2)]);
        (await ActionsAsync(shop)).ShouldHaveSingleItem().Attempts.ShouldBe(3);

        // No answer, no connection, a refresh Google cannot serve: Google is unavailable; any other refusal is 「拒絕」.
        foreach (var (endpoint, behavior, expected) in new (string, GoogleBehavior, string)[]
                 {
                     (FakeGoogleServer.PatchLocation, GoogleBehavior.TimeOut(), CoordinatorFailureCodes.GoogleUnavailable),
                     (FakeGoogleServer.GetLocation, GoogleBehavior.NetworkError(), CoordinatorFailureCodes.GoogleUnavailable),
                     (FakeGoogleServer.Token, FakeGoogleServer.Unavailable(), CoordinatorFailureCodes.GoogleUnavailable),
                     (FakeGoogleServer.PatchLocation, GoogleBehavior.Answer(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"Request contains an invalid argument.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"OVERLAPPED_SPECIAL_HOURS","domain":"mybusinessbusinessinformation.googleapis.com"}]}}"""), CoordinatorFailureCodes.Rejected),
                     (FakeGoogleServer.GetLocation, GoogleBehavior.Answer(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND"}}"""), CoordinatorFailureCodes.Rejected),
                 })
        {
            var result = await RunGoogleAsync(shop, location, endpoint, behavior, subject: endpoint == FakeGoogleServer.Token ? user.RefreshToken : null);
            (result.Outcome, result.FailureCode).ShouldBe((CoordinatorActionOutcome.Failed, expected), endpoint);
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary>A shop of <see cref="CommandShopFile"/>'s hours named <paramref name="name"/> whose closure, early closing
    /// and late opening each set Google 特殊營業時間 automatically, and nothing else.</summary>
    private static string GoogleShopFile(string name)
    {
        var tree = JsonNode.Parse(CommandShopFile)!.AsObject();
        tree["storeName"] = name;
        foreach (var scenario in new[] { "temporary-closure", "close-early", "open-late" })
        {
            tree["scenarios"]![scenario]!["actions"] = new JsonArray(new JsonObject
            {
                ["kind"] = "google-special-hours",
                ["mode"] = "auto",
                ["steps"] = scenario == "temporary-closure" ? "Google 設為休息。" : "Google 改時間。",
            });
        }

        return tree.ToJsonString();
    }

    /// <summary>Authorizes Google for <paramref name="shop"/> as <paramref name="user"/> (one location), all the way.</summary>
    private async Task ConnectGoogleAsync(Organization shop, GoogleUser user)
    {
        using var browser = Browser();
        var done = await PageAsync(await AuthorizeGoogleAsync(browser, shop, user), HttpStatusCode.OK);
        done.ShouldContain("<h1>授權完成</h1>");
    }

    /// <summary>A new link of <paramref name="shop"/>, opened, consented to as <paramref name="user"/>: the callback's answer.</summary>
    private async Task<HttpResponseMessage> AuthorizeGoogleAsync(
        HttpClient browser, Organization shop, GoogleUser user, string scope = CoordinatorGoogleClient.Scope, bool withRefreshToken = true)
    {
        var issued = await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "google");
        issued.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, issued.Error);
        var consent = await OpenLinkAsync(browser, TokenIn(issued.Output));
        var code = GoogleApi.Code(user, consent.Query["code_challenge"], scope, withRefreshToken);
        return await CallbackAsync(browser, "google", consent.Nonce, ("state", consent.State), ("code", code), ("scope", scope));
    }

    /// <summary>The real executor closing 11/30 for <paramref name="shop"/> once, with the next call of
    /// <paramref name="endpoint"/> answering <paramref name="behavior"/> (and <paramref name="regularHours"/> set first, when
    /// given).</summary>
    private async Task<CoordinatorActionResult> RunGoogleAsync(
        Organization shop, FakeLocation location, string endpoint, GoogleBehavior behavior, string? subject = null, string? regularHours = null)
    {
        if (regularHours is not null)
        {
            location.RegularHours = regularHours;
        }

        GoogleApi.Script(endpoint, subject ?? location.Name, behavior);
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var settings = (await ShopSettingsAsync(shop)).ShouldNotBeNull().Document;
        var result = await scope.ServiceProvider.GetRequiredService<CoordinatorGoogleSpecialHoursExecutor>().ExecuteAsync(
            new CoordinatorActionRequest(shop.Id, Guid.CreateVersion7(), ShopActionKind.GoogleSpecialHours, CoordinatorActionStep.Apply,
                ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 30), null, null, null, null, settings),
            CancellationToken);
        if (regularHours is not null)
        {
            location.RegularHours = null;
        }

        return result;
    }

    private static string Closed(int year, int month, int day) =>
        $$"""{"startDate":{"year":{{year}},"month":{{month}},"day":{{day}}},"closed":true}""";

    /// <summary>A period as the executor writes it: open from <paramref name="open"/> to <paramref name="close"/>, the
    /// same day or the next.</summary>
    private static string Open(int year, int month, int day, string open, string close, bool nextDay = false)
    {
        var start = new DateOnly(year, month, day);
        var end = nextDay ? start.AddDays(1) : start;
        return $$"""{"startDate":{{Date(start)}},"openTime":{{Time(open)}},"endDate":{{Date(end)}},"closeTime":{{Time(close)}}}""";

        static string Date(DateOnly date) => $$"""{"year":{{date.Year}},"month":{{date.Month}},"day":{{date.Day}}}""";

        static string Time(string clock) => $$"""{"hours":{{int.Parse(clock[..2], System.Globalization.CultureInfo.InvariantCulture)}},"minutes":{{int.Parse(clock[3..], System.Globalization.CultureInfo.InvariantCulture)}}}""";
    }
}
