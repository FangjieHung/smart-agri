using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Ordering;

/// <summary>
/// One version of a shop's menu (table <c>OrderingMenus</c>, online-ordering design D6, Slice O1, #397): the whole
/// <see cref="MenuDocument"/> as imported. An import whose menu differs from the latest adds the next version; the current
/// menu is the highest version. Versions are never changed or deleted, so an order can say which menu it was priced on
/// and a cart made on an older version can be told the prices changed.
/// </summary>
public sealed class OrderingMenu : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private OrderingMenu()
    {
        Document = new MenuDocument([]);
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>1 for a shop's first menu, then one more each time it changes; unique per organization.</summary>
    public int Version { get; private set; }

    public MenuDocument Document { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static OrderingMenu Create(Guid organizationId, int version, MenuDocument document, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A menu always belongs to an organization.", nameof(organizationId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(document);
        return new OrderingMenu
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Version = version,
            Document = document,
            CreatedAt = now,
        };
    }
}
