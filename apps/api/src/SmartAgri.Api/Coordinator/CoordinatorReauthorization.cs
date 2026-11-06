using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// <see cref="ICoordinatorReauthorization"/> (store-coordinator design §3 Q18, §5 Slice 6, #377): FB or Google refused
/// a shop's credential. The connection is revoked (compare-and-set, as for LINE), then — only by the call that marks
/// <see cref="CoordinatorConnection.ReauthorizationRequestedAt"/> on the revoked row, in the same transaction as the new
/// <see cref="CoordinatorAuthorizationPurpose.Reauthorize"/> link (valid <see cref="CoordinatorAuthorizationLink.DefaultValidity"/>)
/// — the 統籌助理 pushes the link to each 老闆 member of the shop (店長 are not asked: only the owner can authorize).
/// So a revocation sends at most one link, however many actions fail; authorizing again clears the mark.
/// </summary>
/// <remarks>
/// Nothing is sent when the shop has no connection to the service, or the service's flow is not available
/// (<see cref="CoordinatorAuthorizationLinks.IsAvailable"/>; the connection is still revoked). A push LINE refuses is
/// logged and dropped like the 統籌助理's other pushes. The link is in the pushed text only — never logged.
/// </remarks>
public sealed class CoordinatorReauthorization : ICoordinatorReauthorization
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly CoordinatorConnections _connections;
    private readonly CoordinatorAuthorizationLinks _links;
    private readonly CoordinatorBindings _bindings;
    private readonly CoordinatorReplySender _sender;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorReauthorization> _logger;

    public CoordinatorReauthorization(
        DbContextOptions<AppDbContext> options,
        CoordinatorConnections connections,
        CoordinatorAuthorizationLinks links,
        CoordinatorBindings bindings,
        CoordinatorReplySender sender,
        TimeProvider clock,
        ILogger<CoordinatorReauthorization> logger)
    {
        _options = options;
        _connections = connections;
        _links = links;
        _bindings = bindings;
        _sender = sender;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>「{店名} 的 {FB 粉專／Google 商家} 授權已失效，相關動作先改為提醒。請點此重新授權：{link}」.</summary>
    public static string Message(string storeName, CoordinatorService service, string link) =>
        $"{storeName} 的 {CoordinatorAuthorizations.Label(service)} 授權已失效，相關動作先改為提醒。請點此重新授權：{link}";

    public async Task<bool> RequestAsync(Guid organizationId, CoordinatorService service, CancellationToken cancellationToken)
    {
        if (!CoordinatorAuthorizationLink.IsLinkService(service))
        {
            throw new ArgumentOutOfRangeException(nameof(service), service, "Only FB and Google are authorized again through links.");
        }

        var wire = WireNames<CoordinatorService>.ToWire(service);
        if (await _connections.FindAsync(organizationId, service, cancellationToken) is not { } connection)
        {
            _logger.LogWarning("Organization {OrganizationId} has no {Service} connection to authorize again.", organizationId, wire);
            return false;
        }

        if (connection.Status == CoordinatorConnectionStatus.Active && await _connections.RevokeAsync(connection, cancellationToken))
        {
            _logger.LogWarning("The {Service} connection of organization {OrganizationId} was refused by the service; it is revoked.", wire, organizationId);
        }

        if (!_links.IsAvailable(service))
        {
            _logger.LogWarning(
                "Organization {OrganizationId}'s {Service} authorization cannot be asked for again: the provider or PublicChannels:PublicBaseUrl is not configured.",
                organizationId, wire);
            return false;
        }

        var (token, link) = _links.NewLink(organizationId, service, CoordinatorAuthorizationPurpose.Reauthorize, CoordinatorAuthorizationLink.DefaultValidity);
        List<string> owners;
        await using (var dbContext = new AppDbContext(_options, new FixedOrganizationContext(organizationId)))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = _clock.GetUtcNow();
            var marked = await dbContext.CoordinatorConnections
                .Where(row => row.Id == connection.Id
                    && row.Status == CoordinatorConnectionStatus.Revoked
                    && row.ReauthorizationRequestedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReauthorizationRequestedAt, now), cancellationToken);
            if (marked != 1)
            {
                // Asked for already for this revocation, or authorized again meanwhile.
                return false;
            }

            dbContext.CoordinatorAuthorizationLinks.Add(link);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            owners = await dbContext.CoordinatorMembers.AsNoTracking()
                .Where(member => member.Role == CoordinatorRole.Owner)
                .OrderBy(member => member.BoundAt)
                .Select(member => member.LineUserId)
                .ToListAsync(cancellationToken);
        }

        var storeName = await _bindings.StoreNameAsync(organizationId, cancellationToken) ?? string.Empty;
        var message = LineMessages.Text(Message(storeName, service, _links.ConnectUrl(token)));
        var pushed = 0;
        foreach (var owner in owners)
        {
            if (await _sender.PushAsync(owner, [message], Guid.CreateVersion7(), "re-authorization link", cancellationToken))
            {
                pushed++;
            }
        }

        _logger.LogWarning(
            "Organization {OrganizationId} was asked to authorize {Service} again: link {LinkId} pushed to {Pushed} of {Owners} owners.",
            organizationId, wire, link.Id, pushed, owners.Count);
        return true;
    }
}
