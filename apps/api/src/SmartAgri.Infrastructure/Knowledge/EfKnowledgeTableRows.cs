using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// <see cref="IKnowledgeTableRows"/> over <see cref="AppDbContext"/> (scoped with it, so the
/// organization filter applies): one query for the not-excluded table-row chunks of the tables'
/// versions and units (on the <c>VersionId, UnitOrdinal, Ordinal</c> index), narrowed to the exact
/// tables in memory.
/// </summary>
public sealed class EfKnowledgeTableRows : IKnowledgeTableRows
{
    private readonly AppDbContext _dbContext;

    public EfKnowledgeTableRows(AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<KnowledgeTableRow>> FindAsync(IReadOnlyCollection<KnowledgeTableKey> tables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var keys = tables.ToHashSet();
        if (keys.Count == 0)
        {
            return [];
        }

        var versionIds = keys.Select(key => key.VersionId).Distinct().ToList();
        var unitOrdinals = keys.Select(key => key.UnitOrdinal).Distinct().ToList();
        var rows = await _dbContext.KnowledgeChunks
            .AsNoTracking()
            .Where(chunk => versionIds.Contains(chunk.VersionId)
                && unitOrdinals.Contains(chunk.UnitOrdinal)
                && chunk.TableIndex != null
                && !chunk.Excluded)
            .Select(chunk => new { chunk.Id, chunk.VersionId, chunk.UnitOrdinal, TableIndex = chunk.TableIndex!.Value, chunk.Ordinal, chunk.Text })
            .ToListAsync(cancellationToken);

        return [.. rows
            .Select(row => new KnowledgeTableRow(row.Id, new KnowledgeTableKey(row.VersionId, row.UnitOrdinal, row.TableIndex), row.Ordinal, row.Text))
            .Where(row => keys.Contains(row.Table))];
    }
}
