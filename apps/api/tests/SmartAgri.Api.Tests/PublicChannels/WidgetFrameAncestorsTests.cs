using Shouldly;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Assistants;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>How a website channel becomes <c>frame-ancestors</c> sources (M5a plan §3 B; #201). No database.</summary>
public class WidgetFrameAncestorsTests
{
    [Theory]
    [InlineData(WebsiteChannelState.Published)]
    [InlineData(WebsiteChannelState.Paused)]
    public void A_published_or_paused_channel_lists_each_domain_over_https(WebsiteChannelState state)
    {
        WidgetFrameAncestors.For(new PublicWebsiteChannel(state, ["a.example", "b.example"]), allowLocalhost: false)
            .ShouldBe("https://a.example https://b.example");
    }

    [Fact]
    public void Localhost_comes_last_and_only_when_allowed()
    {
        var channel = new PublicWebsiteChannel(WebsiteChannelState.Published, ["a.example"]);

        WidgetFrameAncestors.For(channel, allowLocalhost: true).ShouldBe("https://a.example http://localhost:*");
        WidgetFrameAncestors.For(channel, allowLocalhost: false).ShouldBe("https://a.example");
    }

    [Fact]
    public void Nothing_may_embed_a_missing_channel_a_draft_or_an_empty_list_even_with_localhost_allowed()
    {
        WidgetFrameAncestors.For(null, allowLocalhost: true).ShouldBeNull();
        WidgetFrameAncestors.For(new PublicWebsiteChannel(WebsiteChannelState.Draft, ["a.example"]), allowLocalhost: true).ShouldBeNull();
        WidgetFrameAncestors.For(new PublicWebsiteChannel(WebsiteChannelState.Published, []), allowLocalhost: true).ShouldBeNull();
    }

    [Theory]
    [InlineData("a.example; script-src *")]
    [InlineData("a.example http://evil.example")]
    [InlineData("*.example.com")]
    [InlineData("https://a.example")]
    [InlineData("A.EXAMPLE")]
    [InlineData(" a.example")]
    [InlineData("a.example\r\nSet-Cookie: x=1")]
    [InlineData("")]
    public void A_stored_value_that_is_not_a_plain_lower_case_domain_never_reaches_the_header(string stored)
    {
        var channel = new PublicWebsiteChannel(WebsiteChannelState.Published, [stored, "a.example"]);

        WidgetFrameAncestors.For(channel, allowLocalhost: false).ShouldBe("https://a.example");
        WidgetFrameAncestors.For(new PublicWebsiteChannel(WebsiteChannelState.Published, [stored]), allowLocalhost: false).ShouldBeNull();
    }
}
