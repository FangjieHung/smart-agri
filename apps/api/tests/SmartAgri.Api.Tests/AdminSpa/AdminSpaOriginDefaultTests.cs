using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.AdminSpa;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.AdminSpa;

/// <summary>
/// Which origins <c>migrate</c> registers for the <c>admin-spa</c> client (#306): explicit
/// <c>Authentication:AdminSpa:Origins</c> win; without them, an Api that serves the admin itself
/// (<c>Admin:RootPath</c>) defaults to the origin of <c>PublicChannels:PublicBaseUrl</c>. Read from the
/// host's own registrar, the one <c>migrate</c> resolves. No database needed (nothing is written).
/// </summary>
public sealed class AdminSpaOriginDefaultTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _base;
    private readonly string _adminRoot = Directory.CreateTempSubdirectory("smartagri-admin-origin-").FullName;

    public AdminSpaOriginDefaultTests(WebApplicationFactory<Program> factory)
    {
        _base = factory;
        File.WriteAllText(Path.Combine(_adminRoot, "index.html"), "<!doctype html><title>admin</title>");
    }

    public void Dispose() => Directory.Delete(_adminRoot, recursive: true);

    [Theory]
    // A blank ADMIN_SPA_ORIGIN in deploy/.env binds as one empty entry: the same as none.
    [InlineData("")]
    [InlineData("   ")]
    public void Served_admin_without_origins_defaults_to_the_public_base_url_origin(string blankOrigin)
    {
        var origins = EffectiveOrigins(
            ("Admin:RootPath", _adminRoot),
            ("Authentication:AdminSpa:Origins:0", blankOrigin),
            ("PublicChannels:PublicBaseUrl", "https://assistant.example.org/"));

        origins.ShouldBe(["https://assistant.example.org"]);
    }

    [Fact]
    public void The_default_keeps_a_non_default_port()
    {
        var origins = EffectiveOrigins(
            ("Admin:RootPath", _adminRoot),
            ("Authentication:AdminSpa:Origins:0", ""),
            ("PublicChannels:PublicBaseUrl", "https://localhost:8491"));

        origins.ShouldBe(["https://localhost:8491"]);
    }

    [Fact]
    public void Explicit_origins_win_over_the_default()
    {
        var origins = EffectiveOrigins(
            ("Admin:RootPath", _adminRoot),
            ("Authentication:AdminSpa:Origins:0", "https://admin.example.org"),
            ("Authentication:AdminSpa:Origins:1", ""),
            ("PublicChannels:PublicBaseUrl", "https://assistant.example.org"));

        origins.ShouldBe(["https://admin.example.org"]);
    }

    [Fact]
    public void Without_a_served_admin_there_is_no_default()
    {
        var origins = EffectiveOrigins(
            ("Authentication:AdminSpa:Origins:0", ""),
            ("PublicChannels:PublicBaseUrl", "https://assistant.example.org"));

        origins.ShouldBeEmpty();
    }

    [Fact]
    public void A_public_base_url_with_a_path_is_no_default()
    {
        // The admin is served at the Api's root, so https://example.org would not be where it is.
        var origins = EffectiveOrigins(
            ("Admin:RootPath", _adminRoot),
            ("Authentication:AdminSpa:Origins:0", ""),
            ("PublicChannels:PublicBaseUrl", "https://example.org/assistant"));

        origins.ShouldBeEmpty();
    }

    [Fact]
    public void Without_a_public_base_url_there_is_no_default()
    {
        var origins = EffectiveOrigins(
            ("Admin:RootPath", _adminRoot),
            ("Authentication:AdminSpa:Origins:0", ""),
            ("PublicChannels:PublicBaseUrl", ""));

        origins.ShouldBeEmpty();
    }

    [Fact]
    public void The_default_becomes_the_admin_spa_redirect_uris()
    {
        var origins = AdminSpaClientRegistrar.EffectiveOrigins(
            new SmartAgriAuthenticationOptions(),
            new AdminSpaOptions { RootPath = "wwwroot/admin" },
            new PublicChannelsOptions { PublicBaseUrl = "https://assistant.example.org" });

        var descriptor = AdminSpaClientRegistrar.Describe(origins);

        descriptor.RedirectUris.ShouldBe([new Uri("https://assistant.example.org/auth/callback")]);
        descriptor.PostLogoutRedirectUris.ShouldBe([new Uri("https://assistant.example.org/login")]);
    }

    private IReadOnlyList<string> EffectiveOrigins(params (string Key, string Value)[] settings)
    {
        using var factory = _base.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Jobs:WorkerEnabled", "false");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AdminSpaClientRegistrar>().EffectiveOrigins();
    }
}
