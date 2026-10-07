using System.Diagnostics;
using Microsoft.Extensions.AI;
using SmartAgri.Api.Answers.Evaluation;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Chat;
using SmartAgri.Application.Databases;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Chat.Evaluation;

/// <summary>
/// The one-shot <c>eval-case-proposals</c> subcommand (M7-12 #257): judges, on
/// <see cref="CaseProposalEvalSet"/>'s labelled questions, how often each way of triggering a case
/// proposal misses (should have proposed), false-triggers (should not have, or should have given the
/// form or the database query) or picks the wrong type — the keyword rule
/// (<see cref="CaseProposalRules.KeywordProposal"/>, decision T) and the model choosing
/// <c>propose_case</c> with the same declaration and prompt production uses
/// (<see cref="CaseProposalRules.Declaration"/>, <see cref="CaseProposalRules.SelectionPrompt"/>,
/// <see cref="CaseProposalRules.ParseCall"/>; <see cref="ChatCaseProposalTool"/>).
/// </summary>
/// <remarks>
/// Each question is judged for the case layer alone and for the whole proposal stage (decision L):
/// the query layer is the <see cref="DatabaseQueryTools.AsksForStatistics"/> gate; then, by keyword,
/// <see cref="AssistantFormRequestRules.AsksForForm"/> and the case rule in order, or, by model, the one
/// combined selection call production makes when the assistant has both a form and case types (#286,
/// <see cref="ProposalSelectionRules"/>, as <see cref="ChatProposalSelectionTool"/> calls it). In model mode
/// every question gets that combined call and one case-only call (the case layer, judged on its own, is
/// production's call for an assistant without a form). The calls go to the configured chat model (<c>Ai:Chat</c>) directly; they touch no
/// database and no organization, so they are not in <c>ModelInvocations</c> (the report has the token
/// usage). Development and Testing only, like the other evaluations. With <c>Fake</c> the model columns
/// only prove the pipeline (the fake follows the keyword words), and the report says so.
/// </remarks>
public sealed partial class EvalCaseProposalsCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Keyword = "keyword";
    public const string Model = "model";
    public const string Both = "both";

    public static IReadOnlyList<string> Environments => ChatModelOptions.FakeEnvironments;

    public const string Usage =
        "用法：eval-case-proposals [--trigger keyword|model|both] [--set <題庫目錄>] [--types <類型說明檔>] [--report <報告檔路徑>]\n" +
        "評測對話中提議開案的觸發方式（#257）：關鍵字與模型選擇工具的漏觸、誤觸、選錯類型比率，寫出 Markdown 報告。只能在 Development 或 Testing 環境執行，不需要資料庫。\n" +
        "  --trigger  keyword 只評測關鍵字（不需要模型）；model 或 both 需要設定對話模型（Ai:Chat），預設 both。\n" +
        "  --set      題庫目錄（questions.json），預設為隨程式附帶的題庫（apps/api/eval/case-proposals）。\n" +
        "  --types    改用另一組案件類型說明（JSON，caseTypes 的 key 與順序要和題庫相同），例如 types-with-exclusions.json；相對路徑先找工作目錄、再找題庫目錄。預設用題庫內建的說明。\n" +
        "  --report   報告檔路徑，預設為 <repo>/docs/evals/<日期>-case-proposals-<模型或 keyword>.md，已存在就覆寫。";

    private readonly ChatClientProvider _chatProvider;
    private readonly TimeProvider _clock;

    public EvalCaseProposalsCommand(ChatClientProvider chatProvider, TimeProvider clock)
    {
        _chatProvider = chatProvider;
        _clock = clock;
    }

    /// <param name="TypesPath">#293: a file whose <c>caseTypes</c> replace the set's own names and
    /// descriptions (<see cref="CaseProposalEvalSet.WithTypes"/>); <see langword="null"/> keeps them.</param>
    public sealed record Arguments(string Trigger, string? SetDirectory, string? ReportPath, bool Help, string? TypesPath = null);

    public static async Task<int> RunAsync(
        IServiceProvider services, IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
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
            await error.WriteLineAsync($"eval-case-proposals 只能在 {string.Join(" 或 ", Environments)} 環境執行（目前是 {environment}）。");
            return ExitUsage;
        }

        var command = new EvalCaseProposalsCommand(services.GetRequiredService<ChatClientProvider>(), services.GetRequiredService<TimeProvider>());
        return await command.RunAsync(arguments, output, error, cancellationToken);
    }

    public static bool TryParse(IReadOnlyList<string> args, out Arguments arguments, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        string trigger = Both;
        string? set = null, report = null, types = null;
        var help = false;
        error = null;
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            if (name is "--help" or "-h")
            {
                help = true;
                continue;
            }

            if (name is not ("--trigger" or "--set" or "--types" or "--report"))
            {
                error = $"不認得的參數：{name}";
                break;
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                error = $"{name} 需要一個值。";
                break;
            }

            var value = args[++index];
            switch (name)
            {
                case "--trigger" when value is Keyword or Model or Both:
                    trigger = value;
                    break;
                case "--trigger":
                    error = $"--trigger 必須是 keyword、model 或 both，不是「{value}」。";
                    break;
                case "--set":
                    set = value;
                    break;
                case "--types":
                    types = value;
                    break;
                default:
                    report = value;
                    break;
            }

            if (error is not null)
            {
                break;
            }
        }

        arguments = new Arguments(trigger, set, report, help, types);
        return error is null;
    }

    public async Task<int> RunAsync(Arguments arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var runModel = arguments.Trigger != Keyword;
        if (runModel && !_chatProvider.IsConfigured)
        {
            await error.WriteLineAsync("沒有設定對話模型（Ai:Chat:Provider），無法評測模型觸發；只評測關鍵字請加 --trigger keyword。");
            return ExitFailed;
        }

        CaseProposalEvalSet set;
        try
        {
            set = CaseProposalEvalSet.Load(arguments.SetDirectory ?? CaseProposalEvalSet.DefaultDirectory);
            if (arguments.TypesPath is not null)
            {
                set = set.WithTypes(arguments.TypesPath);
            }
        }
        catch (CaseProposalEvalSetException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return ExitUsage;
        }

        var startedAt = _clock.GetLocalNow();
        var stopwatch = Stopwatch.StartNew();
        await output.WriteLineAsync(runModel
            ? $"以對話模型 {_chatProvider.Model}（{_chatProvider.Name}）與關鍵字評測 {set.Questions.Count} 題…"
            : $"以關鍵字評測 {set.Questions.Count} 題…");
        if (set.TypesPath is not null)
        {
            await output.WriteLineAsync($"案件類型說明取自 {set.TypesPath}（指紋 {set.TypesFingerprint}）。");
        }

        IReadOnlyList<AssistantFormToolOffer> formOffers = [new AssistantFormToolOffer(CaseProposalEvalSet.SampleFormId, set.SampleForm.Title, set.SampleForm.Purpose)];
        var results = new List<CaseProposalEvalResult>(set.Questions.Count);
        foreach (var question in set.Questions)
        {
            var asksForStatistics = DatabaseQueryTools.AsksForStatistics(question.Question);

            var keywordCase = CaseProposalRules.KeywordProposal(question.Question, set.Offers) is { } keywordDraft
                ? CaseProposalEvalOutcome.Case(set.KeyOf(keywordDraft.Offer.TypeId), keywordDraft.Title)
                : CaseProposalEvalOutcome.None;
            var keywordForm = AssistantFormRequestRules.AsksForForm(question.Question) ? CaseProposalEvalFormDecision.Form : CaseProposalEvalFormDecision.None;
            var keyword = new CaseProposalEvalDecisions(keywordCase, CaseProposalEvalScoring.Stage(asksForStatistics, keywordForm, keywordCase));

            CaseProposalEvalDecisions? model = null;
            CaseProposalEvalUsage? usage = null;
            if (runModel)
            {
                var (selection, selectionRejected, selectionUsage) = await SelectCombinedAsync(set, question, formOffers, error, cancellationToken);
                var (caseLayer, caseRejected, caseUsage) = await SelectCaseAsync(set, question, error, cancellationToken);
                model = new CaseProposalEvalDecisions(
                    caseLayer, CaseProposalEvalScoring.Stage(asksForStatistics, selection), caseRejected, selectionRejected);
                usage = new CaseProposalEvalUsage(
                    selectionUsage?.InputTokenCount, selectionUsage?.OutputTokenCount, caseUsage?.InputTokenCount, caseUsage?.OutputTokenCount);
            }

            results.Add(new CaseProposalEvalResult(question, keyword, model, usage));
        }

        var run = new CaseProposalEvalRun(
            startedAt,
            stopwatch.Elapsed,
            DisplayName(set.Directory),
            set,
            runModel ? _chatProvider.Name : null,
            runModel ? _chatProvider.Model : null,
            results,
            set.TypesPath is null ? null : TypesDisplayName(set));

        var path = Path.GetFullPath(arguments.ReportPath ?? Path.Combine(
            EvalAnswersCommand.RepositoryRoot(), "docs", "evals", CaseProposalEvalReport.FileName(startedAt, runModel ? _chatProvider.Model : null)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, CaseProposalEvalReport.Render(run), cancellationToken);

        foreach (var summary in new[] { run.KeywordCaseLayer, run.KeywordStage, run.ModelCaseLayer, run.ModelStage }.OfType<CaseProposalEvalSummary>())
        {
            await output.WriteLineAsync(
                $"{summary.Trigger}（{(summary.Stage ? "提議階段" : "案件層")}）：漏觸 {summary.Missed}/{summary.CaseQuestions}，" +
                $"誤觸 {summary.FalseTriggers}/{summary.Negatives}，選錯類型 {summary.WrongType}/{summary.Proposed}，" +
                $"模糊題一致 {summary.AmbiguousAgreed}/{summary.Ambiguous}，失敗 {summary.Errors}。");
        }

        await output.WriteLineAsync($"報告：{path}");
        return run.ModelStage is { Errors: > 0 } || run.ModelCaseLayer is { Errors: > 0 } ? ExitFailed : ExitSuccess;
    }

    /// <summary>The combined selection (#286), exactly as <see cref="ChatProposalSelectionTool.SelectAsync"/> calls it
    /// (minus the keyword fallback: a failure is counted here, not judged).</summary>
    private async Task<(CaseProposalEvalOutcome Outcome, bool Rejected, UsageDetails? Usage)> SelectCombinedAsync(
        CaseProposalEvalSet set, CaseProposalEvalQuestion question, IReadOnlyList<AssistantFormToolOffer> formOffers, TextWriter error, CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            Tools = ProposalSelectionRules.Declarations(formOffers, set.Offers),
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false,
        };
        try
        {
            var response = await _chatProvider.Client.GetResponseAsync(ProposalSelectionRules.SelectionPrompt(question.Question), options, cancellationToken);
            var call = ProposalSelectionRules.ParseCall(response, formOffers, set.Offers, question.Question);
            var outcome = call.Match switch
            {
                ProposalSelectionCallMatch.Form => CaseProposalEvalOutcome.Form,
                ProposalSelectionCallMatch.Case => CaseProposalEvalOutcome.Case(set.KeyOf(call.CaseDraft!.Offer.TypeId), call.CaseDraft.Title),
                _ => CaseProposalEvalOutcome.None,
            };
            return (outcome, call.Match == ProposalSelectionCallMatch.Rejected, response.Usage);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            await error.WriteLineAsync($"  {question.Id}：合成選擇呼叫失敗（{Describe(exception)}）");
            return (CaseProposalEvalOutcome.Error, false, null);
        }
    }

    /// <summary>The case selection, exactly as <see cref="ChatCaseProposalTool.SelectAsync"/> calls it
    /// (minus the keyword fallback: a failure is counted here, not judged).</summary>
    private async Task<(CaseProposalEvalOutcome Outcome, bool Rejected, UsageDetails? Usage)> SelectCaseAsync(
        CaseProposalEvalSet set, CaseProposalEvalQuestion question, TextWriter error, CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            Tools = [CaseProposalRules.Declaration(set.Offers)],
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false,
        };
        try
        {
            var response = await _chatProvider.Client.GetResponseAsync(CaseProposalRules.SelectionPrompt(question.Question), options, cancellationToken);
            var (match, draft) = CaseProposalRules.ParseCall(response, set.Offers, question.Question);
            var outcome = draft is not null
                ? CaseProposalEvalOutcome.Case(set.KeyOf(draft.Offer.TypeId), draft.Title)
                : CaseProposalEvalOutcome.None;
            return (outcome, match == CaseProposalCallMatch.Rejected, response.Usage);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            await error.WriteLineAsync($"  {question.Id}：案件選擇呼叫失敗（{Describe(exception)}）");
            return (CaseProposalEvalOutcome.Error, false, null);
        }
    }

    /// <summary>The failure's type and message, with anything shaped like a provider key masked (a
    /// provider's 401 may echo part of the key; the output is pasted into reports).</summary>
    internal static string Describe(Exception exception) =>
        $"{exception.GetType().Name}：{KeyLike().Replace(exception.Message, "<已遮蔽>")}";

    [System.Text.RegularExpressions.GeneratedRegex(@"\bs[k]-[^\s'\"",;]*")]
    private static partial System.Text.RegularExpressions.Regex KeyLike();

    /// <summary>The types file as the report names it: next to the set under the set's display name, else
    /// relative to the repository, else the full path.</summary>
    private static string TypesDisplayName(CaseProposalEvalSet set)
    {
        var path = set.TypesPath!;
        return string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(set.Directory), StringComparison.Ordinal)
            ? $"{DisplayName(set.Directory)}/{Path.GetFileName(path)}"
            : DisplayName(path);
    }

    private static string DisplayName(string directory)
    {
        if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(CaseProposalEvalSet.DefaultDirectory), StringComparison.Ordinal))
        {
            return "apps/api/eval/case-proposals";
        }

        var root = EvalAnswersCommand.RepositoryRoot();
        var relative = Path.GetRelativePath(root, directory);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? directory
            : relative.Replace('\\', '/');
    }
}
