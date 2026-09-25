namespace SmartAgri.Domain.Organizations;

/// <summary>
/// The organization the current operation acts on behalf of. In the API it comes from
/// the authenticated <c>org_id</c> claim. When there is none (anonymous request,
/// background job without an explicit organization, design-time tooling),
/// <see cref="OrganizationId"/> is <see langword="null"/> — "no organization" — and every
/// organization-filtered query returns an empty result, never every organization's rows.
/// </summary>
public interface IOrganizationContext
{
    Guid? OrganizationId { get; }
}
