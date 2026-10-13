using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Notifications;
using SmartAgri.Infrastructure.Notifications;

namespace SmartAgri.Api.Tests.Notifications;

/// <summary>
/// <c>Smtp</c> (#355): optional — no host means Email notifications are off and startup is unaffected,
/// including the blank values compose passes for unset <c>.env</c> entries; a host makes the rest
/// required and checked. No database needed.
/// </summary>
public sealed class SmtpOptionsTests
{
    private static SmtpOptions Bind(Dictionary<string, string?> values)
    {
        var options = new SmtpOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(SmtpOptions.SectionName).Bind(options);
        return options;
    }

    private static Dictionary<string, string?> Gmail(Action<Dictionary<string, string?>>? change = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.gmail.com",
            ["Smtp:Port"] = "587",
            ["Smtp:Username"] = "shop@gmail.com",
            ["Smtp:Password"] = "abcd efgh ijkl mnop",
            ["Smtp:From"] = "shop@gmail.com",
        };
        change?.Invoke(values);
        return values;
    }

    [Fact]
    public void Blank_values_like_unset_env_entries_in_compose_bind_as_unset_and_turn_Email_off()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "", ["Smtp:Port"] = "", ["Smtp:Username"] = "", ["Smtp:Password"] = "",
            ["Smtp:From"] = "", ["Smtp:FromName"] = "", ["Smtp:UseStartTls"] = "", ["Smtp:TimeoutSeconds"] = "",
        });

        options.IsConfigured.ShouldBeFalse();
        options.Validate("Production").ShouldBeNull();
        options.EffectivePort.ShouldBe(587);
        options.EffectiveUseStartTls.ShouldBeTrue();
    }

    [Fact]
    public void A_Gmail_configuration_is_valid_in_Production()
    {
        var options = Bind(Gmail());

        options.IsConfigured.ShouldBeTrue();
        options.Validate("Production").ShouldBeNull();
        options.EffectiveTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.EffectiveFromName.ShouldBe("AI 助理工作台");
    }

    [Theory]
    [InlineData("Smtp:From", "", "Smtp:From")]
    [InlineData("Smtp:From", "Shop <shop@gmail.com>", "Smtp:From")]
    [InlineData("Smtp:Port", "465", "465")]
    [InlineData("Smtp:Port", "70000", "Smtp:Port")]
    [InlineData("Smtp:Password", "", "set together")]
    [InlineData("Smtp:Host", "smtp://smtp.gmail.com", "Smtp:Host")]
    [InlineData("Smtp:TimeoutSeconds", "0", "Smtp:TimeoutSeconds")]
    [InlineData("Smtp:UseStartTls", "false", "unencrypted")]
    public void A_configured_host_needs_a_sender_a_submission_port_and_both_or_no_credentials(string key, string value, string expected)
    {
        var options = Bind(Gmail(values => values[key] = value));

        options.Validate("Production").ShouldNotBeNull().ShouldContain(expected);
    }

    [Fact]
    public void A_password_without_TLS_is_only_for_Development_and_Testing()
    {
        var options = Bind(Gmail(values => values["Smtp:UseStartTls"] = "false"));

        options.Validate("Development").ShouldBeNull();
        options.Validate("Testing").ShouldBeNull();
        options.Validate("Staging").ShouldNotBeNull();
    }

    [Fact]
    public async Task An_invalid_setting_refuses_to_start_and_names_it()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Smtp:Host", "smtp.gmail.com");
            builder.UseSetting("Smtp:From", "not an address");
        });

        var failure = StartupFailure.Of(factory);

        failure.ShouldNotBeNull().Message.ShouldContain("Smtp:From");
    }

    [Fact]
    public async Task Without_SMTP_the_Api_starts_and_its_sender_says_it_is_not_configured()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString));

        factory.Services.GetRequiredService<IEmailSender>().IsConfigured.ShouldBeFalse();
    }
}
