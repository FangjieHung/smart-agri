using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Organizations;

/// <summary>How much of its monthly token limit an organization has used (M5a plan §3 F).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TokenUsageState>))]
public enum TokenUsageState
{
    /// <summary>Under 80% of the limit.</summary>
    [JsonStringEnumMemberName("normal")]
    Normal,

    /// <summary>At least 80% and under 100%: the admin shows a heads-up.</summary>
    [JsonStringEnumMemberName("near")]
    Near,

    /// <summary>The limit is reached: website replies are suspended (<c>suspended-quota</c>).</summary>
    [JsonStringEnumMemberName("exceeded")]
    Exceeded,
}
