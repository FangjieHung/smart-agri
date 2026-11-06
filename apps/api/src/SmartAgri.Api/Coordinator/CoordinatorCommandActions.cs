using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>How pressing 〔已完成〕 or 〔重試〕 went (<see cref="CoordinatorCommandActions"/>).</summary>
public enum CoordinatorActionPressOutcome
{
    /// <summary>This press changed the row.</summary>
    Done,

    /// <summary>The sender is not (or no longer) an owner or manager of the shop.</summary>
    NotAMember,

    /// <summary>No such action in the shop.</summary>
    Unknown,

    /// <summary>The row is not in the state the button is for (<see cref="CoordinatorActionPress.Action"/> says where it is).</summary>
    NotApplicable,
}

/// <summary>A press's outcome and the row as it is after it (<see langword="null"/> when unknown).</summary>
public sealed record CoordinatorActionPress(CoordinatorActionPressOutcome Outcome, CoordinatorCommandAction? Action);

/// <summary>
/// The actions of confirmed 統籌助理 commands (table <c>CoordinatorCommandActions</c>, store-coordinator design decision F,
/// §5 Slice 4, #375), each read and written in a context acting for one organization
/// (<see cref="FixedOrganizationContext"/>), under the usual filter and write guard. Every change of a row's status is a
/// compare-and-set, so a job whose row was cancelled does nothing, 〔已完成〕 records once, and 〔重試〕 re-runs only a
/// failed row (never one that succeeded).
/// </summary>
public sealed class CoordinatorCommandActions
{
    private readonly DbContextOptions<AppDbContext> _options;

    public CoordinatorCommandActions(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    /// <summary>Command <paramref name="commandId"/>'s rows in position order.</summary>
    public async Task<IReadOnlyList<CoordinatorCommandAction>> ListAsync(Guid organizationId, Guid commandId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommandActions.AsNoTracking()
            .Where(action => action.CommandId == commandId)
            .OrderBy(action => action.Position)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The rows of each of <paramref name="commandIds"/>, in position order.</summary>
    public async Task<ILookup<Guid, CoordinatorCommandAction>> ListAsync(
        Guid organizationId, IReadOnlyCollection<Guid> commandIds, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var rows = await dbContext.CoordinatorCommandActions.AsNoTracking()
            .Where(action => commandIds.Contains(action.CommandId))
            .OrderBy(action => action.Position)
            .ToListAsync(cancellationToken);
        return rows.ToLookup(action => action.CommandId);
    }

    public async Task<CoordinatorCommandAction?> FindAsync(Guid organizationId, Guid actionId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommandActions.AsNoTracking().SingleOrDefaultAsync(action => action.Id == actionId, cancellationToken);
    }

    /// <summary>
    /// Starts a confirmed command, in one transaction: drops the rows <paramref name="cancel"/> (a reopen's — those
    /// still scheduled or failed become cancelled, reported now), runs <paramref name="alongside"/> (a closure cancelling
    /// the reservations it takes away, #392) and writes <paramref name="actions"/> with the rows it returns, then queues
    /// one <see cref="CoordinatorRunActionJob"/> per scheduled row at its due time. Not cancellable: the command is
    /// confirmed already. Every row written, in position order.
    /// </summary>
    /// <param name="alongside">Work of the same transaction, given its context (acting for the shop): what it changes
    /// commits with the rows or not at all; the rows it returns are written too.</param>
    public async Task<IReadOnlyList<CoordinatorCommandAction>> StartAsync(
        Guid organizationId,
        IReadOnlyList<CoordinatorCommandAction> actions,
        IReadOnlyList<Guid> cancel,
        DateTimeOffset now,
        Func<AppDbContext, Task<IReadOnlyList<CoordinatorCommandAction>>>? alongside = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(cancel);
        await using var dbContext = ForOrganization(organizationId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(CancellationToken.None);
        if (cancel.Count > 0)
        {
            await dbContext.CoordinatorCommandActions
                .Where(action => cancel.Contains(action.Id)
                    && (action.Status == CoordinatorActionStatus.Scheduled || action.Status == CoordinatorActionStatus.Failed))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(action => action.Status, CoordinatorActionStatus.Cancelled)
                        .SetProperty(action => action.ReportedAt, now),
                    CancellationToken.None);
        }

        var rows = alongside is null ? actions : [.. actions, .. await alongside(dbContext)];
        dbContext.CoordinatorCommandActions.AddRange(rows);
        foreach (var action in rows.Where(action => action.Status == CoordinatorActionStatus.Scheduled))
        {
            dbContext.BackgroundJobs.Add(Job(organizationId, action.Id, now, action.DueAt));
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return [.. rows.OrderBy(action => action.Position)];
    }

    /// <summary>A reminder that is due: <c>scheduled → awaiting-manual</c>; <see langword="false"/> when it is no longer
    /// scheduled (cancelled by a reopen).</summary>
    public async Task<bool> BeginReminderAsync(Guid organizationId, Guid actionId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await Scheduled(dbContext, actionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(action => action.Status, CoordinatorActionStatus.AwaitingManual)
                    .SetProperty(action => action.Attempts, action => action.Attempts + 1),
                cancellationToken) == 1;
    }

    /// <summary>
    /// Records an automatic try: succeeded (with what to undo it by), failed (with its code) or — the integration
    /// being unavailable — a reminder now (Q18), with the reason code when there is one (#376). From <c>scheduled</c>; <see langword="false"/> when the row was no
    /// longer scheduled (a reopen dropped it while it ran) — a success is then recorded anyway, since it did happen,
    /// but nobody is told and nothing undoes it.
    /// </summary>
    public async Task<bool> RecordAsync(Guid organizationId, Guid actionId, CoordinatorActionResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var dbContext = ForOrganization(organizationId);
        var token = CancellationToken.None;
        switch (result.Outcome)
        {
            case CoordinatorActionOutcome.Succeeded:
                if (await Succeed(Scheduled(dbContext, actionId), result, now, token))
                {
                    return true;
                }

                await Succeed(
                    dbContext.CoordinatorCommandActions.Where(action => action.Id == actionId && action.Status == CoordinatorActionStatus.Cancelled),
                    result,
                    now,
                    token);
                return false;

            case CoordinatorActionOutcome.Failed:
                return await Scheduled(dbContext, actionId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(action => action.Status, CoordinatorActionStatus.Failed)
                            .SetProperty(action => action.FailureCode, result.FailureCode)
                            .SetProperty(action => action.CompletedAt, now)
                            .SetProperty(action => action.Attempts, action => action.Attempts + 1),
                        token) == 1;

            default:
                return await Scheduled(dbContext, actionId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(action => action.Status, CoordinatorActionStatus.AwaitingManual)
                            .SetProperty(action => action.Mode, ShopActionMode.Reminder)
                            .SetProperty(action => action.FailureCode, result.FailureCode)
                            .SetProperty(action => action.Attempts, action => action.Attempts + 1),
                        token) == 1;
        }

        static async Task<bool> Succeed(
            IQueryable<CoordinatorCommandAction> rows, CoordinatorActionResult result, DateTimeOffset now, CancellationToken token) =>
            await rows.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(action => action.Status, CoordinatorActionStatus.Succeeded)
                    .SetProperty(action => action.ExternalRef, result.ExternalRef)
                    .SetProperty(action => action.CompletedAt, now)
                    .SetProperty(action => action.Attempts, action => action.Attempts + 1),
                token) == 1;
    }

    /// <summary>
    /// Keeps <paramref name="externalRef"/> — what an integration answered, e.g. the FB post's id (#379) — on the row at
    /// once, before the try is recorded: a run of the same row again (the job retried after the database failed, or
    /// 〔重試〕 after it gave up) then finds it and does not act twice, where the service has no idempotency key. Only
    /// a row without one yet; <see langword="false"/> otherwise. Not cancellable: the service has acted already.
    /// </summary>
    public async Task<bool> KeepExternalRefAsync(Guid organizationId, Guid actionId, string externalRef)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalRef);
        if (externalRef.Length > CoordinatorCommandAction.ExternalRefMaxLength)
        {
            throw new ArgumentException($"At most {CoordinatorCommandAction.ExternalRefMaxLength} characters.", nameof(externalRef));
        }

        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommandActions
            .Where(action => action.Id == actionId && action.ExternalRef == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(action => action.ExternalRef, externalRef), CancellationToken.None) == 1;
    }

    /// <summary>The job gave up on a row (it kept failing before recording anything): <c>scheduled → failed</c>
    /// (<see cref="CoordinatorFailureCodes.Error"/>), in <paramref name="dbContext"/> — the job's own, in its
    /// final-failure transaction.</summary>
    public static Task<int> GiveUpAsync(AppDbContext dbContext, Guid actionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return Scheduled(dbContext, actionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(action => action.Status, CoordinatorActionStatus.Failed)
                    .SetProperty(action => action.FailureCode, CoordinatorFailureCodes.Error)
                    .SetProperty(action => action.CompletedAt, now),
                cancellationToken);
    }

    /// <summary>〔已完成〕: <c>awaiting-manual → done-manually</c> by <paramref name="lineUserId"/> at <paramref name="now"/>,
    /// if they are a member of the shop; any later press changes nothing.</summary>
    public async Task<CoordinatorActionPress> CompleteAsync(
        Guid organizationId, Guid actionId, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var done = await dbContext.CoordinatorCommandActions
            .Where(action => action.Id == actionId
                && action.Status == CoordinatorActionStatus.AwaitingManual
                && dbContext.CoordinatorMembers.Any(member => member.LineUserId == lineUserId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(action => action.Status, CoordinatorActionStatus.DoneManually)
                    .SetProperty(action => action.CompletedAt, now)
                    .SetProperty(action => action.CompletedByLineUserId, lineUserId),
                cancellationToken);
        return await PressAsync(dbContext, actionId, lineUserId, done == 1, cancellationToken);
    }

    /// <summary>
    /// 〔重試〕: <c>failed → scheduled</c> now, with a new job, if <paramref name="lineUserId"/> is a member of the shop —
    /// in one transaction, so of two presses one retries. A row that succeeded, waits or was cancelled is not touched.
    /// </summary>
    public async Task<CoordinatorActionPress> RetryAsync(
        Guid organizationId, Guid actionId, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var retried = await dbContext.CoordinatorCommandActions
                .Where(action => action.Id == actionId
                    && action.Status == CoordinatorActionStatus.Failed
                    && dbContext.CoordinatorMembers.Any(member => member.LineUserId == lineUserId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(action => action.Status, CoordinatorActionStatus.Scheduled)
                        .SetProperty(action => action.DueAt, now)
                        .SetProperty(action => action.FailureCode, (string?)null)
                        .SetProperty(action => action.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(action => action.ReportedAt, (DateTimeOffset?)null),
                    cancellationToken);
            if (retried == 1)
            {
                dbContext.BackgroundJobs.Add(Job(organizationId, actionId, now, now));
                await dbContext.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                return await PressAsync(dbContext, actionId, lineUserId, changed: true, cancellationToken);
            }
        }

        return await PressAsync(dbContext, actionId, lineUserId, changed: false, cancellationToken);
    }

    /// <summary>
    /// The rows of command <paramref name="commandId"/> whose outcome nobody has been told yet, marked told now — but
    /// only once none of its automatic tries due by <paramref name="now"/> is still waiting (so the follow-up comes
    /// once, after the last of them). Serialized per command by the row lock of a no-op update of the command (no raw
    /// SQL), held until the end of the transaction, so of several jobs finishing together exactly one gets the rows.
    /// Empty when there is nothing (yet) to tell.
    /// </summary>
    public async Task<IReadOnlyList<CoordinatorCommandAction>> TakeUnreportedAsync(
        Guid organizationId, Guid commandId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.CoordinatorCommands
            .Where(command => command.Id == commandId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(command => command.Version, command => command.Version), cancellationToken);
        var rows = await dbContext.CoordinatorCommandActions.AsNoTracking()
            .Where(action => action.CommandId == commandId && action.ReportedAt == null)
            .OrderBy(action => action.Position)
            .ToListAsync(cancellationToken);
        if (rows.Any(action => action.Status == CoordinatorActionStatus.Scheduled && action.DueAt <= now))
        {
            return [];
        }

        var finished = rows.Where(action => action.Status != CoordinatorActionStatus.Scheduled).ToList();
        if (finished.Count == 0)
        {
            return [];
        }

        var ids = finished.Select(action => action.Id).ToList();
        await dbContext.CoordinatorCommandActions
            .Where(action => ids.Contains(action.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(action => action.ReportedAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return finished;
    }

    private static BackgroundJob Job(Guid organizationId, Guid actionId, DateTimeOffset now, DateTimeOffset runAfter) =>
        BackgroundJob.Create(organizationId, CoordinatorRunActionJob.Kind, new CoordinatorRunActionJob(actionId), now, runAfter);

    private static IQueryable<CoordinatorCommandAction> Scheduled(AppDbContext dbContext, Guid actionId) =>
        dbContext.CoordinatorCommandActions.Where(action => action.Id == actionId && action.Status == CoordinatorActionStatus.Scheduled);

    private static async Task<CoordinatorActionPress> PressAsync(
        AppDbContext dbContext, Guid actionId, string lineUserId, bool changed, CancellationToken cancellationToken)
    {
        if (!changed && !await dbContext.CoordinatorMembers.AnyAsync(member => member.LineUserId == lineUserId, cancellationToken))
        {
            return new CoordinatorActionPress(CoordinatorActionPressOutcome.NotAMember, null);
        }

        var action = await dbContext.CoordinatorCommandActions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == actionId, cancellationToken);
        return action is null
            ? new CoordinatorActionPress(CoordinatorActionPressOutcome.Unknown, null)
            : new CoordinatorActionPress(changed ? CoordinatorActionPressOutcome.Done : CoordinatorActionPressOutcome.NotApplicable, action);
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
