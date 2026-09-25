using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Shouldly;
using SmartAgri.Api.Setup;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>
/// The <c>setup</c> command's prompting, refusal and output rules, with a fake store. No
/// database needed; the database side is covered by <c>InitialSetupTests</c> (Docker).
/// </summary>
public class SetupCommandTests
{
    private static readonly string[] AllFlags =
        ["--organization-name", "安心農場", "--organization-code", "AnXin", "--admin-login", "admin", "--admin-display-name", "王小明"];

    private readonly FakeInitialSetupStore _store = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task All_flags_without_a_terminal_creates_the_organization_and_prints_the_password_once()
    {
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await RunAsync(console, AllFlags);

        exitCode.ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        var (request, password) = _store.Created.ShouldHaveSingleItem();
        request.ShouldBe(new InitialSetupRequest("安心農場", "anxin", "admin", "王小明"));
        console.PrintedPassword.ShouldBe(password);
        CountOccurrences(console.OutWriter.ToString(), password).ShouldBe(1);
        console.ErrorWriter.ToString().ShouldNotContain(password);
        console.OutWriter.ToString().ShouldContain("anxin");
    }

    [Fact]
    public async Task Missing_display_name_without_a_terminal_defaults_to_the_login_name()
    {
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await RunAsync(console, "--organization-name", "安心農場", "--organization-code", "anxin", "--admin-login", "admin");

        exitCode.ShouldBe(SetupCommand.ExitSuccess);
        _store.Created.ShouldHaveSingleItem().Request.AdminDisplayName.ShouldBe("admin");
    }

    [Fact]
    public async Task Missing_required_values_without_a_terminal_fail_and_name_every_missing_flag()
    {
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await RunAsync(console, "--organization-name", "安心農場");

        exitCode.ShouldBe(SetupCommand.ExitUsage);
        console.ErrorWriter.ToString().ShouldContain("--organization-code");
        console.ErrorWriter.ToString().ShouldContain("--admin-login");
        _store.Created.ShouldBeEmpty();
        console.PrintedPassword.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_values_are_prompted_for_on_a_terminal()
    {
        var console = new FakeSetupConsole(interactive: true, "安心農場", "anxin", "admin", "王小明");

        var exitCode = await RunAsync(console);

        exitCode.ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        _store.Created.ShouldHaveSingleItem().Request.ShouldBe(new InitialSetupRequest("安心農場", "anxin", "admin", "王小明"));
        console.LinesRead.ShouldBe(4);
    }

    [Fact]
    public async Task Only_missing_values_are_prompted_for_and_an_empty_display_name_takes_the_default()
    {
        var console = new FakeSetupConsole(interactive: true, "admin", "");

        var exitCode = await RunAsync(console, "--organization-name", "安心農場", "--organization-code", "anxin");

        exitCode.ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        _store.Created.ShouldHaveSingleItem().Request.ShouldBe(new InitialSetupRequest("安心農場", "anxin", "admin", "admin"));
        console.OutWriter.ToString().ShouldContain("[admin]");
    }

    [Fact]
    public async Task An_invalid_answer_is_explained_and_asked_again()
    {
        var console = new FakeSetupConsole(interactive: true, "安心農場", "安心/農場", "anxin", "admin", "王小明");

        var exitCode = await RunAsync(console);

        exitCode.ShouldBe(SetupCommand.ExitSuccess);
        console.ErrorWriter.ToString().ShouldContain("組織代碼");
        _store.Created.ShouldHaveSingleItem().Request.OrganizationCode.ShouldBe("anxin");
    }

    [Fact]
    public async Task End_of_input_while_prompting_changes_nothing()
    {
        var console = new FakeSetupConsole(interactive: true, "安心農場");

        (await RunAsync(console)).ShouldBe(SetupCommand.ExitUsage);
        _store.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Repeated_invalid_answers_give_up()
    {
        var console = new FakeSetupConsole(interactive: true, "安心農場", "a/b", "a/b", "a/b", "a/b", "a/b", "anxin");

        (await RunAsync(console)).ShouldBe(SetupCommand.ExitUsage);
        _store.Created.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("--organization-code", "an/xin")]
    [InlineData("--admin-login", "a/b")]
    [InlineData("--admin-login", "管理者")]
    [InlineData("--organization-name", " ")]
    public async Task An_invalid_flag_value_is_an_error_even_on_a_terminal(string flag, string value)
    {
        var args = AllFlags.ToArray();
        args[Array.IndexOf(args, flag) + 1] = value;
        var console = new FakeSetupConsole(interactive: true, "anything", "anything", "anything");

        (await RunAsync(console, args)).ShouldBe(SetupCommand.ExitUsage);
        _store.Created.ShouldBeEmpty();
        console.LinesRead.ShouldBe(0);
    }

    [Fact]
    public async Task An_existing_organization_is_refused_before_asking_anything()
    {
        _store.State = InitialSetupState.AlreadyInitialized;
        var console = new FakeSetupConsole(interactive: true, "安心農場", "anxin", "admin", "王小明");

        var exitCode = await RunAsync(console);

        exitCode.ShouldBe(SetupCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("已經有組織");
        console.LinesRead.ShouldBe(0);
        _store.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Losing_the_race_to_another_setup_is_refused_without_printing_a_password()
    {
        _store.CreateResult = InitialSetupResult.AlreadyInitialized;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, AllFlags)).ShouldBe(SetupCommand.ExitRefused);
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

    [Fact]
    public async Task Identity_rejections_are_reported_without_the_password()
    {
        var store = new EchoingRejectStore();
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await new SetupCommand(store, IdentityOptions(), NullLogger<SetupCommand>.Instance)
            .RunAsync(AllFlags, console, CancellationToken);

        exitCode.ShouldBe(SetupCommand.ExitRefused);
        store.Password.ShouldNotBeNull();
        console.ErrorWriter.ToString().ShouldContain("rejected");
        console.ErrorWriter.ToString().ShouldNotContain(store.Password);
        console.OutWriter.ToString().ShouldNotContain(store.Password);
    }

    [Fact]
    public async Task A_failure_after_the_password_exists_never_prints_or_logs_it()
    {
        var store = new ThrowingStore();
        var console = new FakeSetupConsole(interactive: false);
        var telemetry = new TelemetryCapture();
        using var factory = CreateHost(telemetry, store);

        var exitCode = await SetupCommand.RunAsync(factory.Services, AllFlags, console, CancellationToken);
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        exitCode.ShouldBe(SetupCommand.ExitRefused);
        store.Password.ShouldNotBeNull();
        console.ErrorWriter.ToString().ShouldContain("boom");
        console.ErrorWriter.ToString().ShouldNotContain(store.Password);
        console.OutWriter.ToString().ShouldNotContain(store.Password);
        telemetry.Logs.ShouldContain(entry => entry.Contains("Initial setup failed", StringComparison.Ordinal));
        telemetry.All.ShouldNotContain(entry => entry.Contains(store.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_password_reaches_the_terminal_but_no_log_OpenTelemetry_log_or_span()
    {
        var telemetry = new TelemetryCapture();
        var console = new FakeSetupConsole(interactive: false);
        using var factory = CreateHost(telemetry, _store);

        var exitCode = await SetupCommand.RunAsync(factory.Services, AllFlags, console, CancellationToken);
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        exitCode.ShouldBe(SetupCommand.ExitSuccess, console.ErrorWriter.ToString());
        var password = console.PrintedPassword.ShouldNotBeNull();

        // The capture works: the command's own log entry arrived through both pipelines.
        telemetry.Logs.ShouldContain(entry => entry.Contains("Initial setup created organization anxin", StringComparison.Ordinal));
        telemetry.OpenTelemetryLogs.ShouldContain(entry => entry.Contains("Initial setup created organization anxin", StringComparison.Ordinal));

        telemetry.All.ShouldNotContain(entry => entry.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Help_prints_usage_without_touching_the_database()
    {
        _store.State = InitialSetupState.AlreadyInitialized;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--help")).ShouldBe(SetupCommand.ExitSuccess);
        console.OutWriter.ToString().ShouldContain("--organization-code");
    }

    [Fact]
    public async Task Bad_arguments_print_usage_and_exit_2()
    {
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--password", "x")).ShouldBe(SetupCommand.ExitUsage);
        console.ErrorWriter.ToString().ShouldContain("--password");
        console.ErrorWriter.ToString().ShouldContain("用法");
    }

    private Task<int> RunAsync(FakeSetupConsole console, params string[] args) =>
        new SetupCommand(_store, IdentityOptions(), NullLogger<SetupCommand>.Instance).RunAsync(args, console, CancellationToken);

    private static IOptions<IdentityOptions> IdentityOptions()
    {
        var options = new IdentityOptions();
        options.User.AllowedUserNameCharacters += "/";
        return Options.Create(options);
    }

    private static WebApplicationFactory<Program> CreateHost(TelemetryCapture telemetry, IInitialSetupStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            telemetry.Attach(builder);
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(store)));
        });

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A store whose error message would contain the password, to prove it is scrubbed.</summary>
    private sealed class EchoingRejectStore : IInitialSetupStore
    {
        public string? Password { get; private set; }

        public Task<InitialSetupState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(InitialSetupState.Ready);

        public Task<InitialSetupResult> CreateAsync(InitialSetupRequest request, string password, CancellationToken cancellationToken)
        {
            Password = password;
            return Task.FromResult(InitialSetupResult.Rejected([$"rejected {password}"]));
        }
    }

    /// <summary>A store that fails with the password inside the exception message.</summary>
    private sealed class ThrowingStore : IInitialSetupStore
    {
        public string? Password { get; private set; }

        public Task<InitialSetupState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(InitialSetupState.Ready);

        public Task<InitialSetupResult> CreateAsync(InitialSetupRequest request, string password, CancellationToken cancellationToken)
        {
            Password = password;
            throw new InvalidOperationException($"boom {password}");
        }
    }
}
