namespace SmartAgri.Domain.Organizations;

/// <summary>
/// A company or group using the platform (glossary: 組織). Not itself
/// <see cref="IOrganizationScoped"/>: it is the tenant boundary.
/// </summary>
public sealed class Organization
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 32;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    public Organization(Guid id, string name, string code)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An organization needs a non-empty id.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException($"An organization name must be 1-{NameMaxLength} characters.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        Code = NormalizeCode(code);
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// Globally unique short code used at login to tell apart same-named accounts in
    /// different organizations (glossary: 組織代碼). Always stored normalized (see
    /// <see cref="NormalizeCode"/>), so the database's unique index is effectively
    /// case-insensitive. Immutable after creation (authentication ADR): it is embedded in
    /// every account's internal Identity user name.
    /// </summary>
    public string Code { get; private set; }

    /// <summary>
    /// Trims and lower-cases a code, and rejects anything outside
    /// <c>[a-z0-9-]</c>. In particular <c>/</c> is never allowed: account user names are
    /// stored as <c>{code}/{loginName}</c>, and a <c>/</c> inside the code would make two
    /// different (code, login name) pairs collide on the same user name.
    /// </summary>
    public static string NormalizeCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > CodeMaxLength)
        {
            throw new ArgumentException($"An organization code must be 1-{CodeMaxLength} characters.", nameof(code));
        }

        foreach (var character in normalized)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                throw new ArgumentException(
                    "An organization code may only contain letters a-z, digits and '-'.",
                    nameof(code));
            }
        }

        return normalized;
    }
}
