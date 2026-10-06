namespace SmartAgri.Application.Secrets;

/// <summary>
/// A stored secret cannot be decrypted: it was protected for another purpose, or the Data Protection
/// key ring that protected it is gone or was replaced (a restore without the keys, a new certificate).
/// Recovery is to enter the secret again; the message says so and names the purpose, never the value.
/// </summary>
public sealed class SecretUnprotectException : Exception
{
    public SecretUnprotectException(string purpose, Exception innerException)
        : base(
            $"The stored secret '{purpose}' cannot be decrypted: the encryption keys that protected it are not available " +
            "(the Data Protection key ring was lost or replaced) or it was stored for another purpose. Enter the secret again.",
            innerException)
    {
        Purpose = purpose;
    }

    public string Purpose { get; }
}
