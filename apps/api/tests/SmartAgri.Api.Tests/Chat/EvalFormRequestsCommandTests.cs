using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Api.Chat.Evaluation;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Chat;

/// <summary>
/// <c>eval-form-requests</c> (M4 #164) without a database: the committed labelled set loads and is
/// balanced; scoring counts missed and false triggers per category; the keyword trigger runs with no
/// model; the model trigger runs the production declaration and prompt through the configured client
/// (here <c>Fake</c>, which the report flags); a failing model is counted, not judged.
/// </summary>
public sealed class EvalFormRequestsCommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("form-requests-eval-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void The_committed_set_has_positives_negatives_and_ambiguous_questions()
    {
        var set = FormRequestEvalSet.Load(FormRequestEvalSet.DefaultDirectory);

        set.Questions.Count.ShouldBeInRange(30, 60);
        set.Questions.Count(question => question.Category == FormRequestEvalSet.Positive).ShouldBeGreaterThanOrEqualTo(10);
        set.Questions.Count(question => question.Category == FormRequestEvalSet.Negative).ShouldBeGreaterThanOrEqualTo(10);
        set.Questions.Count(question => question.Category == FormRequestEvalSet.Ambiguous).ShouldBeGreaterThanOrEqualTo(5);
        set.Fingerprint.Length.ShouldBe(12);
    }

    [Fact]
    public void An_inconsistent_set_is_refused_with_every_problem()
    {
        File.WriteAllText(Path.Combine(_directory, "questions.json"), """
            {
              "form": { "title": "表單", "purpose": "" },
              "questions": [
                { "id": "x", "question": "我要回報", "expected": "none", "category": "positive" },
                { "id": "x", "question": "", "expected": "maybe", "category": "other" }
              ]
            }
            """);

        var exception = Should.Throw<FormRequestEvalSetException>(() => FormRequestEvalSet.Load(_directory));
        exception.Message.ShouldContain("form 需要 title 與 purpose");
        exception.Message.ShouldContain("expected 必須是 form 或 none");
        exception.Message.ShouldContain("題目 id 重複：x");
    }

    [Fact]
    public void Scoring_counts_missed_and_false_triggers_by_category_and_leaves_errors_out()
    {
        FormRequestEvalQuestion Question(string category, string expected) => new("q", "問題", expected, category, null);

        var summary = FormRequestEvalScoring.Summarize("model",
        [
            (Question("positive", "form"), FormRequestEvalDecision.Form),
            (Question("positive", "form"), FormRequestEvalDecision.None),
            (Question("negative", "none"), FormRequestEvalDecision.Form),
            (Question("negative", "none"), FormRequestEvalDecision.None),
            (Question("negative", "none"), FormRequestEvalDecision.None),
            (Question("ambiguous", "form"), FormRequestEvalDecision.None),
            (Question("ambiguous", "none"), FormRequestEvalDecision.None),
            (Question("positive", "form"), FormRequestEvalDecision.Error),
        ]);

        summary.ShouldBe(new FormRequestEvalSummary("model", 2, 1, 3, 1, 2, 1, 1));
        summary.MissedRate.ShouldBe(0.5);
        summary.FalseTriggerRate!.Value.ShouldBe(1.0 / 3, 1e-9);
        summary.Accuracy!.Value.ShouldBe(3.0 / 5, 1e-9);
        summary.AmbiguousAgreement.ShouldBe(0.5);
    }

    [Fact]
    public async Task The_keyword_trigger_runs_without_a_model()
    {
        var report = Path.Combine(_directory, "keyword.md");
        var unconfigured = ChatClientProvider.Create(new ChatModelOptions());
        var command = new EvalFormRequestsCommand(unconfigured, TimeProvider.System);
        var output = new StringWriter();

        var exit = await command.RunAsync(new EvalFormRequestsCommand.Arguments("keyword", null, report, false), output, new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalFormRequestsCommand.ExitSuccess);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain("| keyword |");
        text.ShouldContain("對話模型：未執行");
        text.ShouldNotContain("| model |");
        // The keyword gate's known misses are in the set (e.g. 「通報」).
        text.ShouldContain("| p05 | positive | form | none ✗ |");

        // The model trigger needs a model.
        (await command.RunAsync(new EvalFormRequestsCommand.Arguments("model", null, report, false), output, new StringWriter(), CancellationToken))
            .ShouldBe(EvalFormRequestsCommand.ExitFailed);
    }

    [Fact]
    public async Task The_model_trigger_runs_the_production_tool_and_flags_the_fake()
    {
        var report = Path.Combine(_directory, "model.md");
        var fake = new ChatClientProvider(new FakeChatClient("fake-eval"), "fake", "smartagri.fake", "fake-eval", null);
        var command = new EvalFormRequestsCommand(fake, TimeProvider.System);

        var exit = await command.RunAsync(new EvalFormRequestsCommand.Arguments("both", null, report, false), new StringWriter(), new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalFormRequestsCommand.ExitSuccess);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain("| model |");
        text.ShouldContain("對話模型是 `fake`");
        // Without directives the fake follows the keyword gate, so the two rows agree.
        var rows = text.Split('\n').Where(line => line.StartsWith("| keyword |", StringComparison.Ordinal) || line.StartsWith("| model |", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf('|', 2) + 1)..]).ToList();
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(rows[1]);
    }

    [Fact]
    public async Task With_case_types_each_question_also_gets_the_combined_call_of_286()
    {
        var report = Path.Combine(_directory, "combined.md");
        var fake = new ChatClientProvider(new FakeChatClient("fake-eval"), "fake", "smartagri.fake", "fake-eval", null);
        var command = new EvalFormRequestsCommand(fake, TimeProvider.System);

        var exit = await command.RunAsync(
            new EvalFormRequestsCommand.Arguments("model", null, report, false, CaseProposalEvalSet.DefaultDirectory), new StringWriter(), new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalFormRequestsCommand.ExitSuccess);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain($"| {EvalFormRequestsCommand.Combined} |");
        text.ShouldContain("合成呼叫（#286）：表單工具之外，同時提供這些可提議的案件類型");
        text.ShouldContain("`repair`＝「設備報修」");
        text.ShouldContain("| id | 類別 | 標記 | 關鍵字 | 模型 | 合成呼叫 | 問題 |");
        text.ShouldContain("合成呼叫改提議案件（沒有給表單）的有");
        // The fake decides the form first in the combined call too (decision L).
        text.ShouldContain("| p01 | positive | form | form ✓ | form ✓ | form ✓ |");

        // It is a model call: not with --trigger keyword; and the case-type set must load.
        (await command.RunAsync(new EvalFormRequestsCommand.Arguments("keyword", null, report, false, CaseProposalEvalSet.DefaultDirectory), new StringWriter(), new StringWriter(), CancellationToken))
            .ShouldBe(EvalFormRequestsCommand.ExitUsage);
        (await command.RunAsync(new EvalFormRequestsCommand.Arguments("model", null, report, false, _directory), new StringWriter(), new StringWriter(), CancellationToken))
            .ShouldBe(EvalFormRequestsCommand.ExitUsage);
    }

    [Fact]
    public async Task A_failing_model_is_counted_and_not_judged()
    {
        var report = Path.Combine(_directory, "failing.md");
        var failing = new ChatClientProvider(new FailingChatClient(), "openai", "openai", "broken", null);
        var command = new EvalFormRequestsCommand(failing, TimeProvider.System);

        var exit = await command.RunAsync(new EvalFormRequestsCommand.Arguments("model", null, report, false), new StringWriter(), new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalFormRequestsCommand.ExitFailed);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        var count = FormRequestEvalSet.Load(FormRequestEvalSet.DefaultDirectory).Questions.Count;
        text.ShouldContain($"| model | 0/0（—） | 0/0（—） | — | 0/0（—） | {count} |");
    }

    [Theory]
    [InlineData(new string[0], "both", true)]
    [InlineData(new[] { "--trigger", "keyword" }, "keyword", true)]
    [InlineData(new[] { "--trigger", "sometimes" }, "both", false)]
    [InlineData(new[] { "--unknown" }, "both", false)]
    [InlineData(new[] { "--case-types", "apps/api/eval/case-proposals" }, "both", true)]
    [InlineData(new[] { "--case-types" }, "both", false)]
    public void Arguments_are_parsed(string[] args, string trigger, bool valid)
    {
        EvalFormRequestsCommand.TryParse(args, out var arguments, out var error).ShouldBe(valid);
        if (valid)
        {
            arguments.Trigger.ShouldBe(trigger);
            error.ShouldBeNull();
        }
        else
        {
            error.ShouldNotBeNull();
        }
    }

    private sealed class FailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("The provider is unreachable.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
