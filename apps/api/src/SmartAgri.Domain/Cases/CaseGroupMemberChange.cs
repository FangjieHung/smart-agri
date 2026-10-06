using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// One addition to or removal from a <see cref="CaseGroup"/>, appended and never changed (M7 plan
/// §4; issue #246) — like <c>DatabaseDataManagerChange</c>. A single save that changes several
/// accounts writes one row each, all with the same <see cref="ChangedAt"/>.
/// </summary>
/// <remarks>
/// The account ids are audit values with no foreign key, so the history survives the accounts; a
/// name that can no longer be found shows as 「已停用的帳號」.
/// </remarks>
public sealed class CaseGroupMemberChange : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private CaseGroupMemberChange()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid GroupId { get; private set; }

    /// <summary>The account that was added or removed.</summary>
    public Guid AccountId { get; private set; }

    /// <summary><see langword="true"/> for an addition, <see langword="false"/> for a removal.</summary>
    public bool Added { get; private set; }

    /// <summary>The manager who made the change.</summary>
    public Guid ChangedByAccountId { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public static CaseGroupMemberChange Create(CaseGroup group, Guid accountId, bool added, Guid changedByAccountId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(accountId));
        }

        if (changedByAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(changedByAccountId));
        }

        return new CaseGroupMemberChange
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = group.OrganizationId,
            GroupId = group.Id,
            AccountId = accountId,
            Added = added,
            ChangedByAccountId = changedByAccountId,
            ChangedAt = now,
        };
    }
}
