using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// FB and Google authorization links (store-coordinator design §3 Q12, Q18, §5 Slice 6, #377) through
/// <c>coordinator-shop auth-link</c>, the real anonymous endpoints, PostgreSQL and the host's providers (FB's
/// <see cref="FakeOAuthProvider"/> without PKCE; the real Google one with PKCE against <see cref="FakeGoogleServer"/>, #378),
/// the test playing both the owner's browser (cookies by hand) and the service: the flow done at once and with a choice of location, every refusal (expired, used, tampered
/// or expired state, missing or wrong nonce cookie, the service's <c>error</c>, a choice not offered, a service not
/// configured), re-authorization pushed once per revocation to the 老闆 members, and no token, code, state, nonce,
/// verifier or credential in any page, redirect (but the service's), CLI output or log.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    /// <summary><c>PublicChannels:PublicBaseUrl</c> of the test host (appsettings.Development.json).</summary>
    private const string PublicBase = "http://localhost:5153";

    private FakeOAuthProvider FacebookOAuth => _host.Facebook;

    private FakeGoogleServer GoogleApi => _host.GoogleApi;

    [Fact]
    public async Task An_owner_authorizes_FB_through_a_link_once_and_the_credential_is_stored_encrypted()
    {
        var shop = await _host.CreateOrganizationAsync("授權連結店");
        var secret = "fb-page-token-" + Guid.NewGuid().ToString("N");
        var code = "fb-code-" + Guid.NewGuid().ToString("N");
        FacebookOAuth.Expect(code, _ => CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential(secret, "1029384756", "王記粉專")));
        using var browser = Browser();
        (int Exit, string Output, string Error) issued;
        Consent consent;
        string done, reopened, replayed;
        HttpResponseMessage callback;
        _host.Log.Start();
        try
        {
            issued = await RunShopAsync("auth-link", "--organization", shop.Code.ToUpperInvariant(), "--service", "facebook");
            issued.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, issued.Error);
            consent = await OpenLinkAsync(browser, TokenIn(issued.Output));
            (await LinksAsync(shop)).ShouldHaveSingleItem().UsedAt.ShouldBeNull("opening the link does not use it");

            callback = await CallbackAsync(browser, "facebook", consent.Nonce, ("code", code), ("state", consent.State));
            done = await PageAsync(callback, HttpStatusCode.OK);
            reopened = await PageAsync(await browser.GetAsync(ConnectPath(consent.Token), CancellationToken), HttpStatusCode.NotFound);
            replayed = await PageAsync(
                await CallbackAsync(browser, "facebook", consent.Nonce, ("code", code), ("state", consent.State)), HttpStatusCode.NotFound);
        }
        finally
        {
            _host.Log.Stop();
        }

        // The command printed the link once, with the shop; only its hash is stored.
        issued.Output.ShouldStartWith($"組織 {shop.Code}（授權連結店）的FB 粉專授權連結：");
        issued.Output.ShouldContain($"{PublicBase}/api/v1/coordinator/connect?token={consent.Token}");
        issued.Output.ShouldContain("只能使用一次");
        var link = (await LinksAsync(shop)).ShouldHaveSingleItem();
        link.ShouldSatisfyAllConditions(
            row => row.Service.ShouldBe(CoordinatorService.Facebook),
            row => row.Purpose.ShouldBe(CoordinatorAuthorizationPurpose.Initial),
            row => row.TokenHash.ShouldBe(AuthorizationLinkTokens.Hash(consent.Token)),
            row => (row.ExpiresAt - row.CreatedAt).ShouldBe(TimeSpan.FromHours(72)),
            row => row.UsedAt.ShouldNotBeNull());
        issued.Output.ShouldNotContain(link.TokenHash);

        // To FB's consent screen, with the callback and a state; the nonce in a strict cookie.
        consent.Location.GetLeftPart(UriPartial.Path).ShouldBe("https://fake-oauth.test/facebook/authorize");
        consent.Query["redirect_uri"].ShouldBe($"{PublicBase}/api/v1/coordinator/oauth/facebook/callback");
        consent.Query.ShouldNotContainKey("code_challenge", "FB's fake does not use PKCE");
        consent.Location.ToString().ShouldNotContain(consent.Token);
        consent.SetCookie.ShouldStartWith($"coordinator_oauth_facebook={consent.Nonce};");
        foreach (var attribute in new[] { "max-age=1800", "path=/api/v1/coordinator/oauth/facebook", "secure", "samesite=lax", "httponly" })
        {
            consent.SetCookie.ShouldContain(attribute, Case.Insensitive);
        }

        // 「授權完成」 naming the shop; the cookie dropped; the code exchanged once, without a verifier.
        done.ShouldContain("<h1>授權完成</h1>");
        done.ShouldContain("授權連結店 已連結FB 粉專：王記粉專。");
        callback.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().ShouldContain("expires=Thu, 01 Jan 1970", Case.Insensitive);
        var exchange = FacebookOAuth.Exchanges.Where(call => call.Code == code).ShouldHaveSingleItem("a used link is never exchanged again");
        (exchange.RedirectUri, exchange.CodeVerifier).ShouldBe(($"{PublicBase}/api/v1/coordinator/oauth/facebook/callback", null));

        // Stored encrypted: only the secret protector reads it back.
        var connection = (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldNotBeNull();
        connection.ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorConnectionStatus.Active),
            row => row.ExternalId.ShouldBe("1029384756"),
            row => row.DisplayName.ShouldBe("王記粉專"),
            row => row.Secret.Ciphertext.ShouldNotContain(secret),
            row => row.ReauthorizationRequestedAt.ShouldBeNull());
        _host.Factory.Services.GetRequiredService<ISecretProtector>()
            .Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Facebook), connection.Secret)
            .ShouldBe(secret);
        var activity = (await ConnectionActivitiesAsync(shop)).ShouldHaveSingleItem();
        JsonNode.Parse(activity.Detail!)!.ToJsonString().ShouldBe("""{"change":"set","service":"facebook"}""");
        activity.ActorAccountId.ShouldBeNull();

        // Used: opening it again and replaying the callback both get 「連結已失效」.
        reopened.ShouldContain(CoordinatorAuthorizationPages.InvalidLinkMessage);
        replayed.ShouldContain(CoordinatorAuthorizationPages.InvalidLinkMessage);

        // Nothing secret in a page, the command's output or a log line.
        var forbidden = new[] { secret, code, consent.Token, consent.State, consent.Nonce };
        foreach (var text in new[] { done, reopened, replayed, issued.Error })
        {
            forbidden.ShouldAllBe(value => !text.Contains(value, StringComparison.Ordinal));
        }

        var logged = ProductionLogLines();
        logged.ShouldContain(line => line.Contains("connected facebook", StringComparison.Ordinal));
        logged.ShouldContain(line => line.Contains("RequestPath=/api/v1/coordinator/connect", StringComparison.Ordinal), "scopes are checked too");
        logged.ShouldAllBe(line => forbidden.All(value => !line.Contains(value, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_Google_owner_with_several_locations_chooses_one_and_only_an_offered_choice_is_accepted()
    {
        var shop = await _host.CreateOrganizationAsync("選地點店");
        var taipei = GoogleApi.AddLocation("台北店");
        var kaohsiung = GoogleApi.AddLocation("<b>高雄店</b>", address: null);
        var user = GoogleApi.AddUser([taipei], [kaohsiung]);
        using var browser = Browser();
        Consent consent;
        string code, form, done;
        HttpResponseMessage selection;
        var refusals = new List<string>();
        _host.Log.Start();
        try
        {
            var issued = await RunShopAsync("auth-link", "--organization", shop.Code, "--service=google", "--hours=1");
            issued.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, issued.Error);
            consent = await OpenLinkAsync(browser, TokenIn(issued.Output));
            code = GoogleApi.Code(user, consent.Query["code_challenge"]);
            selection = await CallbackAsync(browser, "google", consent.Nonce, ("state", consent.State), ("code", code), ("scope", CoordinatorGoogleClient.Scope));
            form = await PageAsync(selection, HttpStatusCode.OK);
            var formState = FormState(form);

            // Not offered, no cookie, another nonce, the consent state instead of the form's, no choice.
            foreach (var (cookie, state, choice) in new (string?, string?, string?)[]
                     {
                         (consent.Nonce, formState, "locations/999"),
                         (null, formState, kaohsiung.Name),
                         (CoordinatorOAuthStates.NewRandom(), formState, kaohsiung.Name),
                         (consent.Nonce, consent.State, kaohsiung.Name),
                         (consent.Nonce, formState, null),
                     })
            {
                refusals.Add(await PageAsync(await ChooseAsync(browser, "google", cookie, state, choice), HttpStatusCode.BadRequest));
            }

            (await ConnectionOfAsync(shop, CoordinatorService.Google)).ShouldBeNull("nothing stored before a valid choice");

            done = await PageAsync(await ChooseAsync(browser, "google", consent.Nonce, formState, kaohsiung.Name), HttpStatusCode.OK);
            refusals.Add(await PageAsync(await ChooseAsync(browser, "google", consent.Nonce, formState, taipei.Name), HttpStatusCode.BadRequest));
        }
        finally
        {
            _host.Log.Stop();
        }

        (await LinksAsync(shop)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            link => (link.ExpiresAt - link.CreatedAt).ShouldBe(TimeSpan.FromHours(1)),
            link => link.UsedAt.ShouldNotBeNull("the callback took it"));

        // PKCE: the token request carried the verifier of the consent URL's challenge (the fake checks it), the
        // callback and the platform's client; both accounts' locations were listed.
        consent.Query["code_challenge_method"].ShouldBe("S256");
        var exchange = GoogleApi.Requests.Where(request => request.Endpoint == FakeGoogleServer.Token && request.Form.GetValueOrDefault("code") == code)
            .ShouldHaveSingleItem();
        var verifier = exchange.Form["code_verifier"];
        CoordinatorOAuthStates.Challenge(verifier).ShouldBe(consent.Query["code_challenge"]);
        exchange.Form["redirect_uri"].ShouldBe($"{PublicBase}/api/v1/coordinator/oauth/google/callback");
        GoogleApi.Requests.Count(request => request.Endpoint == FakeGoogleServer.Locations && user.Accounts.Any(account => account.Name == request.Subject))
            .ShouldBe(2);

        // The form: every location of every account, with its address, encoded; it may post only to this origin.
        form.ShouldContain("<h1>選擇要連結的營業地點</h1>");
        ChoicePattern().Matches(form).Select(match => match.Groups["id"].Value).ShouldBe([taipei.Name, kaohsiung.Name]);
        form.ShouldContain("台北店（台北市大安區忠孝東路四段 100 號）");
        form.ShouldContain("&lt;b&gt;高雄店&lt;/b&gt;");
        form.ShouldNotContain("<b>高雄店");
        form.ShouldContain("action=\"select\"");
        selection.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldContain("form-action 'self'");
        refusals.ShouldAllBe(page => page.Contains(CoordinatorAuthorizationPages.FailedMessage));

        done.ShouldContain("選地點店 已連結Google 商家：&lt;b&gt;高雄店&lt;/b&gt;。");
        var connection = (await ConnectionOfAsync(shop, CoordinatorService.Google)).ShouldNotBeNull();
        (connection.ExternalId, connection.DisplayName).ShouldBe((kaohsiung.Name, "<b>高雄店</b>"));
        _host.Factory.Services.GetRequiredService<ISecretProtector>()
            .Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Google), connection.Secret)
            .ShouldBe(user.RefreshToken, "the refresh token is the credential");
        JsonNode.Parse((await ConnectionActivitiesAsync(shop)).ShouldHaveSingleItem().Detail!)!.ToJsonString()
            .ShouldBe("""{"change":"set","service":"google"}""");

        var forbidden = new[] { user.RefreshToken, code, verifier, consent.Token, consent.State, consent.Nonce, GoogleApi.ClientSecret }
            .Concat(GoogleApi.AccessTokens)
            .ToList();
        foreach (var page in refusals.Append(form).Append(done))
        {
            forbidden.ShouldAllBe(value => !page.Contains(value, StringComparison.Ordinal));
        }

        ProductionLogLines().ShouldAllBe(line => forbidden.All(value => !line.Contains(value, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unknown_expired_and_used_links_tampered_or_expired_states_wrong_cookies_and_the_services_error_are_refused()
    {
        var shop = await _host.CreateOrganizationAsync("拒絕測試店");
        using var browser = Browser();
        var code = "fb-code-" + Guid.NewGuid().ToString("N");
        FacebookOAuth.Expect(code, _ => CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential("never-stored", "1", "x")));

        // Unknown and malformed tokens: one page.
        var unknown = await PageAsync(await browser.GetAsync(ConnectPath(AuthorizationLinkTokens.Generate()), CancellationToken), HttpStatusCode.NotFound);
        unknown.ShouldContain(CoordinatorAuthorizationPages.InvalidLinkMessage);
        (await PageAsync(await browser.GetAsync(ConnectPath("not-a-token"), CancellationToken), HttpStatusCode.NotFound)).ShouldBe(unknown);

        var issued = await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "facebook", "--hours", "1");
        issued.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, issued.Error);
        var token = TokenIn(issued.Output);
        var first = await OpenLinkAsync(browser, token);
        var state = first.State;
        var tampered = state[..^3] + (state[^3] == 'A' ? 'B' : 'A') + state[^2..];

        foreach (var (service, cookie, query) in new (string, string?, (string, string)[])[]
                 {
                     ("facebook", first.Nonce, [("code", code), ("state", tampered)]),
                     ("facebook", first.Nonce, [("code", code), ("state", "")]),
                     ("facebook", first.Nonce, [("code", code)]),
                     ("facebook", null, [("code", code), ("state", state)]),
                     ("facebook", CoordinatorOAuthStates.NewRandom(), [("code", code), ("state", state)]),
                     ("google", first.Nonce, [("code", code), ("state", state)]),
                     ("facebook", first.Nonce, [("state", state)]),
                 })
        {
            var refused = await PageAsync(await CallbackAsync(browser, service, cookie, query), HttpStatusCode.BadRequest);
            refused.ShouldContain(CoordinatorAuthorizationPages.FailedMessage);
        }

        // The service's error: 「已取消授權」 with its short code only; the link stays usable.
        var cancelled = await CallbackAsync(
            browser, "facebook", first.Nonce, ("error", "access_denied"), ("error_description", "Permissions error (#200) detail"), ("state", state));
        var cancelledPage = await PageAsync(cancelled, HttpStatusCode.OK);
        cancelledPage.ShouldContain(CoordinatorAuthorizationPages.CancelledMessage);
        cancelledPage.ShouldContain("（代碼：access_denied）");
        cancelledPage.ShouldNotContain("Permissions error");
        cancelled.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().ShouldContain("expires=Thu, 01 Jan 1970", Case.Insensitive);
        (await LinksAsync(shop)).ShouldHaveSingleItem().UsedAt.ShouldBeNull();

        // The state expires after 30 minutes (the link is still valid).
        _host.Clock.Advance(TimeSpan.FromMinutes(31));
        (await PageAsync(await CallbackAsync(browser, "facebook", first.Nonce, ("code", code), ("state", state)), HttpStatusCode.BadRequest))
            .ShouldContain(CoordinatorAuthorizationPages.FailedMessage);
        FacebookOAuth.Exchanges.ShouldNotContain(call => call.Code == code, "nothing above reached the exchange");

        // Opened again now, the state outlives the link (an hour): the callback finds it expired.
        var second = await OpenLinkAsync(browser, token);
        _host.Clock.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(30));
        var expired = await PageAsync(
            await CallbackAsync(browser, "facebook", second.Nonce, ("code", code), ("state", second.State)), HttpStatusCode.NotFound);
        expired.ShouldBe(unknown);
        (await PageAsync(await browser.GetAsync(ConnectPath(token), CancellationToken), HttpStatusCode.NotFound)).ShouldBe(unknown);
        (await LinksAsync(shop)).ShouldHaveSingleItem().UsedAt.ShouldBeNull();

        // A code the service refuses: the link is used up and the page shows our short code.
        var other = TokenIn((await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "facebook")).Output);
        var third = await OpenLinkAsync(browser, other);
        var refusedCode = await PageAsync(
            await CallbackAsync(browser, "facebook", third.Nonce, ("code", "unknown-" + code), ("state", third.State)), HttpStatusCode.BadRequest);
        refusedCode.ShouldContain("（代碼：invalid-grant）");
        (await PageAsync(await browser.GetAsync(ConnectPath(other), CancellationToken), HttpStatusCode.NotFound)).ShouldBe(unknown);
        (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldBeNull();
        (await ConnectionActivitiesAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unconfigured_provider_gets_no_links_and_its_routes_are_404_and_the_command_checks_its_arguments()
    {
        var shop = await _host.CreateOrganizationAsync("未設定店");
        using var browser = Browser();
        var made = TokenIn((await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "google")).Output);
        _host.GoogleConfigured = false;
        (int Exit, string Output, string Error) refused;
        HttpResponseMessage callback, select, opened;
        try
        {
            refused = await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "google");
            callback = await CallbackAsync(browser, "google", "x", ("code", "c"), ("state", "s"));
            select = await ChooseAsync(browser, "google", "x", "s", "c");
            opened = await browser.GetAsync(ConnectPath(made), CancellationToken);
        }
        finally
        {
            _host.GoogleConfigured = true;
        }

        refused.Exit.ShouldBe(CoordinatorShopCommand.ExitFailed);
        refused.Error.ShouldContain("Coordinator:Google:ClientId");
        (await LinksAsync(shop)).Count.ShouldBe(1, "only the link made while configured");
        callback.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await callback.Content.ReadAsStringAsync(CancellationToken)).ShouldBeEmpty();
        select.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PageAsync(opened, HttpStatusCode.NotFound)).ShouldContain(CoordinatorAuthorizationPages.InvalidLinkMessage);
        foreach (var service in new[] { "line", "twitter", "Facebook" })
        {
            (await CallbackAsync(browser, service, "x", ("code", "c"), ("state", "s"))).StatusCode.ShouldBe(HttpStatusCode.NotFound, service);
        }

        foreach (var args in new[]
                 {
                     new[] { "auth-link", "--organization", shop.Code },
                     ["auth-link", "--organization", shop.Code, "--service", "line"],
                     ["auth-link", "--organization", shop.Code, "--service", "facebook", "--hours", "169"],
                     ["auth-link", "--organization", shop.Code, "--service", "facebook", "--hours", "0"],
                     ["auth-link", "--service", "facebook"],
                     ["show", "--organization", shop.Code, "--service", "facebook"],
                 })
        {
            var result = await RunShopAsync(args);
            result.Exit.ShouldBe(CoordinatorShopCommand.ExitUsage, string.Join(' ', args));
            result.Error.ShouldContain("auth-link --organization <組織代碼> --service facebook|google");
        }

        (await RunShopAsync("auth-link", "--organization", "no-such-shop", "--service", "facebook")).Exit.ShouldBe(CoordinatorShopCommand.ExitFailed);
        var longest = await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "facebook", "--hours", "168");
        longest.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, longest.Error);
        (await LinksAsync(shop)).Single(link => link.Service == CoordinatorService.Facebook).ShouldSatisfyAllConditions(
            link => (link.ExpiresAt - link.CreatedAt).ShouldBe(TimeSpan.FromHours(168)));
    }

    [Fact]
    public async Task A_refused_authorization_revokes_it_and_pushes_one_new_link_to_each_owner_per_revocation()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("重新授權店", WithFacebook("重新授權店"));
        var partner = NewUser();
        (await SendAsync(partner, await OwnerCodeAsync(shop))).ShouldBe("已綁定 重新授權店（老闆）。");
        var manager = NewUser();
        var managerCode = BindingCodes.Generate();
        await AddCodeAsync(shop, managerCode, CoordinatorRole.Manager, _host.Clock.GetUtcNow(), TimeSpan.FromHours(24));
        (await SendAsync(manager, BindingCodes.Format(managerCode))).ShouldBe("已綁定 重新授權店（店長）。");
        using var browser = Browser();
        await ConnectFacebookAsync(browser, shop, (await RunShopAsync("auth-link", "--organization", shop.Code, "--service", "facebook")).Output);
        Func<CoordinatorActionRequest, CoordinatorActionResult> revoked =
            _ => CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked);
        Executors.Enroll(shop.Id, ShopActionKind.FacebookPost, revoked, revoked, revoked);

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

        // Revoked; one re-authorization link of 72 hours; pushed to each owner, not to the manager.
        var connection = (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldNotBeNull();
        connection.Status.ShouldBe(CoordinatorConnectionStatus.Revoked);
        connection.ReauthorizationRequestedAt.ShouldNotBeNull();
        var reauthorization = (await LinksAsync(shop)).Where(link => link.Purpose == CoordinatorAuthorizationPurpose.Reauthorize).ShouldHaveSingleItem();
        (reauthorization.ExpiresAt - reauthorization.CreatedAt).ShouldBe(TimeSpan.FromHours(72));
        var pushed = ReauthorizationPushes(owner).ShouldHaveSingleItem();
        pushed.ShouldStartWith($"重新授權店 的 FB 粉專 授權已失效，相關動作先改為提醒。請點此重新授權：{PublicBase}/api/v1/coordinator/connect?token=");
        var token = TokenIn(pushed);
        AuthorizationLinkTokens.Hash(token).ShouldBe(reauthorization.TokenHash);
        ReauthorizationPushes(partner).ShouldBe([pushed]);
        ReauthorizationPushes(manager).ShouldBeEmpty();
        ProductionLogLines().ShouldAllBe(line => !line.Contains(token, StringComparison.Ordinal));

        // The follow-up says why FB 貼文 became a reminder.
        var followUp = Pushes(owner).Select(PushedMessages).First(messages => messages[0].GetProperty("type").GetString() == "flex");
        Report(followUp[0]).Body.ShouldContain("　FB 粉專授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權");
        (await ActionsAsync(shop)).Single(action => action.Kind == ShopActionKind.FacebookPost).ShouldSatisfyAllConditions(
            action => action.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual),
            action => action.FailureCode.ShouldBe(CoordinatorFailureCodes.TokenRevoked));

        // Another failure of the same revocation sends nothing more.
        await ConfirmAsync(owner, Scripted("明天臨時店休", "temporary-closure", "2026-11-03"));
        await RunJobsAsync();
        (await LinksAsync(shop)).Count(link => link.Purpose == CoordinatorAuthorizationPurpose.Reauthorize).ShouldBe(1);
        ReauthorizationPushes(owner).Count.ShouldBe(1);
        ReauthorizationPushes(partner).Count.ShouldBe(1);

        // The owner authorizes again through the pushed link: active, the mark cleared — the next refusal asks again.
        await ConnectFacebookAsync(browser, shop, pushed);
        (await ConnectionOfAsync(shop, CoordinatorService.Facebook)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorConnectionStatus.Active),
            row => row.ReauthorizationRequestedAt.ShouldBeNull());
        await ConfirmAsync(owner, Scripted("11/5 店休", "temporary-closure", "2026-11-05"));
        await RunJobsAsync();
        (await LinksAsync(shop)).Count(link => link.Purpose == CoordinatorAuthorizationPurpose.Reauthorize).ShouldBe(2);
        ReauthorizationPushes(owner).Count.ShouldBe(2);
        ReauthorizationPushes(partner).Count.ShouldBe(2);
        ReauthorizationPushes(manager).ShouldBeEmpty();
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary>
    /// The captured log lines a deployment writes: all of them — scopes included (ASP.NET Core's <c>RequestPath</c>) —
    /// except ASP.NET Core's own below <c>Warning</c>, which <c>appsettings.json</c> (<c>Microsoft.AspNetCore: Warning</c>)
    /// never writes: its request-start and redirect lines carry the whole URL, query included (the callback's code).
    /// </summary>
    private List<string> ProductionLogLines() =>
        [.. _host.Log.Lines.Where(line => !AspNetCoreDetail().IsMatch(line))];

    [GeneratedRegex(@"\AMicrosoft\.AspNetCore\S* (Trace|Debug|Information): ")]
    private static partial Regex AspNetCoreDetail();

    /// <summary>What opening a link answered: the redirect to the consent screen, its query, and the nonce cookie.</summary>
    private sealed record Consent(string Token, Uri Location, Dictionary<string, string> Query, string SetCookie, string Nonce)
    {
        public string State => Query["state"];
    }

    /// <summary>A browser without a cookie jar (cookies are sent by hand) that does not follow redirects.</summary>
    private HttpClient Browser() =>
        _host.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static string ConnectPath(string token) => $"/api/v1/coordinator/connect?token={token}";

    /// <summary>Opens the link of <paramref name="token"/> and expects the redirect to the consent screen.</summary>
    private static async Task<Consent> OpenLinkAsync(HttpClient browser, string token)
    {
        var response = await browser.GetAsync(ConnectPath(token), CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        PageHeaders(response);
        var location = response.Headers.Location.ShouldNotBeNull();
        var query = QueryHelpers.ParseQuery(location.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
        var setCookie = response.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem();
        var nonce = setCookie[(setCookie.IndexOf('=', StringComparison.Ordinal) + 1)..setCookie.IndexOf(';', StringComparison.Ordinal)];
        nonce.Length.ShouldBe(43);
        return new Consent(token, location, query, setCookie, nonce);
    }

    /// <summary>The service sending the owner's browser back, with <paramref name="nonce"/> as its cookie.</summary>
    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient browser, string service, string? nonce, params (string Key, string Value)[] query)
    {
        var url = QueryHelpers.AddQueryString(
            $"/api/v1/coordinator/oauth/{service}/callback", query.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (nonce is not null)
        {
            request.Headers.Add("Cookie", $"coordinator_oauth_{service}={nonce}");
        }

        return await browser.SendAsync(request, CancellationToken);
    }

    /// <summary>The selection form posted.</summary>
    private static async Task<HttpResponseMessage> ChooseAsync(HttpClient browser, string service, string? nonce, string? state, string? choice)
    {
        var fields = new List<KeyValuePair<string, string>>();
        if (state is not null)
        {
            fields.Add(new("state", state));
        }

        if (choice is not null)
        {
            fields.Add(new("choice", choice));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/coordinator/oauth/{service}/select")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        if (nonce is not null)
        {
            request.Headers.Add("Cookie", $"coordinator_oauth_{service}={nonce}");
        }

        return await browser.SendAsync(request, CancellationToken);
    }

    /// <summary>A page: its status, its headers, its HTML.</summary>
    private static async Task<string> PageAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/html");
        PageHeaders(response);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        html.ShouldStartWith("<!doctype html>");
        return html;
    }

    /// <summary>Every answer is <c>no-store</c>, <c>no-referrer</c>, <c>noindex</c>, with a CSP that loads nothing.</summary>
    private static void PageHeaders(HttpResponseMessage response)
    {
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("X-Robots-Tag").ShouldBe(["noindex"]);
        var csp = response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
        csp.ShouldStartWith("default-src 'none'; style-src 'sha256-");
        csp.ShouldContain("frame-ancestors 'none'");
    }

    private static string TokenIn(string text) => LinkPattern().Match(text) is { Success: true } match
        ? match.Groups["token"].Value
        : throw new ShouldAssertException($"No authorization link in: {text}");

    [GeneratedRegex(@"https?://\S+?/api/v1/coordinator/connect\?token=(?<token>[A-Za-z0-9_-]{43})(?![A-Za-z0-9_-])")]
    private static partial Regex LinkPattern();

    private static string FormState(string html) => FormStatePattern().Match(html) is { Success: true } match
        ? match.Groups["state"].Value
        : throw new ShouldAssertException("No state in the selection form.");

    [GeneratedRegex("name=\"state\" value=\"(?<state>[A-Za-z0-9_-]+)\"")]
    private static partial Regex FormStatePattern();

    [GeneratedRegex("name=\"choice\" value=\"(?<id>[^\"]+)\"")]
    private static partial Regex ChoicePattern();

    /// <summary>Authorizes FB for <paramref name="shop"/> through the link in <paramref name="text"/>, all the way.</summary>
    private async Task ConnectFacebookAsync(HttpClient browser, Organization shop, string text)
    {
        var code = "fb-code-" + Guid.NewGuid().ToString("N");
        FacebookOAuth.Expect(code, _ => CoordinatorOAuthOutcome.Connected(
            new CoordinatorOAuthCredential("fb-page-token-" + Guid.NewGuid().ToString("N"), "5550001", shop.Name)));
        var consent = await OpenLinkAsync(browser, TokenIn(text));
        (await PageAsync(await CallbackAsync(browser, "facebook", consent.Nonce, ("code", code), ("state", consent.State)), HttpStatusCode.OK))
            .ShouldContain("<h1>授權完成</h1>");
    }

    /// <summary>The texts of the re-authorization links pushed to <paramref name="user"/>.</summary>
    private List<string> ReauthorizationPushes(string user) =>
        [.. Pushes(user)
            .SelectMany(PushedMessages)
            .Where(message => message.GetProperty("type").GetString() == "text")
            .Select(message => message.GetProperty("text").GetString()!)
            .Where(text => text.Contains("授權已失效", StringComparison.Ordinal))];

    private async Task<List<CoordinatorAuthorizationLink>> LinksAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorAuthorizationLinks.AsNoTracking().OrderBy(link => link.CreatedAt).ToListAsync(CancellationToken);
    }

    private async Task<CoordinatorConnection?> ConnectionOfAsync(Organization shop, CoordinatorService service)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorConnections.AsNoTracking().SingleOrDefaultAsync(row => row.Service == service, CancellationToken);
    }
}
