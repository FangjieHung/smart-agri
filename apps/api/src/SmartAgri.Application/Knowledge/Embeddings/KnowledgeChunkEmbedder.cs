using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;
using SmartAgri.Domain.Ai;

namespace SmartAgri.Application.Knowledge.Embeddings;

/// <summary>
/// Embeds chunk texts and questions with the deployment's <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
/// (M2 plan, Slice 7): adds the configured prefix, sends chunks in batches of
/// <see cref="KnowledgeEmbeddingSettings.BatchSize"/> (one model call, so one
/// <c>ModelInvocation</c>, per batch), attributes every call and checks every answer. Scoped
/// with the generator, whose recording middleware writes the audit rows for the scope's
/// organization.
/// </summary>
public sealed class KnowledgeChunkEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly KnowledgeEmbeddingSettings _settings;

    public KnowledgeChunkEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator, KnowledgeEmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(settings);
        _generator = generator;
        _settings = settings;
    }

    /// <summary>The model every vector this returns comes from.</summary>
    public string Model => _settings.Model;

    /// <summary>
    /// One vector per text (built with <see cref="KnowledgeEmbeddingText.For"/>), in order,
    /// for <see cref="ModelInvocationPurpose.EmbedDocument"/> on behalf of <paramref name="accountId"/>.
    /// </summary>
    /// <exception cref="KnowledgeEmbeddingException">A call failed or answered unusably; nothing
    /// is returned for the batches that did succeed.</exception>
    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, Guid? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var attribution = new ModelInvocationAttribution(ModelInvocationPurpose.EmbedDocument, accountId, AssistantId: null);
        var vectors = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(_settings.BatchSize))
        {
            vectors.AddRange(await GenerateAsync([.. batch.Select(text => _settings.DocumentPrefix + text)], attribution, cancellationToken));
        }

        return vectors;
    }

    /// <summary>The vector of <paramref name="question"/> for <see cref="ModelInvocationPurpose.EmbedQuery"/>.</summary>
    /// <exception cref="KnowledgeEmbeddingException">The call failed or answered unusably.</exception>
    public async Task<float[]> EmbedQueryAsync(string question, Guid? accountId, Guid? assistantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var attribution = new ModelInvocationAttribution(ModelInvocationPurpose.EmbedQuery, accountId, assistantId);
        return (await GenerateAsync([_settings.QueryPrefix + question], attribution, cancellationToken))[0];
    }

    private async Task<IReadOnlyList<float[]>> GenerateAsync(string[] inputs, ModelInvocationAttribution attribution, CancellationToken cancellationToken)
    {
        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await _generator.GenerateAsync(inputs, attribution.ToEmbeddingOptions(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EmbeddingProviderNotConfiguredException exception)
        {
            throw new KnowledgeEmbeddingException(providerNotConfigured: true, exception);
        }
        catch (Exception exception)
        {
            throw new KnowledgeEmbeddingException(providerNotConfigured: false, exception);
        }

        if (generated.Count != inputs.Length)
        {
            throw Unusable($"The model returned {generated.Count} vectors for {inputs.Length} inputs.");
        }

        var vectors = new float[inputs.Length][];
        for (var i = 0; i < inputs.Length; i++)
        {
            var vector = generated[i].Vector.ToArray();
            if (vector.Length == 0 || !vector.All(float.IsFinite) || vector.All(value => value == 0))
            {
                throw Unusable($"The model returned an empty, non-finite or all-zero vector for input {i}.");
            }

            if (i > 0 && vector.Length != vectors[0].Length)
            {
                throw Unusable($"The model returned vectors of {vectors[0].Length} and {vector.Length} dimensions in one answer.");
            }

            vectors[i] = vector;
        }

        return vectors;
    }

    private static KnowledgeEmbeddingException Unusable(string reason) =>
        new(providerNotConfigured: false, new InvalidOperationException(reason));
}
