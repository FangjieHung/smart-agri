using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>How pressing a card's button went (<see cref="CoordinatorCommands.ConfirmAsync"/>,
/// <see cref="CoordinatorCommands.CancelAsync"/>).</summary>
public enum CoordinatorCardOutcome
{
    /// <summary>Done: this press changed the command.</summary>
    Done,

    /// <summary>The sender is not (or no longer) an owner or manager of the shop.</summary>
    NotAMember,

    /// <summary>Unknown, or its 30 minutes are over.</summary>
    Expired,

    AlreadyConfirmed,

    AlreadyCancelled,

    /// <summary>The card was edited since (another version), or the command still waits for details.</summary>
    Outdated,

    /// <summary>A reopen whose command was reopened by another card in the meantime.</summary>
    AlreadyReopened,
}

/// <summary>
/// The 統籌助理's commands (store-coordinator design §5 Slice 3, #374), each read and written in a context
/// acting for one organization (<see cref="FixedOrganizationContext"/>), under the usual filter and write guard
/// — which organization comes from the sender's memberships (<c>CoordinatorLookup</c>) or a postback the
/// handler checked against them. Every change of a card is a compare-and-set on its status, version and
/// expiry, so two presses of 〔確認〕 confirm it once and a press on an older card changes nothing.
/// </summary>
public sealed class CoordinatorCommands
{
    /// <summary>EF Core's name for the filtered unique index on <c>CoordinatorCommands.ReopensCommandId</c>
    /// (confirmed rows only).</summary>
    internal const string ConfirmedReopenIndex = "IX_CoordinatorCommands_ReopensCommandId";

    private readonly DbContextOptions<AppDbContext> _options;

    public CoordinatorCommands(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    public async Task AddAsync(CoordinatorCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var dbContext = ForOrganization(command.OrganizationId);
        dbContext.CoordinatorCommands.Add(command);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<CoordinatorCommand?> FindAsync(Guid organizationId, Guid commandId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommands.AsNoTracking().SingleOrDefaultAsync(command => command.Id == commandId, cancellationToken);
    }

    /// <summary>Whether <paramref name="lineUserId"/> is a member (老闆 or 店長) of <paramref name="organizationId"/>,
    /// read in that shop's own context.</summary>
    public async Task<bool> IsMemberAsync(Guid organizationId, string lineUserId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorMembers.AnyAsync(member => member.LineUserId == lineUserId, cancellationToken);
    }

    /// <summary><paramref name="lineUserId"/>'s commands of the shop waiting for details at <paramref name="now"/>,
    /// newest first.</summary>
    public async Task<IReadOnlyList<CoordinatorCommand>> AwaitingAsync(
        Guid organizationId, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommands
            .AsNoTracking()
            .Where(command => command.RequestedByLineUserId == lineUserId
                && command.Status == CoordinatorCommandStatus.AwaitingDetails
                && command.ExpiresAt > now)
            .OrderByDescending(command => command.ExpiresAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary><paramref name="lineUserId"/>'s preview card of the shop sent last that is still open at
    /// <paramref name="now"/>; <see langword="null"/> when none.</summary>
    public async Task<CoordinatorCommand?> LatestPreviewAsync(
        Guid organizationId, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommands
            .AsNoTracking()
            .Where(command => command.RequestedByLineUserId == lineUserId
                && command.Status == CoordinatorCommandStatus.Previewed
                && command.ExpiresAt > now)
            .OrderByDescending(command => command.ExpiresAt)
            .ThenByDescending(command => command.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The shop's confirmed 臨時店休／提早打烊／延後開店 for <paramref name="today"/> or later that no confirmed
    /// reopen has undone yet, earliest date first (what 「恢復營業」 can undo, Q21).
    /// </summary>
    public async Task<IReadOnlyList<CoordinatorCommand>> ReopenCandidatesAsync(Guid organizationId, DateOnly today, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommands
            .AsNoTracking()
            .Where(command => command.Status == CoordinatorCommandStatus.Confirmed
                && command.Scenario != ShopScenario.Reopen
                && command.Date >= today
                && !dbContext.CoordinatorCommands.Any(reopen => reopen.ReopensCommandId == command.Id
                    && reopen.Status == CoordinatorCommandStatus.Confirmed))
            .OrderBy(command => command.Date)
            .ThenBy(command => command.Time)
            .ThenBy(command => command.ConfirmedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The shop's <paramref name="limit"/> latest confirmed commands, newest confirmation first (the execution
    /// records of decision F, #375).</summary>
    public async Task<IReadOnlyList<CoordinatorCommand>> RecentConfirmedAsync(Guid organizationId, int limit, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorCommands
            .AsNoTracking()
            .Where(command => command.Status == CoordinatorCommandStatus.Confirmed)
            .OrderByDescending(command => command.ConfirmedAt)
            .ThenByDescending(command => command.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Records what is known of a command still waiting for details, and restarts its 30 minutes;
    /// <see langword="false"/> when it no longer waits (answered, cancelled or expired meanwhile).</summary>
    public async Task<bool> UpdateAwaitingAsync(
        Guid organizationId, Guid commandId, DateOnly? date, TimeOnly? time, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var expiresAt = now + CoordinatorCommand.CardValidity;
        return await Awaiting(dbContext, commandId, now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(command => command.Date, date)
                    .SetProperty(command => command.Time, time)
                    .SetProperty(command => command.ExpiresAt, expiresAt),
                cancellationToken) == 1;
    }

    /// <summary>A command that waited for details gets them and its preview card (same version);
    /// <see langword="false"/> when it no longer waits.</summary>
    public async Task<bool> PreviewAwaitingAsync(
        Guid organizationId, Guid commandId, DateOnly date, TimeOnly? time, string announcement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!CoordinatorCommand.IsValidAnnouncement(announcement))
        {
            throw new ArgumentException("Not a valid announcement.", nameof(announcement));
        }

        await using var dbContext = ForOrganization(organizationId);
        var expiresAt = now + CoordinatorCommand.CardValidity;
        return await Awaiting(dbContext, commandId, now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(command => command.Date, date)
                    .SetProperty(command => command.Time, time)
                    .SetProperty(command => command.AnnouncementText, announcement)
                    .SetProperty(command => command.Status, CoordinatorCommandStatus.Previewed)
                    .SetProperty(command => command.ExpiresAt, expiresAt),
                cancellationToken) == 1;
    }

    /// <summary>Drops a command that waited for details (the conversation moved on).</summary>
    public async Task CancelAwaitingAsync(Guid organizationId, Guid commandId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        await dbContext.CoordinatorCommands
            .Where(command => command.Id == commandId && command.Status == CoordinatorCommandStatus.AwaitingDetails)
            .ExecuteUpdateAsync(setters => setters.SetProperty(command => command.Status, CoordinatorCommandStatus.Cancelled), cancellationToken);
    }

    /// <summary>「改成：」: replaces the announcement of version <paramref name="version"/> of an open card, a new
    /// version with 30 more minutes; <see langword="false"/> when that card changed or closed meanwhile.</summary>
    public async Task<bool> EditAsync(
        Guid organizationId, Guid commandId, int version, string announcement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!CoordinatorCommand.IsValidAnnouncement(announcement))
        {
            throw new ArgumentException("Not a valid announcement.", nameof(announcement));
        }

        await using var dbContext = ForOrganization(organizationId);
        var expiresAt = now + CoordinatorCommand.CardValidity;
        return await Open(dbContext, commandId, version, now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(command => command.AnnouncementText, announcement)
                    .SetProperty(command => command.Version, command => command.Version + 1)
                    .SetProperty(command => command.ExpiresAt, expiresAt),
                cancellationToken) == 1;
    }

    /// <summary>「加發更正公告」: flips a reopen card's correction broadcast, a new version with 30 more minutes,
    /// if <paramref name="lineUserId"/> is a member; <see langword="false"/> when that card changed or closed.</summary>
    public async Task<bool> ToggleCorrectionAsync(
        Guid organizationId, Guid commandId, int version, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var expiresAt = now + CoordinatorCommand.CardValidity;
        return await Open(dbContext, commandId, version, now)
            .Where(command => command.Scenario == ShopScenario.Reopen
                && dbContext.CoordinatorMembers.Any(member => member.LineUserId == lineUserId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(command => command.CorrectionBroadcast, command => !command.CorrectionBroadcast)
                    .SetProperty(command => command.Version, command => command.Version + 1)
                    .SetProperty(command => command.ExpiresAt, expiresAt),
                cancellationToken) == 1;
    }

    /// <summary>
    /// 〔確認〕 on version <paramref name="version"/>: <c>previewed → confirmed</c> by <paramref name="lineUserId"/>
    /// in one compare-and-set (same version, unexpired, the sender a member of the shop), so of any number of
    /// presses exactly one is <see cref="CoordinatorCardOutcome.Done"/>. A reopen of a command another reopen
    /// already undid is <see cref="CoordinatorCardOutcome.AlreadyReopened"/> (the unique index).
    /// </summary>
    public async Task<CoordinatorCardOutcome> ConfirmAsync(
        Guid organizationId, Guid commandId, int version, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        try
        {
            var confirmed = await Open(dbContext, commandId, version, now)
                .Where(command => dbContext.CoordinatorMembers.Any(member => member.LineUserId == lineUserId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(command => command.Status, CoordinatorCommandStatus.Confirmed)
                        .SetProperty(command => command.ConfirmedAt, now)
                        .SetProperty(command => command.ConfirmedByLineUserId, lineUserId),
                    cancellationToken);
            if (confirmed == 1)
            {
                return CoordinatorCardOutcome.Done;
            }
        }
        catch (Exception exception) when (DatabaseErrors.IsUniqueViolationOf(exception, ConfirmedReopenIndex))
        {
            return CoordinatorCardOutcome.AlreadyReopened;
        }

        return await WhyNotAsync(dbContext, commandId, version, lineUserId, now, cancellationToken);
    }

    /// <summary>〔取消〕 on version <paramref name="version"/>: <c>previewed → cancelled</c>, the same checks as
    /// <see cref="ConfirmAsync"/>.</summary>
    public async Task<CoordinatorCardOutcome> CancelAsync(
        Guid organizationId, Guid commandId, int version, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var cancelled = await Open(dbContext, commandId, version, now)
            .Where(command => dbContext.CoordinatorMembers.Any(member => member.LineUserId == lineUserId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(command => command.Status, CoordinatorCommandStatus.Cancelled), cancellationToken);
        return cancelled == 1
            ? CoordinatorCardOutcome.Done
            : await WhyNotAsync(dbContext, commandId, version, lineUserId, now, cancellationToken);
    }

    /// <summary>Why a card's button changed nothing, in this order: not a member, unknown, already confirmed or
    /// cancelled, expired, another version.</summary>
    internal async Task<CoordinatorCardOutcome> WhyNotAsync(
        Guid organizationId, Guid commandId, int version, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await WhyNotAsync(dbContext, commandId, version, lineUserId, now, cancellationToken);
    }

    private static async Task<CoordinatorCardOutcome> WhyNotAsync(
        AppDbContext dbContext, Guid commandId, int version, string lineUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await dbContext.CoordinatorMembers.AnyAsync(member => member.LineUserId == lineUserId, cancellationToken))
        {
            return CoordinatorCardOutcome.NotAMember;
        }

        var command = await dbContext.CoordinatorCommands.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == commandId, cancellationToken);
        return command switch
        {
            null => CoordinatorCardOutcome.Expired,
            { Status: CoordinatorCommandStatus.Confirmed } => CoordinatorCardOutcome.AlreadyConfirmed,
            { Status: CoordinatorCommandStatus.Cancelled } => CoordinatorCardOutcome.AlreadyCancelled,
            _ when command.IsExpired(now) => CoordinatorCardOutcome.Expired,
            _ => CoordinatorCardOutcome.Outdated,
        };
    }

    private static IQueryable<CoordinatorCommand> Awaiting(AppDbContext dbContext, Guid commandId, DateTimeOffset now) =>
        dbContext.CoordinatorCommands.Where(command => command.Id == commandId
            && command.Status == CoordinatorCommandStatus.AwaitingDetails
            && command.ExpiresAt > now);

    private static IQueryable<CoordinatorCommand> Open(AppDbContext dbContext, Guid commandId, int version, DateTimeOffset now) =>
        dbContext.CoordinatorCommands.Where(command => command.Id == commandId
            && command.Status == CoordinatorCommandStatus.Previewed
            && command.Version == version
            && command.ExpiresAt > now);

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
