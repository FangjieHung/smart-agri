using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tenancy;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Assistants;

namespace SmartAgri.Api.PublicChannels;

/// <summary><c>POST /api/v1/public/assistants/{id}/visitor-sessions</c> request: the origin of the
/// page the chat window is embedded in, as the loader reported it (<c>?host=</c>), or
/// <see langword="null"/>. Used only for passive installation detection.</summary>
public sealed record CreateVisitorSessionRequest(string? Host);

/// <summary>What the widget shows for the assistant: the website channel's settings, and whether to
/// show citations (the assistant's own setting).</summary>
public sealed record VisitorAssistantView(string DisplayName, string WelcomeMessage, WebsiteBrandColor BrandColor, bool ShowCitations);

/// <summary>A new visitor session: the token for <c>Authorization: Visitor &lt;token&gt;</c>, when it
/// expires (12 hours, decision C), and the assistant's display settings.</summary>
public sealed record VisitorSessionView(string Token, DateTimeOffset ExpiresAt, VisitorAssistantView Assistant);

/// <summary>
/// Starts an anonymous website visitor's session (M5a plan §3 D, issue #196). Anonymous: any
/// credential sent with it is ignored. While the assistant's website channel is
/// <see cref="ChannelServingState.Serving"/> (derived now, never cached beyond the monthly-usage
/// cache) it answers <c>201</c> with a new <see cref="VisitorTokens"/> token; in every other case —
/// no such assistant (or not even a GUID), not published, paused, suspended for any reason — the very
/// same <c>403 public-assistant</c>, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// The assistant's organization is found across organizations (<see cref="PublicAssistantLookup"/>:
/// the caller has none), then the request pins it (<see cref="ClaimsOrganizationContext"/>) — an
/// organization established from the server's own row, as sign-in does from an account row — so the
/// assistant, the serving state and the installation detection are read and written under the usual
/// organization filter and write guard.
/// </para>
/// <para>
/// <b>Passive installation detection</b> (decision F, plan §3 H): when <c>host</c> is an
/// <c>https://</c> origin (default port, no path) whose host is one of the allowed domains, that
/// domain's <c>LastSeenAt</c> becomes now. Information only — never a security decision — and
/// nothing about the visitor is stored.
/// </para>
/// </remarks>
public static class VisitorSessionEndpoints
{
    /// <summary>The body read for <c>host</c>; anything larger is ignored (as if no body was sent).</summary>
    private const int MaxBodyBytes = 4096;

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);

    internal static async Task<IResult> CreateAsync(
        string id,
        HttpContext httpContext,
        AppDbContext dbContext,
        ClaimsOrganizationContext organizationContext,
        PublicAssistantLookup lookup,
        OrganizationTokenUsage tokenUsage,
        VisitorTokens tokens,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Anonymous by design: a member's bearer token or another visitor's token says nothing here,
        // and must not decide the organization this request acts for.
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        httpContext.Response.Headers.CacheControl = "no-store";

        var host = await ReadHostAsync(httpContext.Request, cancellationToken);

        if (!Guid.TryParse(id, out var assistantId))
        {
            return Refused();
        }

        if (await lookup.FindOrganizationIdAsync(assistantId, cancellationToken) is not { } organizationId)
        {
            return Refused();
        }

        organizationContext.Pin(organizationId);
        var assistant = await dbContext.Assistants
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == assistantId, cancellationToken);
        if (assistant is null)
        {
            return Refused();
        }

        var (serving, channel) = await AssistantWebsiteChannelEndpoints.ServingStateAsync(dbContext, assistant, tokenUsage, cancellationToken);
        if (serving != ChannelServingState.Serving || channel is null)
        {
            return Refused();
        }

        await MarkSeenAsync(dbContext, assistant.Id, host, clock, loggerFactory, cancellationToken);

        var (token, expiresAt) = tokens.Issue(assistant.Id, assistant.OrganizationId);
        return Results.Json(
            new VisitorSessionView(
                token,
                expiresAt,
                new VisitorAssistantView(channel.DisplayName, channel.WelcomeMessage, channel.BrandColor, assistant.ShowCitations)),
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The one refusal of the visitor API (<see cref="ForbiddenReason.PublicAssistant"/>).</summary>
    internal static IResult Refused() => ApiErrors.Forbidden(ForbiddenReason.PublicAssistant);

    /// <summary>The body's <c>host</c>, or <see langword="null"/> when there is no body, it is larger
    /// than <see cref="MaxBodyBytes"/>, or it is not the expected JSON — never an error.</summary>
    private static async Task<string?> ReadHostAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
        {
            length += read;
        }

        if (length == 0 || length > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CreateVisitorSessionRequest>(buffer.AsSpan(0, length), BodyJson)?.Host;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Passive installation detection: <paramref name="host"/>'s domain, if allowed, was seen now.</summary>
    private static async Task MarkSeenAsync(
        AppDbContext dbContext, Guid assistantId, string? host, TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (EmbeddingDomain(host) is not { } domainName)
        {
            return;
        }

        var domain = await dbContext.AssistantWebsiteDomains
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistantId && candidate.Domain == domainName, cancellationToken);
        if (domain is null || !domain.MarkSeen(clock.GetUtcNow()))
        {
            return;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Information only: losing one "last seen" never fails a visitor's session.
            loggerFactory.CreateLogger(typeof(VisitorSessionEndpoints).FullName!)
                .LogWarning(exception, "Could not record where a website chat window was last seen.");
        }
    }

    /// <summary>The lower-case host of an <c>https://</c> origin on the default port with no path,
    /// query, fragment or user info; otherwise <see langword="null"/> (allowed domains are always
    /// <c>https://</c>, plan §3 B).</summary>
    internal static string? EmbeddingDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)
            || !Uri.TryCreate(host.Trim(), UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps
            || !origin.IsDefaultPort
            || origin.UserInfo.Length > 0
            || origin.AbsolutePath != "/"
            || origin.Query.Length > 0
            || origin.Fragment.Length > 0)
        {
            return null;
        }

        return origin.IdnHost.ToLowerInvariant();
    }
}
