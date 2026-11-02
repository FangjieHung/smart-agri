using System.Collections.Concurrent;
using Microsoft.AspNetCore.WebUtilities;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// A stand-in for the FB (#379) and Google (#378) providers of the authorization links (#377): its consent screen is
/// <c>https://fake-oauth.test/{service}/authorize?…</c> (never requested — the test plays the owner and the service), and
/// each code a test <see cref="Expect"/>s answers with its outcome; any other code is <c>invalid-grant</c>. A selection
/// answers the pending data and the chosen id as the credential. Every exchange and selection is recorded. A test may
/// <see cref="ForwardTo"/> the real provider (FB's, #379) for a while: every call then goes to it.
/// </summary>
public sealed class FakeOAuthProvider(CoordinatorService service, bool usesPkce) : ICoordinatorOAuthProvider
{
    public const string ConsentHost = "fake-oauth.test";

    private readonly ConcurrentDictionary<string, Func<CoordinatorOAuthCodeExchange, CoordinatorOAuthOutcome>> _codes = new(StringComparer.Ordinal);
    private volatile bool _configured = true;
    private volatile ICoordinatorOAuthProvider? _forwardTo;

    public CoordinatorService Service => service;

    /// <summary>Settable by a test (and set back): an unconfigured provider is as if its settings were blank.</summary>
    public bool Configured
    {
        get => _configured;
        set => _configured = value;
    }

    /// <summary>Settable by a test (and set back): the provider every call goes to instead, e.g. the real FB provider
    /// resolved from a scope the test keeps open (#379).</summary>
    public ICoordinatorOAuthProvider? ForwardTo
    {
        get => _forwardTo;
        set => _forwardTo = value;
    }

    public bool IsConfigured => _forwardTo?.IsConfigured ?? _configured;

    public bool UsesPkce => _forwardTo?.UsesPkce ?? usesPkce;

    public ConcurrentQueue<CoordinatorOAuthCodeExchange> Exchanges { get; } = new();

    public ConcurrentQueue<(string Pending, string Choice)> Selections { get; } = new();

    public void Expect(string code, Func<CoordinatorOAuthCodeExchange, CoordinatorOAuthOutcome> outcome) => _codes[code] = outcome;

    public Uri ConsentUrl(CoordinatorOAuthConsent consent)
    {
        if (_forwardTo is { } real)
        {
            return real.ConsentUrl(consent);
        }

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = "fake-client",
            ["redirect_uri"] = consent.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "pages_manage_posts",
            ["state"] = consent.State,
        };
        if (consent.CodeChallenge is { } challenge)
        {
            query["code_challenge"] = challenge;
            query["code_challenge_method"] = "S256";
        }

        return new Uri(QueryHelpers.AddQueryString($"https://{ConsentHost}/{WireNames<CoordinatorService>.ToWire(service)}/authorize", query));
    }

    public Task<CoordinatorOAuthOutcome> ExchangeAsync(CoordinatorOAuthCodeExchange exchange, CancellationToken cancellationToken)
    {
        Exchanges.Enqueue(exchange);
        if (_forwardTo is { } real)
        {
            return real.ExchangeAsync(exchange, cancellationToken);
        }

        return Task.FromResult(_codes.TryGetValue(exchange.Code, out var outcome) ? outcome(exchange) : CoordinatorOAuthOutcome.Failed("invalid-grant"));
    }

    public Task<CoordinatorOAuthOutcome> SelectAsync(CoordinatorOAuthPending pending, string choiceId, CancellationToken cancellationToken)
    {
        Selections.Enqueue((pending.Data, choiceId));
        if (_forwardTo is { } real)
        {
            return real.SelectAsync(pending, choiceId, cancellationToken);
        }

        return Task.FromResult(CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential($"{pending.Data}:{choiceId}", choiceId, $"地點 {choiceId}")));
    }
}
