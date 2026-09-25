namespace SmartAgri.Domain.Accounts;

/// <summary>
/// The order the frontend lists permissions in (<c>ACCOUNT_PERMISSIONS</c> in
/// <c>team.model.ts</c>), used whenever the API returns or stores a permission list. It is
/// not the enum's declaration order, which follows the <c>AccountPermission</c> union in
/// <c>account.model.ts</c> and swaps <c>use-shared-assistants</c> and
/// <c>submit-authorized-forms</c>.
/// </summary>
public static class AccountPermissionOrder
{
    public static IReadOnlyList<AccountPermission> All { get; } =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
        AccountPermission.SubmitAuthorizedForms,
        AccountPermission.UseSharedAssistants,
        AccountPermission.ReadOwnTracking,
    ];

    /// <summary>Distinct, in <see cref="All"/> order.</summary>
    public static IReadOnlyList<AccountPermission> Sort(IReadOnlyCollection<AccountPermission> permissions) =>
        [.. All.Where(permissions.Contains)];
}
