using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// 案件類型 (M7 plan §3 B, §4; issue #247): a kind of case the manager defines. Each has a
/// description (what the assistant reads to choose a type when it proposes a case, M7-9), a default
/// case group and a default handling time; creating a case starts from a type, which fills in the
/// group and the due time (M7-3). Never deleted: a type that is no longer used is deactivated
/// (decision O), and existing cases keep pointing at it.
/// </summary>
/// <remarks>
/// <para>
/// The name is unique within the organization, active or not (unique index on
/// <c>(OrganizationId, Name)</c>). The default group is a same-organization composite foreign key with
/// <c>Restrict</c>; it must not be archived while the type is active (checked by the endpoints on
/// both sides: a type cannot choose an archived group, a group that is an active type's default
/// cannot be archived).
/// </para>
/// <para>
/// The handling time is kept in hours and counted in calendar time, not working days (decision H):
/// 1–<see cref="MaxDueHours"/> hours (90 days).
/// </para>
/// </remarks>
public sealed class CaseType : IOrganizationScoped
{
    public const int NameMaxLength = 40;

    public const int DescriptionMaxLength = 500;

    public const int MinDueHours = 1;

    /// <summary>90 days.</summary>
    public const int MaxDueHours = 2160;

    /// <summary>For EF Core materialization.</summary>
    private CaseType()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Trimmed, 1–<see cref="NameMaxLength"/> characters.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Trimmed, 0–<see cref="DescriptionMaxLength"/> characters (empty when there is none).</summary>
    public string Description { get; private set; } = string.Empty;

    public Guid DefaultGroupId { get; private set; }

    /// <summary><see cref="MinDueHours"/>–<see cref="MaxDueHours"/>.</summary>
    public int DefaultDueHours { get; private set; }

    /// <summary>Whether new cases may use it (and, from M7-9, the assistant may propose it).</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <param name="name">Already validated and trimmed (<c>CaseTypeRules</c>).</param>
    /// <param name="description">Already validated and trimmed; empty for none.</param>
    /// <param name="defaultGroup">A group of the same organization, not archived.</param>
    public static CaseType Create(
        Guid organizationId,
        string name,
        string description,
        CaseGroup defaultGroup,
        int defaultDueHours,
        bool isActive,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A case type needs its organization.", nameof(organizationId));
        }

        ArgumentNullException.ThrowIfNull(defaultGroup);
        if (defaultGroup.IsArchived)
        {
            throw new InvalidOperationException("A new case type cannot default to an archived group.");
        }

        var type = new CaseType
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        type.Apply(CheckedName(name), CheckedDescription(description), CheckedGroup(organizationId, defaultGroup), CheckedHours(defaultDueHours), isActive);
        return type;
    }

    /// <summary>
    /// Replaces every field. An archived <paramref name="defaultGroup"/> may only stay (unchanged) on
    /// a type that is, or becomes, inactive. Returns the wire names of the fields that changed (<c>name</c>,
    /// <c>description</c>, <c>defaultGroupId</c>, <c>defaultDueHours</c>, <c>isActive</c>); empty —
    /// and <see cref="UpdatedAt"/> untouched — when nothing did.
    /// </summary>
    public IReadOnlyList<string> Update(
        string name, string description, CaseGroup defaultGroup, int defaultDueHours, bool isActive, DateTimeOffset now)
    {
        var checkedName = CheckedName(name);
        var checkedDescription = CheckedDescription(description);
        var groupId = CheckedGroup(OrganizationId, defaultGroup);
        var hours = CheckedHours(defaultDueHours);
        if (defaultGroup.IsArchived && (isActive || groupId != DefaultGroupId))
        {
            throw new InvalidOperationException(
                "An archived group cannot be chosen, nor stay the default of a type that is active.");
        }

        var changed = new List<string>();
        if (!string.Equals(checkedName, Name, StringComparison.Ordinal))
        {
            changed.Add("name");
        }

        if (!string.Equals(checkedDescription, Description, StringComparison.Ordinal))
        {
            changed.Add("description");
        }

        if (groupId != DefaultGroupId)
        {
            changed.Add("defaultGroupId");
        }

        if (hours != DefaultDueHours)
        {
            changed.Add("defaultDueHours");
        }

        if (isActive != IsActive)
        {
            changed.Add("isActive");
        }

        if (changed.Count > 0)
        {
            Apply(checkedName, checkedDescription, groupId, hours, isActive);
            UpdatedAt = now;
        }

        return changed;
    }

    private void Apply(string name, string description, Guid defaultGroupId, int defaultDueHours, bool isActive)
    {
        Name = name;
        Description = description;
        DefaultGroupId = defaultGroupId;
        DefaultDueHours = defaultDueHours;
        IsActive = isActive;
    }

    private static string CheckedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length > NameMaxLength || name.Trim().Length != name.Length)
        {
            throw new ArgumentException($"A case type name is trimmed and 1–{NameMaxLength} characters.", nameof(name));
        }

        return name;
    }

    private static string CheckedDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.Length > DescriptionMaxLength || description.Trim().Length != description.Length)
        {
            throw new ArgumentException($"A case type description is trimmed and at most {DescriptionMaxLength} characters.", nameof(description));
        }

        return description;
    }

    private static Guid CheckedGroup(Guid organizationId, CaseGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.OrganizationId != organizationId)
        {
            throw new ArgumentException("A case type's default group belongs to the same organization.", nameof(group));
        }

        return group.Id;
    }

    private static int CheckedHours(int hours)
    {
        if (hours is < MinDueHours or > MaxDueHours)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), hours, $"A handling time is {MinDueHours}–{MaxDueHours} hours.");
        }

        return hours;
    }
}
