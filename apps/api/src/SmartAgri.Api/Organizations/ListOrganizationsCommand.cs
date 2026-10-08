using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// The one-shot <c>list-organizations</c> subcommand (#333): prints each organization's code,
/// name, creation time and number of accounts, one per line, and exits. Nothing personal is
/// read or printed: no login names, display names or contact details.
/// </summary>
/// <remarks>
/// <para>
/// The Organizations table is the tenant boundary itself and carries no organization filter, so
/// it needs no organization context. Accounts are organization filtered; counting them across
/// organizations opts out of exactly that one named filter
/// (<see cref="AppDbContext.OrganizationFilter"/>) and only ever selects
/// <c>OrganizationId</c> and a count, never an account row.
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
        "列出每個組織的組織代碼、名稱、建立時間（UTC）與帳號數，不含任何帳號的個人資料。\n" +
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
            await using var dbContext = new AppDbContext(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), FixedOrganizationContext.None);

            var organizations = await dbContext.Organizations.AsNoTracking()
                .Select(organization => new { organization.Id, organization.Code, organization.Name })
                .ToListAsync(cancellationToken);
            var accountCounts = await dbContext.Accounts
                .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
                .GroupBy(account => account.OrganizationId)
                .Select(group => new { OrganizationId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.OrganizationId, row => row.Count, cancellationToken);

            await output.WriteLineAsync("組織代碼\t名稱\t建立時間(UTC)\t帳號數");
            foreach (var organization in organizations.OrderBy(o => CreatedAt(o.Id) ?? DateTimeOffset.MaxValue).ThenBy(o => o.Code, StringComparer.Ordinal))
            {
                var created = CreatedAt(organization.Id)?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
                await output.WriteLineAsync(
                    $"{organization.Code}\t{organization.Name}\t{created}\t{accountCounts.GetValueOrDefault(organization.Id)}");
            }

            await output.WriteLineAsync($"共 {organizations.Count} 個組織。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法讀取組織：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
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
