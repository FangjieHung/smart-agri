namespace SmartAgri.Infrastructure.Tenancy;

/// <summary>
/// Thrown by <see cref="OrganizationSaveChangesInterceptor"/> before anything is sent to
/// the database when a save would add, change or delete an organization-scoped row that
/// does not belong to the current organization, or when there is no current organization.
/// Indicates a programming error, not bad user input.
/// </summary>
public sealed class CrossOrganizationWriteException : InvalidOperationException
{
    public CrossOrganizationWriteException(string message)
        : base(message)
    {
    }
}
