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
    /// When this organization's team permissions (<c>PUT
    /// /api/v1/team/members/{id}/permissions</c>) were last successfully changed, or
    /// <see langword="null"/> before the first change. Whole-organization, not per member —
    /// mirrors the mock's single <c>TeamView.savedAt</c> (<c>mock-demo-repository.ts</c>'s
    /// <c>StoredTeamPermissions.savedAt</c>), which is one timestamp for the whole team, not
    /// one per row. A dedicated column rather than reusing account or permission-grant rows:
    /// those describe "what is granted now", and seeding also writes them, so their own
    /// timestamps would not stay null the way the mock's does before any team-panel edit.
    /// </summary>
    public DateTimeOffset? TeamPermissionsSavedAt { get; private set; }

    /// <summary>Records that the team's permissions were just saved (called from
    /// <c>SmartAgri.Api.Team.TeamEndpoints</c>).</summary>
    public void RecordTeamPermissionsSaved(DateTimeOffset when) => TeamPermissionsSavedAt = when;

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
