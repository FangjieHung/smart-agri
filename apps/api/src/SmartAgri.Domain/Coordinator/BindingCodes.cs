using System.Security.Cryptography;
using System.Text;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// 綁定碼 as people type them (store-coordinator design §5 Slice 1, #372): <see cref="Length"/>
/// characters from <see cref="Alphabet"/> — no 0/O, 1/I/L — shown as <c>XXXX-XXXX</c>, made by a
/// cryptographic random number generator. Only <see cref="Hash"/> of a code is stored; the code itself is
/// shown once (to the operator's terminal or the 老闆's LINE chat) and never logged.
/// </summary>
public static class BindingCodes
{
    /// <summary>The characters a code is made of: digits and capital letters people cannot mistake
    /// for one another.</summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>Characters in a code (without the hyphen it is shown with).</summary>
    public const int Length = 8;

    /// <summary>A new random code (<see cref="Length"/> characters of <see cref="Alphabet"/>).</summary>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary><paramref name="code"/> (as from <see cref="Generate"/> or <see cref="TryNormalize"/>)
    /// as people see it: <c>XXXX-XXXX</c>.</summary>
    public static string Format(string code)
    {
        if (!IsCode(code))
        {
            throw new ArgumentException("Not a binding code.", nameof(code));
        }

        return string.Concat(code.AsSpan(0, Length / 2), "-", code.AsSpan(Length / 2));
    }

    /// <summary>
    /// Whether <paramref name="text"/> looks like a binding code, and the code if so. The text is read
    /// the way people type it into LINE: Unicode NFKC (full-width letters and digits become half-width),
    /// trimmed, upper-cased, white space and hyphens dropped; it is a code when exactly
    /// <see cref="Length"/> characters of <see cref="Alphabet"/> are left.
    /// </summary>
    public static bool TryNormalize(string? text, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Not valid Unicode (a lone surrogate): not something anyone typed as a code.
            return false;
        }

        var builder = new StringBuilder(Length);
        foreach (var character in normalized.Trim())
        {
            if (char.IsWhiteSpace(character) || IsHyphen(character))
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
            if (builder.Length > Length)
            {
                return false;
            }
        }

        var candidate = builder.ToString();
        if (!IsCode(candidate))
        {
            return false;
        }

        code = candidate;
        return true;
    }

    /// <summary>The stored form of <paramref name="code"/>: SHA-256 of its ASCII bytes, as 64 lower-case
    /// hexadecimal digits.</summary>
    public static string Hash(string code)
    {
        if (!IsCode(code))
        {
            throw new ArgumentException("Not a binding code.", nameof(code));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(code)));
    }

    /// <summary>Exactly <see cref="Length"/> characters, all of <see cref="Alphabet"/>.</summary>
    public static bool IsCode(string? code) =>
        code is { Length: Length } && code.All(character => Alphabet.Contains(character, StringComparison.Ordinal));

    private static bool IsHyphen(char character) =>
        character is '-' or '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212';
}
