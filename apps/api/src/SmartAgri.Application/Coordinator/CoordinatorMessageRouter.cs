using System.Globalization;
using System.Text;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>What the 統籌助理 does with one LINE event (<see cref="CoordinatorMessageRouter"/>).</summary>
public enum CoordinatorIntentKind
{
    /// <summary>Nothing: not a one-to-one chat, standby mode, an event type the coordinator does not
    /// answer (<c>unfollow</c>, <c>unsend</c>, …) or a postback it did not make.</summary>
    Ignore,

    /// <summary>An unbound person added the account: ask for a binding code.</summary>
    Welcome,

    /// <summary>A bound member: list what the coordinator can do (Q24).</summary>
    Menu,

    /// <summary>An unbound person sent something that is not a binding code.</summary>
    AskForCode,

    /// <summary>The text looks like a binding code (<see cref="CoordinatorIntent.Code"/>): try to bind,
    /// whether or not the sender is bound already.</summary>
    Bind,

    /// <summary>A bound member asked for a 店長 binding code (「產生店長綁定碼」): the handler checks they
    /// are a 老闆 and, of several shops, asks which one.</summary>
    IssueManagerCode,

    /// <summary>A bound member chose the shop (<see cref="CoordinatorIntent.OrganizationId"/>) to issue
    /// a 店長 binding code for, by postback; the handler checks again that they are its 老闆.</summary>
    IssueManagerCodeFor,

    /// <summary>A bound member's text (<see cref="CoordinatorIntent.Text"/>, as written, trimmed) that is
    /// none of the above: an answer to a question the 統籌助理 asked, or a command for the model (#374).</summary>
    Command,

    /// <summary>「改成：…」: the new announcement (<see cref="CoordinatorIntent.Text"/>, as written, trimmed;
    /// may be empty) for the sender's latest preview card (#374).</summary>
    EditAnnouncement,

    /// <summary>The shop a held command is for (<see cref="CoordinatorIntent.OrganizationId"/>), or every
    /// shop (<see langword="null"/>, 「全部」), by postback (#374).</summary>
    ChooseShop,

    /// <summary>The date a command waits for (<see cref="CoordinatorIntent.Date"/>; <see langword="null"/>
    /// when the picker sent nothing usable), by postback (#374).</summary>
    FillDate,

    /// <summary>The time a command waits for (<see cref="CoordinatorIntent.Time"/>; <see langword="null"/>
    /// when the picker sent nothing usable), by postback (#374).</summary>
    FillTime,

    /// <summary>〔確認〕 on a preview card (<see cref="CoordinatorIntent.CommandId"/>,
    /// <see cref="CoordinatorIntent.Version"/>) (#374).</summary>
    Confirm,

    /// <summary>〔取消〕 on a preview card (#374).</summary>
    Cancel,

    /// <summary>「加發更正公告」 on a reopen card (#374).</summary>
    ToggleCorrection,

    /// <summary>The confirmed command (<see cref="CoordinatorIntent.CommandId"/>) a reopen is for, picked
    /// from several (#374).</summary>
    ChooseReopen,
}

/// <summary>One routing decision: the kind and what it carries — for <see cref="CoordinatorIntentKind.Bind"/>
/// the normalized code; for the postbacks the organization (untrusted: the handler checks the sender is a
/// member), command, card version, date or time; for <see cref="CoordinatorIntentKind.Command"/> and
/// <see cref="CoordinatorIntentKind.EditAnnouncement"/> what the member wrote.</summary>
public sealed record CoordinatorIntent(
    CoordinatorIntentKind Kind,
    string? Code = null,
    Guid? OrganizationId = null,
    Guid? CommandId = null,
    int? Version = null,
    DateOnly? Date = null,
    TimeOnly? Time = null,
    string? Text = null)
{
    public static CoordinatorIntent Of(CoordinatorIntentKind kind) => new(kind);

    /// <summary>Only the kind: the code is a secret, the text is what a person wrote, and neither is ever logged.</summary>
    public override string ToString() => $"{nameof(CoordinatorIntent)} {{ Kind = {Kind} }}";
}

/// <summary>
/// Decides what the 統籌助理 does with a LINE event (store-coordinator design §5 Slice 1, #372), from the
/// event alone and whether its sender is bound to any shop. Pure: the handler looks up the sender's
/// memberships, then carries out the decision (binding, issuing codes, replying).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only one-to-one chats (<c>source.type</c> <c>user</c>) in active mode count; groups and rooms
/// are ignored.</item>
/// <item><c>follow</c>: unbound → <see cref="CoordinatorIntentKind.Welcome"/>; bound →
/// <see cref="CoordinatorIntentKind.Menu"/>.</item>
/// <item>Text is read as NFKC, trimmed. Text that looks like a code (<see cref="BindingCodes.TryNormalize"/>)
/// → <see cref="CoordinatorIntentKind.Bind"/>, bound or not.</item>
/// <item>Unbound, any other message (also stickers and images) → <see cref="CoordinatorIntentKind.AskForCode"/>.</item>
/// <item>Bound, 「<see cref="IssueManagerCodeCommand"/>」 → <see cref="CoordinatorIntentKind.IssueManagerCode"/>;
/// text starting with 「改成：」 or 「改成:」 → <see cref="CoordinatorIntentKind.EditAnnouncement"/>; any other
/// text → <see cref="CoordinatorIntentKind.Command"/> (Slice 3, #374: the handler decides whether it answers
/// a question or goes to the model). Other messages (stickers, images, …) → the menu.</item>
/// <item>A postback this server made (<see cref="ManagerCodePostback"/>, <see cref="ShopPostback"/>,
/// <see cref="DatePostback"/>, <see cref="TimePostback"/>, <see cref="ConfirmPostback"/>,
/// <see cref="CancelPostback"/>, <see cref="CorrectionPostback"/>, <see cref="ReopenPostback"/>) → its intent
/// when bound, <see cref="CoordinatorIntentKind.AskForCode"/> when not; any other postback is ignored.</item>
/// </list>
/// The command and edit text are kept as written (not NFKC'd): they may end up in an announcement.
/// </remarks>
public static class CoordinatorMessageRouter
{
    /// <summary>What a 老闆 sends to get a 店長 binding code.</summary>
    public const string IssueManagerCodeCommand = "產生店長綁定碼";

    /// <summary>What starts an edit of the announcement on the latest preview card, full- or half-width colon.</summary>
    public static readonly IReadOnlyList<string> EditPrefixes = ["改成：", "改成:"];

    private const string ManagerCodePostbackPrefix = "coordinator:manager-code:";
    private const string ShopPostbackPrefix = "coordinator:shop:";
    private const string AllShops = "all";
    private const string DatePostbackPrefix = "coordinator:date:";
    private const string TimePostbackPrefix = "coordinator:time:";
    private const string ConfirmPostbackPrefix = "coordinator:confirm:";
    private const string CancelPostbackPrefix = "coordinator:cancel:";
    private const string CorrectionPostbackPrefix = "coordinator:correction:";
    private const string ReopenPostbackPrefix = "coordinator:reopen:";

    public static CoordinatorIntent Route(LineWebhookEvent lineEvent, bool isBound)
    {
        ArgumentNullException.ThrowIfNull(lineEvent);
        if (!lineEvent.Source.IsOneToOne || lineEvent.Source.UserId is not { Length: > 0 } || lineEvent.IsStandby)
        {
            return CoordinatorIntent.Of(CoordinatorIntentKind.Ignore);
        }

        switch (lineEvent.Type)
        {
            case LineWebhookEvent.Types.Follow:
                return CoordinatorIntent.Of(isBound ? CoordinatorIntentKind.Menu : CoordinatorIntentKind.Welcome);

            case LineWebhookEvent.Types.Message when lineEvent.Message is { IsText: true, Text: { } text }:
                if (BindingCodes.TryNormalize(text, out var code))
                {
                    return new CoordinatorIntent(CoordinatorIntentKind.Bind, Code: code);
                }

                return isBound ? BoundMessage(text) : CoordinatorIntent.Of(CoordinatorIntentKind.AskForCode);

            case LineWebhookEvent.Types.Message when lineEvent.Message is not null:
                return CoordinatorIntent.Of(isBound ? CoordinatorIntentKind.Menu : CoordinatorIntentKind.AskForCode);

            case LineWebhookEvent.Types.Postback when ParsePostback(lineEvent) is { } postback:
                return isBound ? postback : CoordinatorIntent.Of(CoordinatorIntentKind.AskForCode);

            default:
                return CoordinatorIntent.Of(CoordinatorIntentKind.Ignore);
        }
    }

    /// <summary>The <c>postback.data</c> of choosing <paramref name="organizationId"/>, or every shop
    /// (<see langword="null"/>), for a held command.</summary>
    public static string ShopPostback(Guid? organizationId) =>
        ShopPostbackPrefix + (organizationId is { } id ? id.ToString("N") : AllShops);

    /// <summary>The <c>postback.data</c> of answering command <paramref name="commandId"/>'s date question:
    /// with <paramref name="date"/> (a 「今天」/「明天」 button), or without (a date picker, whose choice LINE
    /// sends in <c>postback.params.date</c>).</summary>
    public static string DatePostback(Guid organizationId, Guid commandId, DateOnly? date = null) =>
        $"{DatePostbackPrefix}{organizationId:N}:{commandId:N}"
        + (date is { } chosen ? ":" + chosen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty);

    /// <summary>The <c>postback.data</c> of a time picker answering command <paramref name="commandId"/>'s
    /// time question (LINE sends the choice in <c>postback.params.time</c>).</summary>
    public static string TimePostback(Guid organizationId, Guid commandId) => $"{TimePostbackPrefix}{organizationId:N}:{commandId:N}";

    /// <summary>The <c>postback.data</c> of 〔確認〕 on version <paramref name="version"/> of a card.</summary>
    public static string ConfirmPostback(Guid organizationId, Guid commandId, int version) =>
        Versioned(ConfirmPostbackPrefix, organizationId, commandId, version);

    /// <summary>The <c>postback.data</c> of 〔取消〕 on version <paramref name="version"/> of a card.</summary>
    public static string CancelPostback(Guid organizationId, Guid commandId, int version) =>
        Versioned(CancelPostbackPrefix, organizationId, commandId, version);

    /// <summary>The <c>postback.data</c> of 「加發更正公告」 on version <paramref name="version"/> of a reopen card.</summary>
    public static string CorrectionPostback(Guid organizationId, Guid commandId, int version) =>
        Versioned(CorrectionPostbackPrefix, organizationId, commandId, version);

    /// <summary>The <c>postback.data</c> of choosing to undo confirmed command <paramref name="commandId"/>.</summary>
    public static string ReopenPostback(Guid organizationId, Guid commandId) => $"{ReopenPostbackPrefix}{organizationId:N}:{commandId:N}";

    /// <summary>The <c>postback.data</c> of the store choice for issuing a 店長 code for
    /// <paramref name="organizationId"/>.</summary>
    public static string ManagerCodePostback(Guid organizationId) => ManagerCodePostbackPrefix + organizationId.ToString("N");

    /// <summary>Whether <paramref name="data"/> is a <see cref="ManagerCodePostback"/>, and its
    /// organization. Untrusted: the handler checks the sender is that organization's 老闆.</summary>
    public static bool TryParseManagerCodePostback(string? data, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        return data is not null
            && data.StartsWith(ManagerCodePostbackPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(data[ManagerCodePostbackPrefix.Length..], "N", out organizationId)
            && organizationId != Guid.Empty;
    }

    /// <summary><paramref name="text"/> as NFKC (full-width becomes half-width), trimmed; unchanged
    /// (trimmed) if it is not valid Unicode.</summary>
    public static string NormalizeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return text.Normalize(NormalizationForm.FormKC).Trim();
        }
        catch (ArgumentException)
        {
            return text.Trim();
        }
    }

    /// <summary>
    /// A bound member's text message that is not a binding code: 「產生店長綁定碼」 (compared as NFKC), an edit
    /// of the announcement, or anything else — a command or an answer, for the handler (#374).
    /// </summary>
    private static CoordinatorIntent BoundMessage(string text)
    {
        if (NormalizeText(text) == IssueManagerCodeCommand)
        {
            return CoordinatorIntent.Of(CoordinatorIntentKind.IssueManagerCode);
        }

        var written = text.Trim();
        foreach (var prefix in EditPrefixes)
        {
            if (written.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new CoordinatorIntent(CoordinatorIntentKind.EditAnnouncement, Text: written[prefix.Length..].Trim());
            }
        }

        return written.Length == 0
            ? CoordinatorIntent.Of(CoordinatorIntentKind.Menu)
            : new CoordinatorIntent(CoordinatorIntentKind.Command, Text: written);
    }

    /// <summary>The intent of a postback this server made, or <see langword="null"/> for any other data.
    /// Ids are parsed, never trusted.</summary>
    private static CoordinatorIntent? ParsePostback(LineWebhookEvent lineEvent)
    {
        var data = lineEvent.PostbackData;
        if (data is null)
        {
            return null;
        }

        if (TryParseManagerCodePostback(data, out var managed))
        {
            return new CoordinatorIntent(CoordinatorIntentKind.IssueManagerCodeFor, OrganizationId: managed);
        }

        if (data.StartsWith(ShopPostbackPrefix, StringComparison.Ordinal))
        {
            var rest = data[ShopPostbackPrefix.Length..];
            return rest == AllShops
                ? new CoordinatorIntent(CoordinatorIntentKind.ChooseShop)
                : TryId(rest, out var shop) ? new CoordinatorIntent(CoordinatorIntentKind.ChooseShop, OrganizationId: shop) : null;
        }

        if (Parts(data, DatePostbackPrefix) is [var dateOrganization, var dateCommand, .. var dateRest] && dateRest.Length <= 1
            && TryId(dateOrganization, out var dateOrganizationId) && TryId(dateCommand, out var dateCommandId))
        {
            var picked = dateRest.Length == 1 ? dateRest[0] : lineEvent.PostbackDate;
            DateOnly? date = DateOnly.TryParseExact(picked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
            return new CoordinatorIntent(CoordinatorIntentKind.FillDate, OrganizationId: dateOrganizationId, CommandId: dateCommandId, Date: date);
        }

        if (Parts(data, TimePostbackPrefix) is [var timeOrganization, var timeCommand]
            && TryId(timeOrganization, out var timeOrganizationId) && TryId(timeCommand, out var timeCommandId))
        {
            TimeOnly? time = TimeOnly.TryParseExact(lineEvent.PostbackTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
            return new CoordinatorIntent(CoordinatorIntentKind.FillTime, OrganizationId: timeOrganizationId, CommandId: timeCommandId, Time: time);
        }

        foreach (var (prefix, kind) in new[]
                 {
                     (ConfirmPostbackPrefix, CoordinatorIntentKind.Confirm),
                     (CancelPostbackPrefix, CoordinatorIntentKind.Cancel),
                     (CorrectionPostbackPrefix, CoordinatorIntentKind.ToggleCorrection),
                 })
        {
            if (Parts(data, prefix) is [var organization, var command, var version]
                && TryId(organization, out var organizationId) && TryId(command, out var commandId)
                && int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1)
            {
                return new CoordinatorIntent(kind, OrganizationId: organizationId, CommandId: commandId, Version: number);
            }
        }

        if (Parts(data, ReopenPostbackPrefix) is [var reopenOrganization, var reopenCommand]
            && TryId(reopenOrganization, out var reopenOrganizationId) && TryId(reopenCommand, out var reopenCommandId))
        {
            return new CoordinatorIntent(CoordinatorIntentKind.ChooseReopen, OrganizationId: reopenOrganizationId, CommandId: reopenCommandId);
        }

        return null;
    }

    private static string Versioned(string prefix, Guid organizationId, Guid commandId, int version) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}{organizationId:N}:{commandId:N}:{version}");

    private static string[]? Parts(string data, string prefix) =>
        data.StartsWith(prefix, StringComparison.Ordinal) ? data[prefix.Length..].Split(':') : null;

    private static bool TryId(string text, out Guid id) =>
        Guid.TryParseExact(text, "N", out id) && id != Guid.Empty;
}
