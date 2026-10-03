using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Databases;

/// <summary>
/// The template a database was created from (「你要收集什麼？」). The serialized names must equal
/// the frontend's <c>DatabaseTemplateId</c> union in
/// <c>apps/admin/src/app/core/domain/database.model.ts</c>; the database stores the same
/// names. The templates' content lives in <c>SmartAgri.Application.Databases.DatabaseTemplates</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseTemplateId>))]
public enum DatabaseTemplateId
{
    /// <summary>客戶基本資料.</summary>
    [JsonStringEnumMemberName("template-customer-profile")]
    CustomerProfile,

    /// <summary>定期回報.</summary>
    [JsonStringEnumMemberName("template-periodic-report")]
    PeriodicReport,

    /// <summary>滿意度調查.</summary>
    [JsonStringEnumMemberName("template-satisfaction")]
    Satisfaction,

    /// <summary>症狀或進度追蹤.</summary>
    [JsonStringEnumMemberName("template-progress")]
    Progress,

    /// <summary>空白模板: one text field to start from.</summary>
    [JsonStringEnumMemberName("template-blank")]
    Blank,
}
