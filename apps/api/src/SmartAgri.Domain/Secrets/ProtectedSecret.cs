using System.Text;

namespace SmartAgri.Domain.Secrets;

/// <summary>
/// A secret setting (a LINE channel secret, an organization's own model API key) as it is stored:
/// encrypted, never the plaintext (secrets-storage ADR). Write-only by design: the plaintext is
/// given to <c>ISecretProtector.Protect</c> once and is not kept anywhere; what can be shown to a
/// person afterwards is only "configured", <see cref="LastFour"/> and <see cref="SetAt"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ciphertext"/> is a Data Protection payload bound to one purpose string per use, so a
/// value protected for one field cannot be unprotected as another. It is opaque to the domain.
/// </para>
/// <para>
/// A table that stores one maps it as an EF Core complex property (three columns: ciphertext,
/// last four, set-at). The first is <c>AssistantLineChannels</c> (M5b #229; see
/// <c>AssistantLineChannelConfiguration</c>).
/// </para>
/// </remarks>
public sealed record ProtectedSecret
{
    /// <summary>How many trailing characters <see cref="LastFour"/> keeps.</summary>
    public const int LastFourLength = 4;

    public ProtectedSecret(string ciphertext, string? lastFour, DateTimeOffset setAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        Ciphertext = ciphertext;
        LastFour = lastFour;
        SetAt = setAt;
    }

    /// <summary>The encrypted value. Never logged, never serialized to a client.</summary>
    public string Ciphertext { get; }

    /// <summary>The last four characters of the plaintext so a person can recognize which secret
    /// is configured; null when the plaintext has fewer than four characters (see
    /// <see cref="LastFourOf"/>).</summary>
    public string? LastFour { get; }

    public DateTimeOffset SetAt { get; }

    /// <summary>
    /// The last <see cref="LastFourLength"/> characters (Unicode scalar values, so a surrogate pair
    /// is never cut in half) of <paramref name="plaintext"/>, or null when it has fewer. A value
    /// that short would be revealed in full by its "last four".
    /// </summary>
    public static string? LastFourOf(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var runes = plaintext.EnumerateRunes().ToArray();
        if (runes.Length < LastFourLength)
        {
            return null;
        }

        var tail = new StringBuilder();
        foreach (var rune in runes[^LastFourLength..])
        {
            tail.Append(rune.ToString());
        }

        return tail.ToString();
    }

    /// <summary>
    /// Shows no part of the value: the record's generated <c>ToString</c> would print the
    /// ciphertext into any log line that formats the object.
    /// </summary>
    public override string ToString() => $"{nameof(ProtectedSecret)} {{ LastFour = {LastFour}, SetAt = {SetAt:O} }}";
}
