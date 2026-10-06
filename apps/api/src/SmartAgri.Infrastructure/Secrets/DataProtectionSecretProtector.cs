using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Infrastructure.Secrets;

/// <summary>
/// <see cref="ISecretProtector"/> over Data Protection: one protector per purpose string, under a
/// fixed prefix that no other use of the key ring (Identity cookies, OpenIddict, the visitor token)
/// shares, so a secret's ciphertext cannot be replayed as one of those or the other way round.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    /// <summary>Versioned so a future change of format can use a new prefix.</summary>
    public const string PurposePrefix = "SmartAgri.Secrets.v1:";

    private readonly IDataProtectionProvider _provider;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _provider = provider;
    }

    public ProtectedSecret Protect(string purpose, string plaintext, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var ciphertext = _provider.CreateProtector(PurposePrefix + purpose).Protect(plaintext);
        return new ProtectedSecret(ciphertext, ProtectedSecret.LastFourOf(plaintext), now);
    }

    public string Unprotect(string purpose, ProtectedSecret secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(secret);

        try
        {
            return _provider.CreateProtector(PurposePrefix + purpose).Unprotect(secret.Ciphertext);
        }
        catch (CryptographicException exception)
        {
            throw new SecretUnprotectException(purpose, exception);
        }
    }
}
