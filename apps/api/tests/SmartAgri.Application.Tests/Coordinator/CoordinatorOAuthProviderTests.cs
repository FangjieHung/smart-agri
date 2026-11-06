using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// The provider seam of the FB and Google authorization links (#377): what a provider may answer (a credential with a
/// valid external id, 1–100 distinct choices, a short code of ours), that none of it shows a secret, and which provider
/// counts as configured.
/// </summary>
public class CoordinatorOAuthProviderTests
{
    private const string Secret = "EAAG-page-token-never-shown";

    [Fact]
    public void Outcomes_check_what_a_provider_answers_and_never_show_a_secret()
    {
        var connected = CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential(Secret, "1234567890", "王記麵店"));
        connected.Kind.ShouldBe(CoordinatorOAuthOutcomeKind.Connected);
        connected.ToString().ShouldNotContain(Secret);
        connected.Credential!.ToString().ShouldNotContain(Secret);

        var pending = new CoordinatorOAuthPending(Secret);
        var selection = CoordinatorOAuthOutcome.NeedsSelection(
            [new("p-1", "  王記麵店  "), new("p-2", " "), new("p-3", new string('名', 150))], pending);
        selection.Choices.Select(choice => choice.Label).ShouldBe(["王記麵店", "p-2", new string('名', CoordinatorOAuthOutcome.ChoiceLabelMaxLength)]);
        selection.Pending.ShouldBeSameAs(pending);
        selection.ToString().ShouldNotContain(Secret);
        pending.ToString().ShouldNotContain(Secret);
        new CoordinatorOAuthCodeExchange("code-never-shown", "https://example.org/cb", "verifier-never-shown").ToString()
            .ShouldSatisfyAllConditions(text => text.ShouldNotContain("code-never-shown"), text => text.ShouldNotContain("verifier-never-shown"));
        new CoordinatorOAuthConsent("state-never-shown", "https://example.org/cb", "challenge").ToString().ShouldNotContain("state-never-shown");

        CoordinatorOAuthOutcome.Failed("invalid-grant").FailureCode.ShouldBe("invalid-grant");
        Should.Throw<ArgumentException>(() => CoordinatorOAuthOutcome.Failed("The user denied: (#200) Permissions error"));
        Should.Throw<ArgumentException>(() => CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential("", "1", "x")));
        Should.Throw<ArgumentException>(() => CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential(Secret, "has space", "x")));
        Should.Throw<ArgumentException>(() => CoordinatorOAuthOutcome.NeedsSelection([], pending));
        Should.Throw<ArgumentException>(() => CoordinatorOAuthOutcome.NeedsSelection([new("a", "A"), new("a", "B")], pending));
        Should.Throw<ArgumentException>(() => CoordinatorOAuthOutcome.NeedsSelection([new(new string('9', 65), "A")], pending));
        Should.Throw<ArgumentException>(() => CoordinatorOAuthOutcome.NeedsSelection(
            [.. Enumerable.Range(0, CoordinatorOAuthOutcome.MaxChoices + 1).Select(i => new CoordinatorOAuthChoice($"p-{i}", "x"))], pending));
    }

    [Fact]
    public void Only_a_configured_provider_counts_and_the_last_registered_wins()
    {
        var unconfigured = new Provider(CoordinatorService.Google, configured: false);
        var first = new Provider(CoordinatorService.Facebook, configured: true);
        var second = new Provider(CoordinatorService.Facebook, configured: true);

        var providers = new CoordinatorOAuthProviders([unconfigured, first, second]);

        providers.For(CoordinatorService.Facebook).ShouldBeSameAs(second);
        providers.For(CoordinatorService.Google).ShouldBeNull();
        providers.For(CoordinatorService.Line).ShouldBeNull();
        new CoordinatorOAuthProviders([]).For(CoordinatorService.Facebook).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => new CoordinatorOAuthProviders([new Provider(CoordinatorService.Line, configured: true)]));
        CoordinatorAuthorizations.Label(CoordinatorService.Facebook).ShouldBe("FB 粉專");
        CoordinatorAuthorizations.Label(CoordinatorService.Google).ShouldBe("Google 商家");
    }

    private sealed class Provider(CoordinatorService service, bool configured) : ICoordinatorOAuthProvider
    {
        public CoordinatorService Service => service;

        public bool IsConfigured => configured;

        public bool UsesPkce => false;

        public Uri ConsentUrl(CoordinatorOAuthConsent consent) => new("https://example.org/consent");

        public Task<CoordinatorOAuthOutcome> ExchangeAsync(CoordinatorOAuthCodeExchange exchange, CancellationToken cancellationToken) =>
            Task.FromResult(CoordinatorOAuthOutcome.Failed("unused"));

        public Task<CoordinatorOAuthOutcome> SelectAsync(CoordinatorOAuthPending pending, string choiceId, CancellationToken cancellationToken) =>
            Task.FromResult(CoordinatorOAuthOutcome.Failed("unused"));
    }
}
