using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// What a <see cref="CoordinatorMember"/> may do for their shop (store-coordinator design §3 Q5/Q19,
/// #372). The column stores the wire name, so adding a role needs no data migration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorRole>))]
public enum CoordinatorRole
{
    /// <summary>老闆: gives commands and is the only one who can issue a 店長 binding code.</summary>
    [JsonStringEnumMemberName("owner")]
    Owner,

    /// <summary>店長: gives commands.</summary>
    [JsonStringEnumMemberName("manager")]
    Manager,
}

/// <summary>How a <see cref="CoordinatorRole"/> is named to LINE users.</summary>
public static class CoordinatorRoleNames
{
    /// <summary>「老闆」 or 「店長」.</summary>
    public static string Display(CoordinatorRole role) => role switch
    {
        CoordinatorRole.Owner => "老闆",
        CoordinatorRole.Manager => "店長",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a coordinator role."),
    };
}
