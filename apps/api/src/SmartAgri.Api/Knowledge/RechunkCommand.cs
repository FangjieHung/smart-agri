using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartAgri.Api.Ai;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Knowledge;

/// <summary>
/// The one-shot <c>rechunk</c> subcommand (pre-launch plan §3 A, #301): after the chunking rules
/// changed (<see cref="KnowledgeChunkFormat.Current"/> raised), cuts every processed version of
/// an older <see cref="KnowledgeDocumentVersion.ChunkFormat"/> again from its stored original
/// file, embeds the chunks that changed and swaps them in, organization by organization.
/// <c>--dry-run</c> prints what it would do and writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// Organizations are processed like <see cref="ReindexCommand"/>: each through a context acting
/// for it (<see cref="FixedOrganizationContext"/>), so the organization filter and write guard
/// apply as in a request, and each model call is recorded as that organization's
/// (<c>embed-document</c>, no account).
/// </para>
/// <para>
/// Only <c>ready</c> and <c>partially-readable</c> versions are cut again — the ones with chunks
/// (a failed one has none; a queued or processing one gets the current rules anyway). What
/// changes and which exclusions carry over is <see cref="KnowledgeRechunking.Plan"/>: a unit
/// whose chunks come out the same keeps them (ids, vectors, exclusions), so a version without
/// tables only has its format updated and costs no model call; one whose table rows were cut
/// before <see cref="KnowledgeChunkFormat.TableIdentity"/> (#324) only has their table index
/// filled in, likewise without a model call. A version whose file now reads
/// differently from what is stored is skipped and reported, still in the old format.
/// </para>
/// <para>
/// The model is called before any transaction, so the old chunks keep serving meanwhile. The
/// swap — delete the changed units' chunks, insert the new ones, set the format — is one
/// repeatable-read transaction that first checks the version is still in the old format and its
/// changed units' chunks are still the ones planned with (same ids, same exclusions): an owner
/// excluding a chunk at that moment makes the swap fail rather than be lost, and the version is
/// reported for another run. Running it again after it finished does nothing.
/// </para>
/// </remarks>
public sealed class RechunkCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "用法：rechunk [--organization <組織代碼>] [--dry-run]\n" +
        "以目前的切段規則，從原始檔重新切段、嵌入切段格式較舊的已處理版本；不指定組織時處理所有組織。\n" +
        "--dry-run 只列出要處理的版本與預計段落數，不呼叫嵌入模型，也不寫入資料庫。";

    private readonly IServiceProvider _services;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly EmbeddingProvider _provider;
    private readonly KnowledgeEmbeddingSettings _settings;
    private readonly IEnumerable<IDocumentTextExtractor> _extractors;
    private readonly KnowledgeOptions _options;
    private readonly ILogger<RechunkCommand> _logger;

    public RechunkCommand(
        IServiceProvider services,
        DbContextOptions<AppDbContext> dbContextOptions,
        EmbeddingProvider provider,
        KnowledgeEmbeddingSettings settings,
        IEnumerable<IDocumentTextExtractor> extractors,
        IOptions<KnowledgeOptions> options,
        ILogger<RechunkCommand> logger)
    {
        _services = services;
        _dbContextOptions = dbContextOptions;
        _provider = provider;
        _settings = settings;
        _extractors = extractors;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs <c>rechunk</c> in a new DI scope of <paramref name="services"/>.</summary>
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
        RechunkCommand command;
        try
        {
            command = scope.ServiceProvider.GetRequiredService<RechunkCommand>();
        }
        catch (OptionsValidationException exception)
        {
            // E.g. Ai:Embedding:Provider=Fake outside Development: the same refusal as startup.
            await error.WriteLineAsync($"設定無法使用：{exception.Message}");
            return ExitUsage;
        }

        return await command.RunAsync(args, output, error, cancellationToken);
    }

    /// <returns><see cref="ExitSuccess"/>; <see cref="ExitFailed"/> when the run stopped or left
    /// a version in the old format; <see cref="ExitUsage"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var organizationCode, out var dryRun, out var help, out var usageError))
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

        if (!dryRun && !_provider.IsConfigured)
        {
            await error.WriteLineAsync("沒有設定嵌入模型（Ai:Embedding:Provider），無法重新嵌入；可以先用 --dry-run 試算。");
            return ExitFailed;
        }

        var totals = new Totals();
        try
        {
            var organizations = await OrganizationsAsync(organizationCode, cancellationToken);
            if (organizations.Count == 0)
            {
                await error.WriteLineAsync(organizationCode is null ? "資料庫裡沒有任何組織。" : $"找不到組織代碼「{organizationCode}」。");
                return organizationCode is null ? ExitSuccess : ExitFailed;
            }

            await output.WriteLineAsync(dryRun
                ? $"試算重新切段（切段格式 {KnowledgeChunkFormat.Current}）：不呼叫嵌入模型，也不寫入資料庫。"
                : $"重新切段為切段格式 {KnowledgeChunkFormat.Current}，以模型 {_settings.Model} 嵌入有變動的段落。");
            foreach (var organization in organizations)
            {
                await RechunkAsync(organization, dryRun, output, totals, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "rechunk failed.");
            await error.WriteLineAsync(
                $"重新切段中斷：{Describe(exception)}。已完成的版本都已儲存；排除問題後再執行一次 rechunk，會從格式仍較舊的版本繼續。");
            return ExitFailed;
        }

        await output.WriteLineAsync(dryRun
            ? string.Create(CultureInfo.InvariantCulture, $"試算結果：{totals.Versions} 個版本，段落 {totals.OldChunks} → {totals.NewChunks} 個，預計重新嵌入 {totals.Embedded} 個段落；無法處理 {totals.Skipped} 個。沒有寫入任何資料。")
            : string.Create(CultureInfo.InvariantCulture, $"完成：{totals.Versions} 個版本改為切段格式 {KnowledgeChunkFormat.Current}，段落 {totals.OldChunks} → {totals.NewChunks} 個，重新嵌入 {totals.Embedded} 個段落；未處理 {totals.Skipped} 個。"));
        if (totals.UnmappedExclusions > 0)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"有 {totals.UnmappedExclusions} 個原本排除的段落對不到新段落（上面標示「！」），請到擷取預覽重新檢查排除設定。"));
        }

        return totals.Skipped == 0 ? ExitSuccess : ExitFailed;
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

    private async Task RechunkAsync((Guid Id, string Code, string Name) organization, bool dryRun, TextWriter output, Totals totals, CancellationToken cancellationToken)
    {
        var organizationContext = new FixedOrganizationContext(organization.Id);
        await using var dbContext = new AppDbContext(_dbContextOptions, organizationContext);
        var embedder = dryRun
            ? null
            : new KnowledgeChunkEmbedder(
                EmbeddingServiceCollectionExtensions.CreateGenerator(_services, organizationContext, new EfModelInvocationRecorder(_dbContextOptions, organizationContext)),
                _settings);

        var versions = await dbContext.KnowledgeDocumentVersions.AsNoTracking()
            .Where(version => version.ChunkFormat < KnowledgeChunkFormat.Current
                && (version.ProcessingStatus == KnowledgeDocumentStatus.Ready || version.ProcessingStatus == KnowledgeDocumentStatus.PartiallyReadable))
            .OrderBy(version => version.UploadedAt).ThenBy(version => version.Id)
            .ToListAsync(cancellationToken);
        await output.WriteLineAsync($"組織 {organization.Code}（{organization.Name}）：{versions.Count} 個版本的切段格式較舊。");

        foreach (var version in versions)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{version.FileName} 第 {version.VersionNumber} 版（{version.Id}）");
            var plan = await PlanAsync(dbContext, version, cancellationToken);
            if (plan.Mismatch is { } reason)
            {
                totals.Skipped++;
                await output.WriteLineAsync($"  ！{name}：略過，{reason}；仍是舊格式。");
                continue;
            }

            if (!dryRun && !await SwapAsync(dbContext, embedder!, version.Id, plan, cancellationToken))
            {
                totals.Skipped++;
                await output.WriteLineAsync($"  ！{name}：處理期間版本或段落被修改，未處理；請再執行一次 rechunk。");
                continue;
            }

            totals.Versions++;
            totals.OldChunks += plan.OldChunkCount;
            totals.NewChunks += plan.NewChunkCount;
            totals.Embedded += plan.ChunksToEmbed;
            totals.UnmappedExclusions += plan.UnmappedExclusions.Count;
            var tableIndexes = plan.TableIndexUpdates.Count == 0
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"補上 {plan.TableIndexUpdates.Count} 個段落的表格序號，");
            await output.WriteLineAsync(plan.Units.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"  {name}：段落不變（{plan.OldChunkCount} 個），{(tableIndexes.Length == 0 ? "只更新切段格式" : tableIndexes + "並更新切段格式")}。")
                : string.Create(CultureInfo.InvariantCulture, $"  {name}：段落 {plan.OldChunkCount} → {plan.NewChunkCount} 個，重新嵌入 {plan.ChunksToEmbed} 個，{tableIndexes}沿用 {plan.ExclusionsKept} 個排除。"));
            foreach (var lost in plan.UnmappedExclusions)
            {
                await output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    ！原本排除的段落「{lost.LocationLabel}」第 {lost.Ordinal + 1} 段（{lost.Id}）對不到新段落，新段落未排除。"));
            }
        }
    }

    /// <summary>The version's plan from its stored file, units and chunks; a mismatch when the
    /// file cannot be read any more.</summary>
    private async Task<RechunkPlan> PlanAsync(AppDbContext dbContext, KnowledgeDocumentVersion version, CancellationToken cancellationToken)
    {
        var content = await dbContext.KnowledgeFileContents.AsNoTracking()
            .Where(file => file.VersionId == version.Id)
            .Select(file => file.Bytes)
            .SingleOrDefaultAsync(cancellationToken);
        if (content is null)
        {
            return new RechunkPlan("找不到原始檔", [], [], 0, 0);
        }

        ProcessedVersion processed;
        try
        {
            processed = KnowledgeVersionReader.Read(version, content, _extractors, _options.ExtractionLimits, cancellationToken);
        }
        catch (DocumentExtractionException exception)
        {
            return new RechunkPlan($"原始檔無法讀取（{exception.Failure}）", [], [], 0, 0);
        }

        var units = (await dbContext.KnowledgeExtractedUnits.AsNoTracking()
                .Where(unit => unit.VersionId == version.Id)
                .Select(unit => new { unit.Ordinal, unit.LocationKind, unit.LocationLabel, unit.Text, unit.Readable })
                .ToListAsync(cancellationToken))
            .Select(unit => new StoredUnit(unit.Ordinal, unit.LocationKind, unit.LocationLabel, unit.Text, unit.Readable))
            .ToList();
        var chunks = await StoredChunksAsync(dbContext, version.Id, cancellationToken);
        return KnowledgeRechunking.Plan(version.ProcessingStatus, version.Issue, units, chunks, processed);
    }

    private static async Task<List<StoredChunk>> StoredChunksAsync(AppDbContext dbContext, Guid versionId, CancellationToken cancellationToken) =>
        [.. (await dbContext.KnowledgeChunks.AsNoTracking()
                .Where(chunk => chunk.VersionId == versionId)
                .Select(chunk => new { chunk.Id, chunk.UnitOrdinal, chunk.Ordinal, chunk.LocationLabel, chunk.Text, chunk.Excluded, chunk.TableIndex })
                .ToListAsync(cancellationToken))
            .Select(chunk => new StoredChunk(chunk.Id, chunk.UnitOrdinal, chunk.Ordinal, chunk.LocationLabel, chunk.Text, chunk.Excluded, chunk.TableIndex))];

    /// <summary>Embeds the plan's new chunks, then swaps them in (see the remarks); false when
    /// the version or its chunks changed since the plan was made, and nothing was written.</summary>
    private async Task<bool> SwapAsync(AppDbContext dbContext, KnowledgeChunkEmbedder embedder, Guid versionId, RechunkPlan plan, CancellationToken cancellationToken)
    {
        var texts = plan.Units
            .SelectMany(unit => unit.Chunks.Select(chunk => KnowledgeEmbeddingText.For(unit.Kind, chunk.LocationLabel, chunk.Text)))
            .ToList();
        var vectors = texts.Count == 0 ? [] : await embedder.EmbedDocumentsAsync(texts, accountId: null, cancellationToken);

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var version = await dbContext.KnowledgeDocumentVersions.SingleOrDefaultAsync(candidate => candidate.Id == versionId, cancellationToken);
            if (version is null
                || version.ChunkFormat >= KnowledgeChunkFormat.Current
                || version.ProcessingStatus is not (KnowledgeDocumentStatus.Ready or KnowledgeDocumentStatus.PartiallyReadable))
            {
                return false;
            }

            var changedUnits = plan.Units.Select(unit => unit.UnitOrdinal).ToList();
            var planned = plan.Units.SelectMany(unit => unit.OldChunkIds).ToHashSet();
            var stored = await StoredChunksAsync(dbContext, versionId, cancellationToken);
            var current = stored.Where(chunk => changedUnits.Contains(chunk.UnitOrdinal)).ToList();
            if (current.Count != planned.Count || !current.All(chunk => planned.Contains(chunk.Id)) || ExclusionsChanged(plan, current))
            {
                return false;
            }

            // The kept chunks whose table index is filled in (#324) must still be there.
            var storedIds = stored.Select(chunk => chunk.Id).ToHashSet();
            if (!plan.TableIndexUpdates.All(update => storedIds.Contains(update.ChunkId)))
            {
                return false;
            }

            foreach (var update in plan.TableIndexUpdates)
            {
                await dbContext.KnowledgeChunks
                    .Where(chunk => chunk.Id == update.ChunkId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(chunk => chunk.TableIndex, update.TableIndex), cancellationToken);
            }

            if (changedUnits.Count > 0)
            {
                await dbContext.KnowledgeChunks
                    .Where(chunk => chunk.VersionId == versionId && changedUnits.Contains(chunk.UnitOrdinal))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            var next = 0;
            foreach (var unit in plan.Units)
            {
                foreach (var chunkPlan in unit.Chunks)
                {
                    var chunk = KnowledgeChunk.Create(version, unit.UnitOrdinal, chunkPlan.Ordinal, chunkPlan.LocationLabel, chunkPlan.Text, chunkPlan.TableIndex);
                    chunk.SetEmbedding(vectors[next++], embedder.Model);
                    chunk.SetExcluded(chunkPlan.Excluded);
                    dbContext.KnowledgeChunks.Add(chunk);
                }
            }

            version.MarkRechunked();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsConcurrentChange(exception))
        {
            _logger.LogWarning(exception, "rechunk: knowledge version {VersionId} changed during the swap.", versionId);
            return false;
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>Whether an exclusion of a planned chunk changed since the plan.</summary>
    private static bool ExclusionsChanged(RechunkPlan plan, List<StoredChunk> current)
    {
        var excludedAtPlan = plan.Units.SelectMany(unit => unit.ExcludedOldChunkIds).ToHashSet();
        return current.Any(chunk => chunk.Excluded != excludedAtPlan.Contains(chunk.Id));
    }

    private static bool IsConcurrentChange(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => true,
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.ForeignKeyViolation } => true,
        { InnerException: { } inner } => IsConcurrentChange(inner),
        _ => false,
    };

    private static string Describe(Exception exception) => exception switch
    {
        KnowledgeEmbeddingException { InnerException: { } inner } => $"嵌入模型無法使用（{inner.GetType().Name}: {inner.Message}）",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private static bool TryParse(
        IReadOnlyList<string> args,
        out string? organizationCode,
        out bool dryRun,
        out bool help,
        out string error)
    {
        organizationCode = null;
        dryRun = false;
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
                case "--dry-run" when inlineValue is null:
                    dryRun = true;
                    break;
                case "--organization":
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = $"{name} 需要一個值。";
                        return false;
                    }

                    organizationCode = value.Trim();
                    break;
                default:
                    error = $"不認得的參數「{args[i]}」。";
                    return false;
            }
        }

        return true;
    }

    private sealed class Totals
    {
        public int Versions { get; set; }

        public int Skipped { get; set; }

        public int OldChunks { get; set; }

        public int NewChunks { get; set; }

        public int Embedded { get; set; }

        public int UnmappedExclusions { get; set; }
    }
}
