using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// The four fixed 情境 a shop's owner can ask the 統籌助理 for (store-coordinator design §3 Q6, #373).
/// Each shop says in its <see cref="ShopSettings"/> which ones it offers and what each does. The wire
/// names are the keys of the settings file's <c>scenarios</c> object.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShopScenario>))]
public enum ShopScenario
{
    /// <summary>臨時店休: the shop is closed for a whole (local) day.</summary>
    [JsonStringEnumMemberName("temporary-closure")]
    TemporaryClosure,

    /// <summary>提早打烊: the shop closes earlier than usual on a day.</summary>
    [JsonStringEnumMemberName("close-early")]
    CloseEarly,

    /// <summary>延後開店: the shop opens later than usual on a day.</summary>
    [JsonStringEnumMemberName("open-late")]
    OpenLate,

    /// <summary>恢復營業: undoes an earlier command. It has no actions of its own — they follow from the
    /// command it undoes (Slice 4); its announcement is the optional correction broadcast (Q21).</summary>
    [JsonStringEnumMemberName("reopen")]
    Reopen,
}

/// <summary>What a scenario's action does (store-coordinator design §3 Q14, Q18, #373).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShopActionKind>))]
public enum ShopActionKind
{
    /// <summary>LINE 群發 of the announcement from the shop's own official account (Slice 5).</summary>
    [JsonStringEnumMemberName("line-broadcast")]
    LineBroadcast,

    /// <summary>Google 商家 special hours for the day (Slice 7).</summary>
    [JsonStringEnumMemberName("google-special-hours")]
    GoogleSpecialHours,

    /// <summary>A Facebook page post of the announcement and its image (Slice 8).</summary>
    [JsonStringEnumMemberName("facebook-post")]
    FacebookPost,

    /// <summary>Pausing and resuming an ordering system (Xurf, foodpanda, …). No such system has an
    /// API we may use (Q15, Q26), so it is always a reminder.</summary>
    [JsonStringEnumMemberName("ordering")]
    Ordering,
}

/// <summary>How an action is carried out (store-coordinator design §3 Q18, #373).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShopActionMode>))]
public enum ShopActionMode
{
    /// <summary>自動: the system does it — when it can at run time (Slice 4 decides: an action whose
    /// integration is missing or not authorized falls back to its manual steps).</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>提醒: the owner is sent the manual steps and a 〔已完成〕 button.</summary>
    [JsonStringEnumMemberName("reminder")]
    Reminder,
}
