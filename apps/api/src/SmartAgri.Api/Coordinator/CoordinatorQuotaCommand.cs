using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The one-shot <c>coordinator-quota</c> subcommand (store-reservations design §3.1 point 7 and §7, #390): how the 統籌助理
/// LINE account's monthly messages are used — the account's monthly limit and this month's consumption as LINE reports
/// them (<c>GET /v2/bot/message/quota</c>, <c>/quota/consumption</c>, free), then this month's pushes per shop from the
/// push log (<see cref="CoordinatorPushLog"/>, by purpose), and the estimate of design §7 from this month's reservations,
/// members and confirmed 店休 commands. Runs in any environment.
/// </summary>
/// <remarks>
/// Counts and shop names only: never a customer's details, a LINE id or a message. The month is the calendar month of
/// <c>Statistics:TimeZone</c>; LINE counts its own month, and its consumption includes what was sent from LINE Official
/// Account Manager. Each shop is read in a context acting for it (the organization filter stays on).
/// </remarks>
public sealed class CoordinatorQuotaCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    /// <summary>Pushes per confirmed 店休 command in the estimate (design §7: 2–5, take 5).</summary>
    public const int PushesPerCommand = 5;

    public const string Usage =
        "用法：coordinator-quota\n" +
        "印出統籌助理 LINE 官方帳號本月的訊息額度（每月上限，或「無上限」）與 LINE 統計的本月已用量，" +
        "以及本月每家店由統籌助理送出的推播數（訂位卡片、提醒、執行結果、重新授權連結，依推播紀錄）與設計文件第 7 節的估算" +
        "（本月訂位 × 成員人數 ＋ 本月確認的店休類指令 × 5）。只印數字與店名，不印顧客資料。\n" +
        "結束代碼：0 完成；1 統籌助理沒有設定、LINE 查詢失敗或讀取資料庫失敗（其餘部分仍會印出）；2 參數錯誤。";

    private static readonly CoordinatorPushPurpose[] Purposes =
        [CoordinatorPushPurpose.Reservation, CoordinatorPushPurpose.Reminder, CoordinatorPushPurpose.FollowUp, CoordinatorPushPurpose.Reauthorization];

    private readonly IServiceProvider _services;

    public CoordinatorQuotaCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>coordinator-quota</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        return await new CoordinatorQuotaCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <summary>What a push of <paramref name="purpose"/> is called in the output.</summary>
    public static string Label(CoordinatorPushPurpose purpose) => purpose switch
    {
        CoordinatorPushPurpose.Reservation => "訂位卡片",
        CoordinatorPushPurpose.Reminder => "提醒",
        CoordinatorPushPurpose.FollowUp => "執行結果",
        CoordinatorPushPurpose.Reauthorization => "重新授權連結",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Not a push purpose."),
    };

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is ["--help" or "-h"])
        {
            await output.WriteLineAsync(Usage);
            return ExitSuccess;
        }

        if (args.Count > 0)
        {
            await error.WriteLineAsync($"不認得的參數「{args[0]}」。");
            await error.WriteLineAsync(Usage);
            return ExitUsage;
        }

        var exit = ExitSuccess;
        if (!await WriteLineQuotaAsync(output, error, cancellationToken))
        {
            exit = ExitFailed;
        }

        await output.WriteLineAsync();
        try
        {
            await WriteShopsAsync(output, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"無法讀取推播紀錄：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            exit = ExitFailed;
        }

        return exit;
    }

    /// <summary>The account's monthly limit and consumption from LINE; <see langword="false"/> when not available.</summary>
    private async Task<bool> WriteLineQuotaAsync(TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var options = _services.GetRequiredService<IOptions<CoordinatorLineOptions>>().Value;
        if (!options.IsConfigured)
        {
            await error.WriteLineAsync("統籌助理沒有設定（Coordinator:Line），無法向 LINE 查詢額度。");
            return false;
        }

        var line = _services.GetRequiredService<ILineMessagingClient>();
        var quota = await line.GetMessageQuotaAsync(options.AccessToken, cancellationToken);
        var consumption = await line.GetMessageQuotaConsumptionAsync(options.AccessToken, cancellationToken);
        if (quota is not { Succeeded: true, Value: { } limit } || consumption is not { Succeeded: true, Value: { } used })
        {
            LineApiResult failed = quota.Succeeded ? consumption : quota;
            await error.WriteLineAsync(
                $"無法向 LINE 查詢統籌助理的額度：{failed.Outcome}（HTTP {failed.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}，x-line-request-id {failed.RequestId ?? "—"}）。");
            return false;
        }

        await output.WriteLineAsync(limit.Limited
            ? $"統籌助理本月訊息額度：{Number(limit.Value ?? 0)} 則（LINE 的每月上限）。"
            : "統籌助理本月訊息額度：無上限（LINE 回報 none）。");
        await output.WriteLineAsync($"本月已用：{Number(used.TotalUsage)} 則（LINE 統計的約略值，含官方帳號管理後台送出的訊息）。");
        if (limit is { Limited: true, Value: { } value })
        {
            var left = value - used.TotalUsage;
            await output.WriteLineAsync(left > 0
                ? $"剩餘：{Number(left)} 則。"
                : "剩餘：0 則。本月額度已用完：LINE 會拒絕所有推播，新訂位會標成「未通知成員」（coordinator-reservations list 會列出）。");
        }

        return true;
    }

    /// <summary>This month's pushes of every shop from the push log, and the §7 estimate.</summary>
    private async Task WriteShopsAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var zone = _services.GetRequiredService<IOptions<StatisticsOptions>>().Value.TryResolve()
            ?? TimeZoneInfo.FindSystemTimeZoneById(StatisticsOptions.DefaultTimeZone);
        var now = _services.GetRequiredService<TimeProvider>().GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var monthStart = Start(local.Year, local.Month, zone);
        var monthEnd = local.Month == 12 ? Start(local.Year + 1, 1, zone) : Start(local.Year, local.Month + 1, zone);
        var options = _services.GetRequiredService<DbContextOptions<AppDbContext>>();

        List<(Guid Id, string Code, string Name)> organizations;
        await using (var lookup = new AppDbContext(options, FixedOrganizationContext.None))
        {
            organizations = [.. (await lookup.Organizations.AsNoTracking()
                    .Select(organization => new { organization.Id, organization.Code, organization.Name })
                    .ToListAsync(cancellationToken))
                .OrderBy(organization => organization.Code, StringComparer.Ordinal)
                .Select(organization => (organization.Id, organization.Code, organization.Name))];
        }

        await output.WriteLineAsync(
            $"本月（{zone.Id} 的 {local.ToString("yyyy-MM", CultureInfo.InvariantCulture)}）各店由統籌助理送出的推播（依推播紀錄，LINE 接受的才算）：");
        long totalPushes = 0, totalEstimate = 0, totalReservations = 0, totalCommands = 0;
        var shops = 0;
        var terms = new List<string>();
        foreach (var (id, code, name) in organizations)
        {
            await using var dbContext = new AppDbContext(options, new FixedOrganizationContext(id));
            var settings = await dbContext.CoordinatorShopSettings.AsNoTracking()
                .Select(row => new { row.StoreName })
                .SingleOrDefaultAsync(cancellationToken);
            var pushes = (await dbContext.CoordinatorPushLogs.AsNoTracking()
                    .Where(log => log.SentAt >= monthStart && log.SentAt < monthEnd)
                    .GroupBy(log => log.Purpose)
                    .Select(group => new { Purpose = group.Key, Recipients = group.Sum(log => log.Recipients) })
                    .ToListAsync(cancellationToken))
                .ToDictionary(row => row.Purpose, row => (long)row.Recipients);
            if (settings is null && pushes.Count == 0)
            {
                continue;
            }

            var members = await dbContext.CoordinatorMembers.CountAsync(cancellationToken);
            var reservations = await dbContext.CoordinatorReservations
                .CountAsync(reservation => reservation.CreatedAt >= monthStart && reservation.CreatedAt < monthEnd, cancellationToken);
            var notNotified = await dbContext.CoordinatorReservations
                .CountAsync(
                    reservation => reservation.CreatedAt >= monthStart && reservation.CreatedAt < monthEnd
                        && reservation.MembersNotified == CoordinatorReservationNotice.NotSent,
                    cancellationToken);
            var commands = await dbContext.CoordinatorCommands
                .CountAsync(
                    command => command.Status == CoordinatorCommandStatus.Confirmed && command.ConfirmedAt >= monthStart && command.ConfirmedAt < monthEnd,
                    cancellationToken);

            var total = pushes.Values.Sum();
            var estimate = ((long)reservations * members) + ((long)commands * PushesPerCommand);
            shops++;
            totalPushes += total;
            totalEstimate += estimate;
            totalReservations += reservations;
            totalCommands += commands;
            terms.Add($"{Number(reservations)}×{Number(members)} ＋ {Number(commands)}×{PushesPerCommand}");
            var counts = string.Join("、", Purposes.Select(purpose => $"{Label(purpose)} {Number(pushes.GetValueOrDefault(purpose))}"));
            await output.WriteLineAsync(
                $"  組織 {code}（{settings?.StoreName ?? name}）：{counts}，合計 {Number(total)} 則。"
                + $"本月訂位 {Number(reservations)} 筆{(notNotified > 0 ? $"（未通知成員 {Number(notNotified)} 筆）" : string.Empty)}、"
                + $"成員 {Number(members)} 人、確認的店休類指令 {Number(commands)} 次；估算 {Number(estimate)} 則。");
        }

        if (shops == 0)
        {
            await output.WriteLineAsync("  （沒有設定統籌助理的店家）");
        }

        await output.WriteLineAsync($"合計：{Number(shops)} 家店，本月推播 {Number(totalPushes)} 則（訂位 {Number(totalReservations)} 筆、店休類指令 {Number(totalCommands)} 次）。");
        await output.WriteLineAsync(
            "估算（設計文件第 7 節）：Σ各店（每月訂位筆數 × 成員人數）＋ Σ各店（每月店休類指令數 × 5）"
            + (terms.Count == 0 ? $" ＝ 0 則。" : $" ＝ {string.Join(" ＋ ", terms)} ＝ {Number(totalEstimate)} 則。")
            + "回覆（按鈕、查看訂位、確認店休）不算則數。");
    }

    /// <summary>00:00 of the 1st of <paramref name="month"/> in <paramref name="zone"/>, as an instant.</summary>
    private static DateTimeOffset Start(int year, int month, TimeZoneInfo zone)
    {
        var midnight = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(30);
        }

        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
