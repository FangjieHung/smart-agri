using SmartAgri.Domain.Secrets;

namespace SmartAgri.Application.Secrets;

/// <summary>
/// Encrypts and decrypts secret settings (secrets-storage ADR). Implemented in Infrastructure over
/// ASP.NET Core Data Protection; the key ring lives outside the database.
/// </summary>
/// <remarks>
/// Every kind of secret has its own <c>purpose</c> string (a constant next to the code that owns the
/// field, e.g. <c>"line.channel-secret"</c>): a ciphertext only unprotects under the purpose it was
/// protected with. <see cref="Unprotect"/> is for server-side use by the one component that needs the
/// value (a LINE client calling LINE); no endpoint returns its result.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>; the result carries only the ciphertext, its last
    /// four characters and <paramref name="now"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> or <paramref name="plaintext"/> is blank.</exception>
    ProtectedSecret Protect(string purpose, string plaintext, DateTimeOffset now);

    /// <exception cref="SecretUnprotectException">The ciphertext was not protected for
    /// <paramref name="purpose"/>, or the key ring that protected it is not available (keys lost or
    /// replaced): the secret must be entered again.</exception>
    string Unprotect(string purpose, ProtectedSecret secret);
}
