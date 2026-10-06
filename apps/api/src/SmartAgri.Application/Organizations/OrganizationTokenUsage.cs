using System.Collections.Concurrent;

namespace SmartAgri.Application.Organizations;

/// <summary>Where <see cref="OrganizationTokenUsage"/> reads from; the Api implements it over
/// PostgreSQL.</summary>
public interface IOrganizationTokenUsageSource
{
    /// <summary>The organization's own limit; <see langword="null"/> means "the deployment default"
    /// (and is also what a missing organization gets).</summary>
    Task<long?> GetMonthlyTokenLimitAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Input + output tokens of the organization's calls whose purpose is in
    /// <see cref="OrganizationTokenUsageRules.CountedPurposes"/> and whose <c>At</c> is in
    /// <c>[startInclusive, endExclusive)</c>; a null count is 0.</summary>
    Task<long> SumChatTokensAsync(
        Guid organizationId, DateTimeOffset startInclusive, DateTimeOffset endExclusive, CancellationToken cancellationToken);
}

/// <summary>
/// An organization's chat-model token usage this month against its limit (M5a plan §3 F). The
/// answer is cached per organization for <see cref="CacheDuration"/>, so every visitor question can
/// ask without a query each time; a limit changed with <c>set-token-limit</c> therefore takes effect
/// within that long, and a reply that finishes after the check can push usage slightly past the limit.
/// </summary>
/// <remarks>Register as a singleton: the cache lives here.</remarks>
public sealed class OrganizationTokenUsage
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly IOrganizationTokenUsageSource _source;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly long _defaultLimit;
    private readonly ConcurrentDictionary<Guid, (OrganizationTokenUsageSnapshot Snapshot, DateTimeOffset ExpiresAt)> _cache = new();

    /// <param name="timeZone"><c>Statistics:TimeZone</c>: the month's days are this zone's.</param>
    /// <param name="defaultLimit"><c>PublicChannels:DefaultMonthlyTokenLimit</c>.</param>
    public OrganizationTokenUsage(IOrganizationTokenUsageSource source, TimeProvider clock, TimeZoneInfo timeZone, long defaultLimit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentOutOfRangeException.ThrowIfNegative(defaultLimit);
        _source = source;
        _clock = clock;
        _timeZone = timeZone;
        _defaultLimit = defaultLimit;
    }

    /// <summary>The usage of <paramref name="organizationId"/>, from the cache when it is under
    /// <see cref="CacheDuration"/> old.</summary>
    public async Task<OrganizationTokenUsageSnapshot> GetAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(organizationId, out var cached) && now < cached.ExpiresAt)
        {
            return cached.Snapshot;
        }

        var (month, start, end) = OrganizationTokenUsageRules.MonthOf(now, _timeZone);
        var limit = await _source.GetMonthlyTokenLimitAsync(organizationId, cancellationToken) ?? _defaultLimit;
        var used = await _source.SumChatTokensAsync(organizationId, start, end, cancellationToken);
        var snapshot = new OrganizationTokenUsageSnapshot(month, used, limit, OrganizationTokenUsageRules.StateOf(used, limit));

        // Stamped with the time the answer was asked for, not the (later) time it arrived.
        _cache[organizationId] = (snapshot, now + CacheDuration);
        return snapshot;
    }
}
