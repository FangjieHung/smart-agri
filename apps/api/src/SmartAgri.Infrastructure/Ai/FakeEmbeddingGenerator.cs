using System.Text;
using Microsoft.Extensions.AI;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// <c>Ai:Embedding:Provider=Fake</c>: deterministic vectors without any model or network, for
/// local development and tests only (<see cref="EmbeddingOptions.FakeEnvironments"/>; any other
/// environment refuses to start with it).
/// </summary>
/// <remarks>
/// <para>
/// Each character and each pair of neighbouring characters is hashed (FNV-1a, seeded with the
/// model name) into one of <see cref="Dimensions"/> signed buckets, and the sum is normalized.
/// The same text and model always give the same vector, texts that share characters are closer
/// than texts that do not — enough for retrieval to be tried out by hand — and two model names
/// give unrelated vectors, like two real models would.
/// </para>
/// <para>
/// Reports usage as one input token per character (Unicode scalar), so the usage path of the
/// recording middleware runs in development too.
/// </para>
/// </remarks>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 256;

    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private readonly string _model;
    private readonly ulong _seed;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public FakeEmbeddingGenerator(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _model = model;
        _seed = Hash(FnvOffsetBasis, Encoding.UTF8.GetBytes(model));
        _metadata = new EmbeddingGeneratorMetadata("fake", providerUri: null, model, Dimensions);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        long tokens = 0;
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value, nameof(values));
            embeddings.Add(new Embedding<float>(Embed(value)) { ModelId = _model, CreatedAt = null });
            tokens += value.EnumerateRunes().Count();
        }

        embeddings.Usage = new UsageDetails { InputTokenCount = tokens, TotalTokenCount = tokens };
        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null
            : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
    }

    /// <summary>The vector of <paramref name="text"/>: unit length, never all zeros.</summary>
    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sums = new double[Dimensions];
        Rune? previous = null;
        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            Add(sums, Gram(rune, second: null), weight: 1);
            if (previous is { } first)
            {
                Add(sums, Gram(first, rune), weight: 2);
            }

            previous = rune;
        }

        var norm = Math.Sqrt(sums.Sum(value => value * value));
        if (norm == 0)
        {
            // No text (or grams that cancelled out exactly): a fixed direction for this model.
            sums[(int)(_seed % Dimensions)] = 1;
            norm = 1;
        }

        return [.. sums.Select(value => (float)(value / norm))];
    }

    private void Add(double[] sums, byte[] gram, double weight)
    {
        var hash = Hash(_seed, gram);
        var sign = (hash >> 63) == 0 ? 1 : -1;
        sums[(int)(hash % Dimensions)] += sign * weight;
    }

    private static byte[] Gram(Rune first, Rune? second)
    {
        var text = second is { } next ? string.Concat(first.ToString(), next.ToString()) : first.ToString();
        return Encoding.UTF8.GetBytes(text);
    }

    private static ulong Hash(ulong seed, ReadOnlySpan<byte> bytes)
    {
        var hash = seed;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= FnvPrime;
        }

        // FNV-1a's high bits mix poorly for short inputs; one round of a 64-bit finalizer fixes that.
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccd;
        hash ^= hash >> 33;
        return hash;
    }
}
