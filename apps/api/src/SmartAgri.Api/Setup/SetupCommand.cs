using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace SmartAgri.Api.Setup;

/// <summary>
/// The one-shot <c>setup</c> subcommand (M1 plan, Slice 11; on-prem-packaging ADR):
/// creates the first organization and its administrator on an empty database, prints a
/// one-time password once, and exits without starting the web server.
/// </summary>
/// <remarks>
/// <para>
/// Order: parse arguments → check the database (refuse before asking anything if
/// migrations are pending or an organization exists) → ask for missing values → create
/// everything in one serializable transaction (which re-checks "no organization") →
/// print the password.
/// </para>
/// <para>
/// The password only ever goes to <see cref="ISetupConsole.Out"/> and to Identity's
/// hasher. It is never passed to <see cref="ILogger"/>, an activity/span, an exception
/// message or a file; failure messages are additionally scrubbed of it.
/// </para>
/// </remarks>
public sealed class SetupCommand
{
    public const int ExitSuccess = 0;
    public const int ExitRefused = 1;
    public const int ExitUsage = 2;

    private const int MaxPromptAttempts = 5;

    private readonly IInitialSetupStore _store;
    private readonly SetupInputRules _rules;
    private readonly ILogger<SetupCommand> _logger;

    public SetupCommand(IInitialSetupStore store, IOptions<IdentityOptions> identityOptions, ILogger<SetupCommand> logger)
    {
        ArgumentNullException.ThrowIfNull(identityOptions);
        _store = store;
        _rules = new SetupInputRules(identityOptions.Value.User.AllowedUserNameCharacters);
        _logger = logger;
    }

    /// <summary>Runs <c>setup</c> in a new DI scope of <paramref name="services"/>.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        ISetupConsole console,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SetupCommand>().RunAsync(args, console, cancellationToken);
    }

    /// <returns>The process exit code: <see cref="ExitSuccess"/>, <see cref="ExitRefused"/>
    /// or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, ISetupConsole console, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);

        var parsed = SetupArguments.Parse(args);
        if (parsed.Arguments is not { } arguments)
        {
            await console.Error.WriteLineAsync(parsed.Error);
            await console.Error.WriteLineAsync(SetupArguments.Usage);
            return ExitUsage;
        }

        if (arguments.HelpRequested)
        {
            await console.Out.WriteLineAsync(SetupArguments.Usage);
            return ExitSuccess;
        }

        string? password = null;
        try
        {
            switch (await _store.GetStateAsync(cancellationToken))
            {
                case InitialSetupState.PendingMigrations:
                    await console.Error.WriteLineAsync(
                        "資料庫結構尚未更新，請先執行 migrate（dotnet SmartAgri.Api.dll migrate；docker compose 的 run 會自動執行）。");
                    return ExitRefused;
                case InitialSetupState.AlreadyInitialized:
                    return await RefuseAlreadyInitializedAsync(console);
            }

            if (await CollectAsync(arguments, console) is not { } request)
            {
                return ExitUsage;
            }

            password = OneTimePasswordGenerator.Generate();
            var result = await _store.CreateAsync(request, password, cancellationToken);
            switch (result.Outcome)
            {
                case InitialSetupOutcome.AlreadyInitialized:
                    return await RefuseAlreadyInitializedAsync(console);
                case InitialSetupOutcome.Rejected:
                    await console.Error.WriteLineAsync("無法建立管理者帳號：");
                    foreach (var error in result.Errors)
                    {
                        await console.Error.WriteLineAsync("  " + Redact(error, password));
                    }

                    return ExitRefused;
            }

            _logger.LogInformation(
                "Initial setup created organization {OrganizationCode} and administrator account {AccountId}.",
                request.OrganizationCode,
                result.AccountId);

            await PrintResultAsync(console.Out, request, password);
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only a scrubbed message: an exception object is never handed to the logger
            // once a password exists, so nothing can serialize it along the way.
            var message = Redact($"{exception.GetType().Name}: {exception.Message}", password);
            _logger.LogError("Initial setup failed: {Error}", message);
            await console.Error.WriteLineAsync("setup 失敗：" + message);
            return ExitRefused;
        }
    }

    private static async Task<int> RefuseAlreadyInitializedAsync(ISetupConsole console)
    {
        await console.Error.WriteLineAsync(
            "資料庫已經有組織，拒絕執行：setup 只能在尚未初始化的資料庫上執行一次。沒有做任何變更。");
        return ExitRefused;
    }

    private async Task<InitialSetupRequest?> CollectAsync(SetupArguments arguments, ISetupConsole console)
    {
        if (!console.IsInteractive)
        {
            var missing = new List<string>();
            if (arguments.OrganizationName is null)
            {
                missing.Add(SetupArguments.OrganizationNameFlag);
            }

            if (arguments.OrganizationCode is null)
            {
                missing.Add(SetupArguments.OrganizationCodeFlag);
            }

            if (arguments.AdminLogin is null)
            {
                missing.Add(SetupArguments.AdminLoginFlag);
            }

            if (missing.Count > 0)
            {
                await console.Error.WriteLineAsync(
                    $"標準輸入不是終端機，無法詢問；請以參數提供：{string.Join("、", missing)}。");
                return null;
            }
        }

        var organizationName = await ResolveAsync(console, arguments.OrganizationName, "組織名稱", null, SetupInputRules.OrganizationName);
        if (organizationName is null)
        {
            return null;
        }

        var organizationCode = await ResolveAsync(
            console, arguments.OrganizationCode, "組織代碼（a-z、0-9、-；建立後不可修改）", null, SetupInputRules.OrganizationCode);
        if (organizationCode is null)
        {
            return null;
        }

        var adminLogin = await ResolveAsync(console, arguments.AdminLogin, "管理者帳號名稱", null, _rules.AdminLogin);
        if (adminLogin is null)
        {
            return null;
        }

        var displayName = await ResolveAsync(
            console, arguments.AdminDisplayName, "管理者顯示名稱", adminLogin, SetupInputRules.AdminDisplayName);
        if (displayName is null)
        {
            return null;
        }

        return new InitialSetupRequest(organizationName, organizationCode, adminLogin, displayName);
    }

    /// <summary>
    /// A value given as an argument is validated once (an invalid argument is an error, not
    /// a prompt). A missing one is prompted for — re-asking after an invalid answer — or,
    /// without a terminal, takes <paramref name="defaultValue"/>.
    /// </summary>
    private static async Task<string?> ResolveAsync(
        ISetupConsole console,
        string? given,
        string label,
        string? defaultValue,
        Func<string, SetupInputResult> validate)
    {
        if (given is not null || !console.IsInteractive)
        {
            var result = validate(given ?? defaultValue ?? string.Empty);
            if (!result.IsValid)
            {
                await console.Error.WriteLineAsync(result.Error);
                return null;
            }

            return result.Value;
        }

        for (var attempt = 0; attempt < MaxPromptAttempts; attempt++)
        {
            await console.Out.WriteAsync(defaultValue is null ? $"{label}：" : $"{label} [{defaultValue}]：");
            await console.Out.FlushAsync();

            var line = console.ReadLine();
            if (line is null)
            {
                await console.Error.WriteLineAsync("輸入已結束，沒有做任何變更。");
                return null;
            }

            var result = validate(line.Trim().Length == 0 && defaultValue is not null ? defaultValue : line);
            if (result.IsValid)
            {
                return result.Value;
            }

            await console.Error.WriteLineAsync(result.Error);
        }

        await console.Error.WriteLineAsync("輸入錯誤次數過多，沒有做任何變更。");
        return null;
    }

    private static async Task PrintResultAsync(TextWriter output, InitialSetupRequest request, string password)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync("初始化完成。");
        await output.WriteLineAsync($"  組織：{request.OrganizationName}（組織代碼 {request.OrganizationCode}）");
        await output.WriteLineAsync($"  管理者帳號名稱：{request.AdminLogin}");
        await output.WriteLineAsync($"  一次性密碼：{password}");
        await output.WriteLineAsync();
        await output.WriteLineAsync("這組密碼只顯示這一次，沒有寫入任何檔案、日誌或遙測，遺失後無法再查詢。");
        await output.WriteLineAsync("請現在就用它登入管理後台；首次登入後必須設定新密碼，才能使用其他功能。");
        await output.FlushAsync();
    }

    private static string Redact(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "[已隱藏]", StringComparison.Ordinal);
}

public static class SetupServiceCollectionExtensions
{
    /// <summary>Registers the <c>setup</c> subcommand (<see cref="SetupCommand"/>).</summary>
    public static IServiceCollection AddInitialSetup(this IServiceCollection services)
    {
        services.AddScoped<IInitialSetupStore, EfInitialSetupStore>();
        services.AddScoped<SetupCommand>();
        return services;
    }
}
