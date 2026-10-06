using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// 承辦組 (M7 plan §3 A, §4; issue #246): a group of the organization's own accounts that may take
/// on a kind of case. Defined by the organization itself; it does not stand for a department (case
/// ADR). Created, renamed, archived and unarchived by the manager; never deleted, because cases are
/// kept forever and point at their group (decision G).
/// </summary>
/// <remarks>
/// The name is unique within the organization (unique index on <c>(OrganizationId, Name)</c>, the
/// trimmed name as stored). An archived group stays readable for what already points at it, but is
/// no longer offered where a group is chosen (the default list leaves it out).
/// </remarks>
public sealed class CaseGroup : IOrganizationScoped
{
    public const int NameMaxLength = 40;

    /// <summary>For EF Core materialization.</summary>
    private CaseGroup()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Trimmed, 1–<see cref="NameMaxLength"/> characters.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>When the manager archived it; <see langword="null"/> while it is in use.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <param name="name">Already validated and trimmed (<c>CaseGroupRules.ValidateName</c>).</param>
    public static CaseGroup Create(Guid organizationId, string name, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A case group needs its organization.", nameof(organizationId));
        }

        return new CaseGroup
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Name = CheckedName(name),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Renames the group; <see langword="false"/> (nothing changed) for the same name.</summary>
    public bool Rename(string name, DateTimeOffset now)
    {
        var checkedName = CheckedName(name);
        if (string.Equals(checkedName, Name, StringComparison.Ordinal))
        {
            return false;
        }

        Name = checkedName;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Archives the group; <see langword="false"/> when it already is.</summary>
    public bool Archive(DateTimeOffset now)
    {
        if (IsArchived)
        {
            return false;
        }

        ArchivedAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Back in use; <see langword="false"/> when it was not archived.</summary>
    public bool Unarchive(DateTimeOffset now)
    {
        if (!IsArchived)
        {
            return false;
        }

        ArchivedAt = null;
        UpdatedAt = now;
        return true;
    }

    private static string CheckedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length > NameMaxLength || name.Trim().Length != name.Length)
        {
            throw new ArgumentException($"A case group name is trimmed and 1–{NameMaxLength} characters.", nameof(name));
        }

        return name;
    }
}
