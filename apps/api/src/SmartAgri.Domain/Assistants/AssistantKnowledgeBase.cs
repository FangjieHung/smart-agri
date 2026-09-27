using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One knowledge base connected to one assistant (table <c>AssistantKnowledgeBases</c>). The
/// database enforces that the assistant and the knowledge base both belong to
/// <see cref="OrganizationId"/> (two composite foreign keys), and removes the row when either
/// side is deleted (cascade): a deleted knowledge base silently drops out of every assistant
/// it was connected to.
/// </summary>
public sealed class AssistantKnowledgeBase : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private AssistantKnowledgeBase()
    {
    }

    public AssistantKnowledgeBase(Assistant assistant, KnowledgeBase knowledgeBase, DateTimeOffset connectedAt)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        if (assistant.OrganizationId != knowledgeBase.OrganizationId)
        {
            throw new ArgumentException(
                "An assistant may only connect to a knowledge base of its own organization.", nameof(knowledgeBase));
        }

        AssistantId = assistant.Id;
        KnowledgeBaseId = knowledgeBase.Id;
        OrganizationId = assistant.OrganizationId;
        ConnectedAt = connectedAt;
    }

    public Guid AssistantId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public DateTimeOffset ConnectedAt { get; private set; }

    /// <summary>Always the assistant's (and the knowledge base's) organization.</summary>
    public Guid OrganizationId { get; private set; }
}
