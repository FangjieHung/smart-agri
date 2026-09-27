using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Assistants;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// <see cref="IAnswerKnowledgeBases"/> over <see cref="AppDbContext"/> (scoped with it, so the
/// organization filter applies): the connectability check is
/// <see cref="AssistantKnowledgeAccess.ConnectableBy"/> itself, translated into SQL — the same
/// rule the connect endpoint enforces, re-applied on every answer.
/// </summary>
public sealed class EfAnswerKnowledgeBases : IAnswerKnowledgeBases
{
    private readonly AppDbContext _dbContext;

    public EfAnswerKnowledgeBases(AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AnswerKnowledgeBase>> FindConnectableAsync(
        Guid ownerAccountId,
        IReadOnlyCollection<Guid> knowledgeBaseIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBaseIds);
        var ids = knowledgeBaseIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await _dbContext.KnowledgeBases
            .AsNoTracking()
            .Where(knowledgeBase => ids.Contains(knowledgeBase.Id))
            .Where(AssistantKnowledgeAccess.ConnectableBy(ownerAccountId, _dbContext.KnowledgeBaseShares))
            .OrderBy(knowledgeBase => knowledgeBase.Id)
            .Select(knowledgeBase => new AnswerKnowledgeBase(knowledgeBase.Id, knowledgeBase.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ConnectedToAsync(Guid assistantId, CancellationToken cancellationToken) =>
        await _dbContext.AssistantKnowledgeBases
            .AsNoTracking()
            .Where(link => link.AssistantId == assistantId)
            .OrderBy(link => link.ConnectedAt)
            .Select(link => link.KnowledgeBaseId)
            .ToListAsync(cancellationToken);
}
