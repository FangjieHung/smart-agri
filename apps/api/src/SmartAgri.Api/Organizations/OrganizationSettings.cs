using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Errors;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Organizations;

/// <summary>A settings section's 「上次變更」: who last changed it and when (M6 plan §3 D).</summary>
/// <param name="ActorName">The account's display name now; 「已停用的帳號」 when it can no longer be
/// found (as on the other screens), 「系統」 for a system action.</param>
public sealed record OrganizationSettingChangeView(string ActorName, DateTimeOffset At);

/// <summary>
/// What the organization settings APIs share (the chat model here; retention in M6-4): the
/// <c>409</c> for a stale <see cref="Organization.SettingsRevision"/> and each section's last change
/// from <see cref="OrganizationActivity"/>.
/// </summary>
public static class OrganizationSettings
{
    /// <summary>The <c>409</c>'s <c>reason</c>: the settings changed since the caller read them.</summary>
    public const string RevisionConflictReason = "organization-settings-conflict";

    public const string RevisionConflictMessage = "組織設定已在其他分頁或由其他管理者更新過，請重新載入後再修改。";

    internal const string RemovedAccountName = "已停用的帳號";

    internal const string SystemActorName = "系統";

    /// <summary><c>409</c> <see cref="RevisionConflictReason"/>.</summary>
    public static IResult RevisionConflict() =>
        ApiErrors.WithReason(StatusCodes.Status409Conflict, RevisionConflictReason, RevisionConflictMessage);

    /// <summary>The caller's organization row (tracked when <paramref name="tracked"/>), or
    /// <see langword="null"/> without an organization.</summary>
    public static async Task<Organization?> FindAsync(AppDbContext dbContext, bool tracked, CancellationToken cancellationToken)
    {
        if (dbContext.OrganizationContext.OrganizationId is not { } organizationId)
        {
            return null;
        }

        var organizations = tracked ? dbContext.Organizations : dbContext.Organizations.AsNoTracking();
        return await organizations.SingleOrDefaultAsync(organization => organization.Id == organizationId, cancellationToken);
    }

    /// <summary>The newest <see cref="OrganizationActivity"/> with one of <paramref name="actions"/>,
    /// as its section's 「上次變更」, or <see langword="null"/> before the first change.</summary>
    public static async Task<OrganizationSettingChangeView?> LastChangeAsync(
        AppDbContext dbContext, IReadOnlyCollection<OrganizationActivityAction> actions, CancellationToken cancellationToken)
    {
        var last = await dbContext.OrganizationActivities
            .AsNoTracking()
            .Where(activity => actions.Contains(activity.Action))
            .OrderByDescending(activity => activity.At)
            .ThenByDescending(activity => activity.Id)
            .Select(activity => new { activity.ActorAccountId, activity.At })
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null)
        {
            return null;
        }

        if (last.ActorAccountId is not { } actorId)
        {
            return new OrganizationSettingChangeView(SystemActorName, last.At);
        }

        var name = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.Id == actorId)
            .Select(account => account.DisplayName)
            .SingleOrDefaultAsync(cancellationToken);
        return new OrganizationSettingChangeView(name ?? RemovedAccountName, last.At);
    }
}
