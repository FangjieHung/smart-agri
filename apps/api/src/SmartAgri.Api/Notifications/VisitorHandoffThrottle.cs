using System.Security.Cryptography;
using System.Text;
using SmartAgri.Domain.Notifications;

namespace SmartAgri.Api.Notifications;

/// <summary>
/// At most one 轉真人 per visitor conversation per <see cref="Window"/> (#355): a visitor who keeps
/// asking what the assistant cannot answer is one person waiting, not one notification (and one Email)
/// per question. The conversation is the website visitor's session or the LINE chat; only a hash of it
/// is held, in this process's memory, like the LINE history (a restart forgets it). Thread-safe.
/// </summary>
public sealed class VisitorHandoffThrottle
{
    /// <summary>The LINE history's idle timeout: about one visit.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private const int PruneAbove = 10_000;

    private readonly TimeProvider _clock;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public VisitorHandoffThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Whether this conversation may produce a handoff now; when it may, the next
    /// <see cref="Window"/> is taken.</summary>
    public bool TryEnter(Guid assistantId, VisitorHandoffChannel channel, string conversation)
    {
        var key = Key(assistantId, channel, conversation);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_last.TryGetValue(key, out var last) && now - last < Window)
            {
                return false;
            }

            if (_last.Count >= PruneAbove)
            {
                foreach (var stale in _last.Where(entry => now - entry.Value >= Window).Select(entry => entry.Key).ToList())
                {
                    _last.Remove(stale);
                }
            }

            _last[key] = now;
            return true;
        }
    }

    /// <summary>Gives the window back (the handoff could not be saved).</summary>
    public void Release(Guid assistantId, VisitorHandoffChannel channel, string conversation)
    {
        var key = Key(assistantId, channel, conversation);
        lock (_gate)
        {
            _last.Remove(key);
        }
    }

    private static string Key(Guid assistantId, VisitorHandoffChannel channel, string conversation) =>
        $"{assistantId:N}:{channel}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversation ?? string.Empty)))}";
}
