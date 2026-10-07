using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// Who an assistant is meant for (服務對象), chosen in the wizard and editable in its settings
/// (issue #224). The serialized names must equal the frontend's <c>AssistantAudience</c> union in
/// <c>apps/admin/src/app/core/domain/assistant.model.ts</c> exactly; <c>SmartAgri.Domain.Tests</c>
/// compares them against that file.
/// </summary>
/// <remarks>
/// Stored and shown only: it is <b>not</b> an access rule. Who may use an assistant stays
/// <c>AssistantUseAccess.UsableBy</c> (ownership, <c>AssistantShare</c>, <c>use-shared-assistants</c>),
/// and it is not a publishing condition either — the website and LINE gates look at acceptance,
/// domains/connection, the assistant's status, knowledge-base ownership and <c>PublicBaseUrl</c>
/// (M5a plan §3 C).
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantAudience>))]
public enum AssistantAudience
{
    [JsonStringEnumMemberName("account-members")]
    AccountMembers,

    [JsonStringEnumMemberName("authorized-external-customers")]
    AuthorizedExternalCustomers,

    [JsonStringEnumMemberName("members-and-external-customers")]
    MembersAndExternalCustomers,
}
