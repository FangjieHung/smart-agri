using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tenancy;
using SmartAgri.Application.Line;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Assistants;

namespace SmartAgri.Api.Line;

/// <summary>
/// <c>POST /api/v1/line/webhook/{assistantId}</c> (M5b plan §3 C, issue #231): where LINE delivers an
/// assistant's LINE channel events. Anonymous (it opts out of the fallback authorization policy and
/// ignores any credential), outside the visitor API (no same-origin check: LINE sends no
/// <c>Origin</c>), rate-limited per assistant (<see cref="PublicRateLimitOptions.LineWebhooksPerAssistantPerMinute"/>)
/// and not part of the OpenAPI document — it is LINE's contract, not one the admin calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>In this order:</b>
/// <list type="number">
/// <item>The raw body is read, at most <see cref="MaxBodyBytes"/> (else <c>413</c>), before anything
/// is parsed;</item>
/// <item>the assistant's organization is found (<see cref="PublicAssistantLookup"/>) and pinned for
/// the request, and its LINE channel read under the organization filter;</item>
/// <item>the channel secret is decrypted and <c>x-line-signature</c> checked against the raw bytes
/// (<see cref="LineWebhookSignature.IsValid"/>, constant-time). No such assistant (or not a GUID),
/// no LINE channel, a secret that cannot be decrypted, a missing or wrong signature: <b>the same
/// bodiless <c>401</c></b> — the HMAC is computed in every case (with a random key when there is no
/// secret), so the paths differ as little as practical;</item>
/// <item>a signed body that is not a JSON object: <c>400</c>, bodiless;</item>
/// <item><c>destination</c> must be the bot user id stored by the connection test; otherwise
/// (including a channel never tested) the events are ignored and the answer is still <c>200</c> —
/// a signature that verifies means LINE sent it, and a non-2xx answer would only make LINE redeliver;</item>
/// <item>events already accepted within 10 minutes (same <c>webhookEventId</c>, redelivered or not)
/// are dropped (<see cref="LineWebhookDeduplicator"/>);</item>
/// <item>the rest are queued for <see cref="LineWebhookProcessor"/> and the answer is <c>200</c>,
/// bodiless, at once — also for no events (LINE's "Verify" and webhook test requests) and for a draft
/// channel (the connection test needs a passing webhook before the channel can be enabled; the
/// processor sends a draft channel's users nothing).</item>
/// </list>
/// </para>
/// <para>
/// Nothing from LINE — ids, tokens, messages — is written to the database or the logs; the channel
/// row is only read.
/// </para>
/// </remarks>
public static class LineWebhookEndpoints
{
    public const string Route = "/api/v1/line/webhook/{assistantId}";

    /// <summary>The largest body read (1 MB); LINE's deliveries are far smaller.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>Signs nothing real: checked against when there is no usable secret, so that path does
    /// the same work as a wrong signature.</summary>
    private static readonly byte[] NoSecretKey = RandomNumberGenerator.GetBytes(32);

    public static IEndpointRouteBuilder MapLineWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, ReceiveAsync)
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithPublicRateLimit(PublicRateLimitedEndpoint.LineWebhook);
        return endpoints;
    }

    internal static async Task<IResult> ReceiveAsync(
        string assistantId,
        HttpContext httpContext,
        AppDbContext dbContext,
        ClaimsOrganizationContext organizationContext,
        PublicAssistantLookup lookup,
        ISecretProtector secretProtector,
        LineWebhookDeduplicator deduplicator,
        LineWebhookQueue queue,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Anonymous by design: a credential sent along must not decide the organization.
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var receivedAt = clock.GetUtcNow();
        var logger = loggerFactory.CreateLogger(typeof(LineWebhookEndpoints).FullName!);

        var body = await ReadBodyAsync(httpContext.Request, cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var (assistant, organizationId, channel, secret) = await FindSecretAsync(
            assistantId, dbContext, organizationContext, lookup, secretProtector, logger, cancellationToken);
        var signatures = httpContext.Request.Headers[LineWebhookSignature.HeaderName];
        bool verified;
        try
        {
            verified = LineWebhookSignature.IsValid(secret ?? NoSecretKey, body, signatures.Count == 1 ? signatures[0] : null);
        }
        finally
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        if (!verified || secret is null || channel is null)
        {
            if (channel is not null && secret is not null)
            {
                logger.LogInformation("A LINE webhook request for assistant {AssistantId} had no valid signature; refused.", assistant);
            }

            return Refused();
        }

        if (LineWebhookPayload.TryParse(body) is not { } payload)
        {
            logger.LogWarning("A signed LINE webhook request for assistant {AssistantId} was not a JSON object; refused.", assistant);
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (channel.BotUserId is null || !string.Equals(payload.Destination, channel.BotUserId, StringComparison.Ordinal))
        {
            if (payload.Events.Count > 0)
            {
                logger.LogInformation(
                    "A LINE webhook request for assistant {AssistantId} is for another bot (or the connection was never tested); its {EventCount} events are ignored.",
                    assistant, payload.Events.Count);
            }

            return Results.Ok();
        }

        var accepted = new List<LineWebhookEvent>(payload.Events.Count);
        foreach (var lineEvent in payload.Events)
        {
            if (lineEvent.WebhookEventId is { Length: > 0 } eventId && !deduplicator.TryAccept(assistant, eventId))
            {
                continue;
            }

            accepted.Add(lineEvent);
        }

        if (accepted.Count > 0
            && !queue.TryEnqueue(new LineWebhookDelivery(assistant, organizationId, accepted, receivedAt)))
        {
            foreach (var lineEvent in accepted)
            {
                if (lineEvent.WebhookEventId is { Length: > 0 } eventId)
                {
                    deduplicator.Release(assistant, eventId);
                }
            }

            logger.LogWarning(
                "The LINE webhook queue is full; {EventCount} events for assistant {AssistantId} are dropped.", accepted.Count, assistant);
        }

        return Results.Ok();
    }

    /// <summary>The one refusal: <c>401</c> without a body or any header saying why.</summary>
    internal static IResult Refused() => Results.StatusCode(StatusCodes.Status401Unauthorized);

    /// <summary>The body as received, or <see langword="null"/> when it is larger than
    /// <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream(request.ContentLength is { } length ? (int)length : 4096);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The assistant id, its organization, its LINE channel and the channel secret's UTF-8 bytes —
    /// as far as they exist: the channel is <see langword="null"/> when the id is not an assistant's or
    /// the assistant has no LINE channel, and the secret also when it cannot be decrypted (logged: it
    /// must be entered again).
    /// </summary>
    private static async Task<(Guid AssistantId, Guid OrganizationId, AssistantLineChannel? Channel, byte[]? Secret)> FindSecretAsync(
        string assistantId,
        AppDbContext dbContext,
        ClaimsOrganizationContext organizationContext,
        PublicAssistantLookup lookup,
        ISecretProtector secretProtector,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(assistantId, out var id)
            || await lookup.FindOrganizationIdAsync(id, cancellationToken) is not { } organizationId)
        {
            return (Guid.Empty, Guid.Empty, null, null);
        }

        organizationContext.Pin(organizationId);
        var channel = await dbContext.AssistantLineChannels
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == id, cancellationToken);
        if (channel is null)
        {
            return (id, organizationId, null, null);
        }

        try
        {
            var secret = secretProtector.Unprotect(AssistantLineChannel.ChannelSecretPurpose, channel.ChannelSecret);
            return (id, organizationId, channel, Encoding.UTF8.GetBytes(secret));
        }
        catch (SecretUnprotectException)
        {
            logger.LogWarning(
                "The LINE channel secret of assistant {AssistantId} cannot be decrypted (key ring changed?); its webhook requests are refused until it is entered again.",
                id);
            return (id, organizationId, channel, null);
        }
    }
}
