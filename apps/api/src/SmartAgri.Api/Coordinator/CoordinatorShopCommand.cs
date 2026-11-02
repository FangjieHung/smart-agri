using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The one-shot <c>coordinator-shop</c> subcommand (store-coordinator design §5 Slice 2, decision A,
/// #373): the platform operator imports a shop's 統籌助理 settings from a JSON file
/// (<c>import</c>) or prints them in the same format (<c>show</c>). Like <c>coordinator-binding-code</c>
/// it runs in any environment. The organization code is the one used at login, matched
/// case-insensitively.
/// </summary>
/// <remarks>
/// <c>import</c> reads and validates the whole file before touching the database
/// (<see cref="ShopSettingsJson.Parse"/>): every error is printed to standard error as
/// 「JSON 路徑：說明」 and nothing is written unless there are none. A change writes one
/// <see cref="OrganizationActivityAction.CoordinatorShopSettingsUpdated"/>; an identical import writes
/// nothing. <c>show</c> prints only the settings file to standard output, so it can be saved, edited and
/// imported again.
/// </remarks>
public sealed class CoordinatorShopCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    /// <summary>Largest settings file read (bytes).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    public const string Usage =
        "用法：coordinator-shop import --organization <組織代碼> --file <設定檔.json>\n" +
        "      coordinator-shop show --organization <組織代碼>\n" +
        "import：先完整檢查設定檔（店名、時區、每週營業時間、四個情境的公告與動作），有錯就逐條列出「位置：說明」且不寫入；" +
        "全部正確才寫入，有變更時記一筆組織紀錄。\n" +
        "show：以匯入的格式印出目前的設定，可以存檔、修改後再匯入。\n" +
        "結束代碼：0 完成；1 找不到組織、還沒有設定或寫入失敗；2 參數錯誤或設定檔不正確。";

    private readonly IServiceProvider _services;

    public CoordinatorShopCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>coordinator-shop</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        return await new CoordinatorShopCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
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

        ShopSettingsDocument? document = null;
        if (arguments.Import)
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
            Organization? organization;
            await using (var lookup = new AppDbContext(_services.GetRequiredService<DbContextOptions<AppDbContext>>(), FixedOrganizationContext.None))
            {
                organization = await CoordinatorBindingCodeCommand.FindAsync(lookup, arguments.Organization!, cancellationToken);
            }

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
                return ExitSuccess;
            }

            var result = await shops.ImportAsync(organization.Id, document, cancellationToken);
            var shop = $"組織 {organization.Code}（{document.StoreName}）";
            await output.WriteLineAsync(result.Outcome switch
            {
                ShopImportOutcome.Created => $"已匯入{shop}的店家設定。",
                ShopImportOutcome.Updated => $"已更新{shop}的店家設定，變更：{Describe(result.Change)}。",
                _ => $"{shop}的店家設定和檔案相同，沒有變更。",
            });
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法{(arguments.Import ? "匯入" : "讀取")}店家設定：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
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

    private sealed record Arguments(bool Help, bool Import, string? Organization, string? File);

    private static bool TryParse(IReadOnlyList<string> args, out Arguments arguments, out string error)
    {
        arguments = new Arguments(Help: false, Import: false, null, null);
        error = string.Empty;
        if (args is ["--help" or "-h"])
        {
            arguments = arguments with { Help = true };
            return true;
        }

        if (args is not [("import" or "show") and var verb, ..])
        {
            error = args.Count == 0 ? "必須指定 import 或 show。" : $"不認得的動作「{args[0]}」；必須是 import 或 show。";
            return false;
        }

        var import = verb == "import";
        string? organization = null;
        string? file = null;
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
                    else
                    {
                        file = value;
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

        if (import && file is null)
        {
            error = "必須用 --file 指定設定檔。";
            return false;
        }

        arguments = new Arguments(Help: false, import, organization, file);
        return true;
    }
}
