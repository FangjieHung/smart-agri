using System.Security.Cryptography;

namespace SmartAgri.Api.Setup;

/// <summary>
/// Generates the administrator's one-time password with the operating system's
/// cryptographic RNG (<see cref="RandomNumberGenerator"/>).
/// </summary>
/// <remarks>
/// 20 characters from a 64-symbol alphabet (over 100 bits of entropy, far beyond guessing even
/// before lockout), always including an upper-case letter, a lower-case letter, a digit
/// and a symbol so it passes Identity's password rules. Look-alike characters
/// (<c>0 O o 1 l I</c>) are left out because the operator copies it from a terminal, and the
/// symbols avoid quotes, spaces, <c>$</c> and <c>\</c>, which shells and terminals mangle.
/// </remarks>
public static class OneTimePasswordGenerator
{
    public const int Length = 20;

    internal const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    internal const string Lower = "abcdefghijkmnpqrstuvwxyz";
    internal const string Digits = "23456789";
    internal const string Symbols = "!#%+-=?@";

    internal const string Alphabet = Upper + Lower + Digits + Symbols;

    public static string Generate()
    {
        Span<char> password = stackalloc char[Length];

        // One of each required class, the rest from the whole alphabet, then shuffled so
        // the required characters are not at predictable positions.
        password[0] = Pick(Upper);
        password[1] = Pick(Lower);
        password[2] = Pick(Digits);
        password[3] = Pick(Symbols);
        RandomNumberGenerator.GetItems(Alphabet.AsSpan(), password[4..]);
        RandomNumberGenerator.Shuffle(password);

        return new string(password);
    }

    private static char Pick(string characters) => characters[RandomNumberGenerator.GetInt32(characters.Length)];
}
