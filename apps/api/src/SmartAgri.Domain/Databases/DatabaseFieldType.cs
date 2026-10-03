using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Databases;

/// <summary>
/// The six field types of a database form (first version: no conditional questions or
/// formulas). The serialized names must equal the frontend's <c>DatabaseFieldType</c> union in
/// <c>apps/admin/src/app/core/domain/database.model.ts</c>; <c>SmartAgri.Domain.Tests</c>
/// compares them against that file. Stored inside the form version's <c>jsonb</c> by the same
/// names.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseFieldType>))]
public enum DatabaseFieldType
{
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>A number, optionally with a <see cref="DatabaseFormField.Unit"/> (e.g. 元).</summary>
    [JsonStringEnumMemberName("number")]
    Number,

    [JsonStringEnumMemberName("date")]
    Date,

    /// <summary>One of <see cref="DatabaseFormField.Options"/>.</summary>
    [JsonStringEnumMemberName("single-choice")]
    SingleChoice,

    /// <summary>Any of <see cref="DatabaseFormField.Options"/>.</summary>
    [JsonStringEnumMemberName("multiple-choice")]
    MultipleChoice,

    /// <summary>An integer within <see cref="DatabaseFormField.Scale"/>.</summary>
    [JsonStringEnumMemberName("scale")]
    Scale,
}
