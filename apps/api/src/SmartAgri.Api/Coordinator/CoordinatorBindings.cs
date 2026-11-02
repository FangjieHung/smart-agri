using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Coordinator;
using SmartAgri.Infrastructure.Persistence;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>How a bind went (<see cref="CoordinatorBindings.BindAsync"/>).</summary>
public enum CoordinatorBindOutcome
{
    /// <summary>The sender is now a member; the code is used.</summary>
    Bound,

    /// <summary>The sender already was a member of that shop; the code is left unused.</summary>
    AlreadyBound,

    /// <summary>The code is used, expired, or was just used by someone else.</summary>
    Invalid,
}

/// <param name="StoreName">The shop's name (not for <see cref="CoordinatorBindOutcome.Invalid"/>).</param>
/// <param name="Role">The role bound with, or the one already held.</param>
public sealed record CoordinatorBindResult(CoordinatorBindOutcome Outcome, string? StoreName = null, CoordinatorRole? Role = null);

/// <summary>A newly issued binding code. <see cref="Code"/> is the secret to show once, never to log.</summary>
public sealed record IssuedBindingCode(string Code, string StoreName, CoordinatorRole Role, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"{nameof(IssuedBindingCode)} {{ Role = {Role}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>
/// The 統籌助理's writes (#372), each in a context acting for one organization
/// (<see cref="FixedOrganizationContext"/>), so the organization filter and write guard apply as for
/// any request: binding a LINE user with a code, and issuing codes (the operator's 老闆 codes and a
/// 老闆's 店長 codes). Which organization comes from <see cref="CoordinatorLookup"/>.
/// </summary>
public sealed class CoordinatorBindings
{
    /// <summary>EF Core's name for the unique index on <c>CoordinatorBindingCodes.CodeHash</c>.</summary>
    internal const string CodeHashIndex = "IX_CoordinatorBindingCodes_CodeHash";

    /// <summary>EF Core's name for the unique index on <c>CoordinatorMembers (OrganizationId, LineUserId)</c>.</summary>
    internal const string MemberIndex = "IX_CoordinatorMembers_OrganizationId_LineUserId";

    private const int IssueAttempts = 3;

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TimeProvider _clock;

    public CoordinatorBindings(DbContextOptions<AppDbContext> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>
    /// Binds <paramref name="lineUserId"/> to the code's organization with the code's role, and marks the
    /// code used — atomically: the code is taken with a compare-and-set (unused and unexpired), in the
    /// same transaction as the new member, so of two people sending one code exactly one is bound, and a
    /// failed insert leaves the code unused. A sender who already is a member keeps their role and the
    /// code stays unused.
    /// </summary>
    public async Task<CoordinatorBindResult> BindAsync(CoordinatorCodeMatch match, string lineUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!CoordinatorMember.IsValidLineUserId(lineUserId))
        {
            throw new ArgumentException("Not a LINE user id.", nameof(lineUserId));
        }

        await using var dbContext = ForOrganization(match.OrganizationId);
        var now = _clock.GetUtcNow();
        var storeName = await StoreNameAsync(dbContext, match.OrganizationId, cancellationToken);
        var code = await dbContext.CoordinatorBindingCodes
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == match.CodeId, cancellationToken);
        if (storeName is null || code is null || !code.IsUsable(now))
        {
            return new CoordinatorBindResult(CoordinatorBindOutcome.Invalid);
        }

        if (await RoleOfAsync(dbContext, lineUserId, cancellationToken) is { } held)
        {
            return new CoordinatorBindResult(CoordinatorBindOutcome.AlreadyBound, storeName, held);
        }

        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var taken = await dbContext.CoordinatorBindingCodes
                .Where(candidate => candidate.Id == code.Id && candidate.UsedAt == null && candidate.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.UsedAt, now)
                        .SetProperty(candidate => candidate.UsedByLineUserId, lineUserId),
                    cancellationToken);
            if (taken != 1)
            {
                return new CoordinatorBindResult(CoordinatorBindOutcome.Invalid);
            }

            dbContext.CoordinatorMembers.Add(CoordinatorMember.Bind(match.OrganizationId, lineUserId, code.Role, now));
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new CoordinatorBindResult(CoordinatorBindOutcome.Bound, storeName, code.Role);
            }
            catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, MemberIndex))
            {
                // Bound to this shop a moment ago by another delivery (another code): this code stays unused.
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        dbContext.ChangeTracker.Clear();
        return await RoleOfAsync(dbContext, lineUserId, cancellationToken) is { } role
            ? new CoordinatorBindResult(CoordinatorBindOutcome.AlreadyBound, storeName, role)
            : new CoordinatorBindResult(CoordinatorBindOutcome.Invalid);
    }

    /// <summary>
    /// Issues a new code for <paramref name="organizationId"/> that binds as <paramref name="role"/>, valid
    /// for <paramref name="validity"/>. With <paramref name="issuedByLineUserId"/> (a 老闆 asking in LINE)
    /// the issuer must be a 老闆 of that organization — checked again here, under its filter — else
    /// <see langword="null"/>; without one it is the operator's command. <see langword="null"/> also when
    /// the organization does not exist.
    /// </summary>
    public async Task<IssuedBindingCode?> IssueAsync(
        Guid organizationId, CoordinatorRole role, TimeSpan validity, string? issuedByLineUserId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        if (await StoreNameAsync(dbContext, organizationId, cancellationToken) is not { } storeName)
        {
            return null;
        }

        if (issuedByLineUserId is not null && await RoleOfAsync(dbContext, issuedByLineUserId, cancellationToken) != CoordinatorRole.Owner)
        {
            return null;
        }

        for (var attempt = 1; ; attempt++)
        {
            var code = BindingCodes.Generate();
            var row = CoordinatorBindingCode.Issue(
                organizationId, BindingCodes.Hash(code), role, _clock.GetUtcNow(), validity, issuedByLineUserId);
            dbContext.CoordinatorBindingCodes.Add(row);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return new IssuedBindingCode(code, storeName, role, row.ExpiresAt);
            }
            catch (DbUpdateException exception) when (attempt < IssueAttempts && DatabaseErrors.IsUniqueViolation(exception, CodeHashIndex))
            {
                // Another code has that hash (about 1 in 10^12): draw again.
                dbContext.Entry(row).State = EntityState.Detached;
            }
        }
    }

    /// <summary>The time zone a shop's times are shown in: its settings' (#373), else
    /// <see cref="ShopSettings.DefaultTimeZoneId"/> — also when the stored id no longer resolves.</summary>
    public async Task<TimeZoneInfo> TimeZoneOfAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var id = await dbContext.CoordinatorShopSettings
            .AsNoTracking()
            .Select(settings => settings.TimeZoneId)
            .SingleOrDefaultAsync(cancellationToken);
        return id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : TimeZoneInfo.FindSystemTimeZoneById(ShopSettings.DefaultTimeZoneId);
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));

    /// <summary>The shop's store name — its settings' (#373), else the organization's name;
    /// <see langword="null"/> when the organization does not exist. <paramref name="dbContext"/> acts for
    /// <paramref name="organizationId"/>, so its settings are the only ones it sees.</summary>
    private static Task<string?> StoreNameAsync(AppDbContext dbContext, Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => dbContext.CoordinatorShopSettings.Select(settings => settings.StoreName).FirstOrDefault() ?? organization.Name)
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task<CoordinatorRole?> RoleOfAsync(AppDbContext dbContext, string lineUserId, CancellationToken cancellationToken) =>
        await dbContext.CoordinatorMembers
            .AsNoTracking()
            .Where(member => member.LineUserId == lineUserId)
            .Select(member => (CoordinatorRole?)member.Role)
            .SingleOrDefaultAsync(cancellationToken);
}
