using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Line;

/// <summary>
/// A text <c>message</c> event of a LINE channel, as the webhook processor hands it over: the
/// assistant and its LINE channel as read for this delivery (under the assistant's organization),
/// the serving state derived now, and the event itself.
/// </summary>
/// <param name="Chat">The conversation (assistant + user, group or room).</param>
/// <param name="AccessToken">The channel's access token, decrypted for this event only; never
/// logged or stored.</param>
/// <param name="ReceivedAt">When the webhook request arrived (a reply token lasts about a minute
/// from about then).</param>
public sealed record LineQuestionContext(
    Assistant Assistant,
    AssistantLineChannel Channel,
    ChannelServingState ServingState,
    LineWebhookEvent Event,
    LineChatKey Chat,
    string AccessToken,
    DateTimeOffset ReceivedAt)
{
    /// <summary>No token, ids or text: those are never logged.</summary>
    public override string ToString() =>
        $"{nameof(LineQuestionContext)} {{ AssistantId = {Assistant.Id}, ServingState = {ServingState}, Event = {Event} }}";
}

/// <summary>
/// Answers a LINE user's text question (M5b plan §3 D, "每個問題"): resolved from the webhook
/// processor's scope, which acts for the assistant's organization, once per text <c>message</c> event
/// of a published or paused channel that is not in standby — one-to-one, or in a group or room
/// (where the handler decides whether the bot was mentioned, <see cref="LineEventMessage.MentionsSelf"/>).
/// The handler owns the event's reply token (single use) and must not throw for an expected failure.
/// </summary>
/// <remarks>
/// The webhook endpoint slice (#231) registers <see cref="NoLineQuestionHandler"/>, which answers
/// nothing; LINE questions (#232) replace it with the grounded-answer pipeline.
/// </remarks>
public interface ILineQuestionHandler
{
    Task HandleAsync(LineQuestionContext context, CancellationToken cancellationToken);
}

/// <summary>The <see cref="ILineQuestionHandler"/> that answers nothing.</summary>
public sealed class NoLineQuestionHandler : ILineQuestionHandler
{
    public Task HandleAsync(LineQuestionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
