namespace SmartAgri.Application.Coordinator;

/// <summary>
/// Limits guessing binding codes (#372): after <see cref="MaxFailures"/> failed attempts by one LINE
/// user within <see cref="Window"/>, their next attempts are refused without looking at the code until
/// the oldest of those failures is <see cref="Window"/> old. In this process's memory only (one API
/// container per deployment). Thread-safe; a singleton.
/// </summary>
public sealed class CoordinatorBindingThrottle
{
    public const int MaxFailures = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>LINE users remembered before expired entries are swept.</summary>
    public const int SweepThreshold = 10_000;

    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    public CoordinatorBindingThrottle(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Whether <paramref name="lineUserId"/> has used up their attempts for now.</summary>
    public bool IsBlocked(string lineUserId)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineUserId);
        lock (_lock)
        {
            return Recent(lineUserId, _clock.GetUtcNow()) >= MaxFailures;
        }
    }

    /// <summary>Counts a failed attempt (an unknown, used or expired code) by <paramref name="lineUserId"/>.</summary>
    public void RecordFailure(string lineUserId)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineUserId);
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            Recent(lineUserId, now);
            if (!_failures.TryGetValue(lineUserId, out var failures))
            {
                if (_failures.Count >= SweepThreshold)
                {
                    Sweep(now);
                }

                failures = new Queue<DateTimeOffset>();
                _failures[lineUserId] = failures;
            }

            failures.Enqueue(now);
        }
    }

    /// <summary>The user's failures within <see cref="Window"/> of <paramref name="now"/>, forgetting older ones.</summary>
    private int Recent(string lineUserId, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(lineUserId, out var failures))
        {
            return 0;
        }

        while (failures.TryPeek(out var oldest) && now - oldest >= Window)
        {
            failures.Dequeue();
        }

        if (failures.Count == 0)
        {
            _failures.Remove(lineUserId);
        }

        return failures.Count;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var lineUserId in _failures.Keys.ToList())
        {
            Recent(lineUserId, now);
        }
    }
}
