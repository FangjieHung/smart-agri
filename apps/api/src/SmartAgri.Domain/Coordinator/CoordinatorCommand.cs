using System.Text.Json.Serialization;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>Where a <see cref="CoordinatorCommand"/> is (store-coordinator design §3 Q4/Q23, #374).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorCommandStatus>))]
public enum CoordinatorCommandStatus
{
    /// <summary>The 統籌助理 asked for the date or the time and waits for the answer.</summary>
    [JsonStringEnumMemberName("awaiting-details")]
    AwaitingDetails,

    /// <summary>The preview card was sent; 〔確認〕 starts it.</summary>
    [JsonStringEnumMemberName("previewed")]
    Previewed,

    /// <summary>Someone pressed 〔確認〕 in time (Slice 4 carries it out).</summary>
    [JsonStringEnumMemberName("confirmed")]
    Confirmed,

    /// <summary>〔取消〕, or the conversation moved on without it.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

/// <summary>
/// 指令 (table <c>CoordinatorCommands</c>, store-coordinator design §4, decision F, §5 Slice 3, #374): one
/// 「預覽 → 確認 → 執行」 of one shop — the record that Slice 4 adds its per-action results to (「每次指令一筆」).
/// Who asked (the 統籌助理's LINE user id), the scenario, the shop's local date and, for 提早打烊／延後開店,
/// the time; the announcement as it will be sent (rendered from the shop's template, editable with
/// 「改成：」); and, once confirmed, who confirmed it and when.
/// </summary>
/// <remarks>
/// <para>
/// Nothing else a person wrote is stored — not their message, not a separate reason (a reason lives only
/// inside <see cref="AnnouncementText"/>). A card is good for <see cref="CardValidity"/> after it was last
/// sent (<see cref="ExpiresAt"/>); expiry is computed from it, never a status. <see cref="Version"/> grows
/// with every edit of the card, so a press on an older card is refused.
/// </para>
/// <para>
/// Changes of status are compare-and-set updates on (<see cref="Status"/>, <see cref="Version"/>,
/// <see cref="ExpiresAt"/>) in the persistence layer, so two presses of 〔確認〕 confirm it once. A
/// <see cref="ShopScenario.Reopen"/> command undoes <see cref="ReopensCommandId"/>; at most one confirmed
/// reopen per command (a unique index).
/// </para>
/// </remarks>
public sealed class CoordinatorCommand : IOrganizationScoped
{
    /// <summary>How long a preview card (or a question) can be answered after it was sent (Q23).</summary>
    public static readonly TimeSpan CardValidity = TimeSpan.FromMinutes(30);

    /// <summary>The latest date a command can be for: today plus this many days (Q20).</summary>
    public const int MaxDaysAhead = 90;

    /// <summary>Longest announcement (characters), the template's own limit.</summary>
    public const int AnnouncementMaxLength = ShopAnnouncements.TemplateMaxLength;

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorCommand()
    {
        RequestedByLineUserId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The member who gave the command, as the 統籌助理 LINE account sees them.</summary>
    public string RequestedByLineUserId { get; private set; }

    public ShopScenario Scenario { get; private set; }

    /// <summary>The shop's local date the command is about; <see langword="null"/> only while
    /// <see cref="CoordinatorCommandStatus.AwaitingDetails"/> asks for it.</summary>
    public DateOnly? Date { get; private set; }

    /// <summary>The new closing (提早打烊) or opening (延後開店) time; <see langword="null"/> for the other
    /// scenarios, and while <see cref="CoordinatorCommandStatus.AwaitingDetails"/> asks for it.</summary>
    public TimeOnly? Time { get; private set; }

    /// <summary>The announcement as it will be sent; <see langword="null"/> while awaiting details, and for
    /// a reopen of a shop without a correction announcement.</summary>
    public string? AnnouncementText { get; private set; }

    /// <summary>1 at first; one more with every edit of the card.</summary>
    public int Version { get; private set; }

    public CoordinatorCommandStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary><see cref="CardValidity"/> after the latest card or question was sent.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>The member who pressed 〔確認〕 (an owner or manager of the shop, not necessarily the one who
    /// asked); set exactly when <see cref="ConfirmedAt"/> is.</summary>
    public string? ConfirmedByLineUserId { get; private set; }

    /// <summary>The confirmed command a <see cref="ShopScenario.Reopen"/> undoes; reopen only.</summary>
    public Guid? ReopensCommandId { get; private set; }

    /// <summary>Reopen only: also broadcast the correction announcement (Q21; off unless toggled).</summary>
    public bool CorrectionBroadcast { get; private set; }

    /// <summary>A new command waiting for its date or time (<paramref name="date"/> or
    /// <paramref name="time"/> missing), asked at <paramref name="now"/>.</summary>
    public static CoordinatorCommand AwaitDetails(
        Guid organizationId, string requestedByLineUserId, ShopScenario scenario, DateOnly? date, TimeOnly? time, DateTimeOffset now)
    {
        if (scenario == ShopScenario.Reopen)
        {
            throw new ArgumentException("A reopen never waits for details: it is about a command that has them.", nameof(scenario));
        }

        var command = New(organizationId, requestedByLineUserId, scenario, now);
        command.Status = CoordinatorCommandStatus.AwaitingDetails;
        command.Date = date;
        command.Time = NeedsTime(scenario) ? time : null;
        return command;
    }

    /// <summary>A new command whose preview card is sent at <paramref name="now"/>.</summary>
    public static CoordinatorCommand Preview(
        Guid organizationId,
        string requestedByLineUserId,
        ShopScenario scenario,
        DateOnly date,
        TimeOnly? time,
        string? announcementText,
        DateTimeOffset now,
        Guid? reopensCommandId = null)
    {
        if ((scenario == ShopScenario.Reopen) != (reopensCommandId is not null))
        {
            throw new ArgumentException("Exactly a reopen names the command it undoes.", nameof(reopensCommandId));
        }

        if (NeedsTime(scenario) != (time is not null))
        {
            throw new ArgumentException("提早打烊 and 延後開店 have a time; the other scenarios do not.", nameof(time));
        }

        var command = New(organizationId, requestedByLineUserId, scenario, now);
        command.Status = CoordinatorCommandStatus.Previewed;
        command.Date = date;
        command.Time = time;
        command.AnnouncementText = CheckAnnouncement(announcementText, required: scenario != ShopScenario.Reopen);
        command.ReopensCommandId = reopensCommandId;
        return command;
    }

    /// <summary>Whether <paramref name="scenario"/> is about a time of day (提早打烊, 延後開店).</summary>
    public static bool NeedsTime(ShopScenario scenario) => scenario is ShopScenario.CloseEarly or ShopScenario.OpenLate;

    /// <summary>Whether <paramref name="text"/> can be an announcement: not blank, at most
    /// <see cref="AnnouncementMaxLength"/> characters.</summary>
    public static bool IsValidAnnouncement(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= AnnouncementMaxLength;

    /// <summary>Its card or question can no longer be answered at <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    private static CoordinatorCommand New(Guid organizationId, string requestedByLineUserId, ShopScenario scenario, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A command always belongs to an organization.", nameof(organizationId));
        }

        if (!CoordinatorMember.IsValidLineUserId(requestedByLineUserId))
        {
            throw new ArgumentException("Not a LINE user id.", nameof(requestedByLineUserId));
        }

        if (!Enum.IsDefined(scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Not a scenario.");
        }

        return new CoordinatorCommand
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            RequestedByLineUserId = requestedByLineUserId,
            Scenario = scenario,
            Version = 1,
            CreatedAt = now,
            ExpiresAt = now + CardValidity,
        };
    }

    private static string? CheckAnnouncement(string? text, bool required)
    {
        if (text is null && !required)
        {
            return null;
        }

        return IsValidAnnouncement(text)
            ? text
            : throw new ArgumentException($"An announcement is 1-{AnnouncementMaxLength} characters, not blank.", nameof(text));
    }
}

/// <summary>How a <see cref="ShopScenario"/> is named to LINE users.</summary>
public static class ShopScenarioNames
{
    /// <summary>「臨時店休」, 「提早打烊」, 「延後開店」 or 「恢復營業」.</summary>
    public static string Display(ShopScenario scenario) => scenario switch
    {
        ShopScenario.TemporaryClosure => "臨時店休",
        ShopScenario.CloseEarly => "提早打烊",
        ShopScenario.OpenLate => "延後開店",
        ShopScenario.Reopen => "恢復營業",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Not a scenario."),
    };
}
