namespace SmartAgri.Application.Answers;

/// <summary>A knowledge base an answer may search, and the name its citations snapshot.</summary>
public sealed record AnswerKnowledgeBase(Guid Id, string Name);

/// <summary>
/// Reads which knowledge bases an answer may search, in the current organization
/// (Infrastructure implements it over the database, as it does
/// <see cref="Knowledge.Retrieval.IKnowledgeVersionSources"/> for retrieval).
/// </summary>
public interface IAnswerKnowledgeBases
{
    /// <summary>
    /// Those of <paramref name="knowledgeBaseIds"/> that exist in the current organization and
    /// that <paramref name="ownerAccountId"/> may connect <b>now</b>
    /// (<see cref="Assistants.AssistantKnowledgeAccess.ConnectableBy"/>): step 1 of the answer
    /// pipeline (M3 plan §3), so a share withdrawn after an assistant was built stops that
    /// knowledge base from being searched on the very next answer.
    /// </summary>
    Task<IReadOnlyList<AnswerKnowledgeBase>> FindConnectableAsync(
        Guid ownerAccountId,
        IReadOnlyCollection<Guid> knowledgeBaseIds,
        CancellationToken cancellationToken);

    /// <summary>The ids of the knowledge bases connected to <paramref name="assistantId"/>
    /// (none for an assistant of another organization), for
    /// <see cref="GroundedAnswerProfile.For"/>.</summary>
    Task<IReadOnlyList<Guid>> ConnectedToAsync(Guid assistantId, CancellationToken cancellationToken);
}
