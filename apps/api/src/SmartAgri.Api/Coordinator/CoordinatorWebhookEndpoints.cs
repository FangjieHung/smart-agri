using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Line;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Line;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// <c>POST /api/v1/coordinator/line/webhook</c> (store-coordinator design §5 Slice 1, #372): where LINE
/// delivers the platform's shared 統籌助理 account's events. Built like the assistants' webhook
/// (<see cref="LineWebhookEndpoints"/>): anonymous, outside the OpenAPI document, under the same
/// one-minute sliding-window rate limit (its own partition), and quick — the events are handled in the
/// background by <see cref="CoordinatorWebhookProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In this order:</b>
/// <list type="number">
/// <item>The coordinator is off (<see cref="CoordinatorLineOptions"/> not set): <c>404</c>, before
/// anything is read;</item>
/// <item>the raw body is read, at most <see cref="LineWebhookEndpoints.MaxBodyBytes"/> (else <c>413</c>);</item>
/// <item><c>x-line-signature</c> is checked against the raw bytes with the configured channel secret
/// (constant-time); missing or wrong: a bodiless <c>401</c>;</item>
/// <item>a signed body that is not a JSON object: <c>400</c>;</item>
/// <item>no events (LINE's Verify): <c>200</c>;</item>
/// <item><c>destination</c> must be the coordinator bot's user id (<see cref="CoordinatorBotIdentity"/>,
/// read once from LINE with the configured token); otherwise — or when LINE cannot be asked — the events
/// are ignored and the answer is still <c>200</c> (a signature that verifies means LINE sent it; a
/// non-2xx answer would only make LINE redeliver);</item>
/// <item>only one-to-one events (<c>source.type</c> <c>user</c>) are kept — groups and rooms are
/// ignored — and of those, ones already accepted within 10 minutes (same <c>webhookEventId</c>) are
/// dropped (<see cref="CoordinatorWebhookDeduplicator"/>);</item>
/// <item>the rest are queued (<see cref="CoordinatorWebhookQueue"/>) and the answer is <c>200</c>,
/// bodiless, at once.</item>
/// </list>
/// </para>
/// <para>
/// Nothing from LINE — ids, tokens, messages — is logged; neither are the configured secret and token.
/// </para>
/// </remarks>
public static class CoordinatorWebhookEndpoints
{
    public const string Route = "/api/v1/coordinator/line/webhook";

    public static IEndpointRouteBuilder MapCoordinatorWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, ReceiveAsync)
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithPublicRateLimit(PublicRateLimitedEndpoint.CoordinatorLineWebhook);
        return endpoints;
    }

    internal static async Task<IResult> ReceiveAsync(
        HttpContext httpContext,
        IOptions<CoordinatorLineOptions> options,
        ILineMessagingClient line,
        CoordinatorBotIdentity bot,
        CoordinatorWebhookDeduplicator deduplicator,
        CoordinatorWebhookQueue queue,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Anonymous by design: a credential sent along must not decide anything.
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            return Results.NotFound();
        }

        var receivedAt = clock.GetUtcNow();
        var logger = loggerFactory.CreateLogger(typeof(CoordinatorWebhookEndpoints).FullName!);

        var body = await LineWebhookEndpoints.ReadBodyAsync(httpContext.Request, cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var secret = Encoding.UTF8.GetBytes(settings.Secret);
        var signatures = httpContext.Request.Headers[LineWebhookSignature.HeaderName];
        bool verified;
        try
        {
            verified = LineWebhookSignature.IsValid(secret, body, signatures.Count == 1 ? signatures[0] : null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        if (!verified)
        {
            logger.LogInformation("A 統籌助理 webhook request had no valid signature; refused.");
            return LineWebhookEndpoints.Refused();
        }

        if (LineWebhookPayload.TryParse(body) is not { } payload)
        {
            logger.LogWarning("A signed 統籌助理 webhook request was not a JSON object; refused.");
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (payload.Events.Count == 0)
        {
            return Results.Ok();
        }

        var (botUserId, failure) = await bot.GetUserIdAsync(line, settings.AccessToken, cancellationToken);
        if (botUserId is null)
        {
            logger.LogWarning(
                "LINE did not say which bot the 統籌助理 access token belongs to: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}); {EventCount} events are ignored.",
                failure?.Outcome, failure?.StatusCode, failure?.RequestId, payload.Events.Count);
            return Results.Ok();
        }

        if (!string.Equals(payload.Destination, botUserId, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "A 統籌助理 webhook request is for another bot; its {EventCount} events are ignored.", payload.Events.Count);
            return Results.Ok();
        }

        var accepted = new List<LineWebhookEvent>(payload.Events.Count);
        foreach (var lineEvent in payload.Events)
        {
            if (!lineEvent.Source.IsOneToOne)
            {
                continue;
            }

            if (lineEvent.WebhookEventId is { Length: > 0 } eventId && !deduplicator.TryAccept(eventId))
            {
                continue;
            }

            accepted.Add(lineEvent);
        }

        if (accepted.Count > 0 && !queue.TryEnqueue(new CoordinatorWebhookDelivery(accepted, receivedAt)))
        {
            foreach (var lineEvent in accepted)
            {
                if (lineEvent.WebhookEventId is { Length: > 0 } eventId)
                {
                    deduplicator.Release(eventId);
                }
            }

            logger.LogWarning("The 統籌助理 webhook queue is full; {EventCount} events are dropped.", accepted.Count);
        }

        return Results.Ok();
    }
}
