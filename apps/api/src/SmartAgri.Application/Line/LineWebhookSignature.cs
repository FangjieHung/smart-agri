using System.Security.Cryptography;
using System.Text;

namespace SmartAgri.Application.Line;

/// <summary>
/// LINE's webhook signature (M5b plan §2.1 and §3 C): the <c>x-line-signature</c> header is
/// base64(HMAC-SHA256(key = the channel secret, message = the raw request body bytes)). It must be
/// checked against the bytes exactly as received, before any JSON parsing.
/// </summary>
public static class LineWebhookSignature
{
    /// <summary>The header LINE sends it in (header names are case-insensitive).</summary>
    public const string HeaderName = "x-line-signature";

    /// <summary>An HMAC-SHA256 is 32 bytes.</summary>
    private const int SignatureLength = 32;

    /// <summary>base64(HMAC-SHA256(<paramref name="channelSecret"/>, <paramref name="body"/>)).</summary>
    public static string Compute(string channelSecret, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(channelSecret);
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), body));
    }

    /// <summary>
    /// Whether <paramref name="signatureHeader"/> is the signature of <paramref name="body"/> under
    /// <paramref name="key"/> (the channel secret's UTF-8 bytes). The HMAC is always computed and the
    /// comparison is constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>), also when
    /// the header is missing or is not base64 of the right length.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> key, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        Span<byte> expected = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(key, body, expected);

        Span<byte> received = stackalloc byte[SignatureLength];
        var decoded = signatureHeader is not null
            && Convert.TryFromBase64String(signatureHeader.Trim(), received, out var written)
            && written == SignatureLength;
        if (!decoded)
        {
            // Compare anyway, against bytes that cannot match, so a missing or malformed header takes
            // the same path as a wrong one.
            received.Clear();
            expected.CopyTo(received);
            received[0] ^= 0xFF;
        }

        return CryptographicOperations.FixedTimeEquals(expected, received) & decoded;
    }
}
