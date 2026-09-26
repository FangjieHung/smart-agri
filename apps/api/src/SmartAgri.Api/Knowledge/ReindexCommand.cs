using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SmartAgri.Api.Ai;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Knowledge;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Knowledge;

/// <summary>
/// The one-shot <c>reindex</c> subcommand (M2 plan, Slice 7): after <c>Ai:Embedding:Model</c>
/// changed, re-embeds every chunk whose vector is from another model (or that has none yet, from
/// before embeddings existed), organization by organization, in batches, printing progress.
/// Search only compares vectors of the configured model, so until this has run, chunks of the
/// old model are simply not found.
/// </summary>
/// <remarks>
/// <para>
/// Each organization is processed through a context acting for it
/// (<see cref="FixedOrganizationContext"/>), so the organization filter and write guard apply as
/// in a request, and each model call is recorded as that organization's (<c>embed-document</c>,
/// no account). Listing the organizations needs no cross-organization access: the
/// <c>Organizations</c> table is not organization scoped.
/// </para>
/// <para>
/// Excluded chunks are re-embedded too (see <see cref="KnowledgeChunk"/>). Each batch is saved
/// as it completes and only the vector columns are written, so an owner's exclusion made
/// meanwhile survives, and after a failure (the model is unreachable) running it again carries
/// on with what is left. The Api can keep running meanwhile.
/// </para>
/// </remarks>
public sealed class ReindexCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "用法：reindex [--organization <組織代碼>] [--batch-size <1-2048>]\n" +
        "以目前設定的嵌入模型（Ai:Embedding:Model）重新嵌入其他模型產生的段落向量；不指定組織時處理所有組織。";

    private readonly IServiceProvider _services;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly EmbeddingProvider _provider;
    private readonly KnowledgeEmbeddingSettings _settings;
    private readonly ILogger<ReindexCommand> _logger;

    public ReindexCommand(
        IServiceProvider services,
        DbContextOptions<AppDbContext> dbContextOptions,
        EmbeddingProvider provider,
        KnowledgeEmbeddingSettings settings,
        ILogger<ReindexCommand> logger)
    {
        _services = services;
        _dbContextOptions = dbContextOptions;
        _provider = provider;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Runs <c>reindex</c> in a new DI scope of <paramref name="services"/>.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(error);
        await using var scope = services.CreateAsyncScope();
        ReindexCommand command;
        try
        {
            command = scope.ServiceProvider.GetRequiredService<ReindexCommand>();
        }
        catch (OptionsValidationException exception)
        {
            // E.g. Ai:Embedding:Provider=Fake outside Development: the same refusal as startup.
            await error.WriteLineAsync($"嵌入模型設定無法使用：{exception.Message}");
            return ExitUsage;
        }

        return await command.RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var organizationCode, out var batchSize, out var help, out var usageError))
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

        if (!_provider.IsConfigured)
        {
            await error.WriteLineAsync("沒有設定嵌入模型（Ai:Embedding:Provider），無法重新嵌入。");
            return ExitFailed;
        }

        var settings = new KnowledgeEmbeddingSettings(_settings.Model, _settings.DocumentPrefix, _settings.QueryPrefix, batchSize ?? _settings.BatchSize);
        try
        {
            var organizations = await OrganizationsAsync(organizationCode, cancellationToken);
            if (organizations.Count == 0)
            {
                await error.WriteLineAsync(organizationCode is null ? "資料庫裡沒有任何組織。" : $"找不到組織代碼「{organizationCode}」。");
                return organizationCode is null ? ExitSuccess : ExitFailed;
            }

            await output.WriteLineAsync($"以模型 {settings.Model} 重新嵌入段落，每批 {settings.BatchSize} 個。");
            var total = 0;
            foreach (var organization in organizations)
            {
                total += await ReindexAsync(organization, settings, output, cancellationToken);
            }

            await output.WriteLineAsync($"完成：共重新嵌入 {total} 個段落。");
            return ExitSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "reindex failed.");
            await error.WriteLineAsync(
                $"重新嵌入中斷：{Describe(exception)}。已完成的批次都已儲存；排除問題後再執行一次 reindex，會從尚未完成的段落繼續。");
            return ExitFailed;
        }
    }

    private async Task<List<(Guid Id, string Code, string Name)>> OrganizationsAsync(string? code, CancellationToken cancellationToken)
    {
        await using var dbContext = new AppDbContext(_dbContextOptions, FixedOrganizationContext.None);
        var organizations = dbContext.Organizations.AsNoTracking();
        if (code is not null)
        {
            organizations = organizations.Where(organization => organization.Code == code);
        }

        return [.. (await organizations
            .OrderBy(organization => organization.Code)
            .Select(organization => new { organization.Id, organization.Code, organization.Name })
            .ToListAsync(cancellationToken))
            .Select(organization => (organization.Id, organization.Code, organization.Name))];
    }

    /// <summary>Re-embeds one organization's stale chunks; returns how many.</summary>
    private async Task<int> ReindexAsync((Guid Id, string Code, string Name) organization, KnowledgeEmbeddingSettings settings, TextWriter output, CancellationToken cancellationToken)
    {
        var organizationContext = new FixedOrganizationContext(organization.Id);
        await using var dbContext = new AppDbContext(_dbContextOptions, organizationContext);
        var embedder = new KnowledgeChunkEmbedder(
            EmbeddingServiceCollectionExtensions.CreateGenerator(_services, organizationContext, new EfModelInvocationRecorder(_dbContextOptions, organizationContext)),
            settings);
        VectorStoreCollection<Guid, KnowledgeChunk> collection = new KnowledgeChunkVectorCollection(dbContext, settings.Model);

        var model = settings.Model;
        var stale = await dbContext.KnowledgeChunks.CountAsync(chunk => chunk.EmbeddingModel != model, cancellationToken);
        await output.WriteLineAsync($"組織 {organization.Code}（{organization.Name}）：{stale} 個段落需要重新嵌入。");

        var done = 0;
        while (true)
        {
            // Tracked, so saving writes only the vector columns (see KnowledgeChunkVectorCollection.UpsertAsync).
            var batch = await dbContext.KnowledgeChunks
                .Where(chunk => chunk.EmbeddingModel != model)
                .OrderBy(chunk => chunk.Id)
                .Take(settings.BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var versionIds = batch.Select(chunk => chunk.VersionId).Distinct().ToList();
            var kinds = (await dbContext.KnowledgeExtractedUnits.AsNoTracking()
                    .Where(unit => versionIds.Contains(unit.VersionId))
                    .Select(unit => new { unit.VersionId, unit.Ordinal, unit.LocationKind })
                    .ToListAsync(cancellationToken))
                .ToDictionary(unit => (unit.VersionId, unit.Ordinal), unit => unit.LocationKind);

            var vectors = await embedder.EmbedDocumentsAsync(
                [.. batch.Select(chunk => KnowledgeEmbeddingText.For(kinds[(chunk.VersionId, chunk.UnitOrdinal)], chunk.LocationLabel, chunk.Text))],
                accountId: null,
                cancellationToken);
            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].SetEmbedding(vectors[i], model);
            }

            await collection.UpsertAsync(batch, cancellationToken);
            dbContext.ChangeTracker.Clear();

            done += batch.Count;
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {done}/{Math.Max(stale, done)}"));
        }

        return done;
    }

    private static string Describe(Exception exception) => exception switch
    {
        KnowledgeEmbeddingException { InnerException: { } inner } => $"嵌入模型無法使用（{inner.GetType().Name}: {inner.Message}）",
        DbUpdateConcurrencyException => "段落在重新嵌入時被刪除或修改",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private static bool TryParse(
        IReadOnlyList<string> args,
        out string? organizationCode,
        out int? batchSize,
        out bool help,
        out string error)
    {
        organizationCode = null;
        batchSize = null;
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
                case "--organization" or "--batch-size":
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = $"{name} 需要一個值。";
                        return false;
                    }

                    if (name == "--organization")
                    {
                        organizationCode = value.Trim();
                    }
                    else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                        && size is >= 1 and <= KnowledgeEmbeddingSettings.MaxBatchSize)
                    {
                        batchSize = size;
                    }
                    else
                    {
                        error = $"--batch-size 必須是 1–{KnowledgeEmbeddingSettings.MaxBatchSize} 的整數。";
                        return false;
                    }

                    break;
                default:
                    error = $"不認得的參數「{args[i]}」。";
                    return false;
            }
        }

        return true;
    }
}
