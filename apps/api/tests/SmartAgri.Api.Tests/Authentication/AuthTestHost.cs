using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Seeding;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// One Postgres container and one Api host per test class, for the sign-in integration
/// tests. Applies migrations and registers the <c>admin-spa</c> client exactly as the
/// <c>migrate</c> subcommand does. Test data (organizations, accounts) is created by each
/// test through tenancy-aware contexts — seeding is a later slice.
/// </summary>
public class AuthHostFixture : IAsyncLifetime
{
    public const string SpaOrigin = "http://localhost:4200";
    public const string RedirectUri = SpaOrigin + "/auth/callback";

    /// <summary>Satisfies ASP.NET Core Identity's default password rules, for
    /// <c>SEED_DEMO_PASSWORD</c> in tests that exercise <c>DevelopmentSeeder</c>.</summary>
    public const string SeedDemoPassword = "Seed-Demo-Password-1!";

    private readonly PostgresFixture _postgres = new();
    private AuthApiFactory? _factory;

    public PostgresFixture Postgres => _postgres;

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    /// <summary>The clock the host uses (OpenIddict, Identity, cookies).</summary>
    public TestClock Clock { get; } = new();

    public virtual async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();

        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.Database.MigrateAsync();
        }

        _factory = new AuthApiFactory(_postgres.ConnectionString, Clock);
        await using var scope = _factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AdminSpaClientRegistrar>().EnsureAsync()).ShouldBeTrue();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A browser-like client: keeps cookies, does not follow redirects.</summary>
    public SpaClient CreateSpaClient() =>
        new(Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    public async Task<Organization> CreateOrganizationAsync(string name = "測試組織")
    {
        var organization = new Organization(Guid.CreateVersion7(), name, "t" + Guid.NewGuid().ToString("N")[..12]);
        await using var dbContext = _postgres.CreateDbContext();
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync();
        return organization;
    }

    public async Task<Account> CreateAccountAsync(
        Organization organization,
        string loginName,
        string password,
        AccountRole role = AccountRole.SmbAdmin,
        string? displayName = null,
        params AccountPermission[] permissions)
    {
        var account = Account.Create(organization, loginName, displayName ?? $"{organization.Name} {loginName}", role);
        account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, password);

        // Acting for the account's own organization: the write guard accepts the rows.
        await using var dbContext = _postgres.CreateDbContext(organization.Id);
        dbContext.Accounts.Add(account);
        dbContext.AccountPermissions.AddRange(permissions.Select(permission => new AccountPermissionGrant(account, permission)));
        await dbContext.SaveChangesAsync();
        return account;
    }

    /// <summary>Flags <paramref name="account"/> as "must change password" (as <c>setup</c> does).</summary>
    public async Task RequirePasswordChangeAsync(Account account)
    {
        await using var dbContext = _postgres.CreateDbContext(account.OrganizationId);
        var tracked = await dbContext.Accounts.SingleAsync(candidate => candidate.Id == account.Id);
        tracked.RequirePasswordChange();
        await dbContext.SaveChangesAsync();
    }

    private sealed class AuthApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly TimeProvider _clock;

        public AuthApiFactory(string connectionString, TimeProvider clock)
        {
            _connectionString = connectionString;
            _clock = clock;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", _connectionString);
            builder.UseSetting("Authentication:AdminSpa:Origins:0", SpaOrigin);
            builder.UseSetting(DevelopmentSeeder.PasswordConfigurationKey, SeedDemoPassword);
            builder.ConfigureServices(services => services.AddSingleton(_clock).AddProtectedProbeEndpoint());
        }
    }
}

/// <summary>A settable clock starting at the real current time.</summary>
public sealed class TestClock : TimeProvider
{
    private TimeSpan _offset = TimeSpan.Zero;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;

    public void Advance(TimeSpan by) => _offset += by;
}

/// <summary>Drives the admin SPA's side of the flow: login, authorization code + PKCE, token, API calls.</summary>
public sealed class SpaClient : IDisposable
{
    public SpaClient(HttpClient http)
    {
        Http = http;
    }

    public HttpClient Http { get; }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Http.Dispose();

    public Task<HttpResponseMessage> LoginAsync(string? organizationCode, string loginName, string password) =>
        Http.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { organizationCode, loginName, password },
            CancellationToken);

    public Task<HttpResponseMessage> AuthorizeAsync(string codeVerifier, string state = "state-1") =>
        Http.GetAsync(AuthorizeUrl(codeVerifier, state), CancellationToken);

    public static string AuthorizeUrl(string codeVerifier, string state) =>
        QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = AdminSpaClientOptions.ClientId,
            ["redirect_uri"] = AuthHostFixture.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["state"] = state,
            ["code_challenge"] = CodeChallenge(codeVerifier),
            ["code_challenge_method"] = "S256",
        });

    /// <summary>Login (expects 204) → authorize (expects a code) → token. Returns the token response.</summary>
    public async Task<TokenResponse> SignInAsync(string? organizationCode, string loginName, string password)
    {
        var login = await LoginAsync(organizationCode, loginName, password);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var verifier = NewCodeVerifier();
        var authorize = await AuthorizeAsync(verifier, "state-xyz");
        authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = authorize.Headers.Location.ShouldNotBeNull();
        location.IsAbsoluteUri.ShouldBeTrue(location.ToString());
        location.GetLeftPart(UriPartial.Path).ShouldBe(AuthHostFixture.RedirectUri);
        var callback = QueryHelpers.ParseQuery(location.Query);
        callback["state"].ToString().ShouldBe("state-xyz");
        var code = callback["code"].ToString();
        code.ShouldNotBeNullOrEmpty();

        return await RedeemAsync(code, verifier);
    }

    public async Task<TokenResponse> RedeemAsync(string code, string codeVerifier)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = AuthHostFixture.RedirectUri,
            ["client_id"] = AdminSpaClientOptions.ClientId,
            ["code_verifier"] = codeVerifier,
        });
        var response = await Http.PostAsync("/connect/token", form, CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new TokenResponse(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out _),
            root.GetProperty("expires_in").GetInt32());
    }

    public async Task<HttpResponseMessage> GetMeAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Http.SendAsync(request, CancellationToken);
    }

    public async Task<JsonElement> GetMeJsonAsync(string accessToken)
    {
        var response = await GetMeAsync(accessToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>GET with the bearer token.</summary>
    public async Task<HttpResponseMessage> GetAsync(string path, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Http.SendAsync(request, CancellationToken);
    }

    /// <summary><c>POST /api/v1/auth/change-password</c> with the bearer token (none when <see langword="null"/>).</summary>
    public async Task<HttpResponseMessage> ChangePasswordAsync(string? accessToken, string? currentPassword, string? newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new { currentPassword, newPassword }),
        };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await Http.SendAsync(request, CancellationToken);
    }

    public static string NewCodeVerifier() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string CodeChallenge(string verifier) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>The JWT payload of <paramref name="accessToken"/> (signature not checked here).</summary>
    public static JsonElement DecodePayload(string accessToken)
    {
        var parts = accessToken.Split('.');
        parts.Length.ShouldBe(3, "the access token should be a signed, unencrypted JWT");
        return JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1])).RootElement.Clone();
    }
}

public sealed record TokenResponse(string AccessToken, bool HasRefreshToken, int ExpiresInSeconds);

/// <summary>The observable parts of a response, for "identical response" comparisons.</summary>
public sealed record ResponseFingerprint(HttpStatusCode Status, string? ContentType, byte[] Body, bool SetsCookie)
{
    public static async Task<ResponseFingerprint> FromAsync(HttpResponseMessage response) =>
        new(
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken),
            response.Headers.Contains("Set-Cookie"));
}
