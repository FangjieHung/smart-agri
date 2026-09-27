using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One account a knowledge base is shared with, when its scope is
/// <see cref="KnowledgeSharingScope.SpecificAccounts"/> (table <c>KnowledgeBaseShares</c>).
/// The database enforces that the knowledge base and the account both belong to
/// <see cref="OrganizationId"/> (two composite foreign keys), and removes the row with
/// either of them.
/// </summary>
public sealed class KnowledgeBaseShare : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private KnowledgeBaseShare()
    {
    }

    public KnowledgeBaseShare(KnowledgeBase knowledgeBase, Guid accountId)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account id must not be empty.", nameof(accountId));
        }

        KnowledgeBaseId = knowledgeBase.Id;
        OrganizationId = knowledgeBase.OrganizationId;
        AccountId = accountId;
    }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid AccountId { get; private set; }

    /// <summary>Always the knowledge base's organization.</summary>
    public Guid OrganizationId { get; private set; }
}
