using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Observability;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Organizations;

/// <summary>What one cleanup did.</summary>
/// <param name="Days">The retention applied; <see langword="null"/> for forever (nothing deleted).</param>
/// <param name="Cutoff">Threads and answer outcomes strictly before it were deleted; <see langword="null"/>
/// for forever.</param>
/// <param name="TookEffect">The pending retention that became current first, if any.</param>
/// <param name="Batches">How many delete statements (each its own transaction) ran.</param>
public sealed record RetentionCleanupResult(
    int? Days, DateTimeOffset? Cutoff, RetentionSwitch? TookEffect, int ThreadCount, int AnswerOutcomeCount, int Batches);

/// <summary>
/// Scoped: one cleanup of the scope's organization (M6 plan §3 G, steps 1–6), shared by the daily job
/// (<see cref="RetentionCleanupHandler"/>) and the <c>retention-cleanup</c> subcommand.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>A pending retention whose buffer is over becomes current (<c>retention-took-effect</c>).</item>
/// <item>Forever: nothing is deleted.</item>
/// <item>The cutoff is 00:00 of the <c>Statistics:TimeZone</c> day N days before today
/// (<see cref="RetentionCleanupRules.Cutoff"/>).</item>
/// <item>Threads whose last message is before it are deleted whole; the database cascades to their
/// messages and citations.</item>
/// <item><c>AnswerOutcome</c>s are deleted by their own <c>At</c>, whatever the channel (decision B).</item>
/// <item>Both in batches of <see cref="RetentionCleanupRules.BatchSize"/>, each its own transaction.</item>
/// </list>
/// Nothing else is touched: model invocations, handoff copies in issues, reports, database records and
/// test runs keep their own lifecycle. A cleanup that deleted something writes one summary
/// <c>retention-cleanup</c> activity; one that deleted nothing writes none (log and metrics only).
/// </remarks>
public sealed class RetentionCleanupService
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly RetentionCleanupMetrics _metrics;
    private readonly ILogger<RetentionCleanupService> _logger;

    public RetentionCleanupService(
        AppDbContext dbContext,
        TimeProvider clock,
        IOptions<StatisticsOptions> options,
        RetentionCleanupMetrics metrics,
        ILogger<RetentionCleanupService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _timeZone = options.Value.TryResolve()
            ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.");
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Rows per delete statement; <see cref="RetentionCleanupRules.BatchSize"/> except in tests.</summary>
    internal int BatchSize { get; init; } = RetentionCleanupRules.BatchSize;

    /// <summary>The zone whose days the cutoff counts in (<c>Statistics:TimeZone</c>).</summary>
    public TimeZoneInfo TimeZone => _timeZone;

    /// <summary>
    /// The number of the organization's threads a retention of <paramref name="days"/> would delete as
    /// of <paramref name="asOf"/> — exactly what <see cref="RunAsync"/> deletes at the same instant
    /// with that retention current.
    /// </summary>
    public Task<int> CountExpiredThreadsAsync(int days, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var cutoff = RetentionCleanupRules.Cutoff(asOf, days, _timeZone);
        return _dbContext.ChatThreads.CountAsync(thread => thread.LastActivityAt < cutoff, cancellationToken);
    }

    /// <summary>Runs the cleanup as of <paramref name="asOf"/> (now for the daily job; the
    /// subcommand's <c>--as-of</c> in Development/Testing). Activities are stamped with the clock's
    /// real time.</summary>
    public async Task<RetentionCleanupResult> RunAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var organization = await OrganizationSettings.FindAsync(_dbContext, tracked: true, cancellationToken)
            ?? throw new InvalidOperationException("A retention cleanup runs for an existing organization.");

        var tookEffect = await ApplyDueRetentionAsync(organization, asOf, cancellationToken);
        if (organization.RetentionDays is not { } days)
        {
            _metrics.Ran();
            _logger.LogInformation("Retention cleanup of organization {OrganizationId}: conversations are kept forever.", organization.Id);
            return new RetentionCleanupResult(null, null, tookEffect, 0, 0, 0);
        }

        var cutoff = RetentionCleanupRules.Cutoff(asOf, days, _timeZone);
        var threads = await DeleteThreadsInBatchesAsync(
            _dbContext.ChatThreads.Where(thread => thread.LastActivityAt < cutoff), cancellationToken);
        var answerOutcomes = await DeleteInBatchesAsync(
            _dbContext.AnswerOutcomes.Where(outcome => outcome.At < cutoff).OrderBy(outcome => outcome.At), cancellationToken);
        var (threadCount, answerOutcomeCount) = (threads.Count, answerOutcomes.Count);
        var batches = threads.Batches + answerOutcomes.Batches;

        if (threadCount + answerOutcomeCount > 0)
        {
            _dbContext.OrganizationActivities.Add(OrganizationActivity.RetentionCleanup(
                organization.Id, _clock.GetUtcNow(), days, cutoff, threadCount, answerOutcomeCount));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        _metrics.Ran(threadCount, answerOutcomeCount);
        _logger.LogInformation(
            "Retention cleanup of organization {OrganizationId} ({Days} days, cutoff {Cutoff:o}): deleted {ThreadCount} threads and {AnswerOutcomeCount} answer outcomes in {Batches} batches.",
            organization.Id, days, cutoff, threadCount, answerOutcomeCount, batches);
        return new RetentionCleanupResult(days, cutoff, tookEffect, threadCount, answerOutcomeCount, batches);
    }

    /// <summary>Step 1, in its own save with its activity. Idempotent: a second call finds nothing
    /// pending. A settings <c>PUT</c> saved in between wins: the row is read again and checked once
    /// more.</summary>
    private async Task<RetentionSwitch?> ApplyDueRetentionAsync(
        Organization organization, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var change = organization.ApplyDueRetention(asOf);
            if (change is null)
            {
                return null;
            }

            _dbContext.OrganizationActivities.Add(OrganizationActivity.RetentionTookEffect(organization.Id, _clock.GetUtcNow(), change));
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return change;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                _dbContext.ChangeTracker.Clear();
                organization = await OrganizationSettings.FindAsync(_dbContext, tracked: true, cancellationToken)
                    ?? throw new InvalidOperationException("The organization disappeared during its retention cleanup.");
            }
        }
    }

    /// <summary>
    /// Deletes the <paramref name="threads"/> — the cleanup's expired ones, or an assistant's for an
    /// immediate purge (M6-5, <see cref="ConversationPurgeEndpoints"/>) — whole, oldest activity
    /// first, in batches of <see cref="BatchSize"/> (each its own transaction, §3 G step 6). The
    /// database cascades to their messages and citations; nothing else is touched (answer outcomes,
    /// handoff copies in issues). The query runs under the organization filter like every other.
    /// </summary>
    public Task<BatchedDeletion> DeleteThreadsInBatchesAsync(IQueryable<ChatThread> threads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(threads);
        return DeleteInBatchesAsync(threads.OrderBy(thread => thread.LastActivityAt), cancellationToken);
    }

    /// <summary>One delete statement (the first <see cref="BatchSize"/> of <paramref name="rows"/>)
    /// per round, in its own transaction, until a round deletes less than a full batch.</summary>
    private async Task<BatchedDeletion> DeleteInBatchesAsync<T>(IOrderedQueryable<T> rows, CancellationToken cancellationToken)
        where T : class
    {
        var total = 0;
        var batches = 0;
        while (true)
        {
            int deleted;
            await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
            {
                deleted = await rows.Take(BatchSize).ExecuteDeleteAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            batches++;
            total += deleted;
            if (deleted < BatchSize)
            {
                return new BatchedDeletion(total, batches);
            }
        }
    }
}

/// <summary>What <see cref="RetentionCleanupService.DeleteThreadsInBatchesAsync"/> deleted.</summary>
/// <param name="Count">Rows deleted.</param>
/// <param name="Batches">Delete statements run, each its own transaction (at least one).</param>
public sealed record BatchedDeletion(int Count, int Batches);

/// <summary>
/// The cleanup's counters on the application's meter (<see cref="SmartAgriMeter"/>): runs, and
/// deleted rows by kind (<c>record</c> = <c>chat-thread</c> or <c>answer-outcome</c>). A chain that
/// stopped running shows as no runs (M6 plan §7 technical risk 3).
/// </summary>
public sealed class RetentionCleanupMetrics
{
    public const string RunsInstrument = "smartagri.retention.cleanup.runs";

    public const string DeletedInstrument = "smartagri.retention.cleanup.deleted";

    public const string RecordTag = "record";

    private readonly Counter<long> _runs;
    private readonly Counter<long> _deleted;

    public RetentionCleanupMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SmartAgriMeter.Name);
        _runs = meter.CreateCounter<long>(RunsInstrument, unit: "{run}", description: "Conversation retention cleanups run.");
        _deleted = meter.CreateCounter<long>(
            DeletedInstrument, unit: "{record}", description: "Rows deleted by the conversation retention cleanup, by record.");
    }

    internal void Ran(int threadCount = 0, int answerOutcomeCount = 0)
    {
        _runs.Add(1);
        if (threadCount > 0)
        {
            _deleted.Add(threadCount, new KeyValuePair<string, object?>(RecordTag, "chat-thread"));
        }

        if (answerOutcomeCount > 0)
        {
            _deleted.Add(answerOutcomeCount, new KeyValuePair<string, object?>(RecordTag, "answer-outcome"));
        }
    }
}
