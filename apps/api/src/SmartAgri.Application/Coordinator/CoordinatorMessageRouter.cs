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
}

/// <summary>One routing decision: the kind and, for <see cref="CoordinatorIntentKind.Bind"/> the
/// normalized code, for <see cref="CoordinatorIntentKind.IssueManagerCodeFor"/> the organization.</summary>
public sealed record CoordinatorIntent(CoordinatorIntentKind Kind, string? Code = null, Guid? OrganizationId = null)
{
    public static CoordinatorIntent Of(CoordinatorIntentKind kind) => new(kind);

    /// <summary>No code or id: the code is a secret and is never logged.</summary>
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
/// any other message → <see cref="BoundMessage"/>, which is where the scenario commands (臨時店休 …) of
/// Slice 3 come in; for now it is always <see cref="CoordinatorIntentKind.Menu"/>.</item>
/// <item>A postback this server made (<see cref="ManagerCodePostback"/>) → <see cref="CoordinatorIntentKind.IssueManagerCodeFor"/>
/// when bound, <see cref="CoordinatorIntentKind.AskForCode"/> when not; any other postback is ignored.</item>
/// </list>
/// </remarks>
public static class CoordinatorMessageRouter
{
    /// <summary>What a 老闆 sends to get a 店長 binding code.</summary>
    public const string IssueManagerCodeCommand = "產生店長綁定碼";

    private const string ManagerCodePostbackPrefix = "coordinator:manager-code:";

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

                return isBound ? BoundMessage(NormalizeText(text)) : CoordinatorIntent.Of(CoordinatorIntentKind.AskForCode);

            case LineWebhookEvent.Types.Message when lineEvent.Message is not null:
                return CoordinatorIntent.Of(isBound ? CoordinatorIntentKind.Menu : CoordinatorIntentKind.AskForCode);

            case LineWebhookEvent.Types.Postback when TryParseManagerCodePostback(lineEvent.PostbackData, out var organizationId):
                return isBound
                    ? new CoordinatorIntent(CoordinatorIntentKind.IssueManagerCodeFor, OrganizationId: organizationId)
                    : CoordinatorIntent.Of(CoordinatorIntentKind.AskForCode);

            default:
                return CoordinatorIntent.Of(CoordinatorIntentKind.Ignore);
        }
    }

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
    /// A bound member's text message that is not a binding code. The seam for Slice 3 (#374): the
    /// scenario commands (臨時店休、恢復營業、提早打烊、延後開店) will be recognized here, before the
    /// capability menu.
    /// </summary>
    private static CoordinatorIntent BoundMessage(string text) =>
        text == IssueManagerCodeCommand
            ? CoordinatorIntent.Of(CoordinatorIntentKind.IssueManagerCode)
            : CoordinatorIntent.Of(CoordinatorIntentKind.Menu);
}
