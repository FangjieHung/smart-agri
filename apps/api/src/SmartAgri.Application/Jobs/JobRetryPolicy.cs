namespace SmartAgri.Application.Jobs;

/// <summary>When a job that failed with a retryable error runs again.</summary>
public static class JobRetryPolicy
{
    /// <summary>
    /// Exponential backoff: <paramref name="baseDelay"/> after the first attempt, doubling
    /// after each further one, never more than <paramref name="maxDelay"/>.
    /// </summary>
    /// <param name="attempt">The attempt that just failed (1-based).</param>
    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);

        // Capped exponent: 2^30 base delays is far past any sensible maximum already.
        var ticks = baseDelay.Ticks * Math.Pow(2, Math.Min(attempt - 1, 30));
        return ticks >= maxDelay.Ticks ? maxDelay : TimeSpan.FromTicks((long)ticks);
    }
}
