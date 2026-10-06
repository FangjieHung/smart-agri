using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Api.Tests.DataProtection;

/// <summary>
/// The Data Protection key ring (M5a Slice 1): kept in a directory the deployment names, encrypted
/// with a certificate, and required outside Development/Testing. Real hosts, no database (nothing
/// here opens a connection).
/// </summary>
public sealed class DataProtectionKeyRingTests : IDisposable
{
    private const string Purpose = "test.channel-secret";
    private const string Plaintext = "line-channel-secret-value-4321";
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-dp-").FullName;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_secret_protected_before_the_host_was_rebuilt_unprotects_with_the_same_key_directory()
    {
        var keys = Path.Combine(_directory, "keys");
        var certificate = DataProtectionTestSettings.WriteCertificate(_directory);

        ProtectedSecret stored;
        using (var first = ProductionHost(keys, certificate))
        {
            stored = first.Services.GetRequiredService<ISecretProtector>().Protect(Purpose, Plaintext, Now);
        }

        stored.Ciphertext.ShouldNotContain(Plaintext);
        stored.LastFour.ShouldBe("4321");
        Directory.GetFiles(keys, "key-*.xml").ShouldNotBeEmpty("the first host should have written its key to the directory");

        using var rebuilt = ProductionHost(keys, certificate);
        rebuilt.Services.GetRequiredService<ISecretProtector>().Unprotect(Purpose, stored).ShouldBe(Plaintext);
    }

    [Fact]
    public void A_different_empty_key_directory_cannot_unprotect_and_says_why()
    {
        var certificate = DataProtectionTestSettings.WriteCertificate(_directory);

        ProtectedSecret stored;
        using (var first = ProductionHost(Path.Combine(_directory, "keys-a"), certificate))
        {
            stored = first.Services.GetRequiredService<ISecretProtector>().Protect(Purpose, Plaintext, Now);
        }

        using var other = ProductionHost(Path.Combine(_directory, "keys-b"), certificate);
        var exception = Should.Throw<SecretUnprotectException>(
            () => other.Services.GetRequiredService<ISecretProtector>().Unprotect(Purpose, stored));

        exception.Purpose.ShouldBe(Purpose);
        exception.Message.ShouldContain("Enter the secret again");
        exception.Message.ShouldNotContain(Plaintext);
        exception.Message.ShouldNotContain(stored.Ciphertext);
    }

    [Fact]
    public void A_secret_does_not_unprotect_under_another_purpose()
    {
        using var host = ProductionHost(Path.Combine(_directory, "keys"), DataProtectionTestSettings.WriteCertificate(_directory));
        var protector = host.Services.GetRequiredService<ISecretProtector>();
        var stored = protector.Protect(Purpose, Plaintext, Now);

        Should.Throw<SecretUnprotectException>(() => protector.Unprotect("test.other-secret", stored));
    }

    [Fact]
    public void The_key_files_are_encrypted_with_the_certificate()
    {
        var keys = Path.Combine(_directory, "keys");
        using (var host = ProductionHost(keys, DataProtectionTestSettings.WriteCertificate(_directory)))
        {
            host.Services.GetRequiredService<ISecretProtector>().Protect(Purpose, Plaintext, Now);
        }

        var keyFile = File.ReadAllText(Directory.GetFiles(keys, "key-*.xml").ShouldHaveSingleItem());
        keyFile.ShouldContain("EncryptedData");
        keyFile.ShouldNotContain("<masterKey");
    }

    [Theory]
    [InlineData(false, false, "DataProtection:KeysPath")]
    [InlineData(true, false, "DataProtection:CertificatePath")]
    [InlineData(false, true, "DataProtection:KeysPath")]
    public void A_production_host_without_the_key_directory_or_certificate_refuses_to_start(bool withKeys, bool withCertificate, string missing)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            UseProduction(builder);
            if (withKeys)
            {
                builder.UseSetting("DataProtection:KeysPath", Path.Combine(_directory, "keys"));
            }

            if (withCertificate)
            {
                builder.UseSetting("DataProtection:CertificatePath", DataProtectionTestSettings.WriteCertificate(_directory));
                builder.UseSetting("DataProtection:CertificatePassword", DataProtectionTestSettings.Password);
            }
        });

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        exception.ToString().ShouldContain("Refusing to start");
        exception.ToString().ShouldContain(missing);
    }

    [Fact]
    public void A_certificate_path_to_a_missing_file_is_refused()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            UseProduction(builder);
            DataProtectionTestSettings.Use(builder, Path.Combine(_directory, "keys"), Path.Combine(_directory, "nope.pfx"));
        });

        Should.Throw<Exception>(() => factory.CreateClient()).ToString().ShouldContain("DataProtection:CertificatePath");
    }

    [Fact]
    public void A_key_directory_that_is_a_file_is_refused_naming_the_path()
    {
        var notADirectory = Path.Combine(_directory, "keys");
        File.WriteAllText(notADirectory, "x");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            UseProduction(builder);
            DataProtectionTestSettings.Use(builder, notADirectory, DataProtectionTestSettings.WriteCertificate(_directory));
        });

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        exception.ToString().ShouldContain("DataProtection:KeysPath");
        exception.ToString().ShouldContain(notADirectory);
    }

    [Fact]
    public async Task A_production_host_with_both_starts()
    {
        using var host = ProductionHost(Path.Combine(_directory, "keys"), DataProtectionTestSettings.WriteCertificate(_directory));
        using var client = host.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void A_development_host_starts_without_any_data_protection_settings()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString));

        factory.Services.GetRequiredService<ISecretProtector>().ShouldNotBeNull();
    }

    [Fact]
    public void A_testing_host_needs_no_data_protection_settings_either()
    {
        // Testing is not relaxed for the token certificates, only for the key ring.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            UseProduction(builder);
            builder.UseEnvironment("Testing");
        });

        factory.Services.GetRequiredService<ISecretProtector>().ShouldNotBeNull();
    }

    private WebApplicationFactory<Program> ProductionHost(string keysPath, string certificatePath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            UseProduction(builder);
            DataProtectionTestSettings.Use(builder, keysPath, certificatePath);
        });

    private void UseProduction(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
        builder.UseSetting("Authentication:SigningCertificatePath", WriteTokenCertificate("signing", X509KeyUsageFlags.DigitalSignature));
        builder.UseSetting("Authentication:SigningCertificatePassword", DataProtectionTestSettings.Password);
        builder.UseSetting("Authentication:EncryptionCertificatePath", WriteTokenCertificate("encryption", X509KeyUsageFlags.KeyEncipherment));
        builder.UseSetting("Authentication:EncryptionCertificatePassword", DataProtectionTestSettings.Password);
    }

    private string WriteTokenCertificate(string name, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=smartagri-test-{name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(_directory, $"{name}-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, DataProtectionTestSettings.Password));
        return path;
    }
}
