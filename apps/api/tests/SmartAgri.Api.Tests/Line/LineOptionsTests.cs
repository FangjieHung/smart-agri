using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Line;
using SmartAgri.Infrastructure.Line;

namespace SmartAgri.Api.Tests.Line;

/// <summary>
/// <c>Line:ApiBaseUrl</c> (M5b plan §3 H): LINE's own address by default; another one (a fake LINE
/// server) anywhere but Production, which refuses to start with it — the channel access tokens would
/// be sent there. Also the registered client's base address and 10-second timeout. No database needed.
/// </summary>
public sealed class LineOptionsTests : IDisposable
{
    private const string CertificatePassword = "test-password";

    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-line-options-").FullName;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://api.line.me")]
    [InlineData("https://api.line.me/")]
    [InlineData(" HTTPS://API.LINE.ME:443/ ")]
    public void The_official_address_is_the_default_and_accepted_in_production(string? value)
    {
        var options = new LineOptions { ApiBaseUrl = value };

        options.ResolvedApiBaseUrl.ShouldBe(new Uri("https://api.line.me/"));
        options.IsOfficial.ShouldBeTrue();
        options.Validate("Production").ShouldBeNull();
        options.Validate("Development").ShouldBeNull();
    }

    [Theory]
    [InlineData("http://localhost:5199")]
    [InlineData("http://api.line.me")]
    [InlineData("https://api.line.me.evil.example")]
    [InlineData("https://api.line.me/proxy")]
    [InlineData("https://api-data.line.me")]
    public void Any_other_address_works_outside_production_and_refuses_production(string value)
    {
        var options = new LineOptions { ApiBaseUrl = value };

        options.IsOfficial.ShouldBeFalse();
        options.Validate("Development").ShouldBeNull();
        options.Validate("Testing").ShouldBeNull();
        options.Validate("Production")!.ShouldContain("Line:ApiBaseUrl must be https://api.line.me");
    }

    [Theory]
    [InlineData("api.line.me")]
    [InlineData("ftp://api.line.me")]
    [InlineData("https://api.line.me/?a=1")]
    [InlineData("https://user:pass@api.line.me")]
    public void A_malformed_address_refuses_to_start_anywhere(string value)
    {
        var options = new LineOptions { ApiBaseUrl = value };

        options.ResolvedApiBaseUrl.ShouldBeNull();
        options.Validate("Development")!.ShouldContain("must be an absolute http(s) URL");
    }

    [Fact]
    public void A_production_host_with_a_non_official_line_api_base_url_refuses_to_start_and_says_why()
    {
        using var factory = ProductionHost(builder => builder.UseSetting("Line:ApiBaseUrl", "http://fake-line.test"));

        // ValidateOnStart fails inside app.Run(), so read the reason from the host (see StartupFailure).
        StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message
            .ShouldContain("Line:ApiBaseUrl must be https://api.line.me (or unset) in Production, not 'http://fake-line.test'");
    }

    [Fact]
    public async Task A_production_host_without_line_settings_starts_with_the_official_address_and_a_ten_second_timeout()
    {
        using var factory = ProductionHost(_ => { });
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(LineMessagingClient.HttpClientName);
        http.BaseAddress.ShouldBe(new Uri("https://api.line.me/"));
        http.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILineMessagingClient>().ShouldBeOfType<LineMessagingClient>();
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
