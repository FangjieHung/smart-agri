using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.OpenApi;
using SmartAgri.Application.Secrets;
using SmartAgri.Infrastructure.Secrets;

namespace SmartAgri.Api.DataProtection;

public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>The application name every instance of the Api shares; a key ring is only shared
    /// between processes that use the same one.</summary>
    public const string ApplicationName = "SmartAgri";

    /// <summary>
    /// Data Protection with a key ring at a fixed place, encrypted with a certificate, plus
    /// <see cref="ISecretProtector"/>. Identity's sign-in cookie, OpenIddict's tokens (and the
    /// website visitor token, later) and stored secret settings all use this one key ring, so it
    /// has to survive container rebuilds: a key ring that is regenerated signs everyone out and
    /// makes every stored secret unreadable.
    /// </summary>
    /// <remarks>
    /// <c>Development</c> and <c>Testing</c> keep it frictionless: with no <c>DataProtection:KeysPath</c>
    /// ASP.NET Core's own default is used (a per-user folder outside the repository) and the keys are
    /// not encrypted; setting the options still applies them. Any other environment refuses to start
    /// without both <see cref="SmartAgriDataProtectionOptions.KeysPath"/> and the certificate, for the
    /// same reason the token certificates are mandatory there (see <see cref="TokenCredentials"/>).
    /// </remarks>
    /// <exception cref="InvalidOperationException">Outside Development and Testing, the key directory
    /// or certificate is not configured or unusable.</exception>
    public static WebApplicationBuilder AddSmartAgriDataProtection(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(SmartAgriDataProtectionOptions.SectionName);
        var options = section.Get<SmartAgriDataProtectionOptions>() ?? new SmartAgriDataProtectionOptions();

        var dataProtection = builder.Services.AddDataProtection().SetApplicationName(ApplicationName);

        // Microsoft.Extensions.ApiDescription.Server runs this composition root at build time
        // without starting the server (BuildTimeOpenApi): no real key directory or certificate then.
        if (!BuildTimeOpenApi.IsGeneratingDocument)
        {
            var strict = !(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
            var keysPath = ResolveKeysPath(options, strict, builder.Environment.EnvironmentName);
            if (keysPath is not null)
            {
                dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
            }

            var certificate = ResolveCertificate(options, strict, builder.Environment.EnvironmentName);
            if (certificate is not null)
            {
                dataProtection.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
            }
        }

        builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return builder;
    }

    private static string? ResolveKeysPath(SmartAgriDataProtectionOptions options, bool strict, string environmentName)
    {
        var setting = $"{SmartAgriDataProtectionOptions.SectionName}:{nameof(SmartAgriDataProtectionOptions.KeysPath)}";

        if (string.IsNullOrWhiteSpace(options.KeysPath))
        {
            return strict
                ? throw new InvalidOperationException(
                    $"Refusing to start: '{setting}' is not configured. Outside Development the Api needs a directory for the " +
                    $"Data Protection key ring that survives restarts (environment: {environmentName}). " +
                    "See apps/api/README.md, \"Data Protection key ring\".")
                : null;
        }

        // Fail here, with the path in the message, rather than on the first sign-in: a directory the
        // process cannot write to (a volume owned by another user) would only show up as a 500 then.
        try
        {
            var directory = Directory.CreateDirectory(options.KeysPath);
            var probe = Path.Combine(directory.FullName, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Refusing to start: '{setting}' points to '{options.KeysPath}', which cannot be created or written to.",
                exception);
        }

        return options.KeysPath;
    }

    private static X509Certificate2? ResolveCertificate(
        SmartAgriDataProtectionOptions options,
        bool strict,
        string environmentName)
    {
        if (!strict && string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            return null;
        }

        return StartupCertificate.Load(
            options.CertificatePath,
            options.CertificatePassword,
            $"{SmartAgriDataProtectionOptions.SectionName}:{nameof(SmartAgriDataProtectionOptions.CertificatePath)}",
            "encrypting the Data Protection key ring",
            "Data Protection key ring",
            environmentName);
    }
}
