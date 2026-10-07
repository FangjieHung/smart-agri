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

        // The model's stage (#286): the query layer, then the combined selection's own outcome.
        CaseProposalEvalScoring.Stage(true, proposal).ShouldBe(CaseProposalEvalOutcome.Query);
        CaseProposalEvalScoring.Stage(false, proposal).ShouldBe(proposal);
        CaseProposalEvalScoring.Stage(false, CaseProposalEvalOutcome.Form).ShouldBe(CaseProposalEvalOutcome.Form);
        CaseProposalEvalScoring.Stage(false, CaseProposalEvalOutcome.Error).ShouldBe(CaseProposalEvalOutcome.Error);
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
        // Without --types the set's own descriptions are offered (#293), as before.
        text.ShouldContain("可提議的案件類型（依序提供；說明為題庫內建）");
        text.ShouldContain("`repair`＝「設備報修」：農機、灌溉、溫室、冷藏庫等設備故障或損壞，需要派人到場維修。");
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
        // #286: one combined call (both production tools) and one case-only call per question; #297 added
        // no_matching_type to both, as production offers it.
        client.Tools.Count(names => names == "request_database_form+propose_case+no_matching_type").ShouldBe(3);
        client.Tools.Count(names => names == "propose_case+no_matching_type").ShouldBe(3);
        client.Tools.ShouldNotContain("request_database_form");
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain("| c1 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 300／30 | 200／20 |");
        // The combined call gave f1 the form, although the case layer alone would have proposed (a false trigger there).
        text.ShouldContain("| f1 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ |");
        text.ShouldContain("| n1 | none | none | none ✓ | none ✓ | none ✓（案件工具參數不合法） | none ✓（合成呼叫的工具或參數不合法） |");
        text.ShouldContain("| 合成選擇（request_database_form＋propose_case） | 3 | 900 | 90 | 300.0／30.0 |");
        text.ShouldContain("| 案件選擇（只有 propose_case，案件層） | 3 | 600 | 60 | 200.0／20.0 |");
        text.ShouldContain("合成選擇呼叫（#286）");
        text.ShouldContain("| c1 | repair | 模型草擬：噴霧機故障 |");
    }

    [Fact]
    public async Task Choosing_no_match_is_judged_as_no_proposal_and_counted_in_the_report()
    {
        File.WriteAllText(Path.Combine(_directory, "questions.json"), """
            {
              "form": { "title": "田間異常回報", "purpose": "記錄病蟲害。" },
              "caseTypes": [ { "key": "repair", "name": "設備報修", "description": "設備故障。" } ],
              "questions": [
                { "id": "c1", "question": "噴霧機壞了", "expected": "case:repair", "category": "case" },
                { "id": "f1", "question": "番茄有黃斑", "expected": "form", "category": "form" }
              ]
            }
            """);
        var report = Path.Combine(_directory, "no-match.md");
        var command = new EvalCaseProposalsCommand(new ChatClientProvider(new NoMatchChatClient(), "openai", "openai", "declining", null), TimeProvider.System);

        var exit = await command.RunAsync(new EvalCaseProposalsCommand.Arguments("model", _directory, report, false), new StringWriter(), new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalCaseProposalsCommand.ExitSuccess);
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        // #297: not a failure and not a rejected call — no proposal, marked as the explicit choice.
        text.ShouldContain("| c1 | case | case:repair | none ✗ | none ✗ | none ✗（都不符合） | none ✗（都不符合） |");
        text.ShouldContain("| f1 | form | form | none ✓ | none ✗ | none ✓（都不符合） | none ✗（都不符合） |");
        text.ShouldContain("模型明確選「都不符合」（`no_matching_type`）：案件層 2/2 題（c1、f1）；合成選擇 2/2 題（c1、f1）。");
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

    [Fact]
    public void The_committed_exclusions_file_rewords_every_type_with_what_it_covers_and_what_it_does_not()
    {
        var set = CaseProposalEvalSet.Load(CaseProposalEvalSet.DefaultDirectory);

        var reworded = set.WithTypes("types-with-exclusions.json");

        reworded.TypesPath.ShouldBe(Path.Combine(set.Directory, "types-with-exclusions.json"));
        reworded.TypesFingerprint!.Length.ShouldBe(12);
        reworded.Fingerprint.ShouldBe(set.Fingerprint);
        reworded.Questions.ShouldBe(set.Questions);
        reworded.CaseTypes.Select(type => type.Key).ShouldBe(set.CaseTypes.Select(type => type.Key));
        reworded.CaseTypes.Select(type => type.Name).ShouldBe(set.CaseTypes.Select(type => type.Name));
        foreach (var type in reworded.CaseTypes)
        {
            type.Description.ShouldNotBeNull();
            System.Text.RegularExpressions.Regex.IsMatch(type.Description, "(?<!不)包括").ShouldBeTrue(type.Key);
            type.Description.ShouldContain("不包括", Case.Sensitive, type.Key);
            type.Description.Length.ShouldBeLessThanOrEqualTo(SmartAgri.Domain.Cases.CaseType.DescriptionMaxLength);
        }

        reworded.CaseTypes[0].Description!.ShouldContain("不包括作物病蟲害、作物生長異常");
        reworded.Offers.Select(offer => offer.TypeId).ShouldBe(set.Offers.Select(offer => offer.TypeId));
        reworded.Offers[0].Description.ShouldBe(reworded.CaseTypes[0].Description);
        set.TypesPath.ShouldBeNull();
    }

    [Fact]
    public async Task A_types_file_whose_keys_differ_from_the_set_or_that_is_missing_is_refused()
    {
        var types = Path.Combine(_directory, "types.json");
        File.WriteAllText(types, $$"""
            {
              // keys out of order, and one description longer than production allows
              "caseTypes": [
                { "key": "purchase", "name": "採購申請", "description": "{{new string('長', 501)}}" },
                { "key": "repair", "name": "設備報修" },
                { "key": "return", "name": "客戶退貨" }
              ]
            }
            """);
        var command = new EvalCaseProposalsCommand(ChatClientProvider.Create(new ChatModelOptions()), TimeProvider.System);
        var error = new StringWriter();

        var exit = await command.RunAsync(
            new EvalCaseProposalsCommand.Arguments("keyword", null, Path.Combine(_directory, "r.md"), false, types), new StringWriter(), error, CancellationToken);

        exit.ShouldBe(EvalCaseProposalsCommand.ExitUsage);
        error.ToString().ShouldContain("key 必須與題庫相同、順序也相同（題庫：repair、purchase、return；這個檔案：purchase、repair、return）");
        error.ToString().ShouldContain("說明超過 500 字");
        File.Exists(Path.Combine(_directory, "r.md")).ShouldBeFalse();

        error = new StringWriter();
        (await command.RunAsync(
            new EvalCaseProposalsCommand.Arguments("keyword", null, Path.Combine(_directory, "r.md"), false, "no-such-types.json"), new StringWriter(), error, CancellationToken))
            .ShouldBe(EvalCaseProposalsCommand.ExitUsage);
        error.ToString().ShouldContain("找不到類型說明檔");
    }

    [Fact]
    public async Task A_types_file_replaces_the_descriptions_offered_to_both_calls_and_the_report_names_it()
    {
        File.WriteAllText(Path.Combine(_directory, "questions.json"), """
            {
              "form": { "title": "田間異常回報", "purpose": "記錄病蟲害。" },
              "caseTypes": [ { "key": "repair", "name": "設備報修", "description": "設備故障。" } ],
              "questions": [
                { "id": "c1", "question": "噴霧機壞了", "expected": "case:repair", "category": "case" },
                { "id": "f1", "question": "番茄有黃斑", "expected": "form", "category": "form" }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(_directory, "exclusions.json"), """
            { "caseTypes": [ { "key": "repair", "name": "設備報修", "description": "設備故障。不包括作物病蟲害，請用田間異常回報。" } ] }
            """);
        var report = Path.Combine(_directory, "types.md");
        var client = new ScriptedChatClient();
        var command = new EvalCaseProposalsCommand(new ChatClientProvider(client, "openai", "openai", "scripted", null), TimeProvider.System);
        var output = new StringWriter();

        // A bare file name is found next to questions.json.
        var exit = await command.RunAsync(
            new EvalCaseProposalsCommand.Arguments("both", _directory, report, false, "exclusions.json"), output, new StringWriter(), CancellationToken);

        exit.ShouldBe(EvalCaseProposalsCommand.ExitSuccess);
        client.Descriptions.Count.ShouldBe(4);
        client.Descriptions.ShouldAllBe(description => description.Contains("（設備故障。不包括作物病蟲害，請用田間異常回報。）"));
        var text = await File.ReadAllTextAsync(report, CancellationToken);
        text.ShouldContain("說明取自 `");
        text.ShouldContain("exclusions.json`，指紋 `");
        text.ShouldContain("取代題庫內建的說明）");
        text.ShouldContain("`repair`＝「設備報修」：設備故障。不包括作物病蟲害，請用田間異常回報。");
        output.ToString().ShouldContain("案件類型說明取自");
    }

    [Theory]
    [InlineData(new[] { "--types", "types-with-exclusions.json", "--trigger", "model" }, "types-with-exclusions.json")]
    [InlineData(new[] { "--trigger", "model" }, null)]
    public void The_types_argument_is_optional(string[] args, string? types)
    {
        EvalCaseProposalsCommand.TryParse(args, out var arguments, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        arguments.TypesPath.ShouldBe(types);
        arguments.Trigger.ShouldBe("model");
    }

    [Theory]
    [InlineData(new string[0], "both", true)]
    [InlineData(new[] { "--trigger", "keyword" }, "keyword", true)]
    [InlineData(new[] { "--trigger", "sometimes" }, "both", false)]
    [InlineData(new[] { "--set" }, "both", false)]
    [InlineData(new[] { "--types" }, "both", false)]
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

    /// <summary>The case-only call proposes the first offered type for 「壞了」 and 「黃斑」 and calls the case tool
    /// with an unoffered id otherwise; the combined call gives the form for 「黃斑」, the case for 「壞了」 and an
    /// unoffered id otherwise. Fixed usage per kind of call.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public List<string> Tools { get; } = [];

        /// <summary>The case tool's description (it lists the offered types with their descriptions), per call.</summary>
        public List<string> Descriptions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var tools = options!.Tools!.OfType<AIFunctionDeclaration>().ToList();
            Tools.Add(string.Join('+', tools.Select(declaration => declaration.Name)));
            // By name: since #297 both calls end with no_matching_type, which this client never calls.
            var tool = tools.Single(declaration => declaration.Name == "propose_case");
            Descriptions.Add(tool.Description);
            var question = messages.Last().Text;
            var ids = tool.JsonSchema.GetProperty("properties").EnumerateObject().First().Value.GetProperty("enum");
            ChatMessage reply;
            UsageDetails usage;
            if (tools.Any(declaration => declaration.Name == "request_database_form"))
            {
                usage = new UsageDetails { InputTokenCount = 300, OutputTokenCount = 30 };
                var form = tools[0];
                var formIds = form.JsonSchema.GetProperty("properties").GetProperty("databaseId").GetProperty("enum");
                reply = question.Contains("黃斑", StringComparison.Ordinal)
                    ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", form.Name, new Dictionary<string, object?> { ["databaseId"] = formIds[0].GetString() })])
                    : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("2", tool.Name, new Dictionary<string, object?>
                    {
                        ["caseTypeId"] = question.Contains("壞了", StringComparison.Ordinal) ? ids[0].GetString() : Guid.Empty.ToString(),
                        ["title"] = "模型草擬：噴霧機故障",
                        ["description"] = "說明",
                    })]);
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

    /// <summary>Always calls no_matching_type (#297), which both calls must offer.</summary>
    private sealed class NoMatchChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var noMatch = options!.Tools!.OfType<AIFunctionDeclaration>().Single(declaration => declaration.Name == "no_matching_type");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", noMatch.Name, new Dictionary<string, object?>())]))
            {
                Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 5 },
            });
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
