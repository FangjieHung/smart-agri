using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// The one-shot <c>set-token-limit</c> subcommand (M5a plan §3 F, issue #195): sets an
/// organization's monthly chat-model token limit, or sends it back to the deployment default
/// (<c>PublicChannels:DefaultMonthlyTokenLimit</c>), and exits. Like <c>reindex</c> it runs in any
/// environment — operators run it inside the Production container.
/// </summary>
/// <remarks>
/// Nothing is written unless the organization exists and the number is valid. A running Api picks the
/// new limit up when its 30 second usage cache expires (<see cref="Application.Organizations.OrganizationTokenUsage"/>),
/// so website replies resume at most that long after raising it. The organization code is
/// the one used at login (<see cref="Organization.Code"/>), matched case-insensitively.
/// </remarks>
public sealed class SetTokenLimitCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string DefaultKeyword = "default";

    public const string Usage =
        "用法：set-token-limit --organization <組織代碼> --tokens <每月 token 數>|default\n" +
        "設定組織每月的對話模型 token 上限（輸入＋輸出）；default 表示改用部署預設值（PublicChannels:DefaultMonthlyTokenLimit）；" +
        "0 表示本月不允許任何對外回覆。運行中的 Api 最多 30 秒後套用。\n" +
        "結束代碼：0 完成；1 找不到組織或寫入失敗；2 參數錯誤。";

    private readonly IServiceProvider _services;

    public SetTokenLimitCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>set-token-limit</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        return await new SetTokenLimitCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var code, out var tokens, out var help, out var usageError))
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

        try
        {
            // The Organizations table is not organization scoped, so no organization context is needed.
            await using var dbContext = new AppDbContext(
                _services.GetRequiredService<DbContextOptions<AppDbContext>>(), FixedOrganizationContext.None);
            var organization = await FindAsync(dbContext, code!, cancellationToken);
            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{code}」，沒有寫入任何設定。");
                return ExitFailed;
            }

            var before = organization.MonthlyTokenLimit;
            organization.SetMonthlyTokenLimit(tokens);
            await dbContext.SaveChangesAsync(cancellationToken);

            await output.WriteLineAsync(
                $"組織 {organization.Code}（{organization.Name}）的每月 token 上限：{Describe(before)} → {Describe(tokens)}。");
            await output.WriteLineAsync("運行中的 Api 最多 30 秒後套用新的上限。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法設定上限：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    private static async Task<Organization?> FindAsync(AppDbContext dbContext, string code, CancellationToken cancellationToken)
    {
        // A code outside [a-z0-9-] or too long cannot exist (organization codes are validated on
        // creation), so it is just "not found".
        string normalized;
        try
        {
            normalized = Organization.NormalizeCode(code);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return await dbContext.Organizations.SingleOrDefaultAsync(organization => organization.Code == normalized, cancellationToken);
    }

    private static string Describe(long? limit) =>
        limit is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : "預設值（PublicChannels:DefaultMonthlyTokenLimit）";

    private static bool TryParse(
        IReadOnlyList<string> args, out string? code, out long? tokens, out bool help, out string error)
    {
        code = null;
        tokens = null;
        help = false;
        error = string.Empty;
        var tokensGiven = false;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    help = true;
                    break;
                case "--organization" or "--tokens":
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
                    else if (string.Equals(value, DefaultKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        tokens = null;
                        tokensGiven = true;
                    }
                    else if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        tokens = number;
                        tokensGiven = true;
                    }
                    else
                    {
                        error = "--tokens 必須是 0 或正整數，或 default。";
                        return false;
                    }

                    break;
                default:
                    error = $"不認得的參數「{args[i]}」。";
                    return false;
            }
        }

        if (help)
        {
            return true;
        }

        if (code is null)
        {
            error = "必須用 --organization 指定組織代碼。";
            return false;
        }

        if (!tokensGiven)
        {
            error = "必須用 --tokens 指定上限（數字或 default）。";
            return false;
        }

        return true;
    }
}
