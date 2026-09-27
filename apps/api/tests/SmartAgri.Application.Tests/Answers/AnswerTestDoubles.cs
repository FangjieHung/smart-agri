using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Answers;

/// <summary>A chat model that answers with the pieces a test gives it, and remembers every
/// call.</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, bool Streaming)> Calls { get; } = [];

    /// <summary>The answer, in the pieces it streams in (joined for a non-streaming call).</summary>
    public IReadOnlyList<string> Pieces { get; set; } = ["根據資料，收到商品後七天內可以退貨 [1]。"];

    /// <summary>Thrown by the call (non-streaming) or after the first piece (streaming).</summary>
    public Exception? Throw { get; set; }

    public int CallCount => Calls.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options, false));
        return Throw is { } exception
            ? Task.FromException<ChatResponse>(exception)
            : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(Pieces))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options, true));
        foreach (var piece in Pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
            if (Throw is { } exception)
            {
                throw exception;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A retriever over passages a test lists: it searches only the knowledge bases it is
/// asked to, applies the query's threshold (or its settings'), and remembers every query.</summary>
internal sealed class ScriptedRetriever : IKnowledgeRetriever
{
    public List<RetrievedKnowledgePassage> Passages { get; } = [];

    public List<KnowledgeRetrievalQuery> Queries { get; } = [];

    public KnowledgeRetrievalSettings Settings { get; init; } = KnowledgeRetrievalSettings.Default;

    public Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        var found = Passages
            .Where(passage => query.KnowledgeBaseIds.Contains(passage.KnowledgeBaseId))
            .OrderByDescending(passage => passage.Score)
            .Take(query.Top ?? Settings.Top)
            .ToList();
        return Task.FromResult(new KnowledgeRetrievalResult(found, query.MinScore ?? Settings.MinScore));
    }

    public RetrievedKnowledgePassage Add(
        Guid knowledgeBaseId, string documentName, string location, string text, double score, int versionNumber = 1,
        DateTimeOffset? versionEffectiveFrom = null)
    {
        var passage = new RetrievedKnowledgePassage(
            Guid.CreateVersion7(), knowledgeBaseId, Guid.CreateVersion7(), documentName, Guid.CreateVersion7(), versionNumber,
            KnowledgeVersionState.Effective, location, text, score, versionEffectiveFrom ?? DateTimeOffset.UnixEpoch);
        Passages.Add(passage);
        return passage;
    }
}

/// <summary>Knowledge bases and shares in memory, judged by the real
/// <see cref="AssistantKnowledgeAccess.ConnectableBy"/>.</summary>
internal sealed class InMemoryAnswerKnowledgeBases : IAnswerKnowledgeBases
{
    public List<KnowledgeBase> KnowledgeBases { get; } = [];

    public List<KnowledgeBaseShare> Shares { get; } = [];

    public Task<IReadOnlyList<AnswerKnowledgeBase>> FindConnectableAsync(
        Guid ownerAccountId, IReadOnlyCollection<Guid> knowledgeBaseIds, CancellationToken cancellationToken)
    {
        var connectable = AssistantKnowledgeAccess.ConnectableBy(ownerAccountId, Shares.AsQueryable()).Compile();
        IReadOnlyList<AnswerKnowledgeBase> found =
        [
            .. KnowledgeBases
                .Where(knowledgeBase => knowledgeBaseIds.Contains(knowledgeBase.Id) && connectable(knowledgeBase))
                .Select(knowledgeBase => new AnswerKnowledgeBase(knowledgeBase.Id, knowledgeBase.Name)),
        ];
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Guid>> ConnectedToAsync(Guid assistantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>An <see cref="IMeterFactory"/> of its own plus a listener on exactly its meters, so
/// parallel tests never see each other's measurements.</summary>
internal sealed class RecordedMeasurements : IMeterFactory
{
    private readonly List<Meter> _meters = [];
    private readonly MeterListener _listener = new();

    public RecordedMeasurements()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (_meters.Contains(instrument.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (Measurements)
            {
                Measurements.Add((instrument.Name, value, [.. tags]));
            }
        });
        _listener.Start();
    }

    public List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> Measurements { get; } = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
    }
}
