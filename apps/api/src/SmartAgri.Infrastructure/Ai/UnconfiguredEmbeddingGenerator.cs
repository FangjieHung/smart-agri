using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>The generator of a deployment without <c>Ai:Embedding:Provider</c>: every call throws
/// <see cref="EmbeddingProviderNotConfiguredException"/> without reaching any model, so nothing
/// is recorded for it.</summary>
internal sealed class UnconfiguredEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new EmbeddingProviderNotConfiguredException());

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
    }
}
