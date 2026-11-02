using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// FB 粉專貼文 (store-coordinator design §3 Q3, Q13, Q18, Q21, §5 Slice 8, #379) through the real webhook, PostgreSQL,
/// background jobs, authorization links and the real <see cref="CoordinatorFacebookOAuthProvider"/> and
/// <see cref="CoordinatorFacebookPostExecutor"/>, with the host's fake Graph API (<see cref="FakeGraphServer"/>, which
/// refuses any call without the right <c>appsecret_proof</c>): connecting one, several or no postable page; a photo post
/// and a text post; the post id kept at once so a run again never posts twice; a reopen deleting the post (one gone
/// already counts); a refused token becoming a reminder with a re-authorization link; a missing permission a reminder;
/// throttling, outages, a bad image and a timeout failing for 〔重試〕; and no token, secret or proof in any log.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    private FakeGraphServer Graph => _host.Graph;

    private static string FacebookCallback => $"{PublicBase}/api/v1/coordinator/oauth/facebook/callback";

    [Fact]
    public async Task The_real_FB_provider_connects_the_one_postable_page_with_its_long_lived_token()
    {
        var shop = await _host.CreateOrganizationAsync("真粉專店");
        var postable = Graph.NewPage("真粉專店粉專", FakeGraphServer.AdminTasks);
        var analyst = Graph.NewPage("只能看數據的粉專", "ANALYZE", "ADVERTISE");
        var user = Graph.AddUser(FacebookCallback, postable, analyst);
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        using var browser = Browser();
        Consent consent;
        string done;
        FacebookOAuth.ForwardTo = RealFacebookProvider(scope);
        _host.Log.Start();
        try
        {
            var issued = await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "facebook");
            issued.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, issued.Error);
            consent = await OpenLinkAsync(browser, TokenIn(issued.Output));
            done = await PageAsync(
                await CallbackAsync(browser, "facebook", consent.Nonce, ("code", user.Code), ("state", consent.State)), HttpStatusCode.OK);
        }
        finally
        {
            FacebookOAuth.ForwardTo = null;
            _host.Log.Stop();
        }

        // Facebook Login's dialog of the pinned version: the three page permissions, a code, no PKCE.
        consent.Location.GetLeftPart(UriPartial.Path).ShouldBe("https://www.facebook.com/v26.0/dialog/oauth");
        consent.Query.Keys.ShouldBe(["client_id", "redirect_uri", "state", "response_type", "scope"], ignoreOrder: true);
        consent.Query["client_id"].ShouldBe(Graph.AppId);
        consent.Query["redirect_uri"].ShouldBe(FacebookCallback);
        consent.Query["response_type"].ShouldBe("code");
        consent.Query["scope"].ShouldBe("pages_show_list,pages_manage_posts,pages_read_engagement");

        // The only page it may post on: connected with that page's token.
        done.ShouldContain("真粉專店 已連結FB 粉專：真粉專店粉專。");
        var connection = (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldNotBeNull();
        (connection.ExternalId, connection.DisplayName, connection.Status).ShouldBe((postable.Id, "真粉專店粉專", CoordinatorConnectionStatus.Active));
        _host.Factory.Services.GetRequiredService<ISecretProtector>()
            .Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Facebook), connection.Secret)
            .ShouldBe(postable.AccessToken);

        // Code → user token (with the app's secret and the same redirect URI) → long-lived token → its pages, with the proof.
        var exchange = Graph.Requests.Single(request => request.Parameter("code") == user.Code);
        (exchange.Endpoint, exchange.Parameter("client_id"), exchange.Parameter("client_secret"), exchange.Parameter("redirect_uri"))
            .ShouldBe((FakeGraphServer.AccessTokenEndpoint, Graph.AppId, Graph.AppSecret, FacebookCallback));
        var longLived = Graph.RequestsWith(user.ShortToken).ShouldHaveSingleItem();
        (longLived.Endpoint, longLived.Parameter("grant_type")).ShouldBe((FakeGraphServer.AccessTokenEndpoint, "fb_exchange_token"));
        var accounts = Graph.RequestsWith(user.LongToken).ShouldHaveSingleItem();
        accounts.Endpoint.ShouldBe(FakeGraphServer.AccountsEndpoint);
        accounts.Parameter("appsecret_proof").ShouldBe(Graph.ProofOf(user.LongToken));
        accounts.Parameter("fields").ShouldBe("id,name,access_token,tasks");

        // No code, token, secret or proof in the page or a log line.
        var forbidden = new[]
        {
            user.Code, user.ShortToken, user.LongToken, postable.AccessToken, analyst.AccessToken, Graph.AppSecret, Graph.ProofOf(user.LongToken),
        };
        forbidden.ShouldAllBe(value => !done.Contains(value, StringComparison.Ordinal));
        var logged = ProductionLogLines();
        logged.ShouldContain(line => line.Contains("Facebook Graph GET /me/accounts answered 200", StringComparison.Ordinal));
        logged.ShouldAllBe(line => forbidden.All(value => !line.Contains(value, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task With_several_postable_pages_the_owner_chooses_one_and_without_any_the_page_says_so()
    {
        var shop = await _host.CreateOrganizationAsync("多粉專店");
        var taipei = Graph.NewPage("台北店粉專", "CREATE_CONTENT", "MODERATE");
        var kaohsiung = Graph.NewPage("高雄店粉專", FakeGraphServer.AdminTasks);
        var moderator = Graph.NewPage("只能審核的粉專", "MODERATE");
        var user = Graph.AddUser(FacebookCallback, taipei, kaohsiung, moderator);
        var other = await _host.CreateOrganizationAsync("沒粉專店");
        var lonely = Graph.AddUser(FacebookCallback, Graph.NewPage("只能分析的粉專", "ANALYZE"));
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        using var browser = Browser();
        string form, done, none;
        FacebookOAuth.ForwardTo = RealFacebookProvider(scope);
        try
        {
            var consent = await OpenLinkAsync(browser, TokenIn((await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "facebook")).Output));
            form = await PageAsync(
                await CallbackAsync(browser, "facebook", consent.Nonce, ("code", user.Code), ("state", consent.State)), HttpStatusCode.OK);
            done = await PageAsync(
                await ChooseAsync(browser, "facebook", consent.Nonce, FormState(form), kaohsiung.Id), HttpStatusCode.OK);

            var empty = await OpenLinkAsync(browser, TokenIn((await RunShopAsync("auth-link", "--organization", other.Code, "--service", "facebook")).Output));
            none = await PageAsync(
                await CallbackAsync(browser, "facebook", empty.Nonce, ("code", lonely.Code), ("state", empty.State)), HttpStatusCode.BadRequest);
        }
        finally
        {
            FacebookOAuth.ForwardTo = null;
        }

        // Only the pages it may post on are offered; no token in the form.
        form.ShouldContain("你的帳號有 2 個粉專");
        ChoicePattern().Matches(form).Select(match => match.Groups["id"].Value).ShouldBe([taipei.Id, kaohsiung.Id]);
        form.ShouldContain("台北店粉專");
        form.ShouldNotContain("只能審核的粉專");
        new[] { taipei, kaohsiung, moderator }.ShouldAllBe(page => !form.Contains(page.AccessToken, StringComparison.Ordinal));

        // The chosen one is connected with its own token.
        done.ShouldContain("多粉專店 已連結FB 粉專：高雄店粉專。");
        var connection = (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldNotBeNull();
        connection.ExternalId.ShouldBe(kaohsiung.Id);
        _host.Factory.Services.GetRequiredService<ISecretProtector>()
            .Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Facebook), connection.Secret)
            .ShouldBe(kaohsiung.AccessToken);

        // An account without a page it can post on: told so, nothing connected.
        none.ShouldContain("這個 Facebook 帳號沒有可以發文的粉專");
        none.ShouldContain(CoordinatorAuthorizationPages.NoFacebookPagesMessage);
        none.ShouldContain($"（代碼：{CoordinatorFacebookOAuthProvider.NoPagesCode}）");
        (await ConnectionOfAsync(other, CoordinatorService.Facebook)).ShouldBeNull();
    }

    [Fact]
    public async Task An_automatic_FB_post_is_a_photo_post_whose_id_is_kept_at_once_and_never_posted_twice()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("粉專店", FacebookShop("粉專店"));

        // Not connected: 提醒 on the card.
        Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"))).Body[0].ShouldBe("・FB 粉專貼文（提醒）");

        var page = await ConnectPageAsync(shop, "粉專店的粉專");
        CoordinatorActionResult direct;
        CoordinatorCommandAction kept;
        _host.Log.Start();
        try
        {
            var card = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
            card.Body[0].ShouldBe("・FB 粉專貼文（自動）");
            Report((await PostbackForMessagesAsync(owner, card.Button("確認")))[0]).Body.ShouldBe(["已確認，逐項結果：", "⌛ FB 粉專貼文：執行中…"]);

            // The executor posts, then the process stops before the job records anything: the post id is on the row already.
            direct = await RunFacebookExecutorAsync(shop, (await ActionsAsync(shop)).ShouldHaveSingleItem());
            kept = (await ActionsAsync(shop)).ShouldHaveSingleItem();

            // The job runs it again: a success without a second post.
            await RunJobsAsync();
        }
        finally
        {
            _host.Log.Stop();
        }

        var row = (await ActionsAsync(shop)).ShouldHaveSingleItem();
        var command = (await CommandsAsync(shop)).Single(candidate => candidate.Id == row.CommandId);
        var post = Graph.PostsOf(page.Id).ShouldHaveSingleItem();
        (post.ImageUrl, post.Text).ShouldBe((ClosedImage, command.AnnouncementText));
        direct.ShouldSatisfyAllConditions(
            result => result.Outcome.ShouldBe(CoordinatorActionOutcome.Succeeded),
            result => result.ExternalRef.ShouldBe(post.Id));
        (kept.Status, kept.ExternalRef).ShouldBe((CoordinatorActionStatus.Scheduled, post.Id), "kept before the try is recorded");

        // POST /{page-id}/photos with url and caption (not the deprecated message), the page token and its proof.
        var photo = Graph.RequestsWith(page.AccessToken).ShouldHaveSingleItem();
        (photo.Endpoint, photo.Path).ShouldBe((FakeGraphServer.PhotosEndpoint, $"/v26.0/{page.Id}/photos"));
        photo.Form.Keys.ShouldBe(["url", "caption", "access_token", "appsecret_proof"], ignoreOrder: true);
        photo.Query.ShouldBeEmpty("the token goes in the body of a POST");
        (photo.Form["url"], photo.Form["caption"]).ShouldBe((ClosedImage, command.AnnouncementText));
        photo.Form["appsecret_proof"].ShouldBe(Graph.ProofOf(page.AccessToken));

        row.ShouldSatisfyAllConditions(
            action => action.Status.ShouldBe(CoordinatorActionStatus.Succeeded),
            action => action.Mode.ShouldBe(ShopActionMode.Auto),
            action => action.ExternalRef.ShouldBe(post.Id),
            action => action.Attempts.ShouldBe(1));
        Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "✅ FB 粉專貼文"]);

        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains($"FB 貼文 posted on page {page.Id} as {post.Id}", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains($"FB 貼文 already posted as {post.Id}; not posted again", StringComparison.Ordinal));
        var forbidden = new[] { page.AccessToken, Graph.AppSecret, Graph.ProofOf(page.AccessToken), command.AnnouncementText! };
        lines.ShouldAllBe(line => forbidden.All(value => !line.Contains(value, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_an_image_it_is_a_text_post_and_a_reopen_deletes_the_post_even_one_deleted_already()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("刪文店", FacebookShop("刪文店"));
        var page = await ConnectPageAsync(shop, "刪文店粉專");

        // 提早打烊 has no image: a text post.
        await ConfirmAsync(owner, Scripted("今天提早打烊", "close-early", "2026-11-02", "20:00"));
        await RunJobsAsync();
        var text = Graph.PostsOf(page.Id).ShouldHaveSingleItem();
        (text.ImageUrl, text.Text).ShouldBe((null, (await CommandsAsync(shop)).Single().AnnouncementText));
        var feed = Graph.RequestsWith(page.AccessToken).ShouldHaveSingleItem();
        (feed.Endpoint, feed.Path).ShouldBe((FakeGraphServer.FeedEndpoint, $"/v26.0/{page.Id}/feed"));
        feed.Form.Keys.ShouldBe(["message", "access_token", "appsecret_proof"], ignoreOrder: true);
        feed.Form["appsecret_proof"].ShouldBe(Graph.ProofOf(page.AccessToken));

        // Closed on 11/15 and open after all: the closure's post is deleted.
        await ConfirmAsync(owner, Scripted("11/15 店休", "temporary-closure", "2026-11-15"));
        await RunJobsAsync();
        var closure = Graph.PostsOf(page.Id).Single(post => post.ImageUrl is not null);
        var reopen = Card(await SendForMessageAsync(owner, Scripted("11/15 恢復營業", "reopen", "2026-11-15")));
        reopen.Body.ShouldContain("・FB 粉專：刪除公告貼文（自動）");
        Report((await PostbackForMessagesAsync(owner, reopen.Button("確認"))).ShouldHaveSingleItem()).Body
            .ShouldContain("⌛ FB 粉專：刪除公告貼文：執行中…");
        await RunJobsAsync();

        Graph.PostsOf(page.Id).ShouldBe([text], "only the closure's post is gone");
        var delete = Graph.RequestsWith(page.AccessToken).Last();
        (delete.Method, delete.Path, delete.Endpoint).ShouldBe(("DELETE", $"/v26.0/{closure.Id}", FakeGraphServer.DeleteEndpoint));
        delete.Query["appsecret_proof"].ShouldBe(Graph.ProofOf(page.AccessToken));
        (await ActionsAsync(shop)).Single(action => action.Step == CoordinatorActionStep.Undo).Status.ShouldBe(CoordinatorActionStatus.Succeeded);
        Report(PushedMessages(Pushes(owner).Last()).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "取消 11/15（日） 臨時店休", "✅ FB 粉專：刪除公告貼文"]);

        // The owner deleted the next closure's post by hand: deleting it again counts as done.
        await ConfirmAsync(owner, Scripted("11/16 店休", "temporary-closure", "2026-11-16"));
        await RunJobsAsync();
        var byHand = Graph.PostsOf(page.Id).Single(post => post.ImageUrl is not null);
        Graph.DeleteByHand(byHand.Id).ShouldBeTrue();
        await ConfirmAsync(owner, Scripted("11/16 恢復營業", "reopen", "2026-11-16"));
        await RunJobsAsync();
        Graph.RequestsWith(page.AccessToken).Last().Path.ShouldBe($"/v26.0/{byHand.Id}");
        (await ActionsAsync(shop)).Where(action => action.Step == CoordinatorActionStep.Undo).Select(action => action.Status)
            .ShouldBe([CoordinatorActionStatus.Succeeded, CoordinatorActionStatus.Succeeded]);
        Graph.PostsOf(page.Id).ShouldBe([text]);
    }

    [Fact]
    public async Task FB_refusing_the_page_token_makes_the_post_a_reminder_and_asks_the_owners_to_authorize_again()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("失效粉專店", FacebookShop("失效粉專店"));
        var page = await ConnectPageAsync(shop, "失效粉專");
        const string GraphMessage = "Error validating access token: The session has been invalidated because the user changed their password.";
        Graph.Script(page.AccessToken, FakeGraphServer.PhotosEndpoint, GraphBehavior.Error(HttpStatusCode.BadRequest, 190, 460, GraphMessage));
        _host.Log.Start();
        try
        {
            await ConfirmAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"));
            await RunJobsAsync();
        }
        finally
        {
            _host.Log.Stop();
        }

        Graph.PostsOf(page.Id).ShouldBeEmpty();
        (await ActionsAsync(shop)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual),
            row => row.Mode.ShouldBe(ShopActionMode.Reminder),
            row => row.FailureCode.ShouldBe(CoordinatorFailureCodes.TokenRevoked),
            row => row.ExternalRef.ShouldBeNull());
        (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorConnectionStatus.Revoked),
            row => row.ReauthorizationRequestedAt.ShouldNotBeNull());
        ReauthorizationPushes(owner).ShouldHaveSingleItem()
            .ShouldStartWith($"失效粉專店 的 FB 粉專 授權已失效，相關動作先改為提醒。請點此重新授權：{PublicBase}/api/v1/coordinator/connect?token=");
        var followUp = Pushes(owner).Select(PushedMessages).First(messages => messages[0].GetProperty("type").GetString() == "flex");
        Report(followUp[0]).Body.ShouldBe(
        [
            "執行結果：",
            "⏳ FB 粉專貼文", "　FB 粉專授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權", "　FB 發文。", $"　圖片：{ClosedImage}",
        ]);
        followUp[1].GetProperty("text").GetString().ShouldBe((await CommandsAsync(shop)).Single().AnnouncementText, "the announcement to copy");
        _host.Log.Lines.ShouldContain(line => line.Contains("code 190, subcode 460", StringComparison.Ordinal));
        _host.Log.Lines.ShouldAllBe(line => !line.Contains(page.AccessToken, StringComparison.Ordinal) && !line.Contains(GraphMessage, StringComparison.Ordinal));

        // Revoked: the next card says 提醒 and Graph is not asked.
        var asked = Graph.RequestsWith(page.AccessToken).Count;
        Card(await SendForMessageAsync(owner, Scripted("11/5 店休", "temporary-closure", "2026-11-05"))).Body[0].ShouldBe("・FB 粉專貼文（提醒）");
        Graph.RequestsWith(page.AccessToken).Count.ShouldBe(asked);
    }

    [Fact]
    public async Task A_missing_FB_permission_is_a_reminder_and_limits_outages_bad_images_and_timeouts_fail_for_retry()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("權限粉專店", FacebookShop("權限粉專店"));
        var page = await ConnectPageAsync(shop, "權限粉專");

        // App Review not passed (or publishing not granted): a reminder saying so; the authorization stands.
        Graph.Script(page.AccessToken, FakeGraphServer.PhotosEndpoint, GraphBehavior.Error(HttpStatusCode.Forbidden, 200, null, "(#200) Requires pages_manage_posts"));
        await ConfirmAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"));
        await RunJobsAsync();
        (await ActionsAsync(shop)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual),
            row => row.FailureCode.ShouldBe(CoordinatorFailureCodes.FacebookPermission));
        Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem())[0]).Body.ShouldContain("　FB 權限不足（應用程式審核未通過或未授權發文），先以提醒處理");
        (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldNotBeNull().Status.ShouldBe(CoordinatorConnectionStatus.Active);
        ReauthorizationPushes(owner).ShouldBeEmpty();

        // Throttled, then down, then an image Facebook cannot read, then an answer that never comes (it did post): each
        // a failure for 〔重試〕; the next 〔重試〕 posts.
        Graph.Script(
            page.AccessToken,
            FakeGraphServer.PhotosEndpoint,
            GraphBehavior.Error(HttpStatusCode.BadRequest, 32, null, "(#32) Page request limit reached"),
            GraphBehavior.BadGateway,
            GraphBehavior.Error(HttpStatusCode.BadRequest, 324, null, "(#324) Missing or invalid image file"),
            GraphBehavior.AcceptThenTimeOut);
        await ConfirmAsync(owner, Scripted("11/5 店休", "temporary-closure", "2026-11-05"));
        await RunJobsAsync();
        var failed = Report(PushedMessages(Pushes(owner).Last()).ShouldHaveSingleItem());
        failed.Body.ShouldBe(["執行結果：", "❌ FB 粉專貼文：服務忙碌中"]);
        var retry = failed.Button("重試");

        var reasons = new List<string>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var token = $"rt-fb-retry-{attempt}-" + Guid.NewGuid().ToString("N");
            await PressSilentlyAsync(owner, retry, token);
            await RunJobsAsync();
            reasons.Add(Report((await ReplyMessagesAsync(token)).ShouldHaveSingleItem()).Body[1]);
        }

        reasons.ShouldBe(
        [
            "❌ FB 粉專貼文：FB 暫時無法使用",
            "❌ FB 粉專貼文：FB 讀不到公告圖片（圖片網址要能公開下載，JPG 或 PNG、4MB 以內）",
            "❌ FB 粉專貼文：FB 沒有及時回應，貼文可能已經發出；按〔重試〕前請先到粉專確認",
            "✅ FB 粉專貼文",
        ]);

        // What remains (Graph has no idempotency key): a post Facebook accepted without answering is posted again by
        // 〔重試〕 — which is why that reason says to look at the page first.
        Graph.PostsOf(page.Id).Count.ShouldBe(2);
        var row = (await ActionsAsync(shop)).Last();
        row.ShouldSatisfyAllConditions(
            action => action.Status.ShouldBe(CoordinatorActionStatus.Succeeded),
            action => action.ExternalRef.ShouldBe(Graph.PostsOf(page.Id)[1].Id),
            action => action.Attempts.ShouldBe(5));
        Graph.RequestsWith(page.AccessToken).ShouldAllBe(request => request.Parameter("appsecret_proof") == Graph.ProofOf(page.AccessToken));
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary><see cref="CommandShopFile"/> named <paramref name="name"/> whose closure (with its image) and 提早打烊
    /// (no image) post on FB automatically and do nothing else.</summary>
    private static string FacebookShop(string name)
    {
        var tree = JsonNode.Parse(CommandShopFile)!.AsObject();
        tree["storeName"] = name;
        foreach (var scenario in new[] { "temporary-closure", "close-early" })
        {
            tree["scenarios"]![scenario]!["actions"] = new JsonArray(new JsonObject { ["kind"] = "facebook-post", ["mode"] = "auto", ["steps"] = "FB 發文。" });
        }

        return tree.ToJsonString();
    }

    /// <summary>A page of the fake Graph API that <paramref name="shop"/>'s owner connected (stored as the FB
    /// connection, as the authorization link would).</summary>
    private async Task<GraphPage> ConnectPageAsync(Organization shop, string name)
    {
        var page = Graph.AddPage(Graph.NewPage(name, FakeGraphServer.AdminTasks));
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var secret = scope.ServiceProvider.GetRequiredService<ISecretProtector>()
            .Protect(CoordinatorConnection.PurposeOf(CoordinatorService.Facebook), page.AccessToken, _host.Clock.GetUtcNow());
        await scope.ServiceProvider.GetRequiredService<CoordinatorConnections>()
            .SetAsync(shop.Id, CoordinatorService.Facebook, secret, page.Id, page.Name, CancellationToken);
        return page;
    }

    /// <summary>The real FB provider of <paramref name="scope"/> (registered before the host's fake).</summary>
    private static ICoordinatorOAuthProvider RealFacebookProvider(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetServices<ICoordinatorOAuthProvider>().OfType<CoordinatorFacebookOAuthProvider>().Single();

    /// <summary>Runs <paramref name="row"/> through the real FB executor directly, as its job would (without recording).</summary>
    private async Task<CoordinatorActionResult> RunFacebookExecutorAsync(Organization shop, CoordinatorCommandAction row)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var settings = (await scope.ServiceProvider.GetRequiredService<CoordinatorShops>().FindAsync(shop.Id, CancellationToken)).ShouldNotBeNull();
        var command = (await CommandsAsync(shop)).Single(candidate => candidate.Id == row.CommandId);
        var request = new CoordinatorActionRequest(
            shop.Id, row.Id, row.Kind, row.Step, command.Scenario, command.Date!.Value, command.Time, command.AnnouncementText, row.ImageUrl, null, settings);
        return await scope.ServiceProvider.GetRequiredService<CoordinatorFacebookPostExecutor>().ExecuteAsync(request, CancellationToken);
    }
}
