using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// The <c>DataProtection</c> settings a non-Development test host needs to start (outside
/// Development the Api refuses to start without a key directory and certificate). Everything is
/// written under a directory the test owns and deletes, never into the repository or the user's
/// profile.
/// </summary>
internal static class DataProtectionTestSettings
{
    public const string Password = "test-password";

    /// <summary>Gives <paramref name="builder"/> a key directory and certificate inside <paramref name="directory"/>.</summary>
    public static void Use(IWebHostBuilder builder, string directory)
    {
        Use(builder, Path.Combine(directory, "dp-keys"), WriteCertificate(directory));
    }

    /// <summary>Fluent form of <see cref="Use(IWebHostBuilder, string)"/>.</summary>
    public static IWebHostBuilder ConfigureDataProtection(this IWebHostBuilder builder, string directory)
    {
        Use(builder, directory);
        return builder;
    }

    public static void Use(IWebHostBuilder builder, string keysPath, string certificatePath)
    {
        builder.UseSetting("DataProtection:KeysPath", keysPath);
        builder.UseSetting("DataProtection:CertificatePath", certificatePath);
        builder.UseSetting("DataProtection:CertificatePassword", Password);
    }

    /// <summary>Writes a new self-signed certificate (key encipherment) and returns its path.</summary>
    public static string WriteCertificate(string directory)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=smartagri-test-dataprotection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(directory, $"dataprotection-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return path;
    }
}
