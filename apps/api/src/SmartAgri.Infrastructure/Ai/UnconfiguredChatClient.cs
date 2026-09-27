using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>The chat client of a deployment without <c>Ai:Chat:Provider</c>: every call throws
/// <see cref="ChatGenerationException"/> (<c>ProviderNotConfigured</c> = true) without reaching
/// any model, so nothing is recorded for it (the same shape as
/// <see cref="UnconfiguredEmbeddingGenerator"/>).</summary>
internal sealed class UnconfiguredChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ChatResponse>(new ChatGenerationException(providerNotConfigured: true));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new ChatGenerationException(providerNotConfigured: true);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
    }
}
