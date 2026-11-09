using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Ordering;

/// <summary>
/// The one-shot <c>ordering-shop</c> subcommand (online-ordering design O4, Slice O1, #397): the platform operator imports
/// a shop's online-ordering settings and menu from a JSON file or standard input (<c>import</c>), or prints a summary of
/// them (<c>show</c>). Runs in any environment. The organization code is the one used at login, matched case-insensitively.
/// </summary>
/// <remarks>
/// <c>import</c> reads and validates the whole file before touching the database (<see cref="OrderingShopJson.Parse"/>):
/// every error is printed to standard error as 「JSON 路徑：說明」 and nothing is written unless there are none; a slug
/// another shop has is refused like an error of the file. A change writes one
/// <see cref="OrganizationActivityAction.OrderingShopUpdated"/>, and a menu that differs from the current one becomes the next
/// version; an identical import writes nothing. Example file: <c>deploy/ordering-shop.example.json</c>.
/// <c>tables</c> (#402) prints each dine-in table's QR code URL (<c>/order/{slug}/table#&lt;token&gt;</c>, under
/// <c>PublicChannels:PublicBaseUrl</c> when it is set), gives one table a new token (<c>--regenerate</c>), and writes the
/// printable QR page (<c>--html</c>, <see cref="OrderingTablePrint"/>).
/// </remarks>
public sealed class OrderingShopCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    /// <summary>Largest file read (bytes), from a file or standard input.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    public const string Usage =
        "用法：ordering-shop import --organization <組織代碼> [--file <點餐設定.json>]\n" +
        "      ordering-shop show --organization <組織代碼>\n" +
        "      ordering-shop tables --organization <組織代碼> [--regenerate <桌號 id>] [--html <輸出檔.html>]\n" +
        "import：從 --file 指定的檔案（沒有 --file 時從標準輸入）讀取線上點餐的店家設定與菜單，先完整檢查（網址代號、店名、時區、電話、" +
        "取餐方式、可點餐時間、時段、菜單的分類、品項、選項群組與價格），有錯就逐條列出「位置：說明」且不寫入；全部正確才寫入，" +
        "菜單有變更時成為新的版本，有變更時記一筆組織紀錄。暫停接單與即時單準備時間由店員在後台切換，匯入不會改動。\n" +
        "show：印出目前的設定摘要（網址代號、取餐方式、可點餐時間、時段、菜單版本與分類、品項數）。\n" +
        "tables：印出內用每一桌的點餐網址（含桌號 QR code 的 token，有設定 PublicChannels:PublicBaseUrl 時是完整網址）；" +
        "--regenerate 讓指定桌號換一個新的 token（舊的 QR code 立刻失效，要重新列印）；--html 把每桌的 QR code 寫成可列印的 HTML（A4，每頁 6 張），" +
        "需要先設定 PublicChannels:PublicBaseUrl。\n" +
        "結束代碼：0 完成；1 找不到組織、還沒有設定或寫入失敗；2 參數錯誤或設定檔不正確（含網址代號已被其他店使用）。";

    private readonly IServiceProvider _services;

    public OrderingShopCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>ordering-shop</c> in a new DI scope of <paramref name="services"/>; <c>import</c> without
    /// <c>--file</c> reads <paramref name="input"/>.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        return await new OrderingShopCommand(scope.ServiceProvider).RunAsync(args, input, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(
        IReadOnlyList<string> args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var arguments, out var usageError))
        {
            await error.WriteLineAsync(usageError);
            await error.WriteLineAsync(Usage);
            return ExitUsage;
        }

        if (arguments.Help)
        {
            await output.WriteLineAsync(Usage);
            return ExitSuccess;
        }

        OrderingImport? import = null;
        if (arguments.Import)
        {
            var json = arguments.File is { } file
                ? await ReadFileAsync(file, error, cancellationToken)
                : await ReadInputAsync(input, error, cancellationToken);
            if (json is null)
            {
                return ExitUsage;
            }

            var parsed = OrderingShopJson.Parse(json);
            if (!parsed.IsValid)
            {
                foreach (var problem in parsed.Errors)
                {
                    await error.WriteLineAsync(problem.ToString());
                }

                await error.WriteLineAsync($"設定檔有 {parsed.Errors.Count} 個錯誤，沒有寫入任何設定。");
                return ExitUsage;
            }

            import = parsed.Import!;
        }

        try
        {
            var organization = await FindOrganizationAsync(arguments.Organization!, cancellationToken);
            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{arguments.Organization}」。");
                return ExitFailed;
            }

            var shops = _services.GetRequiredService<OrderingShops>();
            if (arguments.Tables)
            {
                return await TablesAsync(organization, shops, arguments, output, error, cancellationToken);
            }

            if (import is null)
            {
                if (await shops.FindAsync(organization.Id, cancellationToken) is not { } view)
                {
                    await error.WriteLineAsync(
                        $"組織 {organization.Code}（{organization.Name}）還沒有線上點餐設定；請用 ordering-shop import 匯入。");
                    return ExitFailed;
                }

                await output.WriteAsync(Summary(organization, view));
                return ExitSuccess;
            }

            var result = await shops.ImportAsync(organization.Id, import, cancellationToken);
            var shop = $"組織 {organization.Code}（{import.Shop.Name}）";
            switch (result.Outcome)
            {
                case OrderingImportOutcome.SlugTaken:
                    await error.WriteLineAsync($"$.shop.slug：「{import.Shop.Slug}」已經是另一家店的點餐網址代號，請換一個。");
                    await error.WriteLineAsync("設定檔有 1 個錯誤，沒有寫入任何設定。");
                    return ExitUsage;
                case OrderingImportOutcome.Conflict:
                    await error.WriteLineAsync($"{shop}的線上點餐設定同時有另一個匯入在寫入，這次沒有寫入任何設定；請重新執行。");
                    return ExitFailed;
                case OrderingImportOutcome.Created:
                    await output.WriteLineAsync($"已匯入{shop}的線上點餐設定，菜單第 {result.MenuVersion} 版。");
                    break;
                case OrderingImportOutcome.Updated:
                    await output.WriteLineAsync(
                        $"已更新{shop}的線上點餐設定，變更：{string.Join('、', result.Changed)}"
                        + (result.Changed.Contains("menu") ? $"（菜單第 {result.MenuVersion} 版）。" : "。"));
                    break;
                default:
                    await output.WriteLineAsync($"{shop}的線上點餐設定與菜單和檔案相同，沒有變更（菜單第 {result.MenuVersion} 版）。");
                    break;
            }

            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法{(arguments.Import ? "匯入" : "讀取")}線上點餐設定：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    /// <summary><c>tables</c> (#402): the URLs, <c>--regenerate</c>, <c>--html</c>.</summary>
    private async Task<int> TablesAsync(
        Organization organization, OrderingShops shops, Arguments arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (await shops.FindAsync(organization.Id, cancellationToken) is not { } view)
        {
            await error.WriteLineAsync($"組織 {organization.Code}（{organization.Name}）還沒有線上點餐設定；請用 ordering-shop import 匯入。");
            return ExitFailed;
        }

        var shop = view.Shop;
        var baseUrl = _services.GetRequiredService<IOptions<PublicChannelsOptions>>().Value.ResolvedPublicBaseUrl;
        if (arguments.Html is not null && baseUrl is null)
        {
            await error.WriteLineAsync("沒有設定 PublicChannels:PublicBaseUrl（對外網址），QR code 不知道要指向哪裡；請先設定再輸出 --html。");
            return ExitFailed;
        }

        if (arguments.Regenerate is { } tableId)
        {
            if (await shops.RegenerateAsync(organization.Id, tableId, cancellationToken) is not { } regenerated)
            {
                await error.WriteLineAsync($"組織 {organization.Code}（{shop.Name}）沒有桌號 id「{tableId}」；請用 ordering-shop tables 查看桌號。");
                return ExitFailed;
            }

            await output.WriteLineAsync($"已重新產生桌號 {regenerated.Name}（{regenerated.TableId}）的 QR code，舊的 QR code 已失效，請重新列印。");
        }

        var tables = await shops.TablesAsync(organization.Id, cancellationToken);
        if (tables.Count == 0)
        {
            await error.WriteLineAsync($"組織 {organization.Code}（{shop.Name}）沒有內用桌號；請在設定檔的 dineIn.tables 加上桌號後再匯入。");
            return ExitFailed;
        }

        string Url(OrderingTable table) => $"{baseUrl}/order/{shop.Slug}/table#{table.Token}";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"組織 {organization.Code}（{shop.Name}）的內用桌號（{tables.Count} 桌）：\n");
        foreach (var table in tables)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {table.Name}（{table.TableId}）：{Url(table)}\n");
        }

        if (!shop.DineInOffered)
        {
            text.Append("內用目前沒有開放（services.dineIn 是 false），這些 QR code 會顯示「已失效」，開放後恢復。\n");
        }

        if (baseUrl is null)
        {
            text.Append("沒有設定 PublicChannels:PublicBaseUrl，上面只有路徑；請在前面加上平台的對外網址。\n");
        }

        await output.WriteAsync(text.ToString());
        if (arguments.Html is { } path)
        {
            var html = OrderingTablePrint.Html(shop.Name, [.. tables.Select(table => new OrderingTableCard(table.Name, Url(table)))]);
            try
            {
                await File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await error.WriteLineAsync($"無法寫入「{path}」：{exception.GetType().Name}。");
                return ExitFailed;
            }

            await output.WriteLineAsync($"已把 {tables.Count} 桌的 QR code 寫到 {path}（A4，每頁 {OrderingTablePrint.CardsPerSheet} 張），用瀏覽器開啟後列印。");
        }

        return ExitSuccess;
    }

    /// <summary>The summary <c>show</c> prints.</summary>
    internal static string Summary(Organization organization, OrderingShopView view)
    {
        var (shop, menu) = (view.Shop, view.Menu);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"組織 {organization.Code}（{shop.Name}）的線上點餐設定\n");
        text.Append(CultureInfo.InvariantCulture, $"網址代號：{shop.Slug}\n");
        text.Append(CultureInfo.InvariantCulture, $"時區：{shop.TimeZoneId}\n");
        text.Append(CultureInfo.InvariantCulture, $"電話：{shop.Phone}\n");
        text.Append(
            CultureInfo.InvariantCulture,
            $"取餐方式：外帶預約（{OnOff(shop.TakeoutScheduled)}）、外帶即時（{OnOff(shop.TakeoutAsap)}）、"
            + $"外送預約（{OnOff(shop.Delivery)}）、外送儘快（{OnOff(shop.DeliveryAsap)}）、內用（{OnOff(shop.DineIn)}）\n");
        text.Append("可點餐時間：\n");
        foreach (var day in WeeklyHours.Days)
        {
            var periods = shop.OrderingHours[day];
            text.Append(
                CultureInfo.InvariantCulture,
                $"  {DayName(day)}：{Periods(periods)}\n");
        }

        text.Append(
            CultureInfo.InvariantCulture,
            $"時段：每 {shop.SlotMinutes} 分鐘，可預約 {shop.DaysAhead} 天（含今天），最短準備 {shop.MinLeadMinutes} 分鐘，" +
            $"每時段上限 {(shop.MaxOrdersPerSlot is { } max ? $"{max} 筆" : "不限")}\n");
        var (lower, upper) = AsapPrepBands.Minutes(shop.AsapPrepBand);
        text.Append(CultureInfo.InvariantCulture, $"即時單準備時間：{(lower == 0 ? $"{upper} 分鐘內" : $"{lower}–{upper} 分鐘")}\n");
        text.Append(CultureInfo.InvariantCulture, $"待確認逾時：營業中 {shop.PendingTimeoutMinutes} 分鐘沒有處理就自動取消\n");
        Delivery(text, shop);
        DineIn(text, shop);
        text.Append(CultureInfo.InvariantCulture, $"暫停接單：{(shop.Paused ? "是" : "否")}\n");
        if (menu is null)
        {
            text.Append("菜單：沒有\n");
        }
        else
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"菜單：第 {menu.Version} 版，{menu.Document.Categories.Count} 個分類、{menu.Document.Items.Count()} 個品項\n");
        }
        return text.ToString();
    }

    /// <summary>The delivery section (#401): zones with fee and minimum, hours, lead time, ASAP extra, note.</summary>
    private static void Delivery(StringBuilder text, OrderingShop shop)
    {
        if (shop.DeliverySettings is not { } delivery)
        {
            text.Append("外送：沒有設定\n");
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"外送區域（{delivery.Zones.Count} 個）：\n");
        foreach (var zone in delivery.Zones)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"  {zone.Name}（{zone.Id}）：運費 NT${zone.Fee}，最低消費 NT${zone.MinimumSubtotal}\n");
        }

        if (delivery.OrderingHours is { } hours)
        {
            text.Append("外送時間：\n");
            foreach (var day in WeeklyHours.Days)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {DayName(day)}：{Periods(hours[day])}\n");
            }
        }
        else
        {
            text.Append("外送時間：同可點餐時間\n");
        }

        text.Append(
            CultureInfo.InvariantCulture,
            $"外送準備：最短 {delivery.MinLeadMinutes} 分鐘，儘快外送另加 {delivery.AsapExtraMinutes} 分鐘\n");
        if (delivery.Note is { } note)
        {
            text.Append(CultureInfo.InvariantCulture, $"外送說明：{note.ReplaceLineEndings(" ")}\n");
        }
    }

    /// <summary>The dine-in section (#402): tables, auto-accept, hours, pending limit, note.</summary>
    private static void DineIn(StringBuilder text, OrderingShop shop)
    {
        if (shop.DineInSettings is not { } dineIn)
        {
            text.Append("內用：沒有設定\n");
            return;
        }

        text.Append(
            CultureInfo.InvariantCulture,
            $"內用桌號（{dineIn.Tables.Count} 桌）：{string.Join('、', dineIn.Tables.Select(table => table.Name))}（網址與 QR code：ordering-shop tables）\n");
        text.Append(
            CultureInfo.InvariantCulture,
            $"內用接單：{(dineIn.AutoAccept ? "自動接單" : "店員接單")}，每桌最多 {dineIn.MaxPendingPerTable} 筆待確認\n");
        if (dineIn.OrderingHours is { } hours)
        {
            text.Append("內用時間：\n");
            foreach (var day in WeeklyHours.Days)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {DayName(day)}：{Periods(hours[day])}\n");
            }
        }
        else
        {
            text.Append("內用時間：同可點餐時間\n");
        }

        if (dineIn.Note is { } note)
        {
            text.Append(CultureInfo.InvariantCulture, $"內用說明：{note.ReplaceLineEndings(" ")}\n");
        }
    }

    private static string Periods(IReadOnlyList<BusinessPeriod> periods) =>
        periods.Count == 0
            ? "不開放"
            : string.Join('、', periods.Select(period => period.IsOvernight ? $"{period}（跨午夜）" : period.ToString()));

    private static string OnOff(bool on) => on ? "開放" : "關閉";

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "週一",
        DayOfWeek.Tuesday => "週二",
        DayOfWeek.Wednesday => "週三",
        DayOfWeek.Thursday => "週四",
        DayOfWeek.Friday => "週五",
        DayOfWeek.Saturday => "週六",
        _ => "週日",
    };

    private async Task<Organization?> FindOrganizationAsync(string code, CancellationToken cancellationToken)
    {
        await using var lookup = new AppDbContext(_services.GetRequiredService<DbContextOptions<AppDbContext>>(), FixedOrganizationContext.None);
        return await CoordinatorBindingCodeCommand.FindAsync(lookup, code, cancellationToken);
    }

    private static async Task<string?> ReadFileAsync(string path, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                await error.WriteLineAsync($"找不到設定檔「{path}」。");
                return null;
            }

            if (file.Length > MaxFileBytes)
            {
                await error.WriteLineAsync($"設定檔「{path}」超過 {MaxFileBytes / 1024} KB。");
                return null;
            }

            return await File.ReadAllTextAsync(file.FullName, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync($"無法讀取設定檔「{path}」：{exception.GetType().Name}。");
            return null;
        }
    }

    /// <summary>Standard input, at most <see cref="MaxFileBytes"/> characters.</summary>
    private static async Task<string?> ReadInputAsync(TextReader input, TextWriter error, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(input, Console.In) && !Console.IsInputRedirected)
        {
            await error.WriteLineAsync("沒有 --file 時從標準輸入讀取設定檔，例如：ordering-shop import --organization <組織代碼> < 點餐設定.json");
            return null;
        }

        var buffer = new char[MaxFileBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaxFileBytes)
        {
            await error.WriteLineAsync($"標準輸入的設定檔超過 {MaxFileBytes / 1024} KB。");
            return null;
        }

        if (length == 0 || new string(buffer, 0, length).Trim().Length == 0)
        {
            await error.WriteLineAsync("沒有從標準輸入讀到設定檔；請用 --file 指定，或用「< 點餐設定.json」導入。");
            return null;
        }

        return new string(buffer, 0, length);
    }

    private sealed record Arguments(
        bool Help, bool Import, string? Organization, string? File, bool Tables = false, string? Regenerate = null, string? Html = null);

    private static bool TryParse(IReadOnlyList<string> args, out Arguments arguments, out string error)
    {
        arguments = new Arguments(Help: false, Import: false, null, null);
        error = string.Empty;
        if (args is ["--help" or "-h"])
        {
            arguments = arguments with { Help = true };
            return true;
        }

        if (args is not [("import" or "show" or "tables") and var verb, ..])
        {
            error = args.Count == 0 ? "必須指定 import、show 或 tables。" : $"不認得的動作「{args[0]}」；必須是 import、show 或 tables。";
            return false;
        }

        var import = verb == "import";
        var tables = verb == "tables";
        string? organization = null;
        string? file = null;
        string? regenerate = null;
        string? html = null;
        for (var i = 1; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    arguments = arguments with { Help = true };
                    return true;
                case "--organization":
                case "--file" when import:
                case "--regenerate" when tables:
                case "--html" when tables:
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = $"{name} 需要一個值。";
                        return false;
                    }

                    switch (name)
                    {
                        case "--organization":
                            organization = value.Trim();
                            break;
                        case "--regenerate":
                            regenerate = value.Trim();
                            break;
                        case "--html":
                            html = value;
                            break;
                        default:
                            file = value;
                            break;
                    }

                    break;
                default:
                    error = $"不認得的參數「{args[i]}」。";
                    return false;
            }
        }

        if (organization is null)
        {
            error = "必須用 --organization 指定組織代碼。";
            return false;
        }

        arguments = new Arguments(Help: false, import, organization, file, tables, regenerate, html);
        return true;
    }
}
