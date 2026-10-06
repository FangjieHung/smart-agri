using SmartAgri.Domain.Secrets;

namespace SmartAgri.Api.Secrets;

/// <summary>
/// What the Api ever says about a stored secret setting: whether it is set, its last four
/// characters and when it was set (secrets-storage ADR). There is deliberately no member that could
/// hold the plaintext or the ciphertext, so no endpoint returning this type can leak either.
/// </summary>
/// <param name="Configured">True when a value is stored.</param>
/// <param name="LastFour">The value's last four characters; null when not configured or the value
/// has fewer than four characters.</param>
/// <param name="UpdatedAt">When the value was last written; null when not configured.</param>
public sealed record SecretStatusView(bool Configured, string? LastFour, DateTimeOffset? UpdatedAt)
{
    public static SecretStatusView NotConfigured { get; } = new(false, null, null);

    public static SecretStatusView From(ProtectedSecret? secret) =>
        secret is null ? NotConfigured : new SecretStatusView(true, secret.LastFour, secret.SetAt);
}
