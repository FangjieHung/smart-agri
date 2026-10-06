using Shouldly;
using SmartAgri.Api.PublicChannels;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary><c>PublicChannels:PublicBaseUrl</c> and the embed code built from it (M5a plan §3 H; #194).</summary>
public class PublicChannelsOptionsTests
{
    private static readonly Guid AssistantId = Guid.Parse("01a10194-0000-7000-8000-000000000001");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_public_base_url_there_is_no_embed_code_and_startup_is_fine(string? value)
    {
        var options = new PublicChannelsOptions { PublicBaseUrl = value };

        options.ResolvedPublicBaseUrl.ShouldBeNull();
        options.EmbedCode(AssistantId).ShouldBeNull();
        new PublicChannelsOptions.Validator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://assistant.example.org", "https://assistant.example.org")]
    [InlineData("https://assistant.example.org/", "https://assistant.example.org")]
    [InlineData(" http://localhost:5153 ", "http://localhost:5153")]
    [InlineData("https://example.org/smart-agri/", "https://example.org/smart-agri")]
    public void The_embed_code_loads_embed_js_from_the_public_base_url(string value, string expectedBase)
    {
        var options = new PublicChannelsOptions { PublicBaseUrl = value };

        options.ResolvedPublicBaseUrl.ShouldBe(expectedBase);
        options.EmbedCode(AssistantId).ShouldBe(
            $"<script src=\"{expectedBase}/embed.js\" data-assistant=\"01a10194-0000-7000-8000-000000000001\" async></script>");
        new PublicChannelsOptions.Validator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("assistant.example.org")]
    [InlineData("ftp://assistant.example.org")]
    [InlineData("https://assistant.example.org/?a=1")]
    [InlineData("https://assistant.example.org/#x")]
    [InlineData("https://user:pass@assistant.example.org")]
    public void A_malformed_public_base_url_fails_startup(string value)
    {
        var options = new PublicChannelsOptions { PublicBaseUrl = value };

        options.EmbedCode(AssistantId).ShouldBeNull();
        new PublicChannelsOptions.Validator().Validate(null, options).Failed.ShouldBeTrue();
    }
}
