using System.Text.Json.Serialization;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>Which half of an action a <see cref="CoordinatorCommandAction"/> is (store-coordinator design §5 Slice 4, #375).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorActionStep>))]
public enum CoordinatorActionStep
{
    /// <summary>LINE 群發, Google 特殊營業時間 or FB 貼文 of a command (or a reopen's correction broadcast).</summary>
    [JsonStringEnumMemberName("apply")]
    Apply,

    /// <summary>A reopen taking back an applied Google 特殊營業時間 or FB 貼文 (Q21).</summary>
    [JsonStringEnumMemberName("undo")]
    Undo,

    /// <summary>An ordering system stops taking orders (before the shop's opening on the day, Q7, Q20).</summary>
    [JsonStringEnumMemberName("pause")]
    Pause,

    /// <summary>An ordering system takes orders again (before the next opening, Q7).</summary>
    [JsonStringEnumMemberName("resume")]
    Resume,
}

/// <summary>Where a <see cref="CoordinatorCommandAction"/> is (store-coordinator design §3 Q8, Q18, Q25, #375).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorActionStatus>))]
public enum CoordinatorActionStatus
{
    /// <summary>Waiting for its background job (at <see cref="CoordinatorCommandAction.DueAt"/>).</summary>
    [JsonStringEnumMemberName("scheduled")]
    Scheduled,

    /// <summary>Done automatically.</summary>
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    /// <summary>Tried automatically and failed (<see cref="CoordinatorCommandAction.FailureCode"/>); 〔重試〕 tries it again.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The owner was given the manual steps and 〔已完成〕.</summary>
    [JsonStringEnumMemberName("awaiting-manual")]
    AwaitingManual,

    /// <summary>A member pressed 〔已完成〕 (<see cref="CoordinatorCommandAction.CompletedByLineUserId"/>).</summary>
    [JsonStringEnumMemberName("done-manually")]
    DoneManually,

    /// <summary>A reopen dropped it before it happened (or after it failed).</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

/// <summary>
/// One action of a confirmed <see cref="CoordinatorCommand"/> and how it went (table <c>CoordinatorCommandActions</c>,
/// store-coordinator design decision F, §5 Slice 4, #375): with the command, 「每個動作一列結果」. Written when the
/// command is confirmed, from the shop's settings at that moment — kind, name, step, the manual steps
/// (<see cref="StepsText"/>) and the announcement's image — so later changes of the settings do not change a
/// command already running. Its mode is the effective one (<see cref="ShopActionMode.Auto"/> only when an
/// integration can run it); <see cref="DueAt"/> is when its background job runs.
/// </summary>
/// <remarks>
/// <para>
/// Nothing a person wrote is stored here: <see cref="StepsText"/> is the operator's text (or a fixed undo text with
/// the date), <see cref="FailureCode"/> a short code of ours, <see cref="ExternalRef"/> an id an integration returned.
/// The announcement stays on the command.
/// </para>
/// <para>
/// Changes of status are compare-and-set updates in the persistence layer: a job does nothing unless its row is
/// still <see cref="CoordinatorActionStatus.Scheduled"/> (so cancelling is setting it
/// <see cref="CoordinatorActionStatus.Cancelled"/>); 〔已完成〕 is <c>awaiting-manual → done-manually</c>; 〔重試〕 is
/// <c>failed → scheduled</c>. <see cref="ReportedAt"/> marks an outcome the member has been told about.
/// </para>
/// </remarks>
public sealed class CoordinatorCommandAction : IOrganizationScoped
{
    /// <summary>Longest <see cref="StepsText"/>: the settings' own limit for steps.</summary>
    public const int StepsTextMaxLength = ShopSettingsJson.StepsMaxLength;

    /// <summary>Longest <see cref="FailureCode"/>.</summary>
    public const int FailureCodeMaxLength = 32;

    /// <summary>Longest <see cref="ExternalRef"/>.</summary>
    public const int ExternalRefMaxLength = 256;

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorCommandAction()
    {
        StepsText = string.Empty;
        RecipientLineUserId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The confirmed command this is an action of.</summary>
    public Guid CommandId { get; private set; }

    /// <summary>0, 1, … in the order the command lists its actions (unique per command).</summary>
    public int Position { get; private set; }

    public ShopActionKind Kind { get; private set; }

    /// <summary>The ordering system's name (e.g. Xurf); <see cref="ShopActionKind.Ordering"/> only.</summary>
    public string? Name { get; private set; }

    public CoordinatorActionStep Step { get; private set; }

    /// <summary>The effective mode: <see cref="ShopActionMode.Auto"/> when an integration runs it; becomes
    /// <see cref="ShopActionMode.Reminder"/> when the integration turns out to be unavailable when it runs (Q18).</summary>
    public ShopActionMode Mode { get; private set; }

    public CoordinatorActionStatus Status { get; private set; }

    /// <summary>When its job runs: now for LINE 群發, Google and FB; before the opening for ordering systems.</summary>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>The manual steps a reminder shows (snapshot of the settings, or the fixed undo text).</summary>
    public string StepsText { get; private set; }

    /// <summary>The announcement's image (LINE 群發 and FB 貼文 only), snapshot of the settings.</summary>
    public string? ImageUrl { get; private set; }

    /// <summary>Who is reminded and told the results: the member who confirmed the command.</summary>
    public string RecipientLineUserId { get; private set; }

    /// <summary>The row of the undone command this undoes (<see cref="CoordinatorActionStep.Undo"/>, and a reopen's
    /// <see cref="CoordinatorActionStep.Resume"/> of a pause that happened).</summary>
    public Guid? UndoesActionId { get; private set; }

    /// <summary>Why it failed, a short code of ours (<c>error</c>, <c>unauthorized</c>, …).</summary>
    public string? FailureCode { get; private set; }

    /// <summary>What the integration returned to undo it by (e.g. the FB post id), Slices 7/8.</summary>
    public string? ExternalRef { get; private set; }

    /// <summary>How many times its job ran it (an automatic try or a pushed reminder).</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it succeeded, failed or was done by hand.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Who pressed 〔已完成〕; set exactly for <see cref="CoordinatorActionStatus.DoneManually"/>.</summary>
    public string? CompletedByLineUserId { get; private set; }

    /// <summary>When the member was told its outcome (the confirmation report, a pushed reminder or a follow-up);
    /// <see langword="null"/> while an automatic try's result is still to be reported.</summary>
    public DateTimeOffset? ReportedAt { get; private set; }

    /// <summary>
    /// A new row. A reminder due by <paramref name="now"/> starts <see cref="CoordinatorActionStatus.AwaitingManual"/>
    /// (it is in the confirmation report); anything else starts <see cref="CoordinatorActionStatus.Scheduled"/> for its
    /// job. Reminders are reported at once (now, or as 「會提醒你」); an automatic try is reported when it has run.
    /// <paramref name="dueAt"/> earlier than <paramref name="now"/> becomes <paramref name="now"/>; times are kept in UTC.
    /// </summary>
    public static CoordinatorCommandAction Create(
        Guid organizationId,
        Guid commandId,
        int position,
        ShopActionKind kind,
        string? name,
        CoordinatorActionStep step,
        ShopActionMode mode,
        DateTimeOffset dueAt,
        string stepsText,
        string? imageUrl,
        string recipientLineUserId,
        Guid? undoesActionId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || commandId == Guid.Empty)
        {
            throw new ArgumentException("An action belongs to an organization's command.", nameof(commandId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(step) || !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Not a kind, step or mode.");
        }

        var ordering = kind == ShopActionKind.Ordering;
        if (ordering != (step is CoordinatorActionStep.Pause or CoordinatorActionStep.Resume))
        {
            throw new ArgumentException("Ordering systems pause and resume; the other actions apply and undo.", nameof(step));
        }

        if (ordering != !string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Exactly an ordering system has a name.", nameof(name));
        }

        if (ordering && mode == ShopActionMode.Auto)
        {
            throw new ArgumentException("An ordering system is always a reminder (Q15).", nameof(mode));
        }

        if ((step == CoordinatorActionStep.Undo && undoesActionId is null) || ((step is CoordinatorActionStep.Apply or CoordinatorActionStep.Pause) && undoesActionId is not null))
        {
            throw new ArgumentException("An undo names what it undoes; an apply or a pause undoes nothing.", nameof(undoesActionId));
        }

        if (string.IsNullOrWhiteSpace(stepsText) || stepsText.Length > StepsTextMaxLength)
        {
            throw new ArgumentException($"Steps are 1-{StepsTextMaxLength} characters.", nameof(stepsText));
        }

        if (!CoordinatorMember.IsValidLineUserId(recipientLineUserId))
        {
            throw new ArgumentException("Not a LINE user id.", nameof(recipientLineUserId));
        }

        // Stored as UTC (PostgreSQL's timestamp with time zone); the shop's offset only matters when it is shown.
        var due = (dueAt < now ? now : dueAt).ToUniversalTime();
        var awaiting = mode == ShopActionMode.Reminder && due <= now;
        return new CoordinatorCommandAction
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            CommandId = commandId,
            Position = position,
            Kind = kind,
            Name = ordering ? name : null,
            Step = step,
            Mode = mode,
            Status = awaiting ? CoordinatorActionStatus.AwaitingManual : CoordinatorActionStatus.Scheduled,
            DueAt = due,
            StepsText = stepsText,
            ImageUrl = (kind is ShopActionKind.LineBroadcast or ShopActionKind.FacebookPost) && step == CoordinatorActionStep.Apply ? imageUrl : null,
            RecipientLineUserId = recipientLineUserId,
            UndoesActionId = undoesActionId,
            CreatedAt = now.ToUniversalTime(),
            ReportedAt = mode == ShopActionMode.Reminder ? now.ToUniversalTime() : null,
        };
    }

    /// <summary>Whether <paramref name="code"/> can be a <see cref="FailureCode"/>: 1-32 of a-z, 0-9 and
    /// <c>-</c> — a code of ours, never text from outside.</summary>
    public static bool IsValidFailureCode(string? code) =>
        code is { Length: > 0 and <= FailureCodeMaxLength } && code.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Whether this row is an ordering system's pause or resume (always a reminder).</summary>
    public bool IsOrdering => Kind == ShopActionKind.Ordering;
}
