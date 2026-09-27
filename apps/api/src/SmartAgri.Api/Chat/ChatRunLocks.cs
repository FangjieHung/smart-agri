using System.Collections.Concurrent;

namespace SmartAgri.Api.Chat;

/// <summary>
/// Which conversation threads have a reply being generated right now, so a second question to
/// the same thread is refused with <c>409 chat-run-in-progress</c> instead of racing the first
/// (M3 plan Slice 7; §7 risk 5: 「單一對話串同時只能有一個執行中的回覆」).
/// </summary>
/// <remarks>
/// <para>
/// In memory, one instance per process (registered as a singleton). That is enough for M3's
/// deployment — one Api process per customer (backend-stack ADR) — but <b>not</b> for several
/// Api processes behind a load balancer: two requests for the same thread landing on different
/// processes would both run. Scaling out needs this moved to shared storage (for example a
/// PostgreSQL advisory lock keyed by the thread id, or a "running since" column with a timeout),
/// together with the per-organization rate limits planned for M5.
/// </para>
/// <para>
/// A lease is released when the request ends however it ends — finished, failed, or the client
/// disconnected — so a crashed run never leaves its thread locked for longer than the request.
/// </para>
/// </remarks>
public sealed class ChatRunLocks
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    /// <summary>A lease on <paramref name="threadId"/> (dispose it to release), or
    /// <see langword="null"/> when another run already holds it.</summary>
    public IDisposable? TryAcquire(Guid threadId) =>
        _running.TryAdd(threadId, 0) ? new Lease(this, threadId) : null;

    /// <summary>Whether a run currently holds <paramref name="threadId"/>.</summary>
    public bool IsRunning(Guid threadId) => _running.ContainsKey(threadId);

    private sealed class Lease : IDisposable
    {
        private readonly ChatRunLocks _owner;
        private readonly Guid _threadId;
        private int _released;

        public Lease(ChatRunLocks owner, Guid threadId)
        {
            _owner = owner;
            _threadId = threadId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner._running.TryRemove(_threadId, out _);
            }
        }
    }
}
