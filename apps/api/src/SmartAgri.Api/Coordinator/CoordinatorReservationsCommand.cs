using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The one-shot <c>coordinator-reservations</c> subcommand (store-reservations design §3.1 point 11, #389): the platform
/// operator lists a shop's upcoming reservations and those of the last days — time, party size, the customer's name and
/// phone, status, whether the members were told. Like the other coordinator subcommands it runs in any environment; the
/// organization code is the one used at login, matched case-insensitively.
/// </summary>
/// <remarks>
/// The phone number shows only its last three digits (<see cref="ReservationContact.MaskPhone"/>); the Email address and
/// the note are never printed, only whether there is one. Times are in the shop's time zone.
/// </remarks>
public sealed class CoordinatorReservationsCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const int DefaultDays = 7;

    /// <summary>The most rows of each list.</summary>
    public const int Limit = 200;

    public const string Usage =
        "用法：coordinator-reservations list --organization <組織代碼> [--days <1-90>]\n" +
        "列出這家店的線上訂位：即將到來的（時間早的在前，最多 200 筆），以及最近幾天內已經過了時間的（預設 7 天，最新的在前，最多 200 筆）。" +
        "每筆印出訂位時間、人數、姓名、電話（只顯示最後 3 碼）、狀態（pending 待處理／accepted 已接受／declined 已婉拒／cancelled 已取消／expired 已過期）、" +
        "是否已通知成員（已通知成員／未通知成員／尚未通知成員），以及有沒有 Email、備註（不印內容）。時間為店家設定的時區。" +
        "有「未通知成員」的訂位時，最前面多印一行提醒（統籌助理推播失敗或本月額度用完，用 coordinator-quota 查看）。\n" +
        "結束代碼：0 完成；1 找不到組織或讀取失敗；2 參數錯誤。";

    private readonly IServiceProvider _services;

    public CoordinatorReservationsCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>coordinator-reservations</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        return await new CoordinatorReservationsCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var code, out var days, out var help, out var usageError))
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
            var shop = $"組織 {organization.Code}（{settings?.StoreName ?? organization.Name}）";
            var now = _services.GetRequiredService<TimeProvider>().GetUtcNow();
            var store = _services.GetRequiredService<CoordinatorReservations>();
            var upcoming = await store.FromAsync(organization.Id, now, Limit, cancellationToken);
            var recent = await store.BetweenAsync(organization.Id, now.AddDays(-days), now, Limit, cancellationToken);
            var state = settings?.Reservations switch
            {
                null => "沒有設定線上訂位",
                { IsOpen: true, Slug: { } slug } => $"線上訂位開放中（/reserve/{slug}）",
                _ => "線上訂位未開放",
            };
            await output.WriteLineAsync($"{shop}：{state}。時間為 {zone.Id}。");
            var notNotified = upcoming.Concat(recent).Count(reservation => reservation.MembersNotified == CoordinatorReservationNotice.NotSent);
            if (notNotified > 0)
            {
                await output.WriteLineAsync(
                    $"注意：其中 {notNotified} 筆「未通知成員」——老闆與店長沒有收到 LINE 卡片（推播都失敗，或統籌助理本月額度用完；用 coordinator-quota 查看）。"
                    + "請另外通知店家，成員仍可傳「訂位」查看與處理。");
            }

            await output.WriteLineAsync();
            await output.WriteLineAsync($"即將到來（{upcoming.Count} 筆）：");
            await WriteAsync(output, upcoming, zone);
            await output.WriteLineAsync();
            await output.WriteLineAsync($"最近 {days} 天（{recent.Count} 筆）：");
            await WriteAsync(output, recent, zone);
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"無法讀取訂位：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    /// <summary>One line of the list: never the Email address or the note, the phone masked.</summary>
    public static string Describe(CoordinatorReservation reservation, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(zone);
        var notified = reservation.MembersNotified switch
        {
            CoordinatorReservationNotice.Sent => "已通知成員",
            CoordinatorReservationNotice.NotSent => "未通知成員",
            _ => "尚未通知成員",
        };
        var extras = (reservation.CustomerEmail is null ? "無 Email" : "有 Email") + (reservation.Note is null ? string.Empty : "｜有備註");
        return $"{Time(reservation.ReservedAt, zone)}（{ShopAnnouncements.FormatDate(reservation.Date)}）｜{reservation.PartySize} 位｜"
            + $"{reservation.CustomerName}｜{ReservationContact.MaskPhone(reservation.CustomerPhone)}｜"
            + $"{WireNames<CoordinatorReservationStatus>.ToWire(reservation.Status)}｜{notified}｜{extras}｜"
            + $"送出 {Time(reservation.CreatedAt, zone)}｜訂位 {reservation.Id}";
    }

    private static async Task WriteAsync(TextWriter output, IReadOnlyList<CoordinatorReservation> reservations, TimeZoneInfo zone)
    {
        if (reservations.Count == 0)
        {
            await output.WriteLineAsync("  （沒有）");
        }

        foreach (var reservation in reservations)
        {
            await output.WriteLineAsync("  " + Describe(reservation, zone));
        }
    }

    private static string Time(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static bool TryParse(IReadOnlyList<string> args, out string? code, out int days, out bool help, out string error)
    {
        code = null;
        days = DefaultDays;
        help = false;
        error = string.Empty;
        if (args is ["--help" or "-h"])
        {
            help = true;
            return true;
        }

        if (args is not ["list", ..])
        {
            error = args.Count == 0 ? "必須指定 list。" : $"不認得的動作「{args[0]}」；必須是 list。";
            return false;
        }

        for (var i = 1; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    help = true;
                    return true;
                case "--organization" or "--days":
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
                    else if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out days)
                             || days is < 1 or > CoordinatorReservation.RetentionDays)
                    {
                        error = $"--days 必須是 1 到 {CoordinatorReservation.RetentionDays} 的整數。";
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
