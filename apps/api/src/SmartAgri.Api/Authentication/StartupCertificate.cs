using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// Loads a PKCS#12 certificate a startup setting points to, or refuses to start with a
/// message naming the setting. Shared by the token keys (<see cref="TokenCredentials"/>) and
/// the Data Protection key ring (<c>SmartAgri.Api.DataProtection</c>), which are configured
/// the same way: outside Development a missing or unusable certificate is fatal, never a
/// silent fall-back to something that would not survive a restart.
/// </summary>
internal static class StartupCertificate
{
    /// <param name="path">The configured file path (may be null or blank).</param>
    /// <param name="password">The configured password (may be null for a passwordless file).</param>
    /// <param name="setting">Full configuration key of <paramref name="path"/>, e.g. <c>Authentication:SigningCertificatePath</c>.</param>
    /// <param name="purpose">What the certificate is for, for the message ("token keys").</param>
    /// <param name="readmeSection">README section that documents it.</param>
    /// <exception cref="InvalidOperationException">Not configured, missing file, wrong password or
    /// not PKCS#12, or no private key.</exception>
    public static X509Certificate2 Load(
        string? path,
        string? password,
        string setting,
        string purpose,
        string readmeSection,
        string environmentName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Refusing to start: '{setting}' is not configured. Outside Development the Api needs a PKCS#12 " +
                $"certificate for {purpose} (environment: {environmentName}). See apps/api/README.md, \"{readmeSection}\".");
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
