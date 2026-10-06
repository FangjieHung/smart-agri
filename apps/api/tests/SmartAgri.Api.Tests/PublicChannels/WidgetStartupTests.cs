using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>
/// <c>PublicChannels:AllowLocalhostAncestors</c> is for a developer's machine: a Production host with it
/// on refuses to start (M5a plan §3 B; #201). No database needed (nothing here opens a connection).
/// </summary>
public sealed class WidgetStartupTests : IDisposable
{
    private const string CertificatePassword = "test-password";

    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-widget-startup-").FullName;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_production_host_with_localhost_ancestors_refuses_to_start_and_says_why()
    {
        using var factory = ProductionHost(builder => builder.UseSetting("PublicChannels:AllowLocalhostAncestors", "true"));

        var exception = StartupFailure.Of(factory);

        exception.ShouldBeOfType<OptionsValidationException>().Message
            .ShouldContain("PublicChannels:AllowLocalhostAncestors=true is only allowed in the Development and Testing environments, not in 'Production'");
    }

    [Fact]
    public async Task A_production_host_without_it_starts_and_serves_embed_js_when_the_file_exists()
    {
        var script = Path.Combine(_directory, "embed.js");
        await File.WriteAllTextAsync(script, "/* loader */", CancellationToken);
        using var factory = ProductionHost(builder => builder.UseSetting("Widget:EmbedScriptPath", script));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/embed.js", CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Headers.GetValues("Cache-Control").ShouldBe(["public, max-age=300"]);
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
