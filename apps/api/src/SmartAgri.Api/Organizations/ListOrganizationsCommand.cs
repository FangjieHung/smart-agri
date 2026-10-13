using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// The one-shot <c>list-organizations</c> subcommand (#333, #344): prints each organization's
/// code, name, creation time, number of accounts and this month's chat-model token usage against
/// its limit, one per line, and exits. Nothing personal is read or printed: no login names,
/// display names or contact details.
/// </summary>
/// <remarks>
/// <para>
/// The Organizations table is the tenant boundary itself and carries no organization filter, so
/// it needs no organization context. Accounts and model calls are organization filtered, so each
/// organization's count and usage are read through contexts acting as that organization
/// (<see cref="FixedOrganizationContext"/>), never by turning the filter off.
/// </para>
/// <para>
/// The usage is the quota check's own answer (<see cref="OrganizationTokenUsage"/>, the singleton
/// the visitor and LINE channels ask): the same month in <c>Statistics:TimeZone</c>, the same
/// counted purposes (input + output tokens), and the same limit — the organization's own or, when
/// it has none, <c>PublicChannels:DefaultMonthlyTokenLimit</c>, which is still enforced, so the
/// share is computed against it.
/// </para>
/// <para>
/// Organization has no creation column (and this command adds no migration); organizations are
/// created with time-ordered version 7 ids, which embed the creation time in milliseconds.
/// Rows whose id is not a version 7 id show "-".
/// </para>
/// </remarks>
public static class ListOrganizationsCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "用法：list-organizations\n" +
        "列出每個組織的組織代碼、名稱、建立時間（UTC）、帳號數，以及本月的對話模型 token 用量（輸入＋輸出）、上限與使用比例，" +
        "不含任何帳號的個人資料。月份與用量的算法和 set-token-limit 的額度判斷相同；未設上限的組織套用部署預設值。\n" +
        "結束代碼：0 完成；1 讀取失敗；2 參數錯誤。";

    /// <summary>Runs <c>list-organizations</c> in a new DI scope of <paramref name="services"/>.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
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

        try
        {
            await using var scope = services.CreateAsyncScope();
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
            await using var dbContext = new AppDbContext(options, FixedOrganizationContext.None);

            var organizations = await dbContext.Organizations.AsNoTracking()
                .Select(organization => new { organization.Id, organization.Code, organization.Name, organization.MonthlyTokenLimit })
                .ToListAsync(cancellationToken);

            // Accounts are organization-filtered, so each organization's count is read as that
            // organization rather than by turning the filter off (Tenancy source tests allow that in sign-in lookups only).
            // The usage is read the same way, by the quota check itself (its source acts as the organization too).
            var tokenUsage = scope.ServiceProvider.GetRequiredService<OrganizationTokenUsage>();
            var accountCounts = new Dictionary<Guid, int>();
            var usages = new Dictionary<Guid, OrganizationTokenUsageSnapshot>();
            foreach (var organization in organizations)
            {
                await using var organizationContext = new AppDbContext(options, new FixedOrganizationContext(organization.Id));
                accountCounts[organization.Id] = await organizationContext.Accounts.CountAsync(cancellationToken);
                usages[organization.Id] = await tokenUsage.GetAsync(organization.Id, cancellationToken);
            }

            // New columns go at the end, so scripts reading the first four keep working.
            await output.WriteLineAsync("組織代碼\t名稱\t建立時間(UTC)\t帳號數\t本月用量\t上限\t使用比例");
            foreach (var organization in organizations.OrderBy(o => CreatedAt(o.Id) ?? DateTimeOffset.MaxValue).ThenBy(o => o.Code, StringComparer.Ordinal))
            {
                var created = CreatedAt(organization.Id)?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
                var usage = usages[organization.Id];
                await output.WriteLineAsync(
                    $"{organization.Code}\t{organization.Name}\t{created}\t{accountCounts.GetValueOrDefault(organization.Id)}\t" +
                    $"{Tokens(usage.UsedTokens)}\t{DescribeLimit(organization.MonthlyTokenLimit is not null, usage.LimitTokens)}\t{DescribeShare(usage)}");
            }

            await output.WriteLineAsync($"共 {organizations.Count} 個組織。");
            if (usages.Count > 0)
            {
                var months = string.Join("、", usages.Values.Select(usage => usage.Month).Distinct().Order(StringComparer.Ordinal));
                var timeZone = scope.ServiceProvider.GetRequiredService<IOptions<StatisticsOptions>>().Value.TryResolveIanaId();
                var defaultLimit = scope.ServiceProvider.GetRequiredService<IOptions<PublicChannelsOptions>>().Value.EffectiveDefaultMonthlyTokenLimit;
                await output.WriteLineAsync(
                    $"本月為 {months}（{timeZone}）；用量為對話模型 token（輸入＋輸出），未設上限的組織套用部署預設值 {Tokens(defaultLimit)}。");
            }

            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法讀取組織：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    private static string Tokens(long tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The limit the quota check applies: the organization's own, or "未設上限" with the
    /// deployment default it falls back to.</summary>
    private static string DescribeLimit(bool hasOwnLimit, long effectiveLimit) =>
        hasOwnLimit ? Tokens(effectiveLimit) : $"未設上限（預設 {Tokens(effectiveLimit)}）";

    /// <summary>The share of the limit used, rounded down to 0.1% (so 99.99% never reads as 100.0%
    /// while replies still go out), followed by the quota check's own state when it is near or
    /// exceeded. A limit of 0 has no share: it is always exceeded.</summary>
    private static string DescribeShare(OrganizationTokenUsageSnapshot usage)
    {
        var share = usage.LimitTokens == 0
            ? "-"
            : (decimal.Floor((decimal)usage.UsedTokens * 1000 / usage.LimitTokens) / 10m).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        return usage.State switch
        {
            TokenUsageState.Exceeded => share + "（已達上限）",
            TokenUsageState.Near => share + "（接近上限）",
            _ => share,
        };
    }

    /// <summary>The creation time embedded in a version 7 id, or <see langword="null"/> for any other id.</summary>
    internal static DateTimeOffset? CreatedAt(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        if (bytes[6] >> 4 != 7)
        {
            return null;
        }

        var milliseconds = 0L;
        for (var i = 0; i < 6; i++)
        {
            milliseconds = (milliseconds << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
