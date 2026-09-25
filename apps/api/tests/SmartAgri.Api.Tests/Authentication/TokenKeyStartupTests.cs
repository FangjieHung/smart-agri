using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// Development uses ephemeral token keys; any other environment must be given
/// certificates or the Api refuses to start. No database needed (startup never opens a
/// connection).
/// </summary>
public class TokenKeyStartupTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "test-password";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-keys-").FullName;

    public TokenKeyStartupTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Development_uses_ephemeral_keys()
    {
        TokenCredentials.Resolve(new SmartAgriAuthenticationOptions(), Environment(Environments.Development))
            .UseEphemeralKeys.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_development_missing_key_paths_are_refused(string environmentName)
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => TokenCredentials.Resolve(new SmartAgriAuthenticationOptions(), Environment(environmentName)));

        exception.Message.ShouldContain("Refusing to start");
        exception.Message.ShouldContain("Authentication:SigningCertificatePath");
    }

    [Fact]
    public void Missing_encryption_certificate_is_refused_even_with_a_signing_certificate()
    {
        var options = new SmartAgriAuthenticationOptions
        {
            SigningCertificatePath = WritePfx("signing", X509KeyUsageFlags.DigitalSignature),
            SigningCertificatePassword = Password,
        };

        Should.Throw<InvalidOperationException>(() => TokenCredentials.Resolve(options, Environment("Production")))
            .Message.ShouldContain("Authentication:EncryptionCertificatePath");
    }

    [Fact]
    public void A_path_to_a_missing_file_is_refused()
    {
        var options = new SmartAgriAuthenticationOptions
        {
            SigningCertificatePath = Path.Combine(_directory, "nope.pfx"),
            EncryptionCertificatePath = Path.Combine(_directory, "nope.pfx"),
        };

        Should.Throw<InvalidOperationException>(() => TokenCredentials.Resolve(options, Environment("Production")))
            .Message.ShouldContain("does not exist");
    }

    [Fact]
    public void A_wrong_certificate_password_is_refused()
    {
        var options = new SmartAgriAuthenticationOptions
        {
            SigningCertificatePath = WritePfx("signing", X509KeyUsageFlags.DigitalSignature),
            SigningCertificatePassword = "wrong",
            EncryptionCertificatePath = WritePfx("encryption", X509KeyUsageFlags.KeyEncipherment),
            EncryptionCertificatePassword = Password,
        };

        Should.Throw<InvalidOperationException>(() => TokenCredentials.Resolve(options, Environment("Production")))
            .Message.ShouldContain("could not be loaded");
    }

    [Fact]
    public void Configured_certificates_are_loaded_outside_development()
    {
        var credentials = TokenCredentials.Resolve(ConfiguredOptions(), Environment("Production"));

        credentials.UseEphemeralKeys.ShouldBeFalse();
        credentials.SigningCertificate!.HasPrivateKey.ShouldBeTrue();
        credentials.EncryptionCertificate!.HasPrivateKey.ShouldBeTrue();
    }

    [Fact]
    public void The_production_host_refuses_to_start_without_key_paths()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString));

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        exception.ToString().ShouldContain("Authentication:SigningCertificatePath");
    }

    [Fact]
    public async Task The_production_host_starts_with_configured_certificates()
    {
        var options = ConfiguredOptions();
        using var factory = _factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString)
            .UseSetting("Authentication:SigningCertificatePath", options.SigningCertificatePath)
            .UseSetting("Authentication:SigningCertificatePassword", options.SigningCertificatePassword)
            .UseSetting("Authentication:EncryptionCertificatePath", options.EncryptionCertificatePath)
            .UseSetting("Authentication:EncryptionCertificatePassword", options.EncryptionCertificatePassword));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private SmartAgriAuthenticationOptions ConfiguredOptions() => new()
    {
        SigningCertificatePath = WritePfx("signing", X509KeyUsageFlags.DigitalSignature),
        SigningCertificatePassword = Password,
        EncryptionCertificatePath = WritePfx("encryption", X509KeyUsageFlags.KeyEncipherment),
        EncryptionCertificatePassword = Password,
    };

    private string WritePfx(string name, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=smartagri-test-{name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(_directory, $"{name}-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return path;
    }

    private static HostingEnvironment Environment(string name) => new() { EnvironmentName = name };
}
