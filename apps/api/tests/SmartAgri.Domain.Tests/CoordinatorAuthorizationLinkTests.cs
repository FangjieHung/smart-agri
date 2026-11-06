using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>FB and Google authorization links (#377): random tokens stored as their hash, single use, a limited validity.</summary>
public class CoordinatorAuthorizationLinkTests
{
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000d1");
    private static readonly DateTimeOffset At = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Tokens_are_43_random_base64url_characters_stored_as_their_SHA256()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => AuthorizationLinkTokens.Generate()).ToList();

        tokens.ShouldAllBe(token => AuthorizationLinkTokens.IsToken(token));
        tokens.Distinct().Count().ShouldBe(tokens.Count);
        var hash = AuthorizationLinkTokens.Hash(tokens[0]);
        hash.Length.ShouldBe(CoordinatorAuthorizationLink.TokenHashLength);
        hash.ShouldBe(hash.ToLowerInvariant());
        hash.ShouldNotContain(tokens[0]);
        AuthorizationLinkTokens.Hash(tokens[0]).ShouldBe(hash, "the same token always has the same hash");

        foreach (var notToken in new[] { null, "", tokens[0][..42], tokens[0] + "A", tokens[0][..42] + "=", tokens[0][..42] + "/", " " + tokens[0][..42] })
        {
            AuthorizationLinkTokens.IsToken(notToken).ShouldBeFalse(notToken ?? "null");
        }

        Should.Throw<ArgumentException>(() => AuthorizationLinkTokens.Hash("short"));
    }

    [Fact]
    public void A_link_is_usable_once_until_it_expires_and_never_shows_its_hash()
    {
        var hash = AuthorizationLinkTokens.Hash(AuthorizationLinkTokens.Generate());

        var link = CoordinatorAuthorizationLink.Issue(
            Organization, CoordinatorService.Facebook, CoordinatorAuthorizationPurpose.Initial, hash.ToUpperInvariant(), At, CoordinatorAuthorizationLink.DefaultValidity);

        link.ShouldSatisfyAllConditions(
            row => row.Id.ShouldNotBe(Guid.Empty),
            row => row.TokenHash.ShouldBe(hash),
            row => row.CreatedAt.ShouldBe(At.ToUniversalTime()),
            row => row.CreatedAt.Offset.ShouldBe(TimeSpan.Zero),
            row => (row.ExpiresAt - row.CreatedAt).ShouldBe(TimeSpan.FromHours(72)),
            row => row.UsedAt.ShouldBeNull());
        link.IsUsable(At.AddHours(71)).ShouldBeTrue();
        link.IsUsable(At.AddHours(72)).ShouldBeFalse();
        link.ToString().ShouldNotContain(hash);
        CoordinatorAuthorizationLink.MaxValidity.ShouldBe(TimeSpan.FromHours(168));
        WireNames<CoordinatorAuthorizationPurpose>.All.ShouldBe(["initial", "reauthorize"]);
        CoordinatorAuthorizationLink.IsLinkService(CoordinatorService.Google).ShouldBeTrue();
        CoordinatorAuthorizationLink.IsLinkService(CoordinatorService.Line).ShouldBeFalse();
    }

    [Fact]
    public void Bad_arguments_are_refused()
    {
        var hash = AuthorizationLinkTokens.Hash(AuthorizationLinkTokens.Generate());
        var validity = CoordinatorAuthorizationLink.DefaultValidity;
        const CoordinatorAuthorizationPurpose initial = CoordinatorAuthorizationPurpose.Initial;

        Should.Throw<ArgumentException>(() => CoordinatorAuthorizationLink.Issue(Guid.Empty, CoordinatorService.Google, initial, hash, At, validity));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorAuthorizationLink.Issue(Organization, CoordinatorService.Line, initial, hash, At, validity));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorAuthorizationLink.Issue(Organization, (CoordinatorService)9, initial, hash, At, validity));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorAuthorizationLink.Issue(Organization, CoordinatorService.Google, (CoordinatorAuthorizationPurpose)5, hash, At, validity));
        Should.Throw<ArgumentException>(() => CoordinatorAuthorizationLink.Issue(Organization, CoordinatorService.Google, initial, hash[..63], At, validity));
        Should.Throw<ArgumentException>(() => CoordinatorAuthorizationLink.Issue(Organization, CoordinatorService.Google, initial, new string('g', 64), At, validity));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorAuthorizationLink.Issue(Organization, CoordinatorService.Google, initial, hash, At, TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorAuthorizationLink.Issue(Organization, CoordinatorService.Google, initial, hash, At, TimeSpan.FromHours(169)));
        CoordinatorAuthorizationLink.Issue(Organization, CoordinatorService.Google, CoordinatorAuthorizationPurpose.Reauthorize, hash, At, TimeSpan.FromHours(168))
            .Purpose.ShouldBe(CoordinatorAuthorizationPurpose.Reauthorize);
    }
}
