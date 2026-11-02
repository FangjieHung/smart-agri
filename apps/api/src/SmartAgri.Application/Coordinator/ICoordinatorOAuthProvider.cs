using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// One external service a shop's owner authorizes through an authorization link (store-coordinator design §3 Q12, §5
/// Slices 6–8, #377): FB (<see cref="CoordinatorService.Facebook"/>, #379) or Google (<see cref="CoordinatorService.Google"/>,
/// #378). The authorization flow (<c>/api/v1/coordinator/connect?token=…</c> and the callback) is the same for every
/// provider; a provider builds the consent URL, exchanges the callback's code and — when the owner has several pages
/// or locations — finishes with the one they chose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not configured</b> (<see cref="IsConfigured"/> false: its platform settings, e.g. <c>Coordinator:Facebook:AppId</c>,
/// are not set) means links for it cannot be made and its callback answers <c>404</c>. Registered with
/// <c>services.AddSingleton&lt;ICoordinatorOAuthProvider, …&gt;()</c> (or scoped); the last one registered for a service wins.
/// </para>
/// <para>
/// <b>Secrets</b>: the code, the code verifier, <see cref="CoordinatorOAuthCredential.Secret"/> and
/// <see cref="CoordinatorOAuthPending"/> must never be logged or put in an exception message; a failure is a short code
/// of ours (<see cref="CoordinatorOAuthOutcome.Failed"/>), never the service's error text.
/// </para>
/// </remarks>
public interface ICoordinatorOAuthProvider
{
    /// <summary><see cref="CoordinatorService.Facebook"/> or <see cref="CoordinatorService.Google"/>.</summary>
    CoordinatorService Service { get; }

    /// <summary>Its platform settings are set (no external call).</summary>
    bool IsConfigured { get; }

    /// <summary>Whether the flow sends a PKCE challenge (<c>S256</c>) and the exchange its verifier.</summary>
    bool UsesPkce { get; }

    /// <summary>The service's consent screen for <paramref name="consent"/> (scopes, client id, redirect URI, state and
    /// the PKCE challenge when <see cref="UsesPkce"/>).</summary>
    Uri ConsentUrl(CoordinatorOAuthConsent consent);

    /// <summary>Exchanges the callback's code: the credential to store, or the pages or locations to choose from.</summary>
    Task<CoordinatorOAuthOutcome> ExchangeAsync(CoordinatorOAuthCodeExchange exchange, CancellationToken cancellationToken);

    /// <summary>Finishes after <see cref="CoordinatorOAuthOutcome.NeedsSelection"/>: the credential of the choice
    /// <paramref name="choiceId"/> (always one of the choices offered).</summary>
    Task<CoordinatorOAuthOutcome> SelectAsync(CoordinatorOAuthPending pending, string choiceId, CancellationToken cancellationToken);
}

/// <summary>What the consent URL carries.</summary>
/// <param name="State">The flow's protected state, to come back unchanged on the callback.</param>
/// <param name="RedirectUri">The callback: <c>{public base URL}/api/v1/coordinator/oauth/{service}/callback</c>.</param>
/// <param name="CodeChallenge">The PKCE <c>S256</c> challenge, when the provider uses PKCE.</param>
public sealed record CoordinatorOAuthConsent(string State, string RedirectUri, string? CodeChallenge)
{
    public override string ToString() => $"{nameof(CoordinatorOAuthConsent)} {{ RedirectUri = {RedirectUri} }}";
}

/// <summary>A callback's code to exchange.</summary>
/// <param name="CodeVerifier">The PKCE verifier, when the provider uses PKCE.</param>
public sealed record CoordinatorOAuthCodeExchange(string Code, string RedirectUri, string? CodeVerifier)
{
    public override string ToString() => $"{nameof(CoordinatorOAuthCodeExchange)} {{ RedirectUri = {RedirectUri} }}";
}

/// <summary>What to store: <see cref="Secret"/> (protected before it is stored), whose it is in the service
/// (<see cref="ExternalId"/>, e.g. the page or location id) and a label for people.</summary>
public sealed record CoordinatorOAuthCredential(string Secret, string ExternalId, string DisplayName)
{
    public override string ToString() => $"{nameof(CoordinatorOAuthCredential)} {{ ExternalId = {ExternalId} }}";
}

/// <summary>A page or location the owner may choose (<see cref="Id"/> becomes the connection's external id).</summary>
public sealed record CoordinatorOAuthChoice(string Id, string Label);

/// <summary>What a provider needs to finish after the owner chose (e.g. the user token and each page's token), kept in
/// the server's memory only while the selection form is open. Opaque to the flow; never shown.</summary>
public sealed record CoordinatorOAuthPending(string Data)
{
    public override string ToString() => nameof(CoordinatorOAuthPending);
}

/// <summary>How a provider's step went.</summary>
public enum CoordinatorOAuthOutcomeKind
{
    /// <summary><see cref="CoordinatorOAuthOutcome.Credential"/> is to be stored.</summary>
    Connected,

    /// <summary>The owner chooses one of <see cref="CoordinatorOAuthOutcome.Choices"/> first.</summary>
    NeedsSelection,

    /// <summary>It did not work (<see cref="CoordinatorOAuthOutcome.FailureCode"/>).</summary>
    Failed,
}

/// <summary>A provider's answer to <see cref="ICoordinatorOAuthProvider.ExchangeAsync"/> or
/// <see cref="ICoordinatorOAuthProvider.SelectAsync"/>.</summary>
public sealed class CoordinatorOAuthOutcome
{
    /// <summary>Most choices a selection form offers.</summary>
    public const int MaxChoices = 100;

    /// <summary>Longest choice label shown (longer ones are shortened).</summary>
    public const int ChoiceLabelMaxLength = 100;

    private CoordinatorOAuthOutcome(
        CoordinatorOAuthOutcomeKind kind,
        CoordinatorOAuthCredential? credential,
        IReadOnlyList<CoordinatorOAuthChoice>? choices,
        CoordinatorOAuthPending? pending,
        string? failureCode)
    {
        Kind = kind;
        Credential = credential;
        Choices = choices ?? [];
        Pending = pending;
        FailureCode = failureCode;
    }

    public CoordinatorOAuthOutcomeKind Kind { get; }

    public CoordinatorOAuthCredential? Credential { get; }

    public IReadOnlyList<CoordinatorOAuthChoice> Choices { get; }

    public CoordinatorOAuthPending? Pending { get; }

    /// <summary>1–32 of a-z, 0-9 and '-': a code of ours (e.g. <c>invalid-grant</c>, <c>no-pages</c>).</summary>
    public string? FailureCode { get; }

    public static CoordinatorOAuthOutcome Connected(CoordinatorOAuthCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrEmpty(credential.Secret))
        {
            throw new ArgumentException("A credential has a secret.", nameof(credential));
        }

        if (!CoordinatorConnection.IsValidExternalId(credential.ExternalId))
        {
            throw new ArgumentException(
                $"An external id is 1-{CoordinatorConnection.ExternalIdMaxLength} characters without white space.", nameof(credential));
        }

        return new(CoordinatorOAuthOutcomeKind.Connected, credential with { DisplayName = credential.DisplayName ?? string.Empty }, null, null, null);
    }

    /// <summary>The owner chooses one of <paramref name="choices"/> (1 to <see cref="MaxChoices"/>, distinct ids that
    /// are valid external ids; labels trimmed and shortened, a blank one replaced by the id).</summary>
    public static CoordinatorOAuthOutcome NeedsSelection(IReadOnlyList<CoordinatorOAuthChoice> choices, CoordinatorOAuthPending pending)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(pending);
        if (choices.Count is 0 or > MaxChoices)
        {
            throw new ArgumentException($"1 to {MaxChoices} choices.", nameof(choices));
        }

        if (choices.Any(choice => !CoordinatorConnection.IsValidExternalId(choice.Id))
            || choices.Select(choice => choice.Id).Distinct(StringComparer.Ordinal).Count() != choices.Count)
        {
            throw new ArgumentException("Choice ids are distinct, valid external ids.", nameof(choices));
        }

        var cleaned = choices
            .Select(choice =>
            {
                var label = string.IsNullOrWhiteSpace(choice.Label) ? choice.Id : choice.Label.Trim();
                return new CoordinatorOAuthChoice(choice.Id, label.Length <= ChoiceLabelMaxLength ? label : label[..ChoiceLabelMaxLength]);
            })
            .ToList();
        return new(CoordinatorOAuthOutcomeKind.NeedsSelection, null, cleaned, pending, null);
    }

    public static CoordinatorOAuthOutcome Failed(string code) =>
        CoordinatorCommandAction.IsValidFailureCode(code)
            ? new(CoordinatorOAuthOutcomeKind.Failed, null, null, null, code)
            : throw new ArgumentException("A failure code is 1-32 of a-z, 0-9 and '-': a code of ours, never text from outside.", nameof(code));

    /// <summary>No secret, no pending data.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorOAuthOutcome)} {{ Kind = {Kind}, Choices = {Choices.Count}, FailureCode = {FailureCode} }}";
}

/// <summary>
/// The registered <see cref="ICoordinatorOAuthProvider"/>s by service (the last one registered for a service wins).
/// </summary>
public sealed class CoordinatorOAuthProviders
{
    private readonly Dictionary<CoordinatorService, ICoordinatorOAuthProvider> _byService = [];

    public CoordinatorOAuthProviders(IEnumerable<ICoordinatorOAuthProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var provider in providers)
        {
            if (!CoordinatorAuthorizationLink.IsLinkService(provider.Service))
            {
                throw new InvalidOperationException("Only FB and Google are authorized through links; LINE's token is set by the operator.");
            }

            _byService[provider.Service] = provider;
        }
    }

    /// <summary>The provider of <paramref name="service"/> when one is registered and configured; otherwise
    /// <see langword="null"/> (not configured).</summary>
    public ICoordinatorOAuthProvider? For(CoordinatorService service) =>
        _byService.TryGetValue(service, out var provider) && provider.IsConfigured ? provider : null;
}

/// <summary>
/// Asks a shop's owners to authorize again (store-coordinator design §3 Q18, §5 Slice 6, #377): FB or Google refused the
/// stored credential (revoked, password changed), so the action became a reminder. Called when an executor answers
/// <see cref="CoordinatorActionResult.UnavailableBecause"/> <see cref="CoordinatorFailureCodes.TokenRevoked"/> for
/// FB 貼文 or Google 特殊營業時間 (LINE's token stays operator-managed, #376).
/// </summary>
public interface ICoordinatorReauthorization
{
    /// <summary>
    /// Revokes the shop's connection to <paramref name="service"/> (when still active) and — once per revocation — makes a
    /// <see cref="CoordinatorAuthorizationPurpose.Reauthorize"/> link and pushes it to the shop's 老闆 members.
    /// <see langword="true"/> when this call sent it.
    /// </summary>
    Task<bool> RequestAsync(Guid organizationId, CoordinatorService service, CancellationToken cancellationToken);
}

/// <summary>Which service an action kind needs an authorization link for (#377).</summary>
public static class CoordinatorAuthorizations
{
    /// <summary><see cref="CoordinatorService.Facebook"/> for FB 貼文, <see cref="CoordinatorService.Google"/> for Google
    /// 特殊營業時間, otherwise <see langword="null"/> (LINE 群發's token is the operator's; ordering systems have none).</summary>
    public static CoordinatorService? ServiceOf(ShopActionKind kind) => kind switch
    {
        ShopActionKind.FacebookPost => CoordinatorService.Facebook,
        ShopActionKind.GoogleSpecialHours => CoordinatorService.Google,
        _ => null,
    };

    /// <summary>How people call <paramref name="service"/>'s account: 「FB 粉專」, 「Google 商家」, 「LINE 官方帳號」.</summary>
    public static string Label(CoordinatorService service) => service switch
    {
        CoordinatorService.Facebook => "FB 粉專",
        CoordinatorService.Google => "Google 商家",
        CoordinatorService.Line => "LINE 官方帳號",
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Not a coordinator service."),
    };
}
