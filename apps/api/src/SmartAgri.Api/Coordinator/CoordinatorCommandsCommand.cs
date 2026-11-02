using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The one-shot <c>coordinator-commands</c> subcommand (store-coordinator design decision F, §5 Slice 4, #375): the
/// platform operator lists a shop's latest confirmed commands — the execution records — with each action's kind,
/// name, step, mode, status, due and completion time. Like the other coordinator subcommands it runs in any
/// environment; the organization code is the one used at login, matched case-insensitively.
/// </summary>
/// <remarks>
/// Prints no announcement (it may hold a reason someone wrote) and no LINE user id in full: only its last four
/// characters (<see cref="MaskLineUserId"/>). Times are in the shop's time zone.
/// </remarks>
public sealed class CoordinatorCommandsCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const int DefaultLimit = 20;
    public const int MaxLimit = 200;

    public const string Usage =
        "用法：coordinator-commands --organization <組織代碼> [--limit <1-200>]\n" +
        "列出這家店最近確認的指令（預設 20 筆，最新的在前）：情境、日期、誰在何時確認，以及每個動作的種類、名稱、步驟（apply／undo／pause／resume）、" +
        "模式（auto／reminder）、狀態（scheduled／succeeded／failed／awaiting-manual／done-manually／cancelled）、預定時間與完成時間。\n" +
        "不印公告內容；LINE 使用者 ID 只顯示最後 4 碼。時間為店家設定的時區。\n" +
        "結束代碼：0 完成；1 找不到組織或讀取失敗；2 參數錯誤。";

    private readonly IServiceProvider _services;

    public CoordinatorCommandsCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>coordinator-commands</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        return await new CoordinatorCommandsCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <summary>「…」 and the last four characters of <paramref name="lineUserId"/>.</summary>
    public static string MaskLineUserId(string lineUserId)
    {
        ArgumentNullException.ThrowIfNull(lineUserId);
        return lineUserId.Length <= 4 ? "…" : "…" + lineUserId[^4..];
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var code, out var limit, out var help, out var usageError))
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
            Organization? organization;
            await using (var lookup = new AppDbContext(_services.GetRequiredService<DbContextOptions<AppDbContext>>(), FixedOrganizationContext.None))
            {
                organization = await CoordinatorBindingCodeCommand.FindAsync(lookup, code!, cancellationToken);
            }

            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{code}」。");
                return ExitFailed;
            }

            var settings = await _services.GetRequiredService<CoordinatorShops>().FindAsync(organization.Id, cancellationToken);
            var zone = settings?.TimeZone ?? TimeZoneInfo.FindSystemTimeZoneById(ShopSettings.DefaultTimeZoneId);
            var commandStore = _services.GetRequiredService<CoordinatorCommands>();
            var commands = await commandStore.RecentConfirmedAsync(organization.Id, limit, cancellationToken);
            var shop = $"組織 {organization.Code}（{settings?.StoreName ?? organization.Name}）";
            if (commands.Count == 0)
            {
                await output.WriteLineAsync($"{shop}還沒有確認過的指令。");
                return ExitSuccess;
            }

            var actions = await _services.GetRequiredService<CoordinatorCommandActions>()
                .ListAsync(organization.Id, [.. commands.Select(command => command.Id)], cancellationToken);
            await output.WriteLineAsync($"{shop}最近 {commands.Count} 筆確認的指令（時間為 {zone.Id}）：");
            foreach (var command in commands)
            {
                await output.WriteLineAsync();
                await output.WriteLineAsync(
                    $"{Time(command.ConfirmedAt!.Value, zone)} 確認｜{ShopAnnouncements.FormatDate(command.Date!.Value)} "
                    + $"{CoordinatorCommandReplies.Title(command.Scenario, command.Time)}｜確認者 {MaskLineUserId(command.ConfirmedByLineUserId!)}｜指令 {command.Id}");
                if (command.ReopensCommandId is { } undoneId)
                {
                    await output.WriteLineAsync($"  恢復的指令 {undoneId}{(command.CorrectionBroadcast ? "，加發更正公告" : string.Empty)}");
                }

                var rows = actions[command.Id].ToList();
                if (rows.Count == 0)
                {
                    await output.WriteLineAsync("  （沒有動作）");
                }

                foreach (var row in rows)
                {
                    await output.WriteLineAsync($"  {row.Position + 1}. {Describe(row, zone)}");
                }
            }

            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"無法讀取執行紀錄：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    private static string Describe(CoordinatorCommandAction row, TimeZoneInfo zone)
    {
        var kind = WireNames<ShopActionKind>.ToWire(row.Kind) + (row.Name is { } name ? $" {name}" : string.Empty);
        var status = WireNames<CoordinatorActionStatus>.ToWire(row.Status) + (row.FailureCode is { } code ? $"（{code}）" : string.Empty);
        var text = $"{kind}｜{WireNames<CoordinatorActionStep>.ToWire(row.Step)}｜{WireNames<ShopActionMode>.ToWire(row.Mode)}｜{status}｜預定 {Time(row.DueAt, zone)}";
        if (row.CompletedAt is { } completed)
        {
            text += $"｜完成 {Time(completed, zone)}";
            if (row.CompletedByLineUserId is { } by)
            {
                text += $"（{MaskLineUserId(by)}）";
            }
        }

        return row.Attempts > 1 ? $"{text}｜執行 {row.Attempts} 次" : text;
    }

    private static string Time(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static bool TryParse(IReadOnlyList<string> args, out string? code, out int limit, out bool help, out string error)
    {
        code = null;
        limit = DefaultLimit;
        help = false;
        error = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    help = true;
                    return true;
                case "--organization" or "--limit":
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = $"{name} 需要一個值。";
                        return false;
                    }

                    if (name == "--organization")
                    {
                        code = value.Trim();
                    }
                    else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit)
                    {
                        error = $"--limit 必須是 1 到 {MaxLimit} 的整數。";
                        return false;
                    }

                    break;
                default:
                    error = $"不認得的參數「{args[i]}」。";
                    return false;
            }
        }

        if (code is null)
        {
            error = "必須用 --organization 指定組織代碼。";
            return false;
        }

        return true;
    }
}
