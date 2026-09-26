using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// <see cref="IKnowledgeVersionSources"/> over <see cref="AppDbContext"/> (scoped with it, so
/// the organization filter applies): two small queries by version id — the versions with their
/// document names, and which of them are in effect at <c>now</c>
/// (<see cref="RetrievableChunks.CurrentEffectiveVersion"/>, the same rule search used).
/// </summary>
public sealed class EfKnowledgeVersionSources : IKnowledgeVersionSources
{
    private readonly AppDbContext _dbContext;

    public EfKnowledgeVersionSources(AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, KnowledgeVersionSource>> FindAsync(
        IReadOnlyCollection<Guid> versionIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versionIds);
        var ids = versionIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, KnowledgeVersionSource>();
        }

        var versions = await _dbContext.KnowledgeDocumentVersions
            .AsNoTracking()
            .Where(version => ids.Contains(version.Id))
            .Select(version => new
            {
                version.Id,
                version.DocumentId,
                DocumentName = version.Document!.Name,
                version.VersionNumber,
                version.ReviewState,
                version.EffectiveFrom,
            })
            .ToListAsync(cancellationToken);
        var inEffect = (await _dbContext.KnowledgeDocumentVersions
                .AsNoTracking()
                .Where(version => ids.Contains(version.Id))
                .Where(RetrievableChunks.CurrentEffectiveVersion(now))
                .Select(version => version.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return versions.ToDictionary(
            version => version.Id,
            version => new KnowledgeVersionSource(
                version.Id,
                version.DocumentId,
                version.DocumentName,
                version.VersionNumber,
                KnowledgeVersionStates.Of(version.ReviewState, version.EffectiveFrom, inEffect.Contains(version.Id), now)));
    }
}
