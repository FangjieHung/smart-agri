using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// Who a knowledge base is shared with. The serialized names must equal the frontend's
/// <c>KnowledgeSharingScope</c> union in
/// <c>apps/admin/src/app/core/domain/knowledge-base.model.ts</c> exactly;
/// <c>SmartAgri.Domain.Tests</c> compares them against that file. The database stores the
/// same names.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeSharingScope>))]
public enum KnowledgeSharingScope
{
    /// <summary>Only the owner (只有我).</summary>
    [JsonStringEnumMemberName("private")]
    Private,

    /// <summary>The accounts listed in <see cref="KnowledgeBaseShare"/> rows (指定帳號／團隊);
    /// at least one is required.</summary>
    [JsonStringEnumMemberName("specific-accounts")]
    SpecificAccounts,

    /// <summary>Every account of the organization (公開分享). The only scope in which
    /// <see cref="KnowledgeBase.AllowOriginalDownload"/> may be true.</summary>
    [JsonStringEnumMemberName("public")]
    Public,
}
