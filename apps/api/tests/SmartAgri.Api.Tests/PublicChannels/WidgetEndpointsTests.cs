using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Assistants;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>
/// What the Api serves to visitors' browsers (M5a plan §3 A/B, Slice 6, issue #201) against real
/// PostgreSQL: <c>GET /use/{assistantId}</c> with its per-assistant <c>frame-ancestors</c>,
/// <c>/widget/*</c> and <c>/embed.js</c>. The widget is a tiny fake build in a temporary folder, shaped
/// like Angular's output, so no Angular build is needed.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class WidgetEndpointsTests : IClassFixture<AuthHostFixture>, IDisposable
{
    private const string Password = "Widget-Endpoints-Pass-1!";

    private const string FakeIndex =
        "<!doctype html>\n<html lang=\"zh-Hant\">\n  <head>\n    <meta charset=\"utf-8\"/>\n    <base href=\"/widget/\"/>\n"
        + "  <link rel=\"stylesheet\" href=\"styles-ABCD1234.css\"></head>\n  <body>\n    <app-root></app-root>\n"
        + "  <script src=\"main-ABCD1234.js\" type=\"module\"></script></body>\n</html>\n";

    private const string UnavailableText = "這個對話視窗目前無法使用";

    private const string OpenFromSiteText = "請從官網開啟這個對話視窗";

    private static readonly Regex NonceInHeader = new(@"style-src 'self' 'nonce-([A-Za-z0-9+/]{24})';", RegexOptions.CultureInvariant);

    private readonly AuthHostFixture _host;
    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-widget-").FullName;
    private readonly string _widgetRoot;
    private readonly string _embedScript;
    private readonly List<IDisposable> _disposables = [];
    private readonly HttpClient _client;
    private readonly Dictionary<Guid, Guid> _organizationOf = [];

    public WidgetEndpointsTests(AuthHostFixture host)
    {
        _host = host;
        _widgetRoot = Path.Combine(_directory, "widget");
        Directory.CreateDirectory(_widgetRoot);
        File.WriteAllText(Path.Combine(_widgetRoot, "index.html"), FakeIndex);
        File.WriteAllText(Path.Combine(_widgetRoot, "main-ABCD1234.js"), "console.log('fake widget');");
        File.WriteAllText(Path.Combine(_widgetRoot, "styles-ABCD1234.css"), "body{margin:0}");
        File.WriteAllText(Path.Combine(_widgetRoot, "notes.txt"), "plain");
        File.WriteAllText(Path.Combine(_widgetRoot, "blob.xyz"), "unknown type");
        File.WriteAllText(Path.Combine(_directory, "secret.txt"), "outside the widget folder");
        _embedScript = Path.Combine(_directory, "embed.js");
        File.WriteAllText(_embedScript, "(function(){/* fake loader */})();");

        _client = CreateClient(builder => { });
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    // --- GET /use/{assistantId}: the allowlist -------------------------------------------------

    [Fact]
    public async Task A_published_channel_with_two_domains_lists_exactly_those_in_frame_ancestors_with_the_other_headers()
    {
        var assistantId = await SeedAsync(WebsiteChannelState.Published, "b.example", "a.example");

        var response = await _client.GetAsync($"/use/{assistantId}?host=https://a.example", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var csp = Header(response, "Content-Security-Policy");
        var nonce = NonceInHeader.Match(csp).Groups[1].Value;
        nonce.ShouldNotBeEmpty();
        csp.ShouldBe(
            "default-src 'self'; script-src 'self'; style-src 'self' 'nonce-" + nonce + "'; connect-src 'self'; "
            + "img-src 'self' data:; base-uri 'self'; form-action 'none'; frame-ancestors https://a.example https://b.example");
        Header(response, "Referrer-Policy").ShouldBe("strict-origin");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Cache-Control").ShouldBe("no-store");
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/html; charset=utf-8");

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        body.ShouldBe(FakeIndex.Replace("<app-root>", $"<app-root ngCspNonce=\"{nonce}\">", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_nonce_is_new_for_every_response_and_the_same_in_the_header_and_the_page()
    {
        var assistantId = await SeedAsync(WebsiteChannelState.Published, "a.example");

        var nonces = new List<string>();
        foreach (var _ in new[] { 1, 2, 3 })
        {
            var response = await _client.GetAsync($"/use/{assistantId}", CancellationToken);
            var headerNonce = NonceInHeader.Match(Header(response, "Content-Security-Policy")).Groups[1].Value;
            (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain($"<app-root ngCspNonce=\"{headerNonce}\">");
            nonces.Add(headerNonce);
        }

        nonces.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task A_paused_channel_still_serves_the_page_with_its_domains()
    {
        var assistantId = await SeedAsync(WebsiteChannelState.Paused, "a.example");

        var response = await _client.GetAsync($"/use/{assistantId}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(response, "Content-Security-Policy").ShouldEndWith("frame-ancestors https://a.example");
    }

    [Fact]
    public async Task A_published_channel_that_is_suspended_for_acceptance_still_serves_the_page_so_the_widget_can_say_so()
    {
        // Seeded without any test run: the acceptance status is not-accepted, so the visitor API will
        // answer 403 "suspended" — but the allowlist, and the page that shows the message, stay.
        var assistantId = await SeedAsync(WebsiteChannelState.Published, "a.example");

        var response = await _client.GetAsync($"/use/{assistantId}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(response, "Content-Security-Policy").ShouldEndWith("frame-ancestors https://a.example");
    }

    [Fact]
    public async Task Draft_missing_and_empty_list_are_the_same_neutral_page_with_frame_ancestors_none_byte_for_byte()
    {
        var draft = await SeedAsync(WebsiteChannelState.Draft, "a.example");
        var published = await SeedAsync(WebsiteChannelState.Published);
        var paused = await SeedAsync(WebsiteChannelState.Paused);
        var withoutChannel = await SeedAsync(channelState: null);
        var paths = new[]
        {
            $"/use/{draft}", $"/use/{published}", $"/use/{paused}", $"/use/{withoutChannel}",
            $"/use/{Guid.NewGuid()}", "/use/not-an-id", $"/use/{Guid.Empty}", $"/use/{draft}?host=https://a.example",
        };

        var responses = new List<string>();
        foreach (var path in paths)
        {
            var response = await _client.GetAsync(path, CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            Header(response, "Content-Security-Policy").ShouldContain("frame-ancestors 'none'");
            Header(response, "Content-Security-Policy").ShouldNotContain("nonce-");
            Header(response, "Cache-Control").ShouldBe("no-store");
            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            body.ShouldContain(UnavailableText);
            body.ShouldNotContain("ngCspNonce");
            responses.Add(await WireFormAsync(response));
        }

        responses.Distinct().Count().ShouldBe(1, "a missing assistant and an unpublished one must not be told apart");
    }

    [Fact]
    public async Task A_domain_removed_in_the_settings_is_gone_from_the_next_response()
    {
        var assistantId = await SeedAsync(WebsiteChannelState.Published, "a.example", "b.example");
        Header(await _client.GetAsync($"/use/{assistantId}", CancellationToken), "Content-Security-Policy")
            .ShouldEndWith("frame-ancestors https://a.example https://b.example");

        await RemoveDomainAsync(assistantId, "b.example");

        Header(await _client.GetAsync($"/use/{assistantId}", CancellationToken), "Content-Security-Policy")
            .ShouldEndWith("frame-ancestors https://a.example");

        await RemoveDomainAsync(assistantId, "a.example");

        var last = await _client.GetAsync($"/use/{assistantId}", CancellationToken);
        last.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Header(last, "Content-Security-Policy").ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Unpublishing_takes_effect_with_the_next_response()
    {
        var assistantId = await SeedAsync(WebsiteChannelState.Published, "a.example");
        (await _client.GetAsync($"/use/{assistantId}", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var dbContext = _host.Postgres.CreateDbContext(_organizationOf[assistantId]))
        {
            var channel = await dbContext.AssistantWebsiteChannels.SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
            channel.Unpublish(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        (await _client.GetAsync($"/use/{assistantId}", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Each_page_lists_only_its_own_assistants_domains_even_with_several_organizations()
    {
        var first = await SeedAsync(WebsiteChannelState.Published, "first.example");
        var second = await SeedAsync(WebsiteChannelState.Published, "second.example", "second-b.example");

        Header(await _client.GetAsync($"/use/{first}", CancellationToken), "Content-Security-Policy")
            .ShouldEndWith("frame-ancestors https://first.example");
        Header(await _client.GetAsync($"/use/{second}", CancellationToken), "Content-Security-Policy")
            .ShouldEndWith("frame-ancestors https://second-b.example https://second.example");
    }

    [Fact]
    public async Task The_page_needs_no_sign_in()
    {
        var assistantId = await SeedAsync(WebsiteChannelState.Published, "a.example");

        // The client sends no token and no cookie: the fallback authorization policy does not apply.
        var response = await _client.GetAsync($"/use/{assistantId}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("WWW-Authenticate").ShouldBeFalse();
    }

    // --- Decision D: opened in a tab -------------------------------------------------------------

    [Theory]
    [InlineData("document")]
    [InlineData("Document")]
    public async Task Opened_as_a_top_level_document_it_is_the_open_from_the_site_page_for_every_id(string dest)
    {
        var published = await SeedAsync(WebsiteChannelState.Published, "a.example");

        var forPublished = await GetWithDestAsync($"/use/{published}", dest);
        var forMissing = await GetWithDestAsync($"/use/{Guid.NewGuid()}", dest);

        forPublished.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await forPublished.Content.ReadAsStringAsync(CancellationToken);
        body.ShouldContain(OpenFromSiteText);
        body.ShouldNotContain("ngCspNonce");
        Header(forPublished, "Content-Security-Policy").ShouldContain("frame-ancestors 'none'");
        Header(forPublished, "Cache-Control").ShouldBe("no-store");
        (await WireFormAsync(forMissing)).ShouldBe(await WireFormAsync(forPublished));
    }

    [Theory]
    [InlineData("iframe")]
    [InlineData("frame")]
    [InlineData("empty")]
    [InlineData(null)]
    public async Task An_iframe_or_a_request_without_the_header_gets_the_chat_window(string? dest)
    {
        var published = await SeedAsync(WebsiteChannelState.Published, "a.example");

        var response = dest is null
            ? await _client.GetAsync($"/use/{published}", CancellationToken)
            : await GetWithDestAsync($"/use/{published}", dest);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("<app-root ngCspNonce=");
    }

    // --- PublicChannels:AllowLocalhostAncestors ----------------------------------------------------

    [Fact]
    public async Task With_allow_localhost_ancestors_a_published_channel_also_allows_localhost_on_any_port()
    {
        using var client = CreateClient(builder => builder.UseSetting("PublicChannels:AllowLocalhostAncestors", "true"));
        var published = await SeedAsync(WebsiteChannelState.Published, "a.example");
        var draft = await SeedAsync(WebsiteChannelState.Draft, "a.example");

        Header(await client.GetAsync($"/use/{published}", CancellationToken), "Content-Security-Policy")
            .ShouldEndWith("frame-ancestors https://a.example http://localhost:*");

        // Not for the neutral page: it is still 'none', the same bytes as without the setting.
        var unpublished = await client.GetAsync($"/use/{draft}", CancellationToken);
        Header(unpublished, "Content-Security-Policy").ShouldContain("frame-ancestors 'none'");
        Header(unpublished, "Content-Security-Policy").ShouldNotContain("localhost");
    }

    [Fact]
    public async Task Without_it_localhost_is_not_allowed()
    {
        var published = await SeedAsync(WebsiteChannelState.Published, "a.example");

        Header(await _client.GetAsync($"/use/{published}", CancellationToken), "Content-Security-Policy")
            .ShouldNotContain("localhost");
    }

    // --- Missing build ---------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_widget_build_the_page_says_so_with_503_for_every_id()
    {
        using var client = CreateClient(builder => builder.UseSetting("Widget:RootPath", Path.Combine(_directory, "not-built")));
        var published = await SeedAsync(WebsiteChannelState.Published, "a.example");

        foreach (var id in new[] { published, Guid.NewGuid() })
        {
            var response = await client.GetAsync($"/use/{id}", CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            body.ShouldContain("The chat window is not built into this server");
            body.ShouldContain("Widget:RootPath");
        }

        (await client.GetAsync("/widget/main-ABCD1234.js", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_index_without_an_app_root_is_a_503_not_a_page_whose_styles_would_be_blocked()
    {
        var root = Path.Combine(_directory, "broken");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><html><body>no root</body></html>");
        using var client = CreateClient(builder => builder.UseSetting("Widget:RootPath", root));
        var published = await SeedAsync(WebsiteChannelState.Published, "a.example");

        var response = await client.GetAsync($"/use/{published}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("<app-root>");
    }

    [Fact]
    public async Task A_rebuilt_widget_is_picked_up_without_restarting()
    {
        var published = await SeedAsync(WebsiteChannelState.Published, "a.example");
        (await (await _client.GetAsync($"/use/{published}", CancellationToken)).Content.ReadAsStringAsync(CancellationToken))
            .ShouldContain("main-ABCD1234.js");

        var index = Path.Combine(_widgetRoot, "index.html");
        File.WriteAllText(index, FakeIndex.Replace("main-ABCD1234.js", "main-ZZZZ9999.js", StringComparison.Ordinal) + "<!-- rebuilt -->");
        File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(1));

        (await (await _client.GetAsync($"/use/{published}", CancellationToken)).Content.ReadAsStringAsync(CancellationToken))
            .ShouldContain("main-ZZZZ9999.js");
    }

    // --- /widget/* -------------------------------------------------------------------------------

    [Theory]
    [InlineData("/widget/main-ABCD1234.js", "text/javascript", "public, max-age=31536000, immutable")]
    [InlineData("/widget/styles-ABCD1234.css", "text/css", "public, max-age=31536000, immutable")]
    [InlineData("/widget/notes.txt", "text/plain", "no-cache")]
    public async Task Widget_files_have_their_content_type_and_hashed_ones_are_immutable(string path, string contentType, string cacheControl)
    {
        var response = await _client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(contentType);
        Header(response, "Cache-Control").ShouldBe(cacheControl);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact]
    public async Task A_widget_file_is_served_as_it_was_built()
    {
        var response = await _client.GetAsync("/widget/main-ABCD1234.js", CancellationToken);

        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("console.log('fake widget');");
    }

    [Theory]
    [InlineData("/widget/index.html")]
    [InlineData("/widget/INDEX.HTML")]
    [InlineData("/widget/")]
    [InlineData("/widget")]
    [InlineData("/widget/nope.js")]
    [InlineData("/widget/blob.xyz")]
    [InlineData("/widget/..%2fsecret.txt")]
    [InlineData("/widget/%2e%2e%2fsecret.txt")]
    [InlineData("/widget/..%5csecret.txt")]
    public async Task Index_html_unknown_files_and_paths_that_leave_the_folder_are_404(string path)
    {
        var response = await _client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("outside the widget folder");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("<app-root>");
    }

    // --- /embed.js ---------------------------------------------------------------------------------

    [Fact]
    public async Task Embed_js_is_the_loader_file_cached_for_five_minutes()
    {
        var response = await _client.GetAsync("/embed.js", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/javascript; charset=utf-8");
        Header(response, "Cache-Control").ShouldBe("public, max-age=300");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Cross-Origin-Resource-Policy").ShouldBe("cross-origin");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("(function(){/* fake loader */})();");
    }

    [Fact]
    public async Task Without_the_loader_file_embed_js_is_a_503_that_says_which_setting()
    {
        using var client = CreateClient(builder => builder.UseSetting("Widget:EmbedScriptPath", Path.Combine(_directory, "missing.js")));

        var response = await client.GetAsync("/embed.js", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("Widget:EmbedScriptPath");
    }

    [Fact]
    public async Task The_real_embed_loader_is_what_the_default_setting_points_at_in_the_image_layout()
    {
        // The Dockerfile copies apps/embed-loader/src/embed.js to wwwroot/embed.js next to the app.
        var source = Path.Combine(FindRepositoryRoot(), "apps", "embed-loader", "src", "embed.js");
        File.Exists(source).ShouldBeTrue(source);
        using var client = CreateClient(builder => builder.UseSetting("Widget:EmbedScriptPath", source));

        var response = await client.GetAsync("/embed.js", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(await File.ReadAllTextAsync(source, CancellationToken));
    }

    // --- The cross-organization lookup behind /use/{id} ----------------------------------------------

    [Fact]
    public async Task The_lookup_finds_one_assistants_channel_across_organizations_and_reveals_nothing_else()
    {
        var first = await SeedAsync(WebsiteChannelState.Published, "a.example", "z.example");
        var second = await SeedAsync(WebsiteChannelState.Draft, "other.example");
        var withoutChannel = await SeedAsync(channelState: null);

        // Without an organization the filter hides every row ...
        await using (var unscoped = _host.Postgres.CreateDbContext())
        {
            (await unscoped.AssistantWebsiteChannels.CountAsync(CancellationToken)).ShouldBe(0);
        }

        // ... and the lookup is the one deliberate way around it.
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<PublicWebsiteChannelLookup>();

        var channel = (await lookup.FindAsync(first, CancellationToken)).ShouldNotBeNull();
        channel.State.ShouldBe(WebsiteChannelState.Published);
        channel.Domains.ShouldBe(["a.example", "z.example"]);

        var draft = (await lookup.FindAsync(second, CancellationToken)).ShouldNotBeNull();
        draft.State.ShouldBe(WebsiteChannelState.Draft);
        draft.Domains.ShouldBe(["other.example"]);

        (await lookup.FindAsync(withoutChannel, CancellationToken)).ShouldBeNull();
        (await lookup.FindAsync(Guid.NewGuid(), CancellationToken)).ShouldBeNull();

        // What it can return is the state and the host names: no organization, assistant or settings.
        typeof(PublicWebsiteChannel).GetProperties().Select(property => property.Name)
            .ShouldBe(["State", "Domains"], ignoreOrder: true);
    }

    // --- Helpers -----------------------------------------------------------------------------------

    private HttpClient CreateClient(Action<IWebHostBuilder> configure)
    {
        var factory = _host.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Widget:RootPath", _widgetRoot);
            builder.UseSetting("Widget:EmbedScriptPath", _embedScript);
            configure(builder);
        });
        _disposables.Add(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _disposables.Add(client);
        return client;
    }

    private async Task<HttpResponseMessage> GetWithDestAsync(string path, string dest)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Sec-Fetch-Dest", dest);
        return await _client.SendAsync(request, CancellationToken);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : string.Empty;

    /// <summary>Everything a client receives: status, every header (except the clock) and the body bytes.</summary>
    private static async Task<string> WireFormAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}");
        var body = Convert.ToHexString(await response.Content.ReadAsByteArrayAsync(CancellationToken));
        return $"{(int)response.StatusCode}\n{string.Join('\n', headers)}\n\n{body}";
    }

    /// <summary>A new organization with an assistant and, unless <paramref name="channelState"/> is
    /// <see langword="null"/>, a website channel in that state with <paramref name="domains"/>.</summary>
    private async Task<Guid> SeedAsync(WebsiteChannelState? channelState, params string[] domains)
    {
        var organization = await _host.CreateOrganizationAsync("嵌入測試組織");
        var owner = await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin);
        var now = _host.Clock.GetUtcNow().AddHours(-2);

        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var assistant = Assistant.Create(
            organization.Id, owner.Id, "客服助理", "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: false, now);
        dbContext.Assistants.Add(assistant);
        if (channelState is { } state)
        {
            var channel = new AssistantWebsiteChannel(
                assistant, "客服", "您好", WebsiteBrandColor.Forest, WebsiteLauncherPosition.BottomRight, now);
            if (state != WebsiteChannelState.Draft)
            {
                channel.Publish(owner.Id, now);
            }

            if (state == WebsiteChannelState.Paused)
            {
                channel.SetPaused(true, now);
            }

            dbContext.AssistantWebsiteChannels.Add(channel);
            dbContext.AssistantWebsiteDomains.AddRange(domains.Select(domain => new AssistantWebsiteDomain(channel, domain, now)));
        }

        await dbContext.SaveChangesAsync(CancellationToken);
        _organizationOf[assistant.Id] = organization.Id;
        return assistant.Id;
    }

    private async Task RemoveDomainAsync(Guid assistantId, string domain)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(_organizationOf[assistantId]);
        var row = await dbContext.AssistantWebsiteDomains
            .SingleAsync(candidate => candidate.AssistantId == assistantId && candidate.Domain == domain, CancellationToken);
        dbContext.AssistantWebsiteDomains.Remove(row);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "apps")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root (global.json) was not found above the test output directory.");
    }
}
