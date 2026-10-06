namespace SmartAgri.Domain.Organizations;

/// <summary>What an organization settings change (<see cref="Organization.ChangeChatModel"/>) did.</summary>
public enum OrganizationSettingsChange
{
    /// <summary>The value changed and <see cref="Organization.SettingsRevision"/> went up by 1:
    /// the caller writes an <see cref="OrganizationActivity"/>.</summary>
    Changed,

    /// <summary>The value was already the requested one: nothing changed, nothing to record.</summary>
    Unchanged,

    /// <summary>The caller read an older <see cref="Organization.SettingsRevision"/>: nothing
    /// changed; the API answers <c>409</c>.</summary>
    RevisionConflict,
}
