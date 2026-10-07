namespace SmartAgri.Application.Knowledge.Retrieval;

/// <summary>One table of one version (#324): unit <paramref name="UnitOrdinal"/>'s table
/// <paramref name="TableIndex"/>.</summary>
public sealed record KnowledgeTableKey(Guid VersionId, int UnitOrdinal, int TableIndex);

/// <summary>One row chunk of a table, as stored.</summary>
/// <param name="Ordinal">The chunk's <see cref="Domain.Knowledge.KnowledgeChunk.Ordinal"/>: the
/// table's rows are in this order.</param>
public sealed record KnowledgeTableRow(Guid ChunkId, KnowledgeTableKey Table, int Ordinal, string Text);

/// <summary>
/// Reads the rows of tables whose rows an answer retrieved (#324, small passages to retrieve,
/// the whole table to answer from), in the current organization (Infrastructure implements it
/// over the database).
/// </summary>
public interface IKnowledgeTableRows
{
    /// <summary>Every row chunk of <paramref name="tables"/> the owner has not excluded, in one
    /// read; in no particular order.</summary>
    Task<IReadOnlyList<KnowledgeTableRow>> FindAsync(IReadOnlyCollection<KnowledgeTableKey> tables, CancellationToken cancellationToken);
}
