using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// Where the token signing and encryption keys come from.
/// <list type="bullet">
/// <item><c>Development</c>: ephemeral in-memory keys, regenerated on every start (tokens
/// from a previous run simply stop validating).</item>
/// <item>Any other environment: certificates from
/// <see cref="SmartAgriAuthenticationOptions.SigningCertificatePath"/> and
/// <see cref="SmartAgriAuthenticationOptions.EncryptionCertificatePath"/>. If either is
/// missing or unreadable the Api refuses to start: silently falling back to ephemeral
/// keys would log everyone out on each restart and break any second instance.</item>
/// </list>
/// </summary>
public sealed class TokenCredentials
{
    private TokenCredentials(X509Certificate2? signing, X509Certificate2? encryption)
    {
        SigningCertificate = signing;
        EncryptionCertificate = encryption;
    }

    /// <summary>True in Development: use OpenIddict's ephemeral keys.</summary>
    public bool UseEphemeralKeys => SigningCertificate is null;

    public X509Certificate2? SigningCertificate { get; }

    public X509Certificate2? EncryptionCertificate { get; }

    /// <exception cref="InvalidOperationException">Outside Development, a certificate
    /// path is not configured, the file is missing, or it cannot be loaded with a private
    /// key.</exception>
    public static TokenCredentials Resolve(SmartAgriAuthenticationOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
        {
            return new TokenCredentials(null, null);
        }

        var signing = Load(
            options.SigningCertificatePath,
            options.SigningCertificatePassword,
            nameof(SmartAgriAuthenticationOptions.SigningCertificatePath),
            environment.EnvironmentName);
        var encryption = Load(
            options.EncryptionCertificatePath,
            options.EncryptionCertificatePassword,
            nameof(SmartAgriAuthenticationOptions.EncryptionCertificatePath),
            environment.EnvironmentName);
        return new TokenCredentials(signing, encryption);
    }

    private static X509Certificate2 Load(string? path, string? password, string settingName, string environmentName)
    {
        var setting = $"{SmartAgriAuthenticationOptions.SectionName}:{settingName}";

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Refusing to start: '{setting}' is not configured. Outside Development the Api needs a PKCS#12 " +
                $"certificate for token keys (environment: {environmentName}). See apps/api/README.md, \"Sign-in and tokens\".");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Refusing to start: '{setting}' points to '{path}', which does not exist.");
        }

        X509Certificate2 certificate;
        try
        {
            // EphemeralKeySet keeps the private key out of the user's key store (the
            // container runs as a non-root user); macOS does not support it.
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                $"Refusing to start: the certificate at '{setting}' could not be loaded (wrong password or not PKCS#12).",
                exception);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"Refusing to start: the certificate at '{setting}' has no private key.");
        }

        return certificate;
    }
}
