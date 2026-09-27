using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Infrastructure.Jobs;

/// <summary>
/// Takes the next job off the queue. This is the one place in <c>apps/api/src</c> that
/// reads or changes <see cref="BackgroundJob"/> rows across organizations, and the one
/// place allowed raw SQL: a source-scanning test fails on raw SQL anywhere else, as it
/// does on turning the organization filter off outside <c>AccountLookup</c>.
/// </summary>
/// <remarks>
/// <para>
/// A runner must not know a job's organization before claiming it, so the claim cannot go
/// through the organization filter. It is a single statement instead, which also makes it
/// atomic: the inner <c>SELECT … FOR UPDATE SKIP LOCKED</c> locks one claimable row and
/// lets concurrent claimers skip it rather than wait, and the outer <c>UPDATE</c> marks it
/// running for a lease before anyone else can see it as claimable.
/// </para>
/// <para>
/// Nothing a claim returns is used to read or write other data directly: the runner then
/// processes the job, and records its outcome, in a scope whose current organization is
/// the job's. The queue-depth count here returns numbers per kind only.
/// </para>
/// <para>
/// Times come from the caller's <see cref="TimeProvider"/>, not the database's
/// <c>now()</c>, so tests can move leases and backoff forward.
/// </para>
/// </remarks>
public sealed class JobClaimer
{
    private static readonly string Queued = WireNames<BackgroundJobStatus>.ToWire(BackgroundJobStatus.Queued);

    private static readonly string Running = WireNames<BackgroundJobStatus>.ToWire(BackgroundJobStatus.Running);

    private readonly DbContextOptions<AppDbContext> _options;

    public JobClaimer(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    /// <summary>
    /// Claims the claimable job of one of <paramref name="kinds"/> with the earliest
    /// <see cref="BackgroundJob.RunAfter"/>, or returns <see langword="null"/> when there is
    /// none. Claimable: queued and due, or still running although its lease has expired
    /// (the process running it stopped). Claiming counts an attempt and leases the job
    /// until <paramref name="now"/> + <paramref name="lease"/>. Jobs of other kinds (no
    /// handler in this process, e.g. during a rolling upgrade) are left alone.
    /// </summary>
    public async Task<ClaimedJob?> TryClaimAsync(
        IReadOnlyCollection<string> kinds,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        if (kinds.Count == 0)
        {
            return null;
        }

        var kindList = kinds.ToArray();
        var lockedUntil = now + lease;

        await using var dbContext = CreateContext();
        var claimed = await dbContext.Database.SqlQuery<ClaimedJob>($"""
            UPDATE "BackgroundJobs" AS job
            SET "Status" = {Running},
                "Attempts" = job."Attempts" + 1,
                "LockedUntil" = {lockedUntil}
            WHERE job."Id" = (
                SELECT candidate."Id"
                FROM "BackgroundJobs" AS candidate
                WHERE candidate."Kind" = ANY({kindList})
                  AND ((candidate."Status" = {Queued} AND candidate."RunAfter" <= {now})
                    OR (candidate."Status" = {Running} AND candidate."LockedUntil" < {now}))
                ORDER BY candidate."RunAfter", candidate."Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING job."Id", job."OrganizationId", job."Kind", job."Payload"::text AS "Payload",
                job."Attempts", job."MaxAttempts", job."LockedUntil"
            """).ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    /// <summary>How many jobs of each kind are queued (due or scheduled for later), across
    /// all organizations. Counts only; for the queue-depth metric.</summary>
    public async Task<IReadOnlyList<QueuedJobCount>> CountQueuedAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = CreateContext();
        return await dbContext.Database.SqlQuery<QueuedJobCount>($"""
            SELECT "Kind", count(*)::integer AS "Count"
            FROM "BackgroundJobs"
            WHERE "Status" = {Queued}
            GROUP BY "Kind"
            """).ToListAsync(cancellationToken);
    }

    // Raw SQL never goes through the organization filter or the write guard, and needs no
    // organization: "no organization" makes sure nothing else done with this context could.
    private AppDbContext CreateContext() => new(_options, FixedOrganizationContext.None);
}

/// <summary>A job <see cref="JobClaimer"/> has just claimed (the row after the claim).</summary>
public sealed class ClaimedJob
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;

    /// <summary>Including this claim.</summary>
    public int Attempts { get; init; }

    public int MaxAttempts { get; init; }

    public DateTimeOffset LockedUntil { get; init; }
}

/// <summary>One row of <see cref="JobClaimer.CountQueuedAsync"/>.</summary>
public sealed class QueuedJobCount
{
    public string Kind { get; init; } = string.Empty;

    public int Count { get; init; }
}
