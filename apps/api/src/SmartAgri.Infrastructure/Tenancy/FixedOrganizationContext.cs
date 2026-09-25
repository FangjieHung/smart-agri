using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Tenancy;

/// <summary>
/// An <see cref="IOrganizationContext"/> with a value fixed at construction. For code
/// that runs outside an HTTP request (design-time tooling, tests, and later seeding or
/// background jobs that act for one explicit organization). Never register this as the
/// API's request-scoped context: requests must take the organization from the
/// authenticated principal.
/// </summary>
public sealed class FixedOrganizationContext : IOrganizationContext
{
    /// <summary>"No organization": every organization-filtered query returns nothing and
    /// every organization-scoped write is refused.</summary>
    public static readonly FixedOrganizationContext None = new(null);

    public FixedOrganizationContext(Guid? organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Use null (no organization), not Guid.Empty.", nameof(organizationId));
        }

        OrganizationId = organizationId;
    }

    public Guid? OrganizationId { get; }
}
