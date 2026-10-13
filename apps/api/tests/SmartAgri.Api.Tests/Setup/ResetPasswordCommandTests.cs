using Shouldly;
using SmartAgri.Api.Setup;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>
/// <c>reset-password</c>'s argument, refusal and output rules with a fake store (the database side is
/// <c>ResetPasswordTests</c>, Docker).
/// </summary>
public class ResetPasswordCommandTests
{
    private readonly FakePasswordResetStore _store = new();
    private readonly RecordingLogger<ResetPasswordCommand> _logger = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reset_passes_a_generated_password_to_the_store_and_prints_it_once_on_stdout_only()
    {
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await RunAsync(console, "--organization", "Harvest", "--login", "boss");

        exitCode.ShouldBe(ResetPasswordCommand.ExitSuccess, console.ErrorWriter.ToString());
        var (code, login, password) = _store.Resets.ShouldHaveSingleItem();
        (code, login).ShouldBe(("Harvest", "boss"));
        password.Length.ShouldBe(OneTimePasswordGenerator.Length);
        console.PrintedPassword.ShouldBe(password);
        console.OutWriter.ToString().Split(password).Length.ShouldBe(2);
        console.OutWriter.ToString().ShouldContain("harvest");
        console.ErrorWriter.ToString().ShouldNotContain(password);
        _logger.Entries.ShouldContain(entry => entry.Contains("reset-password gave account", StringComparison.Ordinal));
        _logger.Entries.ShouldNotContain(entry => entry.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_run_generates_a_new_password()
    {
        await RunAsync(new FakeSetupConsole(interactive: false), "--organization=harvest", "--login=boss");
        await RunAsync(new FakeSetupConsole(interactive: false), "--organization=harvest", "--login=boss");

        _store.Resets.Select(reset => reset.Password).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_organization_exits_1_naming_the_code_without_a_password()
    {
        _store.Result = PasswordResetResult.OrganizationNotFound;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--organization", "nowhere", "--login", "boss")).ShouldBe(ResetPasswordCommand.ExitRefused);

        console.ErrorWriter.ToString().ShouldContain("找不到組織代碼「nowhere」");
        console.ErrorWriter.ToString().ShouldContain("沒有做任何變更");
        console.PrintedPassword.ShouldBeNull();
        console.OutWriter.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_login_exits_1_naming_the_organization_and_login_without_a_password()
    {
        _store.Result = PasswordResetResult.AccountNotFound;
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--organization", "harvest", "--login", "ghost")).ShouldBe(ResetPasswordCommand.ExitRefused);

        console.ErrorWriter.ToString().ShouldContain("「harvest」");
        console.ErrorWriter.ToString().ShouldContain("「ghost」");
        console.ErrorWriter.ToString().ShouldContain("沒有做任何變更");
        console.PrintedPassword.ShouldBeNull();
        console.OutWriter.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejection_exits_1_with_its_errors_scrubbed_of_the_password()
    {
        var console = new FakeSetupConsole(interactive: false);

        // The store echoes the password it was given in its error, as a careless validator might.
        var store = new EchoingRejectionStore();
        var exitCode = await new ResetPasswordCommand(store, _logger)
            .RunAsync(["--organization", "harvest", "--login", "boss"], console, CancellationToken);

        exitCode.ShouldBe(ResetPasswordCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain("無法重設密碼");
        console.ErrorWriter.ToString().ShouldContain("[已隱藏]");
        console.ErrorWriter.ToString().ShouldNotContain(store.Password!);
        console.PrintedPassword.ShouldBeNull();
    }

    [Fact]
    public async Task A_failure_exits_1_and_neither_the_console_nor_the_log_sees_the_password()
    {
        _store.Throw = password => new InvalidOperationException($"database said no to {password}");
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--organization", "harvest", "--login", "boss")).ShouldBe(ResetPasswordCommand.ExitRefused);

        var password = _store.Resets.ShouldHaveSingleItem().Password;
        console.ErrorWriter.ToString().ShouldContain("reset-password 失敗");
        console.ErrorWriter.ToString().ShouldContain("[已隱藏]");
        console.ErrorWriter.ToString().ShouldNotContain(password);
        console.PrintedPassword.ShouldBeNull();
        _logger.Entries.ShouldContain(entry => entry.Contains("reset-password failed", StringComparison.Ordinal));
        _logger.Entries.ShouldNotContain(entry => entry.Contains(password, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData]
    [InlineData("--organization", "harvest")]
    [InlineData("--login", "boss")]
    [InlineData("--organization", "harvest", "--login")]
    [InlineData("--organization", "--login", "boss")]
    [InlineData("--organization", "harvest", "--login", " ")]
    [InlineData("--organization", "harvest", "--login", "boss", "--login", "other")]
    [InlineData("--organization", "harvest", "--login", "boss", "--password", "x")]
    [InlineData("harvest", "boss")]
    public async Task Missing_repeated_or_unknown_arguments_exit_2_with_the_usage_and_reset_nothing(params string[] args)
    {
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, args)).ShouldBe(ResetPasswordCommand.ExitUsage);

        console.ErrorWriter.ToString().ShouldContain("用法：reset-password");
        _store.Resets.ShouldBeEmpty();
        console.PrintedPassword.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_arguments_are_named()
    {
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console)).ShouldBe(ResetPasswordCommand.ExitUsage);

        console.ErrorWriter.ToString().ShouldContain("--organization");
        console.ErrorWriter.ToString().ShouldContain("--login");
    }

    [Fact]
    public async Task Help_prints_the_usage_and_resets_nothing()
    {
        var console = new FakeSetupConsole(interactive: false);

        (await RunAsync(console, "--help")).ShouldBe(ResetPasswordCommand.ExitSuccess);

        console.OutWriter.ToString().ShouldContain("用法：reset-password --organization <組織代碼> --login <登入名稱>");
        _store.Resets.ShouldBeEmpty();
    }

    private Task<int> RunAsync(FakeSetupConsole console, params string[] args) =>
        new ResetPasswordCommand(_store, _logger).RunAsync(args, console, CancellationToken);

    private sealed class EchoingRejectionStore : IPasswordResetStore
    {
        public string? Password { get; private set; }

        public Task<PasswordResetResult> ResetAsync(string organizationCode, string loginName, string password, CancellationToken cancellationToken)
        {
            Password = password;
            return Task.FromResult(PasswordResetResult.Rejected([$"密碼 {password} 不符合規則。"]));
        }
    }
}
