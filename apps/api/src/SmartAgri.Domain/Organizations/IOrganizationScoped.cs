namespace SmartAgri.Domain.Organizations;

/// <summary>
/// Marks an entity whose rows belong to exactly one <see cref="Organization"/>
/// (deployment-and-tenancy ADR: every piece of business data carries
/// <c>OrganizationId</c>). The persistence layer applies the named query filter
/// <c>"Organization"</c> to every entity implementing this, and refuses to write a row
/// whose <see cref="OrganizationId"/> is not the current organization. A model test
/// fails if any mapped entity outside a short whitelist does not implement it.
/// </summary>
public interface IOrganizationScoped
{
    Guid OrganizationId { get; }
}
