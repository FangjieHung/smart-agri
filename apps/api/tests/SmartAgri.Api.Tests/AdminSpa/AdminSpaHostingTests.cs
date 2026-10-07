using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.AdminSpa;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.AdminSpa;

/// <summary>
/// The admin SPA served by the Api itself when <c>Admin:RootPath</c> is set (pre-launch plan §3 E, #306):
/// its files, the SPA fallback to <c>index.html</c>, caching, and that every Api path answers exactly as
/// without it. The admin and the widget are tiny fake builds in a temporary folder, shaped like Angular's
/// output. No database needed: nothing here gets past a database-free answer.
/// </summary>
public sealed class AdminSpaHostingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminIndex =
        "<!doctype html>\n<html lang=\"zh-Hant\">\n<head><meta charset=\"utf-8\"/><title>fake-admin-index</title><base href=\"/\"/>"
        + "<link rel=\"stylesheet\" href=\"styles-WXYZ9876.css\"></head>\n<body><app-root></app-root>"
        + "<script src=\"main-WXYZ9876.js\" type=\"module\"></script></body>\n</html>\n";

    private const string AdminMarker = "fake-admin-index";

    private const string ImmutableCache = "public, max-age=31536000, immutable";

    private readonly WebApplicationFactory<Program> _base;
    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-admin-spa-").FullName;
    private readonly string _adminRoot;
    private readonly string _widgetRoot;
    private readonly string _embedScript;
    private readonly List<IDisposable> _disposables = [];

    public AdminSpaHostingTests(WebApplicationFactory<Program> factory)
    {
        _base = factory;
        _adminRoot = Path.Combine(_directory, "admin");
        Directory.CreateDirectory(Path.Combine(_adminRoot, "media"));
        File.WriteAllText(Path.Combine(_adminRoot, "index.html"), AdminIndex);
        File.WriteAllText(Path.Combine(_adminRoot, "main-WXYZ9876.js"), "console.log('fake admin');");
        File.WriteAllText(Path.Combine(_adminRoot, "styles-WXYZ9876.css"), "body{margin:0}");
        File.WriteAllText(Path.Combine(_adminRoot, "media", "font-QRST5432.woff2"), "fake font");
        foreach (var chunk in new[] { "chunk-B-N0blP72.js", "chunk-BgUK_noc.js", "chunk-B9545APi2.js" })
        {
            File.WriteAllText(Path.Combine(_adminRoot, chunk), "export {};");
        }

        File.WriteAllText(Path.Combine(_adminRoot, "favicon.ico"), "fake icon");
        File.WriteAllText(Path.Combine(_adminRoot, "brand-logo.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        File.WriteAllText(Path.Combine(_adminRoot, "blob.xyz"), "unknown type");
        File.WriteAllText(Path.Combine(_directory, "secret.txt"), "outside the admin folder");

        _widgetRoot = Path.Combine(_directory, "widget");
        Directory.CreateDirectory(_widgetRoot);
        File.WriteAllText(Path.Combine(_widgetRoot, "index.html"), "<!doctype html><title>fake-widget</title><app-root></app-root>");
        File.WriteAllText(Path.Combine(_widgetRoot, "main-ABCD1234.js"), "console.log('fake widget');");
        _embedScript = Path.Combine(_directory, "embed.js");
        File.WriteAllText(_embedScript, "(function(){/* fake loader */})();");
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

    // --- The admin's own paths -----------------------------------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/auth/callback?code=x&state=y")]
    [InlineData("/change-password")]
    [InlineData("/app/assistants/x")]
    [InlineData("/app/assistants/")]
    [InlineData("/chat/0b6f2f9e-1d2c-4e7a-9a43-1f0f8f3b2c11")]
    [InlineData("/index.html")]
    public async Task An_admin_route_is_index_html_never_cached(string path)
    {
        using var client = AdminClient();

        var response = await client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/html; charset=utf-8");
        Header(response, "Cache-Control").ShouldBe("no-cache");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(AdminIndex);
    }

    [Theory]
    [InlineData("/main-WXYZ9876.js", "text/javascript")]
    [InlineData("/styles-WXYZ9876.css", "text/css")]
    [InlineData("/media/font-QRST5432.woff2", "font/woff2")]
    // The application builder's chunk hashes are URL-safe base64, sometimes with a de-duplicating digit.
    [InlineData("/chunk-B-N0blP72.js", "text/javascript")]
    [InlineData("/chunk-BgUK_noc.js", "text/javascript")]
    [InlineData("/chunk-B9545APi2.js", "text/javascript")]
    public async Task A_hashed_file_is_cached_for_a_year(string path, string contentType)
    {
        using var client = AdminClient();

        var response = await client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(contentType);
        Header(response, "Cache-Control").ShouldBe(ImmutableCache);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain(AdminMarker);
    }

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/brand-logo.svg")]
    public async Task A_file_without_a_hash_must_be_revalidated(string path)
    {
        using var client = AdminClient();

        var response = await client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(response, "Cache-Control").ShouldBe("no-cache");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain(AdminMarker);
    }

    [Theory]
    [InlineData("/main-OLD12345.js")]
    [InlineData("/media/missing.png")]
    [InlineData("/app/assistants/report.pdf")]
    [InlineData("/blob.xyz")]
    [InlineData("/../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/media/..%2f..%2fsecret.txt")]
    public async Task A_missing_or_unservable_file_is_404_not_the_page(string path)
    {
        using var client = AdminClient();

        var response = await client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        body.ShouldNotContain(AdminMarker);
        body.ShouldNotContain("outside the admin folder");
    }

    [Fact]
    public async Task Head_is_answered_like_get_without_a_body()
    {
        using var client = AdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/login");

        var response = await client.SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(response, "Cache-Control").ShouldBe("no-cache");
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_post_to_an_admin_route_is_not_the_page()
    {
        using var admin = AdminClient();
        using var plain = PlainClient();

        var withAdmin = await admin.PostAsync("/login", new StringContent("x"), CancellationToken);
        var without = await plain.PostAsync("/login", new StringContent("x"), CancellationToken);

        (await WireFormAsync(withAdmin)).ShouldBe(await WireFormAsync(without));
        (await withAdmin.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain(AdminMarker);
    }

    [Fact]
    public async Task A_rebuilt_index_html_is_served_without_a_restart()
    {
        using var client = AdminClient();
        (await client.GetStringAsync("/", CancellationToken)).ShouldBe(AdminIndex);

        await File.WriteAllTextAsync(Path.Combine(_adminRoot, "index.html"), AdminIndex.Replace("WXYZ9876", "NEWHASH1", StringComparison.Ordinal), CancellationToken);

        (await client.GetStringAsync("/app/home", CancellationToken)).ShouldContain("main-NEWHASH1.js");
    }

    // --- The Api's paths are unaffected ---------------------------------------------------------------

    /// <summary>Every one answers byte for byte as it does without <c>Admin:RootPath</c> (status, headers but
    /// the clock, body), and none of them is the admin.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/me")]
    [InlineData("GET", "/api/v1/does-not-exist")]
    [InlineData("GET", "/api")]
    [InlineData("GET", "/API/v1/me")]
    [InlineData("POST", "/api/v1/auth/login")]
    [InlineData("GET", "/connect/authorize")]
    [InlineData("GET", "/connect/token")]
    [InlineData("GET", "/connect/unknown")]
    [InlineData("GET", "/.well-known/openid-configuration")]
    [InlineData("GET", "/.well-known/unknown")]
    [InlineData("GET", "/health/live")]
    [InlineData("GET", "/health/ready")]
    [InlineData("GET", "/health/unknown")]
    [InlineData("GET", "/use/0b6f2f9e-1d2c-4e7a-9a43-1f0f8f3b2c11")]
    [InlineData("GET", "/use")]
    [InlineData("GET", "/widget/main-ABCD1234.js")]
    [InlineData("GET", "/widget/index.html")]
    [InlineData("GET", "/widget/missing")]
    [InlineData("GET", "/embed.js")]
    [InlineData("GET", "/openapi/v1.json")]
    public async Task An_api_path_answers_exactly_as_without_the_admin(string method, string path)
    {
        using var admin = AdminClient();
        using var plain = PlainClient();

        var withAdmin = await SendAsync(admin, method, path);
        var without = await SendAsync(plain, method, path);

        (await WireFormAsync(withAdmin)).ShouldBe(await WireFormAsync(without));
        (await withAdmin.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain(AdminMarker);
    }

    [Fact]
    public void The_api_paths_are_the_fixed_ones_plus_the_first_literal_segment_of_every_mapped_route()
    {
        RouteEndpoint Route(string pattern) =>
            new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, EndpointMetadataCollection.Empty, pattern);

        var segments = AdminSpaHosting.ApiFirstSegments(
            [Route("/reports/{id}"), Route("/Exports"), Route("/{anything}/x"), Route("/file-{name}.txt"), Route("/")]);

        segments.Order(StringComparer.Ordinal).ShouldBe(
            [".well-known", "Exports", "api", "connect", "embed.js", "health", "openapi", "reports", "use", "widget"]);
        segments.Contains("REPORTS").ShouldBeTrue();
    }

    // --- Without Admin:RootPath -----------------------------------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/main-WXYZ9876.js")]
    public async Task Without_the_setting_no_admin_is_served(string path)
    {
        using var client = PlainClient();

        var response = await client.GetAsync(path, CancellationToken);

        // The fallback authorization policy answers anything without an endpoint, as before #306.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_blank_setting_is_the_same_as_none()
    {
        using var client = Client(builder => builder.UseSetting("Admin:RootPath", "  "));

        var response = await client.GetAsync("/", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Startup validation ---------------------------------------------------------------------------

    [Fact]
    public void A_root_path_that_is_not_a_folder_refuses_to_start_and_says_why()
    {
        var missing = Path.Combine(_directory, "no-such-folder");
        using var factory = _base.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Admin:RootPath", missing);
        });

        var exception = StartupFailure.Of(factory);

        exception.ShouldBeOfType<OptionsValidationException>().Message
            .ShouldContain($"Admin:RootPath '{missing}' (resolved to '{missing}') is not a folder.");
    }

    [Fact]
    public void A_root_path_without_index_html_refuses_to_start_and_says_why()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_directory, "empty")).FullName;
        using var factory = _base.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Admin:RootPath", empty);
        });

        var exception = StartupFailure.Of(factory);

        exception.ShouldBeOfType<OptionsValidationException>().Message
            .ShouldContain($"Admin:RootPath '{empty}' (resolved to '{empty}') has no index.html.");
    }

    // --- Helpers -----------------------------------------------------------------------------------------

    private HttpClient AdminClient() => Client(builder => builder.UseSetting("Admin:RootPath", _adminRoot));

    private HttpClient PlainClient() => Client(_ => { });

    private WebApplicationFactory<Program> Factory(Action<IWebHostBuilder> configure) =>
        _base.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Jobs:WorkerEnabled", "false");
            builder.UseSetting("Widget:RootPath", _widgetRoot);
            builder.UseSetting("Widget:EmbedScriptPath", _embedScript);
            configure(builder);
        });

    private HttpClient Client(Action<IWebHostBuilder> configure)
    {
        var factory = Factory(configure);
        _disposables.Add(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _disposables.Add(client);
        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        if (path.StartsWith("/use/", StringComparison.Ordinal))
        {
            // Opened as a page: the chat window's fixed answer, no database lookup.
            request.Headers.Add("Sec-Fetch-Dest", "document");
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : string.Empty;

    /// <summary>Everything a client receives: status, every header (except the clock and per-response
    /// random values) and the body bytes.</summary>
    private static async Task<string> WireFormAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}");
        var body = Convert.ToHexString(await response.Content.ReadAsByteArrayAsync(CancellationToken));
        return $"{(int)response.StatusCode}\n{string.Join('\n', headers)}\n\n{body}";
    }
}
