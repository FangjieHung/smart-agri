namespace SmartAgri.Api.Setup;

/// <summary>
/// The one-shot <c>reset-password</c> subcommand (#340): gives one account of one organization a new
/// one-time password (generated like <c>setup</c>'s, by <see cref="OneTimePasswordGenerator"/>), prints it
/// once, and exits without starting the web server. The account must set its own password at the next
/// sign-in; its lockout is cleared and its earlier sign-ins end (what exactly ends:
/// <see cref="EfPasswordResetStore"/>).
/// </summary>
/// <remarks>
/// Same password handling as <see cref="SetupCommand"/>: the password only ever goes to
/// <see cref="ISetupConsole.Out"/> and to Identity's hasher — never to <see cref="ILogger"/>, a span, an
/// exception message or a file — and failure messages are scrubbed of it. Exit codes match <c>setup</c>
/// and <c>set-token-limit</c>.
/// </remarks>
public sealed class ResetPasswordCommand
{
    public const int ExitSuccess = 0;
    public const int ExitRefused = 1;
    public const int ExitUsage = 2;

    public const string OrganizationFlag = "--organization";
    public const string LoginFlag = "--login";

    public const string Usage = """
        用法：reset-password --organization <組織代碼> --login <登入名稱>

        為指定組織的指定帳號產生新的一次性密碼，並只在這裡顯示一次；原本的密碼立即失效，
        帳號的鎖定狀態與登入失敗次數一併清除，帳號本人登入後必須設定新密碼。這個帳號原有的
        登入工作階段會失效：無法再取得新的存取權杖，已取得的存取權杖在到期前（最多 30 分鐘）
        也只能查看自己的帳號資料，不能使用其他功能。其他組織的同名帳號不受影響。
          --organization  組織代碼（登入時使用的代碼，不分大小寫）
          --login         帳號的登入名稱（不分大小寫）
        結束代碼：0 完成；1 找不到組織或帳號、或寫入失敗（都不會做任何變更）；2 參數錯誤。
        """;

    private static readonly string[] Flags = [OrganizationFlag, LoginFlag];

    private readonly IPasswordResetStore _store;
    private readonly ILogger<ResetPasswordCommand> _logger;

    public ResetPasswordCommand(IPasswordResetStore store, ILogger<ResetPasswordCommand> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Runs <c>reset-password</c> in a new DI scope of <paramref name="services"/>.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        ISetupConsole console,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ResetPasswordCommand>().RunAsync(args, console, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitRefused"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, ISetupConsole console, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);

        if (!TryParse(args, out var organizationCode, out var loginName, out var help, out var usageError))
        {
            await console.Error.WriteLineAsync(usageError);
            await console.Error.WriteLineAsync(Usage);
            return ExitUsage;
        }

        if (help)
        {
            await console.Out.WriteLineAsync(Usage);
            return ExitSuccess;
        }

        string? password = null;
        try
        {
            password = OneTimePasswordGenerator.Generate();
            var result = await _store.ResetAsync(organizationCode!, loginName!, password, cancellationToken);
            switch (result.Outcome)
            {
                case PasswordResetOutcome.OrganizationNotFound:
                    await console.Error.WriteLineAsync($"找不到組織代碼「{organizationCode}」，沒有做任何變更。");
                    return ExitRefused;
                case PasswordResetOutcome.AccountNotFound:
                    await console.Error.WriteLineAsync(
                        $"組織「{organizationCode}」裡沒有登入名稱為「{loginName}」的帳號，沒有做任何變更。");
                    return ExitRefused;
                case PasswordResetOutcome.Rejected:
                    await console.Error.WriteLineAsync("無法重設密碼，沒有做任何變更：");
                    foreach (var error in result.Errors)
                    {
                        await console.Error.WriteLineAsync("  " + Redact(error, password));
                    }

                    return ExitRefused;
            }

            _logger.LogInformation(
                "reset-password gave account {AccountId} of organization {OrganizationCode} a new one-time password.",
                result.AccountId,
                result.OrganizationCode);

            await PrintResultAsync(console.Out, result, password);
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only a scrubbed message: an exception object is never handed to the logger once a
            // password exists, so nothing can serialize it along the way.
            var message = Redact($"{exception.GetType().Name}: {exception.Message}", password);
            _logger.LogError("reset-password failed: {Error}", message);
            await console.Error.WriteLineAsync($"reset-password 失敗：{message}（資料庫是否已執行 migrate？）");
            return ExitRefused;
        }
    }

    private static async Task PrintResultAsync(TextWriter output, PasswordResetResult result, string password)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync("已重設密碼。");
        await output.WriteLineAsync($"  組織：{result.OrganizationName}（組織代碼 {result.OrganizationCode}）");
        await output.WriteLineAsync($"  帳號名稱：{result.LoginName}");
        await output.WriteLineAsync($"  一次性密碼：{password}");
        await output.WriteLineAsync();
        await output.WriteLineAsync("這組密碼只顯示這一次，沒有寫入任何檔案、日誌或遙測，遺失後無法再查詢（可以再執行一次 reset-password）。");
        await output.WriteLineAsync("原本的密碼已失效，鎖定已解除，這個帳號原有的登入工作階段也已失效。");
        await output.WriteLineAsync("請把密碼交給帳號本人；登入後必須設定新密碼，才能使用其他功能。");
        await output.FlushAsync();
    }

    /// <summary>
    /// Parses <c>--flag value</c> and <c>--flag=value</c>, like <see cref="SetupArguments.Parse"/>: unknown
    /// flags, positional arguments, repeated flags and flags without a value are errors, and both flags are
    /// required (nothing is prompted for).
    /// </summary>
    private static bool TryParse(
        IReadOnlyList<string> args, out string? organizationCode, out string? loginName, out bool help, out string error)
    {
        organizationCode = null;
        loginName = null;
        help = false;
        error = string.Empty;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg is "--help" or "-h")
            {
                help = true;
                return true;
            }

            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            var (flag, value) = arg.StartsWith("--", StringComparison.Ordinal) && equals > 0
                ? (arg[..equals], arg[(equals + 1)..])
                : (arg, (string?)null);

            if (!Flags.Contains(flag, StringComparer.Ordinal))
            {
                error = $"不認得的參數：{flag}";
                return false;
            }

            if (value is null)
            {
                if (index + 1 >= args.Count || Flags.Contains(args[index + 1], StringComparer.Ordinal))
                {
                    error = $"{flag} 需要一個值。";
                    return false;
                }

                value = args[++index];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{flag} 需要一個值。";
                return false;
            }

            if (!values.TryAdd(flag, value.Trim()))
            {
                error = $"{flag} 只能指定一次。";
                return false;
            }
        }

        var missing = Flags.Where(flag => !values.ContainsKey(flag)).ToList();
        if (missing.Count > 0)
        {
            error = $"必須以參數提供：{string.Join("、", missing)}。";
            return false;
        }

        organizationCode = values[OrganizationFlag];
        loginName = values[LoginFlag];
        return true;
    }

    private static string Redact(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "[已隱藏]", StringComparison.Ordinal);
}
