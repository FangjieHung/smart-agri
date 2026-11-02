using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Line;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// <c>Coordinator:Line</c> (store-coordinator design decision E, #372): both values turn the 統籌助理 on,
/// neither keeps it off — its webhook is then <c>404</c> — and only one refuses to start, saying which
/// setting is missing but never a value. Production hosts without a database: none of this reads one.
/// </summary>
public sealed class CoordinatorOptionsTests : IDisposable
{
    private const string CertificatePassword = "test-password";
    private const string SecretValue = "s3cr3t-value-never-shown-0123456789";
    private const string TokenValue = "t0k3n-value-never-shown-0123456789";

    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-coordinator-options-").FullName;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", " ")]
    [InlineData("secret", "token")]
    public void Both_or_neither_is_valid(string? secret, string? token)
    {
        var options = new CoordinatorLineOptions { ChannelSecret = secret, ChannelAccessToken = token };

        options.Validate().ShouldBeNull();
        options.IsConfigured.ShouldBe(secret == "secret");
    }

    [Theory]
    [InlineData(SecretValue, null, "Coordinator:Line:ChannelSecret is set but Coordinator:Line:ChannelAccessToken is not")]
    [InlineData(SecretValue, "", "Coordinator:Line:ChannelSecret is set but Coordinator:Line:ChannelAccessToken is not")]
    [InlineData(null, TokenValue, "Coordinator:Line:ChannelAccessToken is set but Coordinator:Line:ChannelSecret is not")]
    public void Only_one_names_the_missing_setting_and_no_value(string? secret, string? token, string expected)
    {
        var error = new CoordinatorLineOptions { ChannelSecret = secret, ChannelAccessToken = token }.Validate().ShouldNotBeNull();

        error.ShouldContain(expected);
        error.ShouldNotContain(SecretValue);
        error.ShouldNotContain(TokenValue);
    }

    [Theory]
    [InlineData("Coordinator:Line:ChannelSecret", SecretValue, "Coordinator:Line:ChannelAccessToken is not")]
    [InlineData("Coordinator:Line:ChannelAccessToken", TokenValue, "Coordinator:Line:ChannelSecret is not")]
    public void A_host_with_only_one_setting_refuses_to_start_and_says_why(string key, string value, string expected)
    {
        using var factory = ProductionHost(builder => builder.UseSetting(key, value));

        // ValidateOnStart fails inside app.Run(), so read the reason from the host (see StartupFailure).
        var message = StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message;
        message.ShouldContain(expected);
        message.ShouldNotContain(value);
    }

    [Fact]
    public async Task Without_settings_or_with_the_blank_values_compose_passes_the_webhook_is_404()
    {
        foreach (var configure in new Action<IWebHostBuilder>[]
                 {
                     _ => { },
                     builder => builder.UseSetting("Coordinator:Line:ChannelSecret", "").UseSetting("Coordinator:Line:ChannelAccessToken", ""),
                 })
        {
            using var factory = ProductionHost(configure);
            using var client = factory.CreateClient();

            (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = """{"destination":"U0","events":[]}""";
            var response = await PostAsync(client, body, LineWebhookSignature.Compute(SecretValue, Encoding.UTF8.GetBytes(body)));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task With_both_settings_a_bad_signature_is_401_and_a_signed_verify_is_200()
    {
        using var factory = ProductionHost(builder => builder
            .UseSetting("Coordinator:Line:ChannelSecret", SecretValue)
            .UseSetting("Coordinator:Line:ChannelAccessToken", TokenValue));
        using var client = factory.CreateClient();
        var body = """{"destination":"U0","events":[]}""";

        (await PostAsync(client, body, LineWebhookSignature.Compute("another-secret", Encoding.UTF8.GetBytes(body)))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(client, body, signature: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(client, body, LineWebhookSignature.Compute(SecretValue, Encoding.UTF8.GetBytes(body)))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void FB_and_Google_settings_are_both_or_neither()
    {
        new CoordinatorFacebookOptions().ShouldSatisfyAllConditions(options => options.Validate().ShouldBeNull(), options => options.IsConfigured.ShouldBeFalse());
        new CoordinatorFacebookOptions { AppId = "1234", AppSecret = SecretValue }.ShouldSatisfyAllConditions(
            options => options.Validate().ShouldBeNull(), options => options.IsConfigured.ShouldBeTrue());
        new CoordinatorGoogleOptions { ClientId = " ", ClientSecret = "" }.ShouldSatisfyAllConditions(
            options => options.Validate().ShouldBeNull(), options => options.IsConfigured.ShouldBeFalse());
        new CoordinatorGoogleOptions { ClientId = "id.apps.googleusercontent.com", ClientSecret = SecretValue }.IsConfigured.ShouldBeTrue();
        var error = new CoordinatorGoogleOptions { ClientSecret = SecretValue }.Validate().ShouldNotBeNull();
        error.ShouldContain("Coordinator:Google:ClientSecret is set but Coordinator:Google:ClientId is not");
        error.ShouldContain("Coordinator__Google__ClientId");
        error.ShouldNotContain(SecretValue);
    }

    [Theory]
    [InlineData("Coordinator:Facebook:AppId", "Coordinator:Facebook:AppSecret is not")]
    [InlineData("Coordinator:Facebook:AppSecret", "Coordinator:Facebook:AppId is not")]
    [InlineData("Coordinator:Google:ClientId", "Coordinator:Google:ClientSecret is not")]
    [InlineData("Coordinator:Google:ClientSecret", "Coordinator:Google:ClientId is not")]
    public void A_host_with_only_one_FB_or_Google_setting_refuses_to_start_and_says_why(string key, string expected)
    {
        using var factory = ProductionHost(builder => builder.UseSetting(key, SecretValue));

        var message = StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message;
        message.ShouldContain(expected);
        message.ShouldNotContain(SecretValue);
    }

    [Fact]
    public async Task Without_a_provider_or_a_public_base_URL_the_authorization_routes_answer_without_a_database()
    {
        // Settings alone make no provider (#377 registers none; #378/#379 do), and Production here has no
        // PublicChannels:PublicBaseUrl: the link page and the callback answer before anything is read.
        using var factory = ProductionHost(builder => builder
            .UseSetting("Coordinator:Facebook:AppId", "1234")
            .UseSetting("Coordinator:Facebook:AppSecret", SecretValue));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var link = await client.GetAsync($"/api/v1/coordinator/connect?token={SmartAgri.Domain.Coordinator.AuthorizationLinkTokens.Generate()}", CancellationToken);
        link.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await link.Content.ReadAsStringAsync(CancellationToken)).ShouldContain(CoordinatorAuthorizationPages.InvalidLinkMessage);
        (await client.GetAsync("/api/v1/coordinator/oauth/facebook/callback?code=c&state=s", CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_link_page_the_callback_and_the_selection_form_share_one_rate_limit_per_client()
    {
        // #377: two requests a minute here; none of these reaches the database (see the test above).
        using var factory = ProductionHost(builder => builder.UseSetting("PublicChannels:RateLimits:CoordinatorAuthorizationRequestsPerIpPerMinute", "2"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = $"/api/v1/coordinator/connect?token={SmartAgri.Domain.Coordinator.AuthorizationLinkTokens.Generate()}";
        using var form = new FormUrlEncodedContent([new("state", "s"), new("choice", "c")]);

        (await client.GetAsync(link, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync("/api/v1/coordinator/oauth/google/select", form, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var refused = await client.GetAsync("/api/v1/coordinator/oauth/facebook/callback?code=c&state=s", CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(60));
        (await client.GetAsync(link, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK, "nothing else is limited");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CoordinatorWebhookEndpoints.Route)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("x-line-signature", signature);
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private WebApplicationFactory<Program> ProductionHost(Action<IWebHostBuilder> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Authentication:SigningCertificatePath", WritePfx("signing", X509KeyUsageFlags.DigitalSignature));
            builder.UseSetting("Authentication:SigningCertificatePassword", CertificatePassword);
            builder.UseSetting("Authentication:EncryptionCertificatePath", WritePfx("encryption", X509KeyUsageFlags.KeyEncipherment));
            builder.UseSetting("Authentication:EncryptionCertificatePassword", CertificatePassword);
            DataProtectionTestSettings.Use(builder, _directory);
            configure(builder);
        });

    private string WritePfx(string name, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=smartagri-test-{name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(_directory, $"{name}-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
        return path;
    }
}
