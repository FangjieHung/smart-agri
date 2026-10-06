using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// One account in one <see cref="CaseGroup"/> (M7 plan §4; issue #246): being a member is what lets
/// an account take on the group's cases — no permission is added (case ADR). Only the current state;
/// every addition and removal is also written to <see cref="CaseGroupMemberChange"/>.
/// </summary>
/// <remarks>
/// The database enforces, by composite foreign keys, that the group and the account belong to
/// <see cref="OrganizationId"/>. The account's key is <c>Restrict</c> (decision C: accounts are
/// deactivated, never deleted while referenced); the group's cascades. Only internal accounts
/// (<c>smb-admin</c>, <c>internal-employee</c>) may be members — checked by the endpoint, which
/// reads each account's role at the time of the change.
/// </remarks>
public sealed class CaseGroupMember : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private CaseGroupMember()
    {
    }

    public Guid GroupId { get; private set; }

    public Guid AccountId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The manager who added the account. An audit value: no foreign key.</summary>
    public Guid AddedByAccountId { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public static CaseGroupMember Create(CaseGroup group, Guid accountId, Guid addedByAccountId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(accountId));
        }

        if (addedByAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(addedByAccountId));
        }

        return new CaseGroupMember
        {
            GroupId = group.Id,
            AccountId = accountId,
            OrganizationId = group.OrganizationId,
            AddedByAccountId = addedByAccountId,
            AddedAt = now,
        };
    }
}
