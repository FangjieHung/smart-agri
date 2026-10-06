using AGUI.Abstractions;
using AGUI.Formatting;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// The website channel's visitor API under <c>/api/v1/public</c> (M5a plan §3 B and D, issue #196):
/// authorized only with the <see cref="VisitorAuthentication.Scheme"/> scheme
/// (<see cref="VisitorAuthentication.Policy"/>), same-origin only (<see cref="PublicOriginGuard"/>),
/// no CORS policy.
/// </summary>
public static class VisitorEndpoints
{
    public static IEndpointRouteBuilder MapVisitorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assistants = endpoints.MapGroup($"{PublicOriginGuard.PathPrefix}/assistants")
            .RequireAuthorization(VisitorAuthentication.Policy);

        assistants.MapPost("/{id}/visitor-sessions", VisitorSessionEndpoints.CreateAsync)
            .AllowAnonymous()
            .WithSummary("Start an anonymous website visitor session")
            .WithDescription(
                "Anonymous; any credential is ignored. 201 while the assistant's website channel is serving; " +
                "otherwise (unknown id, not published, paused, suspended) the same 403 public-assistant, byte for byte. " +
                "Body { host }: the embedding page's origin, used only for passive installation detection.")
            .Accepts<CreateVisitorSessionRequest>("application/json")
            .Produces<VisitorSessionView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status403Forbidden);

        assistants.MapPost("/{id}/chat/runs", VisitorChatRunEndpoints.RunAsync)
            .WithSummary("Answer one website visitor's question as an AG-UI event stream")
            .WithDescription(
                "Authorization: Visitor <token>. Body: AG-UI RunAgentInput (use @ag-ui/core's type; this schema is only a sketch); " +
                "earlier turns come from messages (at most 20) and nothing is saved. 200: text/event-stream of AG-UI events " +
                "(RUN_STARTED, TEXT_MESSAGE_*, CUSTOM smartagri.reply, RUN_FINISHED or RUN_ERROR); see VisitorChatRunEndpoints' remarks.")
            .Accepts<RunAgentInput>("application/json")
            .Produces<string>(StatusCodes.Status200OK, SseEventStreamFormatter.ServerSentEventsMediaType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }
}
