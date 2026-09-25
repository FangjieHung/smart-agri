using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Accounts;

/// <summary>
/// An account's role. The serialized names must equal the frontend's
/// <c>AccountRole</c> union in <c>apps/admin/src/app/core/domain/account.model.ts</c>
/// exactly; <c>SmartAgri.Domain.Tests</c> compares them against that file.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccountRole>))]
public enum AccountRole
{
    [JsonStringEnumMemberName("smb-admin")]
    SmbAdmin,

    [JsonStringEnumMemberName("internal-employee")]
    InternalEmployee,

    [JsonStringEnumMemberName("external-customer")]
    ExternalCustomer,
}
