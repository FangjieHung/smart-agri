using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Accounts;

/// <summary>
/// A permission granted to an account. The serialized names must equal the frontend's
/// <c>AccountPermission</c> union in <c>apps/admin/src/app/core/domain/account.model.ts</c>
/// exactly; <c>SmartAgri.Domain.Tests</c> compares them against that file. The database
/// stores the same names (not the numeric values), so reordering members is safe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccountPermission>))]
public enum AccountPermission
{
    [JsonStringEnumMemberName("manage-assistants")]
    ManageAssistants,

    [JsonStringEnumMemberName("manage-data-sources")]
    ManageDataSources,

    [JsonStringEnumMemberName("manage-publishing")]
    ManagePublishing,

    [JsonStringEnumMemberName("read-consented-submissions")]
    ReadConsentedSubmissions,

    [JsonStringEnumMemberName("use-shared-assistants")]
    UseSharedAssistants,

    [JsonStringEnumMemberName("submit-authorized-forms")]
    SubmitAuthorizedForms,

    [JsonStringEnumMemberName("read-own-tracking")]
    ReadOwnTracking,
}
