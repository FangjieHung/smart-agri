using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// The <c>Fake</c> chat provider exists only in Development and Testing: a Production host
/// configured with it refuses to start, and a Production host with no chat provider at all still
/// starts (M3 plan, Slice 4; the chat-side twin of <see cref="EmbeddingStartupTests"/>). No
/// database needed (nothing here opens a connection).
/// </summary>
public sealed class ChatStartupTests : IDisposable
{
    private const string CertificatePassword = "test-password";

    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-chat-keys-").FullName;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_production_host_configured_with_the_fake_chat_provider_refuses_to_start_and_says_why()
    {
        using var factory = ProductionHost(builder => builder
            .UseSetting("Ai:Chat:Provider", "Fake")
            .UseSetting("Ai:Chat:Model", "fake-chat-a"));

        // ValidateOnStart fails inside app.Run(), so read the reason from the host (see StartupFailure).
        var exception = StartupFailure.Of(factory);

        exception.ShouldBeOfType<OptionsValidationException>().Message.ShouldContain("Ai:Chat:Provider=Fake is only allowed in the Development and Testing environments, not in 'Production'");
    }

    [Fact]
    public async Task A_production_host_without_a_chat_provider_still_starts()
    {
        using var factory = ProductionHost(_ => { });
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
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
