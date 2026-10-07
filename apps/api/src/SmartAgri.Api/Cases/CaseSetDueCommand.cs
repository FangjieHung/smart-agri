using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Cases;

/// <summary>
/// The one-shot <c>case-set-due</c> subcommand (M7 plan §3 E, decision H; issue #256): moves one open
/// case's due time to any moment — <b>also the past</b>, which the API never accepts — and records a
/// <c>due-changed</c> event with no actor, then exits. The API-mode E2E runs it with <c>cy.exec</c> to make
/// a case overdue. Only in Development and Testing; any other environment is refused before the database
/// is touched.
/// </summary>
/// <remarks>The organization code is the one used at login (<see cref="Organization.Code"/>), matched
/// case-insensitively; the case must belong to that organization.</remarks>
public sealed class CaseSetDueCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "用法：case-set-due --organization <組織代碼> --case <案件 id> --due <ISO 8601 時間>\n" +
        "直接把一件未結案案件的處理時限改成指定時間（可以早於現在，用來製造逾期案件），並記一筆沒有操作人的「調整時限」。" +
        "只能在 Development 或 Testing 使用。\n" +
        "結束代碼：0 完成；1 找不到組織或案件、案件已結案或寫入失敗；2 參數錯誤或不是 Development／Testing。";

    private static readonly string[] AllowedEnvironments = ["Development", "Testing"];

    private readonly IServiceProvider _services;

    public CaseSetDueCommand(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs <c>case-set-due</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        return await new CaseSetDueCommand(scope.ServiceProvider).RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var environment = _services.GetRequiredService<IHostEnvironment>();
        if (!AllowedEnvironments.Any(environment.IsEnvironment))
        {
            await error.WriteLineAsync($"case-set-due 只能在 Development 或 Testing 使用（目前是 {environment.EnvironmentName}），沒有修改任何資料。");
            return ExitUsage;
        }

        if (!TryParse(args, out var code, out var caseId, out var dueAt, out var help, out var usageError))
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
            var options = _services.GetRequiredService<DbContextOptions<AppDbContext>>();
            Organization? organization;
            await using (var lookup = new AppDbContext(options, FixedOrganizationContext.None))
            {
                organization = await FindAsync(lookup, code!, cancellationToken);
            }

            if (organization is null)
            {
                await error.WriteLineAsync($"找不到組織代碼「{code}」，沒有修改任何資料。");
                return ExitFailed;
            }

            await using var dbContext = new AppDbContext(options, new FixedOrganizationContext(organization.Id));
            var item = await dbContext.Cases.SingleOrDefaultAsync(candidate => candidate.Id == caseId, cancellationToken);
            if (item is null)
            {
                await error.WriteLineAsync($"組織 {organization.Code} 沒有案件 {caseId}，沒有修改任何資料。");
                return ExitFailed;
            }

            if (!item.Status.IsOpen())
            {
                await error.WriteLineAsync($"案件 {caseId} 已結案，不能再改時限。");
                return ExitFailed;
            }

            var before = item.DueAt;
            dbContext.CaseEvents.Add(item.SetDueByOperations(dueAt, _services.GetRequiredService<TimeProvider>().GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);

            await output.WriteLineAsync(
                $"組織 {organization.Code} 的案件 {item.Id}（{item.Title}）處理時限：" +
                $"{before.ToString("o", CultureInfo.InvariantCulture)} → {item.DueAt.ToString("o", CultureInfo.InvariantCulture)}。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                $"無法修改時限：{exception.GetType().Name}: {exception.Message}（資料庫是否已執行 migrate？）");
            return ExitFailed;
        }
    }

    private static async Task<Organization?> FindAsync(AppDbContext dbContext, string code, CancellationToken cancellationToken)
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
        IReadOnlyList<string> args,
        out string? code,
        out Guid caseId,
        out DateTimeOffset dueAt,
        out bool help,
        out string error)
    {
        code = null;
        caseId = Guid.Empty;
        dueAt = default;
        help = false;
        error = string.Empty;
        var dueGiven = false;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    help = true;
                    break;
                case "--organization" or "--case" or "--due":
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
                    else if (name == "--case")
                    {
                        if (!Guid.TryParse(value, out caseId) || caseId == Guid.Empty)
                        {
                            error = "--case 必須是案件 id（GUID）。";
                            return false;
                        }
                    }
                    else if (DateTimeOffset.TryParse(
                        value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        dueAt = parsed;
                        dueGiven = true;
                    }
                    else
                    {
                        error = "--due 必須是 ISO 8601 時間，例如 2026-10-01T09:00:00+08:00。";
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

        if (caseId == Guid.Empty)
        {
            error = "必須用 --case 指定案件 id。";
            return false;
        }

        if (!dueGiven)
        {
            error = "必須用 --due 指定新的處理時限。";
            return false;
        }

        return true;
    }
}
