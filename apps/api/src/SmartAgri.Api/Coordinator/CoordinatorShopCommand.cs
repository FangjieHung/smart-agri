using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Secrets;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The one-shot <c>coordinator-shop</c> subcommand (store-coordinator design §5 Slice 2, decision A,
/// #373): the platform operator imports a shop's 統籌助理 settings from a JSON file
/// (<c>import</c>) or prints them in the same format (<c>show</c>), and sets or clears the shop's own LINE
/// official account's channel access token (<c>line-token</c>, #376), and makes an FB or Google authorization link for the
/// shop's owner (<c>auth-link</c>, #377). Like <c>coordinator-binding-code</c>
/// it runs in any environment. The organization code is the one used at login, matched
/// case-insensitively.
/// </summary>
/// <remarks>
/// <para>
/// <c>import</c> reads and validates the whole file before touching the database
/// (<see cref="ShopSettingsJson.Parse"/>): every error is printed to standard error as
/// 「JSON 路徑：說明」 and nothing is written unless there are none. A change writes one
/// <see cref="OrganizationActivityAction.CoordinatorShopSettingsUpdated"/>; an identical import writes
/// nothing. <c>show</c> prints only the settings file to standard output, so it can be saved, edited and
/// imported again. A shop that takes reservations (#389) gets its reservation page's address — on standard output after an
/// import, on standard error after <c>show</c>; a slug another shop has is refused like an error of the file.
/// </para>
/// <para>
/// <c>line-token</c> reads the token from standard input — never from an argument, which would stay in the shell's
/// history — checks it with LINE (<c>GET /v2/bot/info</c>), refuses the 統籌助理's own account, stores it encrypted
/// (<see cref="CoordinatorConnections"/>) and prints only the account's display name and basic id; <c>--clear</c>
/// removes it. Each writes one <see cref="OrganizationActivityAction.CoordinatorConnectionUpdated"/>. The token is
/// never printed, logged or put in an error message.
/// </para>
/// <para>
/// <c>auth-link</c> makes a single-use <see cref="CoordinatorAuthorizationPurpose.Initial"/> link
/// (<see cref="CoordinatorAuthorizationLinks"/>, default <see cref="CoordinatorAuthorizationLink.DefaultValidity"/>, at most
/// <see cref="CoordinatorAuthorizationLink.MaxValidity"/>) and prints it once — only its hash is stored. It refuses a
/// service whose provider is not configured, and a deployment without <c>PublicChannels:PublicBaseUrl</c>.
/// </para>
/// </remarks>
public sealed class CoordinatorShopCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    /// <summary>Largest settings file read (bytes).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>Longest token read from standard input (a LINE long-lived channel access token is about 170
    /// characters).</summary>
    public const int MaxTokenLength = 1000;

    public const string Usage =
        "用法：coordinator-shop import --organization <組織代碼> --file <設定檔.json>\n" +
        "      coordinator-shop show --organization <組織代碼>\n" +
        "      coordinator-shop line-token --organization <組織代碼> < token.txt\n" +
        "      coordinator-shop line-token --organization <組織代碼> --clear\n" +
        "      coordinator-shop auth-link --organization <組織代碼> --service facebook|google [--hours <1-168>]\n" +
        "import：先完整檢查設定檔（店名、時區、每週營業時間、四個情境的公告與動作、線上訂位），有錯就逐條列出「位置：說明」且不寫入；" +
        "全部正確才寫入，有變更時記一筆組織紀錄；有開放線上訂位時印出訂位網址。\n" +
        "show：以匯入的格式印出目前的設定，可以存檔、修改後再匯入；有開放線上訂位時，訂位網址印在標準錯誤（不會混進存下來的檔案）。\n" +
        "line-token：從標準輸入讀取該店自己 LINE 官方帳號的長期 channel access token（不要寫在參數裡），向 LINE 確認後加密保存，" +
        "只印出帳號名稱與 basic id；--clear 清除。之後設為 auto 的 LINE 群發會自動群發。每次設定或清除記一筆組織紀錄。\n" +
        "auth-link：產生一個讓老闆授權 FB 粉專（facebook）或 Google 商家（google）的連結，只顯示這一次；預設 72 小時內有效，" +
        "最多 168 小時，只能使用一次。老闆打開後登入並按同意即完成授權。\n" +
        "結束代碼：0 完成；1 找不到組織、還沒有設定、LINE 不接受權杖、這個部署沒有設定該服務或對外網址、或寫入失敗；" +
        "2 參數錯誤、設定檔或權杖格式不正確。";

    private readonly IServiceProvider _services;

    public CoordinatorShopCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>coordinator-shop</c> in a new DI scope of <paramref name="services"/>, without standard input.</summary>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunAsync(services, args, TextReader.Null, output, error, cancellationToken);

    /// <summary>Runs <c>coordinator-shop</c> in a new DI scope of <paramref name="services"/>; <c>line-token</c> reads
    /// the token from <paramref name="input"/>.</summary>
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
        return await new CoordinatorShopCommand(scope.ServiceProvider).RunAsync(args, input, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default) =>
        RunAsync(args, TextReader.Null, output, error, cancellationToken);

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

        if (arguments.Verb == Verb.LineToken)
        {
            return await LineTokenAsync(arguments, input, output, error, cancellationToken);
        }

        if (arguments.Verb == Verb.AuthLink)
        {
            return await AuthLinkAsync(arguments, output, error, cancellationToken);
        }

        ShopSettingsDocument? document = null;
        if (arguments.Verb == Verb.Import)
        {
            if (await ReadAsync(arguments.File!, error, cancellationToken) is not { } json)
            {
                return ExitUsage;
            }

            var parsed = ShopSettingsJson.Parse(json);
            if (!parsed.IsValid)
            {
                foreach (var problem in parsed.Errors)
                {
                    await error.WriteLineAsync(problem.ToString());
                }

                await error.WriteLineAsync($"設定檔有 {parsed.Errors.Count} 個錯誤，沒有寫入任何設定。");
                return ExitUsage;
            }

            document = parsed.Document!;
        }

        try
        {
            var organization = await FindOrganizationAsync(arguments.Organization!, cancellationToken);
            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{arguments.Organization}」。");
                return ExitFailed;
            }

            var shops = _services.GetRequiredService<CoordinatorShops>();
            if (document is null)
            {
                if (await shops.FindAsync(organization.Id, cancellationToken) is not { } stored)
                {
                    await error.WriteLineAsync(
                        $"組織 {organization.Code}（{organization.Name}）還沒有店家設定；請用 coordinator-shop import 匯入。");
                    return ExitFailed;
                }

                await output.WriteLineAsync(ShopSettingsJson.Write(stored));
                if (ReservationPage(stored) is { } storedPage)
                {
                    await error.WriteLineAsync(storedPage);
                }

                return ExitSuccess;
            }

            var result = await shops.ImportAsync(organization.Id, document, cancellationToken);
            if (result.Outcome == ShopImportOutcome.SlugTaken)
            {
                await error.WriteLineAsync(
                    $"$.reservations.slug：「{document.Reservations!.Slug}」已經是另一家店的訂位網址代號，請換一個。");
                await error.WriteLineAsync("設定檔有 1 個錯誤，沒有寫入任何設定。");
                return ExitUsage;
            }

            var shop = $"組織 {organization.Code}（{document.StoreName}）";
            await output.WriteLineAsync(result.Outcome switch
            {
                ShopImportOutcome.Created => $"已匯入{shop}的店家設定。",
                ShopImportOutcome.Updated => $"已更新{shop}的店家設定，變更：{Describe(result.Change)}。",
                _ => $"{shop}的店家設定和檔案相同，沒有變更。",
            });
            if (ReservationPage(document) is { } page)
            {
                await output.WriteLineAsync(page);
            }

            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法{(arguments.Verb == Verb.Import ? "匯入" : "讀取")}店家設定：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    /// <summary><c>line-token</c>: reads, checks and stores the shop's LINE token, or clears it (#376).</summary>
    private async Task<int> LineTokenAsync(Arguments arguments, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        string? token = null;
        if (!arguments.Clear)
        {
            if (ReferenceEquals(input, Console.In) && !Console.IsInputRedirected)
            {
                await error.WriteLineAsync("請貼上權杖，按 Enter 後再按 Ctrl-D（建議改用「< token.txt」從檔案讀取）：");
            }

            var (read, problem) = await ReadTokenAsync(input, cancellationToken);
            if (read is null)
            {
                await error.WriteLineAsync(problem);
                return ExitUsage;
            }

            token = read;
        }

        try
        {
            var organization = await FindOrganizationAsync(arguments.Organization!, cancellationToken);
            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{arguments.Organization}」。");
                return ExitFailed;
            }

            var shop = $"組織 {organization.Code}（{organization.Name}）";
            var connections = _services.GetRequiredService<CoordinatorConnections>();
            if (token is null)
            {
                var cleared = await connections.ClearAsync(organization.Id, CoordinatorService.Line, cancellationToken);
                await output.WriteLineAsync(cleared
                    ? $"已清除{shop}的 LINE 權杖；LINE 群發改為提醒。"
                    : $"{shop}沒有設定 LINE 權杖，沒有變更。");
                return ExitSuccess;
            }

            var line = _services.GetRequiredService<ILineMessagingClient>();
            var info = await line.GetBotInfoAsync(token, cancellationToken);
            if (!info.Succeeded || info.Value is not { } bot)
            {
                await error.WriteLineAsync(info.Outcome == LineApiOutcome.Unauthorized
                    ? $"LINE 不接受這個權杖（HTTP {info.StatusCode}）：請確認是該店官方帳號 Messaging API 的長期 channel access token。沒有寫入。"
                    : $"暫時無法向 LINE 確認權杖（{info.Outcome}{(info.StatusCode is { } status ? $"，HTTP {status}" : string.Empty)}），沒有寫入；請稍後再試。");
                return ExitFailed;
            }

            if (await IsCoordinatorAccountAsync(line, bot, cancellationToken))
            {
                await error.WriteLineAsync("這是統籌助理自己的官方帳號，不能用來群發店家公告；請設定該店自己的官方帳號的權杖。沒有寫入。");
                return ExitFailed;
            }

            var secret = _services.GetRequiredService<ISecretProtector>()
                .Protect(CoordinatorConnection.PurposeOf(CoordinatorService.Line), token, _services.GetRequiredService<TimeProvider>().GetUtcNow());
            var label = string.IsNullOrWhiteSpace(bot.DisplayName) ? bot.BasicId : $"{bot.DisplayName}（{bot.BasicId}）";
            var change = await connections.SetAsync(organization.Id, CoordinatorService.Line, secret, bot.UserId, label, cancellationToken);
            await output.WriteLineAsync(
                $"已{(change == CoordinatorConnectionChange.Created ? "設定" : "更新")}{shop}的 LINE 官方帳號：{label}。" +
                "設為 auto 的 LINE 群發之後會自動群發。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: no message, which could hold a value.
            await error.WriteLineAsync(
                $"無法{(token is null ? "清除" : "設定")} LINE 權杖：{exception.GetType().Name}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    /// <summary><c>auth-link</c>: makes an FB or Google authorization link and prints it once (#377).</summary>
    private async Task<int> AuthLinkAsync(Arguments arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var service = arguments.Service!.Value;
        var label = CoordinatorAuthorizations.Label(service);
        try
        {
            var organization = await FindOrganizationAsync(arguments.Organization!, cancellationToken);
            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{arguments.Organization}」，沒有產生授權連結。");
                return ExitFailed;
            }

            var links = _services.GetRequiredService<CoordinatorAuthorizationLinks>();
            if (links.PublicBaseUrl is null)
            {
                await error.WriteLineAsync(
                    "伺服器尚未設定對外網址（PublicChannels:PublicBaseUrl，.env 的 PUBLIC_BASE_URL），老闆的瀏覽器與服務無法回到這個伺服器；沒有產生授權連結。");
                return ExitFailed;
            }

            if (!links.IsAvailable(service))
            {
                var settings = service == CoordinatorService.Facebook
                    ? $"{CoordinatorFacebookOptions.SectionName}:AppId／AppSecret"
                    : $"{CoordinatorGoogleOptions.SectionName}:ClientId／ClientSecret";
                await error.WriteLineAsync($"這個部署還不能授權{label}（沒有設定 {settings}，或還不支援）；沒有產生授權連結。");
                return ExitFailed;
            }

            var issued = await links.IssueAsync(
                organization.Id, service, CoordinatorAuthorizationPurpose.Initial, TimeSpan.FromHours(arguments.Hours), cancellationToken);
            if (issued is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{arguments.Organization}」，沒有產生授權連結。");
                return ExitFailed;
            }

            var bindings = _services.GetRequiredService<CoordinatorBindings>();
            var storeName = await bindings.StoreNameAsync(organization.Id, cancellationToken) ?? organization.Name;
            var zone = await bindings.TimeZoneOfAsync(organization.Id, cancellationToken);
            var expiry = TimeZoneInfo.ConvertTime(issued.Link.ExpiresAt, zone);
            await output.WriteLineAsync($"組織 {organization.Code}（{storeName}）的{label}授權連結：");
            await output.WriteLineAsync(issued.Url);
            await output.WriteLineAsync(
                $"有效期限：{expiry.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}（{zone.Id}），只能使用一次。" +
                $"請傳給老闆本人，打開後登入{label}的帳號並按同意。這個連結不會再顯示，遺失請重新產生。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: no message.
            await error.WriteLineAsync($"無法產生授權連結：{exception.GetType().Name}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    /// <summary>「線上訂位網址：…」 of a shop that takes reservations (#389); <see langword="null"/> for one that does not.</summary>
    private string? ReservationPage(ShopSettingsDocument settings)
    {
        if (settings.Reservations is not { IsOpen: true, Slug: { } slug })
        {
            return null;
        }

        return _services.GetRequiredService<IOptions<PublicChannelsOptions>>().Value.ResolvedPublicBaseUrl is { } baseUrl
            ? $"線上訂位網址：{CoordinatorReservationEndpoints.Url(baseUrl, slug)}"
            : $"線上訂位網址：{{PUBLIC_BASE_URL}}/reserve/{slug}（這個部署還沒有設定 PublicChannels:PublicBaseUrl，.env 的 PUBLIC_BASE_URL）";
    }

    /// <summary>Whether <paramref name="bot"/> is the 統籌助理's own account (its broadcast would reach every shop owner).</summary>
    private async Task<bool> IsCoordinatorAccountAsync(ILineMessagingClient line, LineBotInfo bot, CancellationToken cancellationToken)
    {
        var options = _services.GetRequiredService<IOptions<CoordinatorLineOptions>>().Value;
        if (!options.IsConfigured)
        {
            return false;
        }

        var (coordinator, _) = await _services.GetRequiredService<CoordinatorBotIdentity>().GetUserIdAsync(line, options.AccessToken, cancellationToken);
        return string.Equals(coordinator, bot.UserId, StringComparison.Ordinal);
    }

    /// <summary>The token on standard input: one line (surrounding white space dropped), no white space or control
    /// characters inside, at most <see cref="MaxTokenLength"/> characters. The problem never quotes the input.</summary>
    private static async Task<(string? Token, string Problem)> ReadTokenAsync(TextReader input, CancellationToken cancellationToken)
    {
        var buffer = new char[MaxTokenLength + 64];
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

        var token = new string(buffer, 0, length).Trim();
        Array.Clear(buffer);
        if (token.Length == 0)
        {
            return (null, "沒有從標準輸入讀到權杖；請用「< token.txt」把權杖檔案導入（不要寫在參數裡）。");
        }

        if (token.Length > MaxTokenLength || length == buffer.Length)
        {
            return (null, $"權杖超過 {MaxTokenLength} 個字元，不像 LINE 的 channel access token；沒有寫入。");
        }

        if (token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return (null, "權杖只能是一行、不含空白；請確認檔案裡只有權杖本身。沒有寫入。");
        }

        return (token, string.Empty);
    }

    private async Task<Organization?> FindOrganizationAsync(string code, CancellationToken cancellationToken)
    {
        await using var lookup = new AppDbContext(_services.GetRequiredService<DbContextOptions<AppDbContext>>(), FixedOrganizationContext.None);
        return await CoordinatorBindingCodeCommand.FindAsync(lookup, code, cancellationToken);
    }

    private static string Describe(ShopSettingsChange change) =>
        string.Join(
            '、',
            change.Sections.Select(section => section == "scenarios"
                ? $"scenarios（{string.Join('、', change.Scenarios.Select(WireNames<ShopScenario>.ToWire))}）"
                : section));

    private static async Task<string?> ReadAsync(string path, TextWriter error, CancellationToken cancellationToken)
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

    private enum Verb
    {
        Import,
        Show,
        LineToken,
        AuthLink,
    }

    private sealed record Arguments(
        bool Help, Verb Verb, string? Organization, string? File, bool Clear = false, CoordinatorService? Service = null, int Hours = 72);

    private static bool TryParse(IReadOnlyList<string> args, out Arguments arguments, out string error)
    {
        arguments = new Arguments(Help: false, Verb.Show, null, null);
        error = string.Empty;
        if (args is ["--help" or "-h"])
        {
            arguments = arguments with { Help = true };
            return true;
        }

        if (args is not [("import" or "show" or "line-token" or "auth-link") and var verbName, ..])
        {
            error = args.Count == 0
                ? "必須指定 import、show、line-token 或 auth-link。"
                : $"不認得的動作「{args[0]}」；必須是 import、show、line-token 或 auth-link。";
            return false;
        }

        var verb = verbName switch { "import" => Verb.Import, "show" => Verb.Show, "line-token" => Verb.LineToken, _ => Verb.AuthLink };
        var import = verb == Verb.Import;
        var authLink = verb == Verb.AuthLink;
        string? organization = null;
        string? file = null;
        var clear = false;
        CoordinatorService? service = null;
        var hours = (int)CoordinatorAuthorizationLink.DefaultValidity.TotalHours;
        for (var i = 1; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    arguments = arguments with { Help = true };
                    return true;
                case "--clear" when verb == Verb.LineToken && inlineValue is null:
                    clear = true;
                    break;
                case "--organization":
                case "--file" when import:
                case "--service" or "--hours" when authLink:
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = $"{name} 需要一個值。";
                        return false;
                    }

                    if (name == "--organization")
                    {
                        organization = value.Trim();
                    }
                    else if (name == "--service")
                    {
                        service = value.Trim() switch
                        {
                            "facebook" => CoordinatorService.Facebook,
                            "google" => CoordinatorService.Google,
                            _ => null,
                        };
                        if (service is null)
                        {
                            error = "--service 必須是 facebook 或 google（LINE 的權杖用 line-token 設定）。";
                            return false;
                        }
                    }
                    else if (name == "--hours")
                    {
                        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out hours)
                            || hours < 1
                            || hours > CoordinatorAuthorizationLink.MaxValidity.TotalHours)
                        {
                            error = $"--hours 必須是 1 到 {CoordinatorAuthorizationLink.MaxValidity.TotalHours:0} 的整數。";
                            return false;
                        }
                    }
                    else
                    {
                        file = value;
                    }

                    break;
                default:
                    // line-token never echoes an argument's value: it could be a token given in place of standard input.
                    error = verb != Verb.LineToken
                        ? $"不認得的參數「{args[i]}」。"
                        : name.StartsWith("--", StringComparison.Ordinal)
                            ? $"不認得的參數「{name}」；權杖只能從標準輸入提供（「< token.txt」）。"
                            : "line-token 不接受權杖參數：請用「< token.txt」從標準輸入提供權杖。";
                    return false;
            }
        }

        if (organization is null)
        {
            error = "必須用 --organization 指定組織代碼。";
            return false;
        }

        if (import && file is null)
        {
            error = "必須用 --file 指定設定檔。";
            return false;
        }

        if (authLink && service is null)
        {
            error = "必須用 --service 指定 facebook 或 google。";
            return false;
        }

        arguments = new Arguments(Help: false, verb, organization, file, clear, service, hours);
        return true;
    }
}
