using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tests.Authentication;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>Visitor tokens (decision C), the same-origin rule (plan §3 B) and which embedding origins
/// count for installation detection (plan §3 H) — no database needed.</summary>
public class VisitorTokensTests
{
    private readonly TestClock _clock = new();
    private readonly EphemeralDataProtectionProvider _keys = new();

    [Fact]
    public void A_token_carries_a_fresh_visitor_for_one_assistant_and_organization_for_12_hours()
    {
        var tokens = new VisitorTokens(_keys, _clock);
        var assistantId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();

        var (first, expiresAt) = tokens.Issue(assistantId, organizationId);
        var (second, _) = tokens.Issue(assistantId, organizationId);

        var claims = tokens.TryRead(first).ShouldNotBeNull();
        (claims.AssistantId, claims.OrganizationId, claims.ExpiresAt).ShouldBe((assistantId, organizationId, expiresAt));
        (claims.ExpiresAt - claims.IssuedAt).ShouldBe(VisitorTokens.Lifetime);
        claims.VisitorId.ShouldNotBe(Guid.Empty);
        tokens.TryRead(second).ShouldNotBeNull().VisitorId.ShouldNotBe(claims.VisitorId, "every session is a new visitor");
        first.ShouldNotContain(assistantId.ToString(), Case.Insensitive, "the payload is encrypted");
    }

    [Fact]
    public void Expired_tampered_foreign_or_malformed_tokens_are_invalid()
    {
        var tokens = new VisitorTokens(_keys, _clock);
        var (token, _) = tokens.Issue(Guid.NewGuid(), Guid.NewGuid());

        var middle = token.Length / 2;
        tokens.TryRead(token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..]).ShouldBeNull();
        tokens.TryRead(token + "x").ShouldBeNull();
        tokens.TryRead("not a token").ShouldBeNull();
        tokens.TryRead("").ShouldBeNull();
        tokens.TryRead(null).ShouldBeNull();

        // Another deployment's key ring, or the same keys under another purpose.
        new VisitorTokens(new EphemeralDataProtectionProvider(), _clock).TryRead(token).ShouldBeNull();
        var otherPurpose = _keys.CreateProtector("SmartAgri.Something.Else").Protect("{}");
        tokens.TryRead(otherPurpose).ShouldBeNull();

        _clock.Advance(VisitorTokens.Lifetime - TimeSpan.FromSeconds(5));
        tokens.TryRead(token).ShouldNotBeNull("still inside 12 hours");
        _clock.Advance(TimeSpan.FromSeconds(10));
        tokens.TryRead(token).ShouldBeNull("12 hours have passed");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://api.example.org", true)]
    [InlineData("HTTP://API.EXAMPLE.ORG:80", true)]
    [InlineData("https://assistant.example.org", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://api.example.org:8080", false)]
    [InlineData("https://api.example.org", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    public void Only_no_origin_or_the_apis_own_origin_is_allowed(string? origin, bool allowed)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(Options.Create(new PublicChannelsOptions { PublicBaseUrl = "https://assistant.example.org/" }))
                .BuildServiceProvider(),
        };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("api.example.org");
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        PublicOriginGuard.IsAllowed(context).ShouldBe(allowed);
    }

    [Fact]
    public void Two_origin_headers_are_refused()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("api.example.org");
        context.Request.Headers.Origin = new(["http://api.example.org", "http://api.example.org"]);

        PublicOriginGuard.IsAllowed(context).ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://shop.example.com", "shop.example.com")]
    [InlineData("https://Shop.Example.com/", "shop.example.com")]
    [InlineData("https://shop.example.com:443", "shop.example.com")]
    [InlineData("http://shop.example.com", null)]
    [InlineData("https://shop.example.com:8443", null)]
    [InlineData("https://shop.example.com/page", null)]
    [InlineData("https://user@shop.example.com", null)]
    [InlineData("https://shop.example.com/?a=1", null)]
    [InlineData("shop.example.com", null)]
    [InlineData(null, null)]
    public void Installation_detection_counts_only_https_origins_on_the_default_port(string? host, string? domain)
    {
        VisitorSessionEndpoints.EmbeddingDomain(host).ShouldBe(domain);
    }
}
