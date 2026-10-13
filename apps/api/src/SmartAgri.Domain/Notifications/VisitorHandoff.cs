using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Notifications;

/// <summary>
/// 轉真人 (table <c>VisitorHandoffs</c>, #355): a website or LINE visitor was given the assistant's
/// 「找不到資料時的回覆」 — the reply in which the owner tells visitors that a person will follow up — so
/// the assistant's owner is told, in the admin (side-nav count and list) and, when the deployment has
/// SMTP and the owner has not opted out, by Email.
/// </summary>
/// <remarks>
/// <para>
/// <b>No visitor content, no visitor id.</b> Only the assistant, the channel and the time: the website
/// keeps nothing of a visitor (M5a) and LINE keeps no LINE id in the database (M5b), and the cases ADR
/// defers anything that needs a visitor's identity or contact details. A domain test asserts the type
/// has no <see cref="string"/> property at all. Staff follow up where the visitor is (LINE Official
/// Account Manager's chat, or the contact details the refusal message gives).
/// </para>
/// <para>
/// Not a 處理事項 (the assistant does not need fixing) and not a 案件 (visitors cannot open cases yet):
/// the only state is pending → handled, by whoever marks it.
/// </para>
/// </remarks>
public sealed class VisitorHandoff : IOrganizationScoped
{
    /// <summary>
    /// A handled handoff is deleted once it was handled more than this many days ago (#362): the daily
    /// sweep (<c>HandoffRetentionSweep</c>) uses the conversation cleanup's calendar — strictly before
    /// 00:00 of the <c>Statistics:TimeZone</c> day this many days before today. Pending ones are never
    /// deleted, however old. Independent of the organization's conversation retention.
    /// </summary>
    public const int HandledRetentionDays = 365;

    /// <summary>For EF Core materialization.</summary>
    private VisitorHandoff()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid AssistantId { get; private set; }

    public VisitorHandoffChannel Channel { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When someone marked it handled; <see langword="null"/> while pending.</summary>
    public DateTimeOffset? HandledAt { get; private set; }

    public Guid? HandledByAccountId { get; private set; }

    /// <summary>When the notification Email was handed to the SMTP server, so a job that runs again
    /// (at-least-once delivery) does not send it twice.</summary>
    public DateTimeOffset? EmailSentAt { get; private set; }

    public bool IsPending => HandledAt is null;

    public static VisitorHandoff Open(Guid organizationId, Guid assistantId, VisitorHandoffChannel channel, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A handoff always belongs to an organization.", nameof(organizationId));
        }

        if (assistantId == Guid.Empty)
        {
            throw new ArgumentException("A handoff is always about an assistant.", nameof(assistantId));
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Not a visitor channel.");
        }

        return new VisitorHandoff
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AssistantId = assistantId,
            Channel = channel,
            CreatedAt = now,
        };
    }

    /// <summary>Marks the handoff handled; <see langword="false"/> (and nothing changes) when it already
    /// was, so marking twice keeps the first account and time.</summary>
    public bool MarkHandled(Guid accountId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Someone marks a handoff handled.", nameof(accountId));
        }

        if (HandledAt is not null)
        {
            return false;
        }

        HandledAt = now;
        HandledByAccountId = accountId;
        return true;
    }

    public void RecordEmailSent(DateTimeOffset now) => EmailSentAt ??= now;
}
