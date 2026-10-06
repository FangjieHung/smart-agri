using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// The scoped <see cref="IChatClient"/> code calls (M6 plan §3 B): before its first call it asks
/// <see cref="IOrganizationChatModelResolver"/> which model the scope's organization uses, then
/// sends this and every later call of the scope to that model's client, wrapped by
/// <c>wrap</c> in the recording middleware — so every chat purpose, background jobs included,
/// uses and records the organization's model.
/// </summary>
/// <remarks>
/// Resolving lazily (rather than when the scope builds the client) lets the resolver read the
/// database asynchronously (M6-2). The wrapped client is created once per scope; disposing this
/// disposes it, which never disposes the shared provider client (see
/// <see cref="ModelInvocationRecordingChatClient"/>).
/// </remarks>
public sealed class OrganizationChatClient : IChatClient
{
    private readonly IOrganizationChatModelResolver _resolver;
    private readonly Func<ChatModelEntry, IChatClient> _wrap;
    private IChatClient? _client;

    public OrganizationChatClient(IOrganizationChatModelResolver resolver, Func<ChatModelEntry, IChatClient> wrap)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(wrap);
        _resolver = resolver;
        _wrap = wrap;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(cancellationToken);
        return await client.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(cancellationToken);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>This client when asked for it; otherwise, once a call has resolved the model,
    /// what that model's client offers (its <see cref="ChatClientMetadata"/> included).</summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _client?.GetService(serviceType, serviceKey);
    }

    public void Dispose() => _client?.Dispose();

    private async ValueTask<IChatClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            var resolved = await _resolver.ResolveAsync(cancellationToken);
            _client ??= _wrap(resolved.Entry);
        }

        return _client;
    }
}
