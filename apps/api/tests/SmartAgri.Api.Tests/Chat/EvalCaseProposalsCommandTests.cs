using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Api.Chat.Evaluation;
using SmartAgri.Application.Ai;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Chat;

/// <summary>
/// <c>eval-case-proposals</c> (M7-12 #257) without a database: the committed labelled set loads and
/// covers every category; the stage follows decision L (query → form → case); scoring counts missed,
/// false triggers and wrong types; the keyword trigger runs with no model and measures the known
/// false trigger (a type name containing a case word); the model trigger runs the production
/// declarations and prompts through the configured client (here <c>Fake</c>, which the report flags,
/// or a scripted client); a failing model is counted, not judged, and its message never leaks a key.
/// </summary>
public sealed class EvalCaseProposalsCommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("case-proposals-eval-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static CaseProposalEvalQuestion Question(string category, string expected, string id = "q") => new(id, "問題", expected, category, null);

    [Fact]
    public void The_committed_set_covers_every_category_and_every_type()
    {
        var set = CaseProposalEvalSet.Load(CaseProposalEvalSet.DefaultDirectory);

        set.Questions.Count.ShouldBeInRange(30, 50);
        set.CaseTypes.Count.ShouldBeInRange(2, 3);
        set.Questions.Count(question => question.Category == CaseProposalEvalSet.CaseCategory).ShouldBeGreaterThanOrEqualTo(10);
        set.Questions.Count(question => question.Category == CaseProposalEvalSet.None).ShouldBeGreaterThanOrEqualTo(10);
        set.Questions.Count(question => question.Category == CaseProposalEvalSet.Form).ShouldBeGreaterThanOrEqualTo(4);
        set.Questions.Count(question => question.Category == CaseProposalEvalSet.Query).ShouldBeGreaterThanOrEqualTo(4);
        set.Questions.Count(question => question.Category == CaseProposalEvalSet.Ambiguous).ShouldBeGreaterThanOrEqualTo(5);
        foreach (var type in set.CaseTypes)
        {
            set.Questions.Count(question => question.Category == CaseProposalEvalSet.CaseCategory && question.ExpectedTypeKey == type.Key)
                .ShouldBeGreaterThanOrEqualTo(3, type.Key);
        }

        set.Offers.Select(offer => offer.TypeId).Distinct().Count().ShouldBe(set.CaseTypes.Count);
        set.KeyOf(set.Offers[1].TypeId).ShouldBe(set.CaseTypes[1].Key);
        set.Fingerprint.Length.ShouldBe(12);
    }

    [Fact]
    public void An_inconsistent_set_is_refused_with_every_problem()
    {
        File.WriteAllText(Path.Combine(_directory, "questions.json"), """
            {
              "form": { "title": "表單", "purpose": "" },
              "caseTypes": [ { "key": "a:b", "name": "設備報修" } ],
              "questions": [
                { "id": "x", "question": "要報修", "expected": "none", "category": "case" },
                { "id": "x", "question": "", "expected": "case:unknown", "category": "ambiguous" }
              ]
            }
            """);

        var exception = Should.Throw<CaseProposalEvalSetException>(() => CaseProposalEvalSet.Load(_directory));
        exception.Message.ShouldContain("form 需要 title 與 purpose");
        exception.Message.ShouldContain("需要 key（不含冒號）與 name");
        exception.Message.ShouldContain("category 必須是 case");
        exception.Message.ShouldContain("expected 必須是 none、form、query 或 case:");
        exception.Message.ShouldContain("題目 id 重複：x");
    }

    [Fact]
    public void The_stage_takes_the_query_then_the_form_then_the_case()
    {
        var proposal = CaseProposalEvalOutcome.Case("repair", "標題");

        CaseProposalEvalScoring.Stage(true, CaseProposalEvalFormDecision.Error, proposal).ShouldBe(CaseProposalEvalOutcome.Query);
        CaseProposalEvalScoring.Stage(false, CaseProposalEvalFormDecision.Form, proposal).ShouldBe(CaseProposalEvalOutcome.Form);
        CaseProposalEvalScoring.Stage(false, CaseProposalEvalFormDecision.Error, proposal).ShouldBe(CaseProposalEvalOutcome.Error);
        CaseProposalEvalScoring.Stage(false, CaseProposalEvalFormDecision.None, proposal).ShouldBe(proposal);
        CaseProposalEvalScoring.Stage(false, CaseProposalEvalFormDecision.None, CaseProposalEvalOutcome.None).ShouldBe(CaseProposalEvalOutcome.None);
    }

    [Fact]
    public void Scoring_counts_missed_false_triggers_and_wrong_types_and_leaves_errors_out()
    {
        var summary = CaseProposalEvalScoring.Summarize("model", stage: true,
        [
            (Question("case", "case:repair"), CaseProposalEvalOutcome.Case("repair", "t")),
            (Question("case", "case:repair"), CaseProposalEvalOutcome.Case("purchase", "t")),
            (Question("case", "case:return"), CaseProposalEvalOutcome.Form),
            (Question("case", "case:return"), CaseProposalEvalOutcome.Error),
            (Question("none", "none"), CaseProposalEvalOutcome.Case("purchase", "t")),
            (Question("none", "none"), CaseProposalEvalOutcome.None),
            (Question("form", "form"), CaseProposalEvalOutcome.Form),
            (Question("form", "form"), CaseProposalEvalOutcome.Case("repair", "t")),
            (Question("query", "query"), CaseProposalEvalOutcome.Query),
            (Question("ambiguous", "case:repair"), CaseProposalEvalOutcome.Case("repair", "t")),
            (Question("ambiguous", "form"), CaseProposalEvalOutcome.None),
        ]);

        summary.ShouldBe(new CaseProposalEvalSummary("model", true, 3, 1, 2, 1, 2, 1, 2, 1, 1, 1, 0, 1, 2, 1, 1));
        summary.MissedRate!.Value.ShouldBe(1.0 / 3, 1e-9);
        summary.FalseTriggers.ShouldBe(2);
        summary.FalseTriggerRate!.Value.ShouldBe(2.0 / 5, 1e-9);
        summary.WrongTypeRate.ShouldBe(0.5);
        summary.AmbiguousAgreement.ShouldBe(0.5);

        // The case layer judges only the case decision: a form label agrees with no case.
        CaseProposalEvalScoring.Agrees(Question("ambiguous", "form"), CaseProposalEvalOutcome.None, stage: false).ShouldBeTrue();
        CaseProposalEvalScoring.Agrees(Question("ambiguous", "form"), CaseProposalEvalOutcome.None, stage: true).ShouldBeFalse();
    }

    [Fact]
    public async Task The_keyword_trigger_runs_without_a_model_and_measures_the_type_name_false_trigger()
    {
        var report = Path.Combine(_directory, "keyword.md");
        var unconfigured = ChatClientProvider.Create(new ChatModelOptions());
        var command = new EvalCaseProposalsCommand(unconfigured, TimeProvider.System);
        var output = new StringWriter();

        var exit = await command.RunAsync(new EvalCaseProposalsCommand.Arguments("keyword", null, report, false), output, new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalCaseProposalsCommand.ExitSuccess);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain("| keyword |");
        text.ShouldContain("對話模型：未執行");
        text.ShouldNotContain("| model |");
        text.ShouldNotContain("Token 用量");
        // Decision T with three types: a case word without a type name proposes nothing …
        text.ShouldContain("| c01 | case | case:repair | none ✗ | none ✗ |");
        // … and a type name that contains a case word proposes on a question about the type (#254's known false trigger),
        text.ShouldContain("| n01 | none | none | case:purchase ✗ | case:purchase ✗ |");
        text.ShouldContain("## 類型名稱本身含開案關鍵字");
        // … unless a statistics question goes to the query first (decision L).
        text.ShouldContain("| q02 | query | query | case:purchase ✗ | query ✓ |");
        output.ToString().ShouldContain("keyword（提議階段）");

        // The model trigger needs a model.
        (await command.RunAsync(new EvalCaseProposalsCommand.Arguments("model", null, report, false), output, new StringWriter(), CancellationToken))
            .ShouldBe(EvalCaseProposalsCommand.ExitFailed);
    }

    [Fact]
    public async Task The_model_trigger_runs_the_production_tools_and_flags_the_fake()
    {
        var report = Path.Combine(_directory, "model.md");
        var fake = new ChatClientProvider(new FakeChatClient("fake-eval"), "fake", "smartagri.fake", "fake-eval", null);
        var command = new EvalCaseProposalsCommand(fake, TimeProvider.System);

        var exit = await command.RunAsync(new EvalCaseProposalsCommand.Arguments("both", null, report, false), new StringWriter(), new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalCaseProposalsCommand.ExitSuccess);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain("| model |");
        text.ShouldContain("對話模型是 `fake`");
        text.ShouldContain("## Token 用量");
        text.ShouldContain("## 模型草擬的案件標題（案件層）");
        // The fake calls propose_case with the first offered type whenever a case word matches.
        text.ShouldContain("| c02 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ |");
        text.ShouldContain("| c07 | case | case:purchase | case:purchase ✓ | case:purchase ✓ | case:repair ✗ | case:repair ✗ |");
    }

    [Fact]
    public async Task A_scripted_model_is_judged_per_layer_with_its_usage()
    {
        File.WriteAllText(Path.Combine(_directory, "questions.json"), """
            {
              "form": { "title": "田間異常回報", "purpose": "記錄病蟲害。" },
              "caseTypes": [ { "key": "repair", "name": "設備報修", "description": "設備故障。" } ],
              "questions": [
                { "id": "c1", "question": "噴霧機壞了", "expected": "case:repair", "category": "case" },
                { "id": "f1", "question": "番茄有黃斑", "expected": "form", "category": "form" },
                { "id": "n1", "question": "今天天氣如何", "expected": "none", "category": "none" }
              ]
            }
            """);
        var report = Path.Combine(_directory, "scripted.md");
        var client = new ScriptedChatClient();
        var command = new EvalCaseProposalsCommand(new ChatClientProvider(client, "openai", "openai", "scripted", null), TimeProvider.System);

        var exit = await command.RunAsync(new EvalCaseProposalsCommand.Arguments("model", _directory, report, false), new StringWriter(), new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalCaseProposalsCommand.ExitSuccess);
        // One form call and one case call per question, with the production tools.
        client.Tools.Count(name => name == "request_database_form").ShouldBe(3);
        client.Tools.Count(name => name == "propose_case").ShouldBe(3);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain("| c1 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 100／10 | 200／20 |");
        // The form layer took f1 first, although the case layer alone would have proposed (a false trigger there).
        text.ShouldContain("| f1 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ |");
        text.ShouldContain("| n1 | none | none | none ✓ | none ✓ | none ✓（案件工具參數不合法） | none ✓ |");
        text.ShouldContain("| 案件選擇（propose_case） | 3 | 600 | 60 | 200.0／20.0 |");
        text.ShouldContain("| c1 | repair | 模型草擬：噴霧機故障 |");
    }

    [Fact]
    public async Task A_failing_model_is_counted_not_judged_and_its_message_is_masked()
    {
        var report = Path.Combine(_directory, "failing.md");
        var error = new StringWriter();
        var failing = new ChatClientProvider(new FailingChatClient(), "openai", "openai", "broken", null);
        var command = new EvalCaseProposalsCommand(failing, TimeProvider.System);

        var exit = await command.RunAsync(new EvalCaseProposalsCommand.Arguments("model", null, report, false), new StringWriter(), error, CancellationToken);

        exit.ShouldBe(EvalCaseProposalsCommand.ExitFailed);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        var set = CaseProposalEvalSet.Load(CaseProposalEvalSet.DefaultDirectory);
        var count = set.Questions.Count;
        text.ShouldContain($"| model | 0/0（—） | 0/0（—） | 0/0 | 0/0 | 0/0 | 0/0（—） | — | — | 0/0（—） | {count} |");
        // The query layer needs no model, so statistics questions are still judged in the stage view.
        var queries = set.Questions.Count(question => question.Category == CaseProposalEvalSet.Query);
        text.ShouldContain($"| 0/0 | {queries}/{queries} | 0/0（—） | {count - queries} |");
        error.ToString().ShouldContain("<已遮蔽>");
        error.ToString().ShouldNotContain(FailingChatClient.KeyLike);
    }

    [Theory]
    [InlineData(new string[0], "both", true)]
    [InlineData(new[] { "--trigger", "keyword" }, "keyword", true)]
    [InlineData(new[] { "--trigger", "sometimes" }, "both", false)]
    [InlineData(new[] { "--set" }, "both", false)]
    [InlineData(new[] { "--unknown" }, "both", false)]
    public void Arguments_are_parsed(string[] args, string trigger, bool valid)
    {
        EvalCaseProposalsCommand.TryParse(args, out var arguments, out var error).ShouldBe(valid);
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

    /// <summary>Gives the form only for 「黃斑」, proposes the first offered type for 「壞了」 and 「黃斑」, and
    /// calls the case tool with an unoffered id otherwise; reports fixed usage per tool.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public List<string> Tools { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var tool = options!.Tools!.OfType<AIFunctionDeclaration>().Single();
            Tools.Add(tool.Name);
            var question = messages.Last().Text;
            var ids = tool.JsonSchema.GetProperty("properties").EnumerateObject().First().Value.GetProperty("enum");
            ChatMessage reply;
            UsageDetails usage;
            if (tool.Name == "request_database_form")
            {
                usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 };
                reply = question.Contains("黃斑", StringComparison.Ordinal)
                    ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", tool.Name, new Dictionary<string, object?> { ["databaseId"] = ids[0].GetString() })])
                    : new ChatMessage(ChatRole.Assistant, "不需要表單");
            }
            else
            {
                usage = new UsageDetails { InputTokenCount = 200, OutputTokenCount = 20 };
                var typeId = question.Contains("壞了", StringComparison.Ordinal) || question.Contains("黃斑", StringComparison.Ordinal)
                    ? ids[0].GetString()
                    : Guid.Empty.ToString();
                reply = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("2", tool.Name, new Dictionary<string, object?>
                {
                    ["caseTypeId"] = typeId,
                    ["title"] = "模型草擬：噴霧機故障",
                    ["description"] = "說明",
                })]);
            }

            return Task.FromResult(new ChatResponse(reply) { Usage = usage });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FailingChatClient : IChatClient
    {
        /// <summary>Shaped like a provider key (built so the source never contains the literal prefix).</summary>
        public static readonly string KeyLike = "s" + "k-test" + new string('x', 20);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException($"Incorrect API key provided: {KeyLike}.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
