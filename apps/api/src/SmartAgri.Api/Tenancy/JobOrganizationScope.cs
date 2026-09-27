using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tenancy;

/// <summary>
/// Scoped. Lets a dependency-injection scope that <c>JobRunner</c> creates for one
/// background job act for that job's organization: once <see cref="Enter"/> has been
/// called, the scope's <see cref="IOrganizationContext"/> is a
/// <see cref="FixedOrganizationContext"/> for it (see
/// <see cref="TenancyServiceCollectionExtensions.AddOrganizationTenancy"/>), so the
/// organization filter and write guard apply to the job's handler as they do to a
/// request. Request scopes never enter it and keep the claims-based context.
/// </summary>
internal sealed class JobOrganizationScope
{
    private FixedOrganizationContext? _organizationContext;

    /// <summary>The job's organization once entered; otherwise <see langword="null"/>.</summary>
    public IOrganizationContext? OrganizationContext => _organizationContext;

    /// <summary>
    /// Makes <paramref name="organizationId"/> this scope's organization. Must be called
    /// before anything in the scope resolves <see cref="IOrganizationContext"/> (the runner
    /// checks the result), and only once per scope.
    /// </summary>
    public void Enter(Guid organizationId)
    {
        if (_organizationContext is not null)
        {
            throw new InvalidOperationException("This scope already acts for a job's organization.");
        }

        _organizationContext = new FixedOrganizationContext(organizationId);
    }
}
