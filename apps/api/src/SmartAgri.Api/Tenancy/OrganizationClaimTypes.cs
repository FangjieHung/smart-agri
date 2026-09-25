namespace SmartAgri.Api.Tenancy;

/// <summary>Claim types carrying organization membership (M1 plan §3: access tokens
/// carry <c>sub</c>, <c>org_id</c> and <c>role</c>).</summary>
public static class OrganizationClaimTypes
{
    public const string OrganizationId = "org_id";
}
