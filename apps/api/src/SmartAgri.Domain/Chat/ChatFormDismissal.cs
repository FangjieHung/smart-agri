using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Chat;

/// <summary>
/// One "the member closed a form the conversation offered" event (table <c>ChatFormDismissals</c>,
/// M4 #171): written when a member closes an in-conversation form card with 「不用了」 or ×, so
/// mis-triggered form requests (<c>Chat:FormRequests:Trigger = Model</c>, #164) can be sampled after
/// launch (<c>docs/evals/2026-10-05-164-form-request-trigger.md</c> §5).
/// </summary>
/// <remarks>
/// Only the event, the assistant, the form (its database) and the time — no account, no
/// conversation, no question and nothing the member typed into the form. A domain test asserts the
/// type has no <see cref="string"/> property at all. Like <c>AnswerOutcome</c> it is an operational
/// log: no foreign keys, so rows outlive a deleted assistant or database.
/// </remarks>
public sealed class ChatFormDismissal : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private ChatFormDismissal()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid AssistantId { get; private set; }

    /// <summary>The form's database (<c>ChatFormRequestView.Id</c>).</summary>
    public Guid DatabaseId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static ChatFormDismissal Record(Guid organizationId, Guid assistantId, Guid databaseId, DateTimeOffset at)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A dismissal is always recorded for an organization.", nameof(organizationId));
        }

        if (assistantId == Guid.Empty)
        {
            throw new ArgumentException("A dismissal is always for an assistant.", nameof(assistantId));
        }

        if (databaseId == Guid.Empty)
        {
            throw new ArgumentException("A dismissal is always for a form.", nameof(databaseId));
        }

        return new ChatFormDismissal
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AssistantId = assistantId,
            DatabaseId = databaseId,
            At = at,
        };
    }
}
