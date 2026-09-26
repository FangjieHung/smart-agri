using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Ai;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Knowledge;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Knowledge.Evaluation;

/// <summary>
/// The one-shot <c>eval-retrieval</c> subcommand (M2 plan Slice 16; ticket #50): measures
/// retrieval with the configured embedding model against a question bank, so the relevance
/// threshold (<c>Retrieval:MinScore</c>) and the model are chosen by data, not by feel
/// (testing ADR; grounded-answers ADR).
/// </summary>
/// <remarks>
/// <para>
/// It works in an organization of its own, <see cref="OrganizationCode"/> (created on first use,
/// with an account <see cref="AccountLoginName"/> that has no password and cannot sign in), so
/// it never touches the developer's demo data and every run starts from the same state: the
/// organization's knowledge bases are deleted as <c>DELETE /api/v1/knowledge-bases/{id}</c>
/// deletes one, then the set is imported through the normal upload → process → approve pipeline
/// (<see cref="KnowledgeSetImporter"/>: a document's last version in effect, the earlier ones
/// archived). Every question then goes through <see cref="KnowledgeRetriever"/> exactly as the
/// retrieval preview and M3's answers search — top 5, no pending versions — and the Markdown
/// report (<see cref="RetrievalEvalReport"/>) is written.
/// </para>
/// <para>
/// Like <c>reindex</c>, it acts for that organization through contexts pinned to it
/// (<see cref="FixedOrganizationContext"/>) and builds the organization's
/// <see cref="KnowledgeRetriever"/> from them as the DI container builds one for a request —
/// only the job runner may switch a scope's organization.
/// </para>
/// <para>
/// Development and Testing only: it writes an organization into the database, which has no place
/// in a customer's. It needs a real model and key to mean anything, so CI never runs it; the
/// integration tests run it with <c>Fake</c> to prove the pipeline. Model calls are recorded as
/// the evaluation organization's, like any other.
/// </para>
/// </remarks>
public sealed class EvalRetrievalCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string OrganizationCode = "retrieval-eval";
    public const string OrganizationName = "檢索評測（安心商行示範資料）";
    public const string AccountLoginName = "eval";
    public const string AccountDisplayName = "檢索評測";

    /// <summary>The environments it runs in (as <c>Fake</c> embeddings do).</summary>
    public static IReadOnlyList<string> Environments => EmbeddingOptions.FakeEnvironments;

    public static readonly TimeSpan DefaultProcessingTimeout = TimeSpan.FromMinutes(10);

    public const string Usage =
        "用法：eval-retrieval [--set <題庫目錄>] [--report <報告檔路徑>] [--timeout <秒數>]\n" +
        "以目前設定的嵌入模型（Ai:Embedding）跑完檢索評測題庫，寫出 Markdown 報告。只能在 Development 或 Testing 環境執行。\n" +
        "  --set      題庫目錄（documents.json、questions.json 與檔案），預設為隨程式附帶的示範題庫（apps/api/eval/retrieval）。\n" +
        "  --report   報告檔路徑，預設為 <repo>/docs/evals/<日期>-retrieval-<模型>.md，已存在就覆寫。\n" +
        "  --timeout  等待文件處理完成的秒數上限，預設 600。";

    private readonly IServiceProvider _services;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly EmbeddingProvider _provider;
    private readonly KnowledgeEmbeddingSettings _embedding;
    private readonly KnowledgeRetrievalSettings _retrieval;
    private readonly KnowledgeSetImporter _importer;
    private readonly TimeProvider _clock;
    private readonly ILogger<EvalRetrievalCommand> _logger;

    public EvalRetrievalCommand(
        DbContextOptions<AppDbContext> dbContextOptions,
        EmbeddingProvider provider,
        KnowledgeEmbeddingSettings embedding,
        KnowledgeRetrievalSettings retrieval,
        TimeProvider clock,
        ILogger<EvalRetrievalCommand> logger,
        IServiceProvider services)
    {
        _services = services;
        _dbContextOptions = dbContextOptions;
        _provider = provider;
        _embedding = embedding;
        _retrieval = retrieval;
        _clock = clock;
        _logger = logger;
        _importer = ActivatorUtilities.CreateInstance<KnowledgeSetImporter>(services);
    }

    /// <summary>The parsed command line.</summary>
    public sealed record Arguments(string? SetDirectory, string? ReportPath, TimeSpan ProcessingTimeout, bool Help);

    /// <summary>Runs <c>eval-retrieval</c> in a new DI scope of <paramref name="services"/>.</summary>
    /// <returns>The process exit code: <see cref="ExitSuccess"/>, <see cref="ExitFailed"/> (the
    /// model or processing failed) or <see cref="ExitUsage"/> (bad arguments, environment, set or
    /// configuration).</returns>
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

        var environment = services.GetRequiredService<IHostEnvironment>().EnvironmentName;
        if (!Environments.Contains(environment, StringComparer.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync(
                $"eval-retrieval 只能在 {string.Join(" 或 ", Environments)} 環境執行（目前是 {environment}）：它會在資料庫裡建立評測用的組織。");
            return ExitUsage;
        }

        await using var scope = services.CreateAsyncScope();
        EvalRetrievalCommand command;
        try
        {
            command = ActivatorUtilities.CreateInstance<EvalRetrievalCommand>(scope.ServiceProvider);
        }
        catch (OptionsValidationException exception)
        {
            await error.WriteLineAsync($"設定無法使用：{exception.Message}");
            return ExitUsage;
        }

        return await command.RunAsync(arguments, output, error, cancellationToken);
    }

    public async Task<int> RunAsync(Arguments arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!_provider.IsConfigured)
        {
            await error.WriteLineAsync("沒有設定嵌入模型（Ai:Embedding:Provider），無法評測。");
            return ExitFailed;
        }

        RetrievalEvalSet set;
        try
        {
            set = RetrievalEvalSet.Load(arguments.SetDirectory ?? RetrievalEvalSet.DefaultDirectory);
        }
        catch (RetrievalEvalSetException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return ExitUsage;
        }

        var startedAt = _clock.GetLocalNow();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (organization, account) = await EnsureOrganizationAsync(cancellationToken);
            await output.WriteLineAsync($"以模型 {_embedding.Model}（{_provider.Name}）評測題庫 {DisplayName(set.Directory)}：{set.Questions.Count} 題。");
            var removed = await ResetAsync(organization.Id, account.Id, cancellationToken);
            await output.WriteLineAsync($"清除評測組織 {OrganizationCode} 上次留下的 {removed} 個知識庫，重新上傳、處理並確認生效文件…");

            var import = await _importer.ImportAsync(organization.Id, account.Id, set.KnowledgeBases, arguments.ProcessingTimeout, cancellationToken);
            if (import.NotApproved.Count > 0)
            {
                await error.WriteLineAsync("有文件版本沒有生效，評測中止：");
                foreach (var version in import.NotApproved)
                {
                    var status = version.Status is { } known ? WireNames<KnowledgeDocumentStatus>.ToWire(known) : "未上傳";
                    var reason = version.Issue ?? version.Skipped;
                    await error.WriteLineAsync(
                        $"  {version.KnowledgeBaseName}／{version.DocumentName} 第 {version.SetVersion} 版：{status}{(reason is null ? string.Empty : $"（{reason}）")}");
                }

                return ExitFailed;
            }

            var knowledgeBaseIds = import.KnowledgeBaseIds.Values.ToList();
            var organizationContext = new FixedOrganizationContext(organization.Id);
            var results = new List<EvalQuestionResult>(set.Questions.Count);
            foreach (var question in set.Questions)
            {
                await using var dbContext = new AppDbContext(_dbContextOptions, organizationContext);
                var retriever = CreateRetriever(dbContext, organizationContext);
                var retrieved = await retriever.RetrieveAsync(
                    new KnowledgeRetrievalQuery(
                        question.Question,
                        knowledgeBaseIds,
                        AccountId: null,
                        AssistantId: null,
                        IncludePending: false,
                        Top: RetrievalEvalScoring.Top,
                        MinScore: null),
                    cancellationToken);
                results.Add(RetrievalEvalScoring.Judge(question, [.. retrieved.Passages.Select(EvalPassage.From)]));
            }

            var summary = RetrievalEvalScoring.Summarize(results, _retrieval.MinScore);
            var chunks = await CountRetrievableChunksAsync(organization.Id, knowledgeBaseIds, cancellationToken);
            var report = RetrievalEvalReport.Render(new RetrievalEvalRun(
                startedAt,
                stopwatch.Elapsed,
                _provider.Name,
                _embedding.Model,
                _provider.Endpoint?.ToString(),
                _embedding.QueryPrefix,
                _embedding.DocumentPrefix,
                DisplayName(set.Directory),
                set.Fingerprint,
                set.KnowledgeBases.Count,
                set.Documents.Count(),
                set.Documents.Sum(document => document.Versions.Count),
                chunks,
                results,
                summary));

            var path = Path.GetFullPath(arguments.ReportPath ?? Path.Combine(RepositoryRoot(), "docs", "evals", RetrievalEvalReport.FileName(startedAt, _embedding.Model)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, report, cancellationToken);

            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"hit@5 {summary.Hits5}/{summary.Answerable}，hit@1 {summary.Hits1}/{summary.Answerable}；" +
                $"應查無結果的最高分 {Format(summary.MaxUnanswerable?.Score)}，正確命中的最低分 {Format(summary.MinHit?.Score)}；" +
                $"建議門檻 {Format(summary.Suggested?.Value)}（目前 {Format(_retrieval.MinScore)}）。"));
            await output.WriteLineAsync($"報告：{path}");
            return ExitSuccess;
        }
        catch (KnowledgeEmbeddingException exception)
        {
            _logger.LogError(exception, "eval-retrieval could not embed a question.");
            await error.WriteLineAsync(
                $"嵌入模型無法使用：{exception.InnerException?.GetType().Name}: {exception.InnerException?.Message}");
            return ExitFailed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "eval-retrieval failed.");
            await error.WriteLineAsync($"評測中斷：{exception.GetType().Name}: {exception.Message}");
            return ExitFailed;
        }
    }

    /// <summary>The evaluation organization and its account, created on first use.</summary>
    private async Task<(Organization Organization, Account Account)> EnsureOrganizationAsync(CancellationToken cancellationToken)
    {
        Organization organization;
        await using (var dbContext = new AppDbContext(_dbContextOptions, FixedOrganizationContext.None))
        {
            var existing = await dbContext.Organizations.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Code == OrganizationCode, cancellationToken);
            if (existing is null)
            {
                existing = new Organization(Guid.CreateVersion7(), OrganizationName, OrganizationCode);
                dbContext.Organizations.Add(existing);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Created the retrieval evaluation organization {OrganizationCode}.", OrganizationCode);
            }

            organization = existing;
        }

        // Pinned to the organization: the write guard requires it for the account rows.
        await using var organizationDbContext = new AppDbContext(_dbContextOptions, new FixedOrganizationContext(organization.Id));
        var normalized = Account.NormalizeLoginName(AccountLoginName);
        var account = await organizationDbContext.Accounts
            .SingleOrDefaultAsync(candidate => candidate.NormalizedLoginName == normalized, cancellationToken);
        if (account is null)
        {
            // No password: nobody signs in as it. It only owns, uploads and approves the set.
            account = Account.Create(organization, AccountLoginName, AccountDisplayName, AccountRole.SmbAdmin);
            organizationDbContext.Accounts.Add(account);
            organizationDbContext.AccountPermissions.Add(new AccountPermissionGrant(account, AccountPermission.ManageDataSources));
            await organizationDbContext.SaveChangesAsync(cancellationToken);
        }

        return (organization, account);
    }

    /// <summary>Deletes every knowledge base of the evaluation organization, each as
    /// <c>DELETE /api/v1/knowledge-bases/{id}</c> does (its activity rows go too, and the deletion
    /// is recorded); returns how many.</summary>
    private async Task<int> ResetAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var dbContext = new AppDbContext(_dbContextOptions, new FixedOrganizationContext(organizationId));
        var knowledgeBases = await dbContext.KnowledgeBases.ToListAsync(cancellationToken);
        foreach (var knowledgeBase in knowledgeBases)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.KnowledgeActivities
                .Where(activity => activity.KnowledgeBaseId == knowledgeBase.Id)
                .ExecuteDeleteAsync(cancellationToken);
            dbContext.KnowledgeBases.Remove(knowledgeBase);
            dbContext.KnowledgeActivities.Add(KnowledgeActivity.KnowledgeBaseDeleted(knowledgeBase, accountId, _clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return knowledgeBases.Count;
    }

    /// <summary>
    /// The organization's <see cref="KnowledgeRetriever"/> on <paramref name="dbContext"/>, wired as
    /// <c>AddKnowledge</c>/<c>AddEmbeddings</c> wire it per request: the configured model behind the
    /// recording middleware (each question one <c>embed-query</c> row of the organization), the
    /// vector collection of the configured model, the deployment's retrieval settings.
    /// </summary>
    private KnowledgeRetriever CreateRetriever(AppDbContext dbContext, FixedOrganizationContext organization) =>
        new(
            new KnowledgeChunkEmbedder(
                EmbeddingServiceCollectionExtensions.CreateGenerator(_services, organization, new EfModelInvocationRecorder(_dbContextOptions, organization)),
                _embedding),
            new KnowledgeChunkVectorCollection(dbContext, _embedding.Model),
            new EfKnowledgeVersionSources(dbContext),
            _retrieval,
            _clock);

    /// <summary>How many chunks a search of these knowledge bases considers now.</summary>
    private async Task<int> CountRetrievableChunksAsync(Guid organizationId, IReadOnlyCollection<Guid> knowledgeBaseIds, CancellationToken cancellationToken)
    {
        await using var dbContext = new AppDbContext(_dbContextOptions, new FixedOrganizationContext(organizationId));
        return await dbContext.KnowledgeChunks
            .CountAsync(RetrievableChunks.InKnowledgeBases(knowledgeBaseIds, _clock.GetUtcNow(), _embedding.Model, includePending: false), cancellationToken);
    }

    /// <summary>The nearest directory from the current one up that is a Git working tree (it
    /// has <c>.git</c>, a directory or, in a worktree, a file); the current directory when none is.</summary>
    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }
        }

        return Environment.CurrentDirectory;
    }

    /// <summary>The set's path relative to the repository when it is the committed one (or
    /// anywhere under the repository); otherwise as given.</summary>
    private static string DisplayName(string setDirectory)
    {
        if (setDirectory == Path.GetFullPath(RetrievalEvalSet.DefaultDirectory))
        {
            return "apps/api/eval/retrieval";
        }

        var relative = Path.GetRelativePath(RepositoryRoot(), setDirectory);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? setDirectory
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string Format(double? score) => score is { } value ? RetrievalEvalReport.Score(value) : "—";

    internal static bool TryParse(IReadOnlyList<string> args, out Arguments arguments, out string error)
    {
        string? set = null;
        string? report = null;
        var timeout = DefaultProcessingTimeout;
        var help = false;
        arguments = new Arguments(null, null, timeout, false);
        error = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = args[i].Split('=', 2) is [var key, var given] ? (key, given) : (args[i], null);
            switch (name)
            {
                case "--help" or "-h" when inlineValue is null:
                    help = true;
                    break;
                case "--set" or "--report" or "--timeout":
                    var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        error = $"{name} 需要一個值。";
                        return false;
                    }

                    if (name == "--set")
                    {
                        set = value.Trim();
                    }
                    else if (name == "--report")
                    {
                        report = value.Trim();
                    }
                    else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 86_400)
                    {
                        timeout = TimeSpan.FromSeconds(seconds);
                    }
                    else
                    {
                        error = "--timeout 必須是 0–86400 的整數（秒）。";
                        return false;
                    }

                    break;
                default:
                    error = $"不認得的參數「{args[i]}」。";
                    return false;
            }
        }

        arguments = new Arguments(set, report, timeout, help);
        return true;
    }
}
