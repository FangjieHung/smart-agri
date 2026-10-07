using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// The one-shot <c>retention-cleanup</c> subcommand (M6 plan §3 G, issue #241): runs one organization's
/// conversation cleanup now — the same <see cref="RetentionCleanupService"/> as the daily job, so a
/// pending retention whose buffer is over takes effect first — and exits. It does not touch the daily
/// chain. Runs in any environment; <c>--as-of</c> ("clean up as if it were then", for the API-mode E2E
/// that simulates 38 days later) only in Development and Testing.
/// </summary>
public sealed class RetentionCleanupCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "用法：retention-cleanup --organization <組織代碼> [--as-of <ISO 8601 時間>]\n" +
        "立即依組織的保存期限清理一次過期的對話串與回答紀錄（緩衝期已過的新期限會先生效）。" +
        "--as-of 只能在 Development 或 Testing 使用，以指定的時間計算截止點。\n" +
        "結束代碼：0 完成；1 找不到組織或執行失敗；2 參數錯誤。";

    private static readonly string[] AsOfEnvironments = ["Development", "Testing"];

    private readonly IServiceProvider _services;

    public RetentionCleanupCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>retention-cleanup</c> in a new DI scope of <paramref name="services"/>.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        return await new RetentionCleanupCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var code, out var asOf, out var help, out var usageError))
        {
            await error.WriteLineAsync(usageError);
            await error.WriteLineAsync(Usage);
            return ExitUsage;
        }

        if (help)
        {
            await output.WriteLineAsync(Usage);
            return ExitSuccess;
        }

        var environment = _services.GetRequiredService<IHostEnvironment>();
        if (asOf is not null && !AsOfEnvironments.Any(environment.IsEnvironment))
        {
            await error.WriteLineAsync($"--as-of 只能在 Development 或 Testing 使用（目前是 {environment.EnvironmentName}）。");
            await error.WriteLineAsync(Usage);
            return ExitUsage;
        }

        try
        {
            var options = _services.GetRequiredService<DbContextOptions<AppDbContext>>();
            Organization? organization;
            await using (var lookup = new AppDbContext(options, FixedOrganizationContext.None))
            {
                organization = await FindAsync(lookup, code!, cancellationToken);
            }

            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{code}」，沒有刪除任何資料。");
                return ExitFailed;
            }

            await using var dbContext = new AppDbContext(options, new FixedOrganizationContext(organization.Id));
            var cleanup = ActivatorUtilities.CreateInstance<RetentionCleanupService>(_services, dbContext);
            var result = await cleanup.RunAsync(asOf ?? _services.GetRequiredService<TimeProvider>().GetUtcNow(), cancellationToken);

            if (result.TookEffect is { } change)
            {
                await output.WriteLineAsync($"組織 {organization.Code} 的保存期限已生效：{Describe(change.From)} → {Describe(change.To)}。");
            }

            await output.WriteLineAsync(result.Cutoff is { } cutoff
                ? $"組織 {organization.Code}（{organization.Name}）保存 {result.Days} 天，截止點 {cutoff.ToString("o", CultureInfo.InvariantCulture)}：" +
                  $"刪除 {result.ThreadCount} 串對話、{result.AnswerOutcomeCount} 筆回答紀錄。"
                : $"組織 {organization.Code}（{organization.Name}）的對話永久保存，沒有刪除任何資料。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法清理：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    private static async Task<Organization?> FindAsync(AppDbContext dbContext, string code, CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = Organization.NormalizeCode(code);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return await dbContext.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(organization => organization.Code == normalized, cancellationToken);
    }

    private static string Describe(int? days) => days is { } value ? $"{value} 天" : "永久";

    private static bool TryParse(
        IReadOnlyList<string> args, out string? code, out DateTimeOffset? asOf, out bool help, out string error)
    {
        code = null;
        asOf = null;
        help = false;
        error = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    help = true;
                    break;
                case "--organization" or "--as-of":
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = $"{name} 需要一個值。";
                        return false;
                    }

                    value = value.Trim();
                    if (name == "--organization")
                    {
                        code = value;
                    }
                    else if (DateTimeOffset.TryParse(
                        value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        asOf = parsed;
                    }
                    else
                    {
                        error = "--as-of 必須是 ISO 8601 時間，例如 2026-11-13T03:00:00+08:00。";
                        return false;
                    }

                    break;
                default:
                    error = $"不認得的參數「{args[i]}」。";
                    return false;
            }
        }

        if (!help && code is null)
        {
            error = "必須用 --organization 指定組織代碼。";
            return false;
        }

        return true;
    }
}
