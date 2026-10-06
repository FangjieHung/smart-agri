using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
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
        Validator().Validate(null, options).Succeeded.ShouldBeTrue();
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
        Validator().Validate(null, options).Succeeded.ShouldBeTrue();
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
        Validator().Validate(null, options).Failed.ShouldBeTrue();
    }

    // --- DefaultMonthlyTokenLimit (#195) ---------------------------------------------------------

    [Fact]
    public void The_default_monthly_token_limit_is_two_million()
    {
        new PublicChannelsOptions().EffectiveDefaultMonthlyTokenLimit.ShouldBe(2_000_000);
        Bind(null).EffectiveDefaultMonthlyTokenLimit.ShouldBe(2_000_000);
    }

    [Fact]
    public void A_blank_setting_binds_as_unset_like_an_empty_env_value_in_compose()
    {
        // deploy/docker-compose.yml passes DEFAULT_MONTHLY_TOKEN_LIMIT as "${DEFAULT_MONTHLY_TOKEN_LIMIT:-}".
        var options = Bind("");

        options.DefaultMonthlyTokenLimit.ShouldBeNull();
        options.EffectiveDefaultMonthlyTokenLimit.ShouldBe(2_000_000);
        Validator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("5000000", 5_000_000L)]
    public void A_configured_limit_is_bound(string value, long expected)
    {
        var options = Bind(value);

        options.EffectiveDefaultMonthlyTokenLimit.ShouldBe(expected);
        Validator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_negative_limit_fails_startup_and_says_which_setting()
    {
        var result = Validator().Validate(null, Bind("-1"));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("PublicChannels:DefaultMonthlyTokenLimit");
    }

    [Fact]
    public void A_non_numeric_limit_fails_binding_so_startup_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Bind("lots"));
    }

    // --- AllowLocalhostAncestors (#201) ----------------------------------------------------------

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Localhost_ancestors_are_allowed_in_development_and_testing(string environment)
    {
        Validator(environment).Validate(null, new PublicChannelsOptions { AllowLocalhostAncestors = true }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Localhost_ancestors_refuse_to_start_anywhere_else_and_say_why(string environment)
    {
        var result = Validator(environment).Validate(null, new PublicChannelsOptions { AllowLocalhostAncestors = true });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("PublicChannels:AllowLocalhostAncestors=true is only allowed in the Development and Testing environments");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Without_localhost_ancestors_every_environment_starts(string environment)
    {
        Validator(environment).Validate(null, new PublicChannelsOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Localhost_ancestors_default_to_off_and_a_blank_setting_binds_as_off()
    {
        new PublicChannelsOptions().AllowLocalhostAncestors.ShouldBeFalse();
        Bind(null).AllowLocalhostAncestors.ShouldBeFalse();
    }

    private static PublicChannelsOptions.Validator Validator(string environment = "Development") =>
        new(new HostingEnvironment { EnvironmentName = environment });

    private static PublicChannelsOptions Bind(string? limit)
    {
        var settings = new Dictionary<string, string?>();
        if (limit is not null)
        {
            settings["PublicChannels:DefaultMonthlyTokenLimit"] = limit;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build()
            .GetSection(PublicChannelsOptions.SectionName)
            .Get<PublicChannelsOptions>(binder => binder.ErrorOnUnknownConfiguration = false) ?? new PublicChannelsOptions();
    }
}
