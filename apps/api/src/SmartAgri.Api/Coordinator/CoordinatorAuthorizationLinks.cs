using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Coordinator;
using SmartAgri.Infrastructure.Persistence;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>A link just made. <see cref="Url"/> holds the token: shown once, never logged.</summary>
public sealed record IssuedAuthorizationLink(string Url, CoordinatorAuthorizationLink Link)
{
    public override string ToString() => $"{nameof(IssuedAuthorizationLink)} {{ {Link} }}";
}

/// <summary>
/// The shops' FB and Google authorization links (store-coordinator design §3 Q12, §5 Slice 6, #377): where they and
/// their callbacks live, making them (<c>coordinator-shop auth-link</c>, re-authorization), finding the one a token
/// opens (through <see cref="CoordinatorLookup"/>, the only cross-organization read) and taking it — each write in a
/// context acting for its organization (<see cref="FixedOrganizationContext"/>).
/// </summary>
/// <remarks>
/// A service's flow is <em>available</em> when <c>PublicChannels:PublicBaseUrl</c> is set (the owner's browser and the
/// service reach the callback there) and its provider is registered and configured (<see cref="CoordinatorOAuthProviders"/>).
/// </remarks>
public sealed class CoordinatorAuthorizationLinks
{
    /// <summary><c>GET {ConnectPath}?token={token}</c> opens a link. The token is a query parameter, not a path segment:
    /// ASP.NET Core puts the request path (never the query) in the logging scope of everything logged during the request,
    /// which the OpenTelemetry log exporter sends along.</summary>
    public const string ConnectPath = "/api/v1/coordinator/connect";

    /// <summary><c>{OAuthPrefix}/{service}/callback</c> and <c>{OAuthPrefix}/{service}/select</c>.</summary>
    public const string OAuthPrefix = "/api/v1/coordinator/oauth";

    private const int IssueAttempts = 3;

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly CoordinatorLookup _lookup;
    private readonly CoordinatorOAuthProviders _providers;
    private readonly PublicChannelsOptions _publicChannels;
    private readonly TimeProvider _clock;

    public CoordinatorAuthorizationLinks(
        DbContextOptions<AppDbContext> options,
        CoordinatorLookup lookup,
        CoordinatorOAuthProviders providers,
        IOptions<PublicChannelsOptions> publicChannels,
        TimeProvider clock)
    {
        _options = options;
        _lookup = lookup;
        _providers = providers;
        _publicChannels = publicChannels.Value;
        _clock = clock;
    }

    /// <summary><c>PublicChannels:PublicBaseUrl</c>, normalized; <see langword="null"/> when unset.</summary>
    public string? PublicBaseUrl => _publicChannels.ResolvedPublicBaseUrl;

    /// <summary>The configured provider of <paramref name="service"/> when its flow is available (see remarks).</summary>
    public ICoordinatorOAuthProvider? Provider(CoordinatorService service) =>
        PublicBaseUrl is null || !CoordinatorAuthorizationLink.IsLinkService(service) ? null : _providers.For(service);

    /// <summary>Whether links for <paramref name="service"/> can be made and used.</summary>
    public bool IsAvailable(CoordinatorService service) => Provider(service) is not null;

    /// <summary>The link the owner opens.</summary>
    public string ConnectUrl(string token) => $"{RequireBaseUrl()}{ConnectPath}?token={token}";

    /// <summary>The callback registered with the service: <c>{public base URL}/api/v1/coordinator/oauth/{service}/callback</c>.</summary>
    public string RedirectUri(CoordinatorService service) => $"{RequireBaseUrl()}{ServicePath(service)}/callback";

    /// <summary>The path the nonce cookie of <paramref name="service"/> is sent to (the callback and the selection form),
    /// with the public base URL's own path in front.</summary>
    public string CookiePath(CoordinatorService service) =>
        new Uri(RequireBaseUrl()).AbsolutePath.TrimEnd('/') + ServicePath(service);

    /// <summary><c>/api/v1/coordinator/oauth/{service}</c>.</summary>
    public static string ServicePath(CoordinatorService service) => $"{OAuthPrefix}/{WireNames<CoordinatorService>.ToWire(service)}";

    /// <summary>
    /// Makes a link of <paramref name="organizationId"/> for <paramref name="service"/> valid for
    /// <paramref name="validity"/>; <see langword="null"/> when the organization does not exist. The flow must be
    /// available (<see cref="IsAvailable"/>).
    /// </summary>
    public async Task<IssuedAuthorizationLink?> IssueAsync(
        Guid organizationId,
        CoordinatorService service,
        CoordinatorAuthorizationPurpose purpose,
        TimeSpan validity,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable(service))
        {
            throw new InvalidOperationException($"The {WireNames<CoordinatorService>.ToWire(service)} authorization is not configured.");
        }

        await using var dbContext = ForOrganization(organizationId);
        if (!await dbContext.Organizations.AnyAsync(organization => organization.Id == organizationId, cancellationToken))
        {
            return null;
        }

        for (var attempt = 1; ; attempt++)
        {
            var (token, link) = NewLink(organizationId, service, purpose, validity);
            dbContext.CoordinatorAuthorizationLinks.Add(link);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return new IssuedAuthorizationLink(ConnectUrl(token), link);
            }
            catch (DbUpdateException exception) when (attempt < IssueAttempts && DatabaseErrors.IsUniqueViolation(exception))
            {
                // Another link has that hash (about 1 in 2^256): draw again.
                dbContext.Entry(link).State = EntityState.Detached;
            }
        }
    }

    /// <summary>A new link (not saved) and its token.</summary>
    internal (string Token, CoordinatorAuthorizationLink Link) NewLink(
        Guid organizationId, CoordinatorService service, CoordinatorAuthorizationPurpose purpose, TimeSpan validity)
    {
        var token = AuthorizationLinkTokens.Generate();
        return (token, CoordinatorAuthorizationLink.Issue(
            organizationId, service, purpose, AuthorizationLinkTokens.Hash(token), _clock.GetUtcNow(), validity));
    }

    /// <summary>The link <paramref name="token"/> opens, when it exists; whether it is still usable is the caller's
    /// question (<see cref="CoordinatorAuthorizationLink.IsUsable"/>). Never throws for a malformed token.</summary>
    public async Task<CoordinatorAuthorizationLink?> FindAsync(string? token, CancellationToken cancellationToken)
    {
        if (!AuthorizationLinkTokens.IsToken(token))
        {
            return null;
        }

        if (await _lookup.FindAuthorizationLinkAsync(AuthorizationLinkTokens.Hash(token!), cancellationToken) is not { } match)
        {
            return null;
        }

        await using var dbContext = ForOrganization(match.OrganizationId);
        return await dbContext.CoordinatorAuthorizationLinks.AsNoTracking()
            .SingleOrDefaultAsync(link => link.Id == match.LinkId, cancellationToken);
    }

    /// <summary>Marks the link used — only if it still is unused and unexpired (compare-and-set), so of two callbacks
    /// exactly one goes on. <see langword="true"/> when this call took it.</summary>
    public async Task<bool> TakeAsync(Guid organizationId, Guid linkId, CoordinatorService service, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var now = _clock.GetUtcNow();
        return await dbContext.CoordinatorAuthorizationLinks
            .Where(link => link.Id == linkId && link.Service == service && link.UsedAt == null && link.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.UsedAt, now), cancellationToken) == 1;
    }

    private string RequireBaseUrl() =>
        PublicBaseUrl ?? throw new InvalidOperationException("PublicChannels:PublicBaseUrl is not set: authorization links need it.");

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}

/// <summary>
/// What an authorization flow's <c>state</c> (or its selection form's) says (#377): which link, of which organization and
/// service, the nonce its cookie must carry, the PKCE verifier (consent state only) and until when. <see cref="ToString"/>
/// shows neither the nonce nor the verifier.
/// </summary>
public sealed record CoordinatorOAuthState(
    Guid LinkId, Guid OrganizationId, CoordinatorService Service, string Nonce, string? CodeVerifier, DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"{nameof(CoordinatorOAuthState)} {{ LinkId = {LinkId}, OrganizationId = {OrganizationId}, Service = {Service}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>Which of the two protected payloads (they cannot stand in for each other).</summary>
public enum CoordinatorOAuthStateKind
{
    /// <summary>The <c>state</c> sent to the service's consent screen and back to the callback.</summary>
    Consent,

    /// <summary>The selection form's hidden field (no PKCE verifier).</summary>
    Selection,
}

/// <summary>
/// Protects and reads <see cref="CoordinatorOAuthState"/> with Data Protection (#377): one purpose per
/// <see cref="CoordinatorOAuthStateKind"/>; the expiry is inside the authenticated payload and checked against
/// <see cref="TimeProvider"/> (so tests can move time either way) — valid for <see cref="Lifetime"/>. Anything tampered,
/// expired, of another kind, deployment or service reads as <see langword="null"/>; <see cref="TryRead"/> never throws.
/// </summary>
public sealed class CoordinatorOAuthStates
{
    /// <summary>How long an owner has between opening the link and coming back (also: choosing a page or location).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const string ConsentPurpose = "SmartAgri.Coordinator.OAuthState.Consent.v1";
    private const string SelectionPurpose = "SmartAgri.Coordinator.OAuthState.Selection.v1";

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _consent;
    private readonly IDataProtector _selection;
    private readonly TimeProvider _clock;

    public CoordinatorOAuthStates(IDataProtectionProvider provider, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _consent = provider.CreateProtector(ConsentPurpose);
        _selection = provider.CreateProtector(SelectionPurpose);
        _clock = clock;
    }

    /// <summary>A new random value for a nonce or a PKCE verifier: 32 bytes in base64url (43 characters).</summary>
    public static string NewRandom() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The PKCE <c>S256</c> challenge of <paramref name="verifier"/>.</summary>
    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A new state for link <paramref name="linkId"/>, valid for <see cref="Lifetime"/> from now.</summary>
    public CoordinatorOAuthState New(Guid linkId, Guid organizationId, CoordinatorService service, string nonce, string? codeVerifier) =>
        new(linkId, organizationId, service, nonce, codeVerifier, Truncate(_clock.GetUtcNow() + Lifetime));

    public string Protect(CoordinatorOAuthStateKind kind, CoordinatorOAuthState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = new Payload(
            state.LinkId,
            state.OrganizationId,
            WireNames<CoordinatorService>.ToWire(state.Service),
            state.Nonce,
            kind == CoordinatorOAuthStateKind.Consent ? state.CodeVerifier : null,
            state.ExpiresAt.ToUnixTimeSeconds());
        return Protector(kind).Protect(JsonSerializer.Serialize(payload, PayloadJson));
    }

    /// <summary>The state <paramref name="text"/> says, when it is a valid, unexpired <paramref name="kind"/> state of
    /// <paramref name="service"/>; otherwise <see langword="null"/>.</summary>
    public CoordinatorOAuthState? TryRead(CoordinatorOAuthStateKind kind, string? text, CoordinatorService service)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
        {
            return null;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Protector(kind).Unprotect(text.Trim()), PayloadJson);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        {
            return null;
        }

        if (payload is null
            || payload.LinkId == Guid.Empty
            || payload.OrganizationId == Guid.Empty
            || payload.Service != WireNames<CoordinatorService>.ToWire(service)
            || payload.Nonce is not { Length: > 0 })
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
        return _clock.GetUtcNow() >= expiresAt
            ? null
            : new CoordinatorOAuthState(payload.LinkId, payload.OrganizationId, service, payload.Nonce, payload.CodeVerifier, expiresAt);
    }

    private IDataProtector Protector(CoordinatorOAuthStateKind kind) =>
        kind == CoordinatorOAuthStateKind.Consent ? _consent : _selection;

    private static DateTimeOffset Truncate(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());

    private sealed record Payload(
        [property: JsonPropertyName("l")] Guid LinkId,
        [property: JsonPropertyName("o")] Guid OrganizationId,
        [property: JsonPropertyName("s")] string Service,
        [property: JsonPropertyName("n")] string Nonce,
        [property: JsonPropertyName("p")] string? CodeVerifier,
        [property: JsonPropertyName("e")] long ExpiresAt);
}

/// <summary>A provider's choices waiting for the owner (#377), with what it needs to finish.</summary>
public sealed record CoordinatorOAuthPendingSelection(
    Guid OrganizationId,
    CoordinatorService Service,
    string Nonce,
    IReadOnlyList<CoordinatorOAuthChoice> Choices,
    CoordinatorOAuthPending Pending,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"{nameof(CoordinatorOAuthPendingSelection)} {{ OrganizationId = {OrganizationId}, Service = {Service}, Choices = {Choices.Count} }}";
}

/// <summary>
/// The selection forms open now (#377), by link, in this process's memory only — like the 統籌助理's other short-lived
/// state (<see cref="CoordinatorReplyTokens"/>): a restart, or a second Api instance, loses them and the owner asks for a
/// new link. Each lasts <see cref="CoordinatorOAuthStates.Lifetime"/> and is taken once.
/// </summary>
public sealed class CoordinatorOAuthSelections
{
    private readonly ConcurrentDictionary<Guid, CoordinatorOAuthPendingSelection> _pending = new();
    private readonly TimeProvider _clock;

    public CoordinatorOAuthSelections(TimeProvider clock)
    {
        _clock = clock;
    }

    public void Keep(Guid linkId, CoordinatorOAuthPendingSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Prune();
        _pending[linkId] = selection;
    }

    /// <summary>The selection of <paramref name="linkId"/> while it lasts.</summary>
    public CoordinatorOAuthPendingSelection? Find(Guid linkId)
    {
        Prune();
        return _pending.TryGetValue(linkId, out var selection) ? selection : null;
    }

    /// <summary>Takes <paramref name="selection"/> (found with <see cref="Find"/>); <see langword="false"/> when another
    /// request took it first.</summary>
    public bool TryTake(Guid linkId, CoordinatorOAuthPendingSelection selection) =>
        _pending.TryRemove(new KeyValuePair<Guid, CoordinatorOAuthPendingSelection>(linkId, selection));

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in _pending)
        {
            if (now >= entry.Value.ExpiresAt)
            {
                _pending.TryRemove(entry);
            }
        }
    }
}
