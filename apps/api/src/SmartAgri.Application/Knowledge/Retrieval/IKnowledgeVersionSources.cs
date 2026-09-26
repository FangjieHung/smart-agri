namespace SmartAgri.Application.Knowledge.Retrieval;

/// <summary>What a passage is cited as: its document's name and its version's number and
/// review state (<see cref="KnowledgeVersionStates"/>, at the search's <c>now</c>).</summary>
public sealed record KnowledgeVersionSource(
    Guid VersionId,
    Guid DocumentId,
    string DocumentName,
    int VersionNumber,
    KnowledgeVersionState State);

/// <summary>
/// Reads <see cref="KnowledgeVersionSource"/>s by version id, in the current organization
/// (Infrastructure implements it over the database). Vector search returns chunks only — it
/// does not load their version or document — so <see cref="KnowledgeRetriever"/> asks this for
/// what a citation needs.
/// </summary>
public interface IKnowledgeVersionSources
{
    /// <summary>The versions among <paramref name="versionIds"/> that exist in the current
    /// organization, by id; missing ones are left out.</summary>
    Task<IReadOnlyDictionary<Guid, KnowledgeVersionSource>> FindAsync(
        IReadOnlyCollection<Guid> versionIds,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
