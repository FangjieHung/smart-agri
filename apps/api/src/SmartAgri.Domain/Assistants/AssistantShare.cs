using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One account an assistant is shared with — the "平台內分享" (platform sharing) channel of
/// M3 plan §4/§5 Slice 3 (table <c>AssistantShares</c>). The database enforces that the
/// assistant and the account both belong to <see cref="OrganizationId"/> (two composite
/// foreign keys), and removes the row when either side is deleted (cascade), mirroring
/// <see cref="SmartAgri.Domain.Knowledge.KnowledgeBaseShare"/> for knowledge bases.
/// </summary>
/// <remarks>
/// A row here is necessary but not sufficient for the account to actually use the assistant:
/// <see cref="SmartAgri.Application.Assistants.AssistantUseAccess.UsableBy"/> also requires the
/// account to hold <c>use-shared-assistants</c> and the assistant to not be
/// <see cref="AssistantStatus.Paused"/>. Removing a share only revokes access — it never
/// deletes the account's own conversations with the assistant (M3 plan §3 "取消分享只收回權限、
/// 不刪對話"), which do not live on this table at all.
/// </remarks>
public sealed class AssistantShare : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private AssistantShare()
    {
    }

    public AssistantShare(Assistant assistant, Guid accountId)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account id must not be empty.", nameof(accountId));
        }

        AssistantId = assistant.Id;
        AccountId = accountId;
        OrganizationId = assistant.OrganizationId;
    }

    public Guid AssistantId { get; private set; }

    public Guid AccountId { get; private set; }

    /// <summary>Always the assistant's organization.</summary>
    public Guid OrganizationId { get; private set; }
}
