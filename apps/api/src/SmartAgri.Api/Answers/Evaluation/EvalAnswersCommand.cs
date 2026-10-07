using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Answers;
using SmartAgri.Infrastructure.Assistants;
using SmartAgri.Infrastructure.Knowledge;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Answers.Evaluation;

/// <summary>
/// The one-shot <c>eval-answers</c> subcommand (M3 plan Slice 13; ticket #83): runs
/// <see cref="AnswerEvalSet"/>'s question bank through <see cref="GroundedAnswerService.AnswerAsync"/>
/// with the deployment's configured models, so the prompt and the relevance threshold are tuned by
/// data — reply-kind accuracy, citation hit rate, the rejection reason distribution and average
/// token usage (grounded-answers ADR: the bank must cover questions that should find nothing).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="EvalRetrievalCommand"/>, in an organization of its own
/// (<see cref="OrganizationCode"/>, never the developer's demo data), reset and reimported through
/// the normal pipeline (<see cref="KnowledgeSetImporter"/>) on every run so the same set gives the
/// same judgement. Every question is answered with a profile whose knowledge scope is
/// <see cref="AssistantKnowledgeScope.CompanyDataOnly"/> (the deployment's <c>Retrieval:MinScore</c>,
/// no assistant of its own), and a question with <c>followUpOf</c> gets the referenced question and
/// its own answer as one-turn conversation history (M3 plan §7 decision D).
/// </para>
/// <para>
/// Development and Testing only, for the same reason as <c>eval-retrieval</c>. It needs a real
/// chat (and embedding) model and key to mean anything; CI runs it with <c>Fake</c>
/// (<c>EvalAnswersIntegrationTests</c>, in the test project) to prove the pipeline, and the report
/// says so when either model is <c>fake</c>.
/// </para>
/// </remarks>
public sealed class EvalAnswersCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string OrganizationCode = "answers-eval";
    public const string OrganizationName = "回答評測（安心商行示範資料）";
    public const string AccountLoginName = "eval";
    public const string AccountDisplayName = "回答評測";

    public const string AssistantName = "回答評測助理";
    public const string AssistantPurpose = "評測回答流程";
    public const string RefusalMessage = "目前的資料中找不到這個問題的答案。";

    /// <summary>The environments it runs in (as <c>Fake</c> embeddings and chat do).</summary>
    public static IReadOnlyList<string> Environments => EmbeddingOptions.FakeEnvironments;

    public static readonly TimeSpan DefaultProcessingTimeout = TimeSpan.FromMinutes(10);

    public const string Usage =
        "用法：eval-answers [--set <題庫目錄>] [--report <報告檔路徑>] [--timeout <秒數>]\n" +
        "以目前設定的嵌入與對話模型（Ai:Embedding、Ai:Chat）跑完回答評測題庫，寫出 Markdown 報告。只能在 Development 或 Testing 環境執行。\n" +
        "  --set      題庫目錄（documents.json、questions.json 與檔案），預設為隨程式附帶的示範題庫（apps/api/eval/answers）。\n" +
        "  --report   報告檔路徑，預設為 <repo>/docs/evals/<日期>-answers-<模型>.md，已存在就覆寫。\n" +
        "  --timeout  等待文件處理完成的秒數上限，預設 600。";

    private readonly IServiceProvider _services;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly KnowledgeEmbeddingSettings _embedding;
    private readonly KnowledgeRetrievalSettings _retrieval;
    private readonly ChatClientProvider _chatProvider;
    private readonly GroundedAnswerMetrics _metrics;
    private readonly KnowledgeSetImporter _importer;
    private readonly TimeProvider _clock;
    private readonly ILogger<EvalAnswersCommand> _logger;

    public EvalAnswersCommand(
        DbContextOptions<AppDbContext> dbContextOptions,
        EmbeddingProvider embeddingProvider,
        KnowledgeEmbeddingSettings embedding,
        KnowledgeRetrievalSettings retrieval,
        ChatClientProvider chatProvider,
        GroundedAnswerMetrics metrics,
        TimeProvider clock,
        ILogger<EvalAnswersCommand> logger,
        IServiceProvider services)
    {
        _services = services;
        _dbContextOptions = dbContextOptions;
        _embeddingProvider = embeddingProvider;
        _embedding = embedding;
        _retrieval = retrieval;
        _chatProvider = chatProvider;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
        _importer = ActivatorUtilities.CreateInstance<KnowledgeSetImporter>(services);
    }

    /// <summary>The parsed command line.</summary>
    public sealed record Arguments(string? SetDirectory, string? ReportPath, TimeSpan ProcessingTimeout, bool Help);

    /// <summary>Runs <c>eval-answers</c> in a new DI scope of <paramref name="services"/>.</summary>
    /// <returns>The process exit code: <see cref="ExitSuccess"/>, <see cref="ExitFailed"/> (a
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
                $"eval-answers 只能在 {string.Join(" 或 ", Environments)} 環境執行（目前是 {environment}）：它會在資料庫裡建立評測用的組織。");
            return ExitUsage;
        }

        await using var scope = services.CreateAsyncScope();
        EvalAnswersCommand command;
        try
        {
            command = ActivatorUtilities.CreateInstance<EvalAnswersCommand>(scope.ServiceProvider);
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

        if (!_embeddingProvider.IsConfigured)
        {
            await error.WriteLineAsync("沒有設定嵌入模型（Ai:Embedding:Provider），無法評測。");
            return ExitFailed;
        }

        if (!_chatProvider.IsConfigured)
        {
            await error.WriteLineAsync("沒有設定對話模型（Ai:Chat:Provider），無法評測。");
            return ExitFailed;
        }

        AnswerEvalSet set;
        try
        {
            set = AnswerEvalSet.Load(arguments.SetDirectory ?? AnswerEvalSet.DefaultDirectory);
        }
        catch (AnswerEvalSetException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return ExitUsage;
        }

        var startedAt = _clock.GetLocalNow();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (organization, account) = await EnsureOrganizationAsync(cancellationToken);
            await output.WriteLineAsync(
                $"以嵌入模型 {_embedding.Model}（{_embeddingProvider.Name}）與對話模型 {_chatProvider.Model}（{_chatProvider.Name}）評測題庫 " +
                $"{DisplayName(set.Directory)}：{set.Questions.Count} 題。");
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
            var profile = new GroundedAnswerProfile(
                AssistantName,
                AssistantPurpose,
                AssistantTone.Concise,
                RoleInstructions: string.Empty,
                AssistantKnowledgeScope.CompanyDataOnly,
                RefusalMessage,
                MinScore: null,
                account.Id,
                knowledgeBaseIds);

            var replies = new Dictionary<string, GroundedReply>();
            var results = new List<AnswerEvalQuestionResult>(set.Questions.Count);
            foreach (var question in set.Questions)
            {
                var history = History(question, set.Questions, replies);
                await using var dbContext = new AppDbContext(_dbContextOptions, organizationContext);
                var answerService = CreateAnswerService(dbContext, organizationContext);
                var request = new GroundedAnswerRequest(profile, question.Question, history, account.Id, AssistantId: null);
                var answered = await answerService.AnswerAsync(request, cancellationToken);
                replies[question.Id] = answered.Reply;
                results.Add(AnswerEvalScoring.Judge(question, answered.Reply, answered.Retrieval.Passages));
            }

            var (averageInput, averageOutput) = await AverageTokensAsync(organization.Id, cancellationToken);
            var summary = AnswerEvalScoring.Summarize(results, averageInput, averageOutput);
            var report = AnswerEvalReport.Render(new AnswerEvalRun(
                startedAt,
                stopwatch.Elapsed,
                _embeddingProvider.Name,
                _embedding.Model,
                _chatProvider.Name,
                _chatProvider.Model,
                _retrieval.MinScore,
                GroundedAnswerPrompt.Version,
                DisplayName(set.Directory),
                set.Fingerprint,
                set.KnowledgeBases.Count,
                set.Documents.Count(),
                results,
                summary));

            var path = Path.GetFullPath(arguments.ReportPath ?? Path.Combine(RepositoryRoot(), "docs", "evals", AnswerEvalReport.FileName(startedAt, _chatProvider.Model)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, report, cancellationToken);

            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"回覆類型正確率 {summary.ReplyKindCorrect}/{summary.Total}；" +
                $"引用命中率 {(summary.CitationHitRate is { } rate ? $"{summary.CitationHits}/{summary.CompanyDataQuestions}" : "—")}。"));
            await output.WriteLineAsync($"報告：{path}");
            return ExitSuccess;
        }
        catch (KnowledgeEmbeddingException exception)
        {
            _logger.LogError(exception, "eval-answers could not embed a question.");
            await error.WriteLineAsync(
                $"嵌入模型無法使用：{exception.InnerException?.GetType().Name}: {exception.InnerException?.Message}");
            return ExitFailed;
        }
        catch (ChatGenerationException exception)
        {
            _logger.LogError(exception, "eval-answers could not generate an answer.");
            await error.WriteLineAsync(
                $"對話模型無法使用：{exception.InnerException?.GetType().Name}: {exception.InnerException?.Message}");
            return ExitFailed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "eval-answers failed.");
            await error.WriteLineAsync($"評測中斷：{exception.GetType().Name}: {exception.Message}");
            return ExitFailed;
        }
    }

    /// <summary>The one-turn conversation history <paramref name="question"/> is answered with:
    /// empty unless it names an earlier question as <see cref="AnswerEvalQuestion.FollowUpOf"/>,
    /// in which case that question's text and the reply it actually got (already computed,
    /// questions run in the bank's order) become the history (M3 plan §7 decision D).</summary>
    private static IReadOnlyList<ConversationTurn> History(
        AnswerEvalQuestion question, IReadOnlyList<AnswerEvalQuestion> all, IReadOnlyDictionary<string, GroundedReply> replies)
    {
        if (question.FollowUpOf is not { } parentId)
        {
            return [];
        }

        var parent = all.Single(candidate => candidate.Id == parentId);
        var parentReply = replies[parentId];
        return [new ConversationTurn(ConversationAuthor.Account, parent.Question), new ConversationTurn(ConversationAuthor.Assistant, parentReply.Text)];
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
                _logger.LogInformation("Created the answer evaluation organization {OrganizationCode}.", OrganizationCode);
            }

            organization = existing;
        }

        await using var organizationDbContext = new AppDbContext(_dbContextOptions, new FixedOrganizationContext(organization.Id));
        var normalized = Account.NormalizeLoginName(AccountLoginName);
        var account = await organizationDbContext.Accounts
            .SingleOrDefaultAsync(candidate => candidate.NormalizedLoginName == normalized, cancellationToken);
        if (account is null)
        {
            account = Account.Create(organization, AccountLoginName, AccountDisplayName, AccountRole.SmbAdmin);
            organizationDbContext.Accounts.Add(account);
            organizationDbContext.AccountPermissions.Add(new AccountPermissionGrant(account, AccountPermission.ManageDataSources));
            await organizationDbContext.SaveChangesAsync(cancellationToken);
        }

        return (organization, account);
    }

    /// <summary>Deletes every knowledge base of the evaluation organization, each as
    /// <c>DELETE /api/v1/knowledge-bases/{id}</c> does; returns how many.</summary>
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

        // Every earlier run's answer calls are this organization's too; a fresh run's average
        // must not mix in the previous run's tokens.
        await dbContext.ModelInvocations.Where(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateAnswer)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.ModelInvocations.Where(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedQuery)
            .ExecuteDeleteAsync(cancellationToken);

        return knowledgeBases.Count;
    }

    /// <summary>The organization's <see cref="GroundedAnswerService"/> on <paramref name="dbContext"/>,
    /// wired the way the Api's container builds one for a request: the configured retriever and
    /// chat client, each behind the recording middleware, attributed to the organization.</summary>
    private GroundedAnswerService CreateAnswerService(AppDbContext dbContext, FixedOrganizationContext organization)
    {
        var retriever = new KnowledgeRetriever(
            new KnowledgeChunkEmbedder(
                EmbeddingServiceCollectionExtensions.CreateGenerator(_services, organization, new EfModelInvocationRecorder(_dbContextOptions, organization)),
                _embedding),
            new KnowledgeChunkVectorCollection(dbContext, _embedding.Model),
            new EfKnowledgeVersionSources(dbContext),
            _retrieval,
            _clock);
        var chatClient = ChatServiceCollectionExtensions.CreateClient(_services, organization, new EfModelInvocationRecorder(_dbContextOptions, organization));
        var outcomes = ActivatorUtilities.CreateInstance<EfAnswerOutcomeRecorder>(_services, _dbContextOptions, organization);
        return new GroundedAnswerService(new EfAnswerKnowledgeBases(dbContext), retriever, chatClient, _metrics, outcomes, organization, _clock);
    }

    /// <summary>The mean <c>InputTokens</c>/<c>OutputTokens</c> of every <c>generate-answer</c>
    /// call this run made, or <see langword="null"/> when none reported a number (a real provider
    /// may not always report usage).</summary>
    private async Task<(double? Input, double? Output)> AverageTokensAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = new AppDbContext(_dbContextOptions, new FixedOrganizationContext(organizationId));
        var usages = await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateAnswer)
            .Select(invocation => new { invocation.InputTokens, invocation.OutputTokens })
            .ToListAsync(cancellationToken);

        var inputs = usages.Where(usage => usage.InputTokens is not null).Select(usage => (double)usage.InputTokens!.Value).ToList();
        var outputs = usages.Where(usage => usage.OutputTokens is not null).Select(usage => (double)usage.OutputTokens!.Value).ToList();
        return (inputs.Count == 0 ? null : inputs.Average(), outputs.Count == 0 ? null : outputs.Average());
    }

    /// <summary>The nearest directory from the current one up that is a Git working tree; the
    /// current directory when none is (the same rule as <see cref="EvalRetrievalCommand.RepositoryRoot"/>).</summary>
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
        if (setDirectory == Path.GetFullPath(AnswerEvalSet.DefaultDirectory))
        {
            return "apps/api/eval/answers";
        }

        var relative = Path.GetRelativePath(RepositoryRoot(), setDirectory);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? setDirectory
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

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
