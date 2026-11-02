using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The one-shot <c>coordinator-binding-code</c> subcommand (store-coordinator design §5 Slice 1, #372):
/// the platform operator issues a 老闆 binding code for one organization (a shop) and the command prints
/// it once, with its expiry (in the shop's time zone), to standard output; the shop is named by its
/// settings' store name when it has settings (#373). Like <c>set-token-limit</c> it runs in any environment —
/// operators run it inside the Production container. The organization code is the one used at login
/// (<see cref="Organization.Code"/>), matched case-insensitively.
/// </summary>
/// <remarks>
/// Only the code's SHA-256 is stored, so a lost code cannot be shown again: issue a new one (the old one
/// expires by itself). Works whether or not the 統籌助理 is configured on this deployment.
/// </remarks>
public sealed class CoordinatorBindingCodeCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "用法：coordinator-binding-code --organization <組織代碼> [--hours <1-720>]\n" +
        "為這個組織（店家）產生一組老闆用的統籌助理綁定碼，只顯示這一次；老闆加統籌助理的 LINE 好友後輸入即可綁定。" +
        "預設 168 小時（7 天）內有效，最多 720 小時；只能使用一次。\n" +
        "結束代碼：0 完成；1 找不到組織或寫入失敗；2 參數錯誤。";

    private readonly IServiceProvider _services;

    public CoordinatorBindingCodeCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>coordinator-binding-code</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        return await new CoordinatorBindingCodeCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var code, out var validity, out var help, out var usageError))
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
                organization = await FindAsync(lookup, code!, cancellationToken);
            }

            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{code}」，沒有產生綁定碼。");
                return ExitFailed;
            }

            var issued = await _services.GetRequiredService<CoordinatorBindings>()
                .IssueAsync(organization.Id, CoordinatorRole.Owner, validity, issuedByLineUserId: null, cancellationToken);
            if (issued is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{code}」，沒有產生綁定碼。");
                return ExitFailed;
            }

            var zone = await _services.GetRequiredService<CoordinatorBindings>().TimeZoneOfAsync(organization.Id, cancellationToken);
            var local = TimeZoneInfo.ConvertTime(issued.ExpiresAt, zone);
            await output.WriteLineAsync($"組織 {organization.Code}（{issued.StoreName}）的老闆綁定碼：{BindingCodes.Format(issued.Code)}");
            await output.WriteLineAsync(
                $"有效期限：{local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}（{zone.Id}），只能使用一次。");
            await output.WriteLineAsync("請老闆加統籌助理的 LINE 好友後輸入這組碼。這組碼不會再顯示，遺失請重新產生。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法產生綁定碼：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    /// <summary>The organization whose login code is <paramref name="code"/> (any case); <see langword="null"/>
    /// when there is none or it is not a code. Also used by <c>coordinator-shop</c>.</summary>
    internal static async Task<Organization?> FindAsync(AppDbContext dbContext, string code, CancellationToken cancellationToken)
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

    private static bool TryParse(
        IReadOnlyList<string> args, out string? code, out TimeSpan validity, out bool help, out string error)
    {
        code = null;
        validity = CoordinatorBindingCode.OwnerDefaultValidity;
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
                case "--organization" or "--hours":
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
                    else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                             && hours >= 1
                             && hours <= CoordinatorBindingCode.MaxValidity.TotalHours)
                    {
                        validity = TimeSpan.FromHours(hours);
                    }
                    else
                    {
                        error = $"--hours 必須是 1 到 {CoordinatorBindingCode.MaxValidity.TotalHours:0} 的整數。";
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

        return true;
    }
}
