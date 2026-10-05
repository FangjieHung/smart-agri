using System.Diagnostics;
using Microsoft.Extensions.AI;
using SmartAgri.Api.Answers.Evaluation;
using SmartAgri.Application.Assistants;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Chat.Evaluation;

/// <summary>
/// The one-shot <c>eval-form-requests</c> subcommand (M4 #164): judges, on
/// <see cref="FormRequestEvalSet"/>'s labelled questions, how often each way of triggering a form
/// request misses a request (should have shown the form) or false-triggers (should not have) —
/// the keyword gate (<see cref="AssistantFormRequestRules.AsksForForm"/>, #148) and the model
/// choosing <c>request_database_form</c> with the same declaration and prompt production uses
/// (<see cref="AssistantFormRequestRules.Declaration"/>, <see cref="AssistantFormRequestRules.SelectionPrompt"/>).
/// </summary>
/// <remarks>
/// The keyword trigger needs no model. The model trigger calls the configured chat model
/// (<c>Ai:Chat</c>) directly, once per question, with the set's sample form; it touches no database
/// and no organization, so these calls are not in <c>ModelInvocations</c> (the report has the token
/// usage). Development and Testing only, like the other evaluations. With <c>Fake</c> the model column
/// only proves the pipeline (the fake follows the keyword gate), and the report says so.
/// </remarks>
public sealed class EvalFormRequestsCommand
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Keyword = "keyword";
    public const string Model = "model";
    public const string Both = "both";

    /// <summary>The id the sample form is offered under (any fixed GUID; nothing is looked up).</summary>
    public static readonly Guid SampleFormId = Guid.Parse("0199a000-0164-7000-8000-000000000164");

    public static IReadOnlyList<string> Environments => ChatModelOptions.FakeEnvironments;

    public const string Usage =
        "用法：eval-form-requests [--trigger keyword|model|both] [--set <題庫目錄>] [--report <報告檔路徑>]\n" +
        "評測對話表單請求的觸發方式（#164）：關鍵字門檻與模型選擇工具的漏觸、誤觸率，寫出 Markdown 報告。只能在 Development 或 Testing 環境執行。\n" +
        "  --trigger  keyword 只評測關鍵字（不需要模型）；model 或 both 需要設定對話模型（Ai:Chat），預設 both。\n" +
        "  --set      題庫目錄（questions.json），預設為隨程式附帶的題庫（apps/api/eval/form-requests）。\n" +
        "  --report   報告檔路徑，預設為 <repo>/docs/evals/<日期>-form-requests-<模型或 keyword>.md，已存在就覆寫。";

    private readonly ChatClientProvider _chatProvider;
    private readonly TimeProvider _clock;

    public EvalFormRequestsCommand(ChatClientProvider chatProvider, TimeProvider clock)
    {
        _chatProvider = chatProvider;
        _clock = clock;
    }

    public sealed record Arguments(string Trigger, string? SetDirectory, string? ReportPath, bool Help);

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
            await error.WriteLineAsync($"eval-form-requests 只能在 {string.Join(" 或 ", Environments)} 環境執行（目前是 {environment}）。");
            return ExitUsage;
        }

        var command = new EvalFormRequestsCommand(services.GetRequiredService<ChatClientProvider>(), services.GetRequiredService<TimeProvider>());
        return await command.RunAsync(arguments, output, error, cancellationToken);
    }

    public static bool TryParse(IReadOnlyList<string> args, out Arguments arguments, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        string trigger = Both;
        string? set = null, report = null;
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

            if (name is not ("--trigger" or "--set" or "--report"))
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
                default:
                    report = value;
                    break;
            }

            if (error is not null)
            {
                break;
            }
        }

        arguments = new Arguments(trigger, set, report, help);
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

        FormRequestEvalSet set;
        try
        {
            set = FormRequestEvalSet.Load(arguments.SetDirectory ?? FormRequestEvalSet.DefaultDirectory);
        }
        catch (FormRequestEvalSetException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return ExitUsage;
        }

        var startedAt = _clock.GetLocalNow();
        var stopwatch = Stopwatch.StartNew();
        await output.WriteLineAsync(runModel
            ? $"以對話模型 {_chatProvider.Model}（{_chatProvider.Name}）與關鍵字門檻評測 {set.Questions.Count} 題…"
            : $"以關鍵字門檻評測 {set.Questions.Count} 題…");

        IReadOnlyList<AssistantFormToolOffer> offers = [new AssistantFormToolOffer(SampleFormId, set.SampleForm.Title, set.SampleForm.Purpose)];
        var results = new List<FormRequestEvalResult>(set.Questions.Count);
        long inputTokens = 0, outputTokens = 0;
        var usageReported = 0;
        foreach (var question in set.Questions)
        {
            var keyword = AssistantFormRequestRules.AsksForForm(question.Question) ? FormRequestEvalDecision.Form : FormRequestEvalDecision.None;
            FormRequestEvalDecision? model = null;
            var rejected = false;
            if (runModel)
            {
                var options = new ChatOptions
                {
                    Tools = [AssistantFormRequestRules.Declaration(offers)],
                    ToolMode = ChatToolMode.Auto,
                    AllowMultipleToolCalls = false,
                };
                try
                {
                    var response = await _chatProvider.Client.GetResponseAsync(
                        AssistantFormRequestRules.SelectionPrompt(question.Question), options, cancellationToken);
                    var (match, _) = AssistantFormRequestRules.ParseCall(response, offers);
                    model = match == AssistantFormToolCallMatch.Matched ? FormRequestEvalDecision.Form : FormRequestEvalDecision.None;
                    rejected = match == AssistantFormToolCallMatch.Rejected;
                    if (response.Usage is { } usage)
                    {
                        inputTokens += usage.InputTokenCount ?? 0;
                        outputTokens += usage.OutputTokenCount ?? 0;
                        usageReported++;
                    }
                }
                catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    await error.WriteLineAsync($"  {question.Id}：模型呼叫失敗（{exception.GetType().Name}：{exception.Message}）");
                    model = FormRequestEvalDecision.Error;
                }
            }

            results.Add(new FormRequestEvalResult(question, keyword, model, rejected));
        }

        var run = new FormRequestEvalRun(
            startedAt,
            stopwatch.Elapsed,
            DisplayName(set.Directory),
            set.Fingerprint,
            set.SampleForm,
            runModel ? _chatProvider.Name : null,
            runModel ? _chatProvider.Model : null,
            results,
            FormRequestEvalScoring.Summarize(Keyword, results.Select(result => (result.Question, result.Keyword))),
            runModel ? FormRequestEvalScoring.Summarize(Model, results.Select(result => (result.Question, result.Model!.Value))) : null,
            usageReported == 0 ? null : (double)inputTokens / usageReported,
            usageReported == 0 ? null : (double)outputTokens / usageReported);

        var path = Path.GetFullPath(arguments.ReportPath ?? Path.Combine(
            EvalAnswersCommand.RepositoryRoot(), "docs", "evals", FormRequestEvalReport.FileName(startedAt, runModel ? _chatProvider.Model : null)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, FormRequestEvalReport.Render(run), cancellationToken);

        foreach (var summary in new[] { run.Keyword, run.Model }.OfType<FormRequestEvalSummary>())
        {
            await output.WriteLineAsync(
                $"{summary.Trigger}：漏觸 {summary.Missed}/{summary.Positives}，誤觸 {summary.FalseTriggers}/{summary.Negatives}，" +
                $"模糊題一致 {summary.AmbiguousAgreed}/{summary.Ambiguous}，失敗 {summary.Errors}。");
        }

        await output.WriteLineAsync($"報告：{path}");
        return run.Model is { Errors: > 0 } ? ExitFailed : ExitSuccess;
    }

    private static string DisplayName(string directory)
    {
        if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(FormRequestEvalSet.DefaultDirectory), StringComparison.Ordinal))
        {
            return "apps/api/eval/form-requests";
        }

        var root = EvalAnswersCommand.RepositoryRoot();
        var relative = Path.GetRelativePath(root, directory);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? directory
            : relative.Replace('\\', '/');
    }
}
