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

    // --- RateLimits and TrustedProxies (#197) ------------------------------------------------------

    [Fact]
    public void Rate_limits_default_to_the_values_of_decision_C_and_need_no_setting()
    {
        foreach (var options in new[] { new PublicChannelsOptions(), BindSettings([]) })
        {
            options.RateLimits.SessionsPerIpPerMinute.ShouldBe(10);
            options.RateLimits.RunsPerVisitorPerMinute.ShouldBe(6);
            options.RateLimits.RunsPerVisitorPerHour.ShouldBe(60);
            options.RateLimits.RunsPerIpPerMinute.ShouldBe(20);
            options.RateLimits.RunsPerAssistantPerMinute.ShouldBe(120);
            options.RateLimits.MaxConcurrentRunsPerAssistant.ShouldBe(10);
            options.TrustedProxies.ShouldBeEmpty();
            Validator("Production").Validate(null, options).Succeeded.ShouldBeTrue();
        }
    }

    [Fact]
    public void Rate_limits_are_bound_from_the_section()
    {
        var options = BindSettings(new Dictionary<string, string?>
        {
            ["PublicChannels:RateLimits:SessionsPerIpPerMinute"] = "3",
            ["PublicChannels:RateLimits:RunsPerVisitorPerMinute"] = "4",
            ["PublicChannels:RateLimits:RunsPerVisitorPerHour"] = "5",
            ["PublicChannels:RateLimits:RunsPerIpPerMinute"] = "6",
            ["PublicChannels:RateLimits:RunsPerAssistantPerMinute"] = "7",
            ["PublicChannels:RateLimits:MaxConcurrentRunsPerAssistant"] = "8",
        });

        options.RateLimits.Values().Select(value => value.Value).ShouldBe([3, 4, 5, 6, 7, 8]);
        Validator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("SessionsPerIpPerMinute", "0")]
    [InlineData("RunsPerVisitorPerMinute", "-1")]
    [InlineData("RunsPerVisitorPerHour", "0")]
    [InlineData("RunsPerIpPerMinute", "-5")]
    [InlineData("RunsPerAssistantPerMinute", "0")]
    [InlineData("MaxConcurrentRunsPerAssistant", "0")]
    public void A_rate_limit_below_one_fails_startup_and_says_which_setting(string name, string value)
    {
        var result = Validator("Production").Validate(null, BindSettings(new Dictionary<string, string?> { [$"PublicChannels:RateLimits:{name}"] = value }));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"PublicChannels:RateLimits:{name} {value} must be 1 or more");
    }

    [Fact]
    public void A_non_numeric_rate_limit_fails_binding_so_startup_is_refused()
    {
        Should.Throw<InvalidOperationException>(() =>
            BindSettings(new Dictionary<string, string?> { ["PublicChannels:RateLimits:RunsPerIpPerMinute"] = "many" }));
    }

    [Fact]
    public void Trusted_proxies_accept_addresses_and_networks_in_separate_entries_or_one_list()
    {
        var options = BindSettings(new Dictionary<string, string?>
        {
            ["PublicChannels:TrustedProxies:0"] = "10.0.0.5",
            ["PublicChannels:TrustedProxies:1"] = "172.16.0.0/12, 192.168.1.0/24",
            ["PublicChannels:TrustedProxies:2"] = "::1;fd00::/8",
        });

        var (addresses, networks, invalid) = options.ParseTrustedProxies();

        addresses.Select(address => address.ToString()).ShouldBe(["10.0.0.5", "::1"]);
        networks.Select(network => network.ToString()).ShouldBe(["172.16.0.0/12", "192.168.1.0/24", "fd00::/8"]);
        invalid.ShouldBeEmpty();
        Validator("Production").Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_blank_trusted_proxies_value_binds_as_none_like_an_empty_env_value_in_compose()
    {
        // deploy/docker-compose.yml passes TRUSTED_PROXIES as "PublicChannels__TrustedProxies__0: ${TRUSTED_PROXIES:-}".
        var options = BindSettings(new Dictionary<string, string?> { ["PublicChannels:TrustedProxies:0"] = "" });

        var (addresses, networks, invalid) = options.ParseTrustedProxies();

        (addresses.Count + networks.Count + invalid.Count).ShouldBe(0);
        Validator("Production").Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("proxy.example.org")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.256")]
    [InlineData("*")]
    public void A_trusted_proxy_that_is_neither_an_address_nor_a_network_fails_startup_and_names_the_entry(string entry)
    {
        var options = BindSettings(new Dictionary<string, string?>
        {
            ["PublicChannels:TrustedProxies:0"] = "10.0.0.0/8",
            ["PublicChannels:TrustedProxies:1"] = entry,
        });

        var result = Validator("Production").Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"PublicChannels:TrustedProxies entry '{entry}'");
    }

    private static PublicChannelsOptions BindSettings(IEnumerable<KeyValuePair<string, string?>> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build()
            .GetSection(PublicChannelsOptions.SectionName)
            .Get<PublicChannelsOptions>(binder => binder.ErrorOnUnknownConfiguration = false) ?? new PublicChannelsOptions();

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
