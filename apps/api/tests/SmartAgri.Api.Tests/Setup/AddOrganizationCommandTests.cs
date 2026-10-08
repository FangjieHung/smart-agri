using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Setup;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>
/// <c>add-organization</c>'s prompting, refusal and output rules with a fake store (the
/// database side is <c>AddOrganizationTests</c>, Docker). Same command class as <c>setup</c>,
/// in <see cref="SetupMode.AddOrganization"/>.
/// </summary>
public class AddOrganizationCommandTests
{
    private static readonly string[] AllFlags =
        ["--organization-name", "豐收商行", "--organization-code", "Harvest-2", "--admin-login", "boss", "--admin-display-name", "李老闆"];

    private readonly FakeInitialSetupStore _store = new() { State = InitialSetupState.AlreadyInitialized };

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task All_flags_create_the_organization_in_add_mode_and_print_the_password_once()
    {
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await RunAsync(console, AllFlags);

        exitCode.ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        var (request, password) = _store.Created.ShouldHaveSingleItem();
        request.ShouldBe(new InitialSetupRequest("豐收商行", "harvest-2", "boss", "李老闆"));
        _store.Modes.ShouldBe([SetupMode.AddOrganization]);
        console.PrintedPassword.ShouldBe(password);
        console.OutWriter.ToString().Split(password).Length.ShouldBe(2);
        console.ErrorWriter.ToString().ShouldNotContain(password);
    }

    [Fact]
    public async Task Interactive_prompts_collect_the_values()
    {
        var console = new FakeSetupConsole(interactive: true, "豐收商行", "harvest-2", "boss", "");

        (await RunAsync(console)).ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        _store.Created.ShouldHaveSingleItem().Request.ShouldBe(new InitialSetupRequest("豐收商行", "harvest-2", "boss", "boss"));
    }

    [Fact]
    public async Task Without_any_organization_it_is_refused_and_points_to_setup()
    {
        _store.State = InitialSetupState.Ready;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, AllFlags)).ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("setup");
        console.PrintedPassword.ShouldBeNull();
        _store.Created.ShouldBeEmpty();
        console.LinesRead.ShouldBe(0);
    }

    [Fact]
    public async Task Losing_a_race_with_the_first_setup_is_refused_and_points_to_setup()
    {
        _store.CreateResult = InitialSetupResult.NoOrganization;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, AllFlags)).ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("setup");
        console.PrintedPassword.ShouldBeNull();
    }

    [Fact]
    public async Task A_taken_organization_code_is_refused_without_printing_a_password()
    {
        _store.CreateResult = InitialSetupResult.DuplicateCode;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, AllFlags)).ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("harvest-2");
        console.PrintedPassword.ShouldBeNull();
        console.OutWriter.ToString().ShouldNotContain(_store.Created.Single().Password);
    }

    [Fact]
    public async Task Pending_migrations_are_refused_with_a_pointer_to_migrate()
    {
        _store.State = InitialSetupState.PendingMigrations;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, AllFlags)).ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("migrate");
        _store.Created.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Bad_Code")]
    [InlineData("has space")]
    [InlineData("a234567890123456789012345678901234")]
    public async Task An_invalid_organization_code_exits_2_and_writes_nothing(string code)
    {
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--organization-name", "豐收商行", "--organization-code", code, "--admin-login", "boss"))
            .ShouldBe(SetupCommand.ExitUsage);
        _store.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_values_without_a_terminal_exit_2_naming_every_missing_flag()
    {
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--organization-name", "豐收商行")).ShouldBe(SetupCommand.ExitUsage);
        console.ErrorWriter.ToString().ShouldContain("--organization-code");
        console.ErrorWriter.ToString().ShouldContain("--admin-login");
        _store.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Help_prints_the_add_organization_usage_and_unknown_flags_exit_2()
    {
        var console = new FakeSetupConsole(interactive: false);
        (await RunAsync(console, "--help")).ShouldBe(SetupCommand.ExitSuccess);
        console.OutWriter.ToString().ShouldContain("add-organization");

        var bad = new FakeSetupConsole(interactive: false);
        (await RunAsync(bad, "--password", "x")).ShouldBe(SetupCommand.ExitUsage);
        bad.ErrorWriter.ToString().ShouldContain("add-organization");
    }

    private Task<int> RunAsync(FakeSetupConsole console, params string[] args)
    {
        var options = new IdentityOptions();
        options.User.AllowedUserNameCharacters += "/";
        return new SetupCommand(_store, Options.Create(options), NullLogger<SetupCommand>.Instance)
            .RunAsync(SetupMode.AddOrganization, args, console, CancellationToken);
    }
}
