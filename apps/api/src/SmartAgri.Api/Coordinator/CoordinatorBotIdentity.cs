using SmartAgri.Application.Line;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The 統籌助理 bot's own LINE user id — what its webhook deliveries carry as <c>destination</c> — read
/// with <c>GET /v2/bot/info</c> and the coordinator's access token the first time it is needed and then
/// kept in memory (#372). A failed read is not kept, so the next delivery tries again; a changed token
/// means a restart (it is configuration), which forgets the id. A singleton.
/// </summary>
public sealed class CoordinatorBotIdentity
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _userId;

    /// <summary>The bot's user id, or <see langword="null"/> when LINE did not say (logged by the caller).</summary>
    public async Task<(string? UserId, LineApiResult? Failure)> GetUserIdAsync(
        ILineMessagingClient line, string accessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Volatile.Read(ref _userId) is { } known)
        {
            return (known, null);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_userId is { } cached)
            {
                return (cached, null);
            }

            var result = await line.GetBotInfoAsync(accessToken, cancellationToken);
            if (!result.Succeeded || result.Value is not { UserId: { Length: > 0 } userId })
            {
                return (null, result);
            }

            Volatile.Write(ref _userId, userId);
            return (userId, null);
        }
        finally
        {
            _gate.Release();
        }
    }
}
