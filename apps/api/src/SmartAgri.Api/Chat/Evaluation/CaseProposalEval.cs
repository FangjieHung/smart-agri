using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartAgri.Application.Cases;

namespace SmartAgri.Api.Chat.Evaluation;

/// <summary>One labelled question of the case-proposal trigger set (M7-12 #257).</summary>
/// <param name="Expected"><c>case:&lt;key&gt;</c>, <c>none</c>, <c>form</c> or <c>query</c>: what the
/// proposal stage should answer with.</param>
/// <param name="Category"><c>case</c>, <c>none</c>, <c>form</c>, <c>query</c> (each with its own
/// <paramref name="Expected"/>) or <c>ambiguous</c> (any, the labeller's best judgement; reported on its own).</param>
public sealed record CaseProposalEvalQuestion(string Id, string Question, string Expected, string Category, string? Note)
{
    /// <summary>The expected case type's key, or <see langword="null"/> when no case is expected.</summary>
    public string? ExpectedTypeKey =>
        Expected?.StartsWith(CaseProposalEvalSet.CasePrefix, StringComparison.Ordinal) == true ? Expected[CaseProposalEvalSet.CasePrefix.Length..] : null;
}

/// <summary>A case type the set offers: its label key, and the name and description an admin would write.</summary>
public sealed record CaseProposalEvalType(string Key, string Name, string? Description);

public sealed class CaseProposalEvalSetException(string directory, IReadOnlyList<string> problems)
    : Exception($"案件提議評測題庫「{directory}」無法使用：\n- " + string.Join("\n- ", problems));

/// <summary>
/// The labelled question set of <c>eval-case-proposals</c> (M7-12 #257): <c>questions.json</c> in
/// <c>apps/api/eval/case-proposals/</c>, copied next to the Api assembly (<see cref="DefaultDirectory"/>);
/// its README describes the format.
/// </summary>
public sealed class CaseProposalEvalSet
{
    public const string CasePrefix = "case:";
    public const string None = "none";
    public const string Form = "form";
    public const string Query = "query";
    public const string CaseCategory = "case";
    public const string Ambiguous = "ambiguous";

    /// <summary>The id the sample form is offered under (any fixed GUID; nothing is looked up).</summary>
    public static readonly Guid SampleFormId = Guid.Parse("0199a000-0257-7000-8000-000000000000");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private CaseProposalEvalSet(
        string directory, FormRequestEvalForm form, IReadOnlyList<CaseProposalEvalType> types, IReadOnlyList<CaseProposalEvalQuestion> questions, string fingerprint)
    {
        Directory = directory;
        SampleForm = form;
        CaseTypes = types;
        Questions = questions;
        Fingerprint = fingerprint;
        Offers = [.. types.Select((type, index) => new CaseProposalOffer(TypeId(index), type.Name, type.Description ?? string.Empty))];
    }

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "eval", "case-proposals");

    public string Directory { get; }

    public FormRequestEvalForm SampleForm { get; }

    public IReadOnlyList<CaseProposalEvalType> CaseTypes { get; }

    /// <summary>The types as offered to the rules and the model, in the set's order, under fixed ids.</summary>
    public IReadOnlyList<CaseProposalOffer> Offers { get; }

    public IReadOnlyList<CaseProposalEvalQuestion> Questions { get; }

    /// <summary>The first 12 hex digits of <c>questions.json</c>'s SHA-256: which set a report judged.</summary>
    public string Fingerprint { get; }

    /// <summary>The fixed id of the <paramref name="index"/>-th type.</summary>
    public static Guid TypeId(int index) => Guid.Parse($"0199a000-0257-7000-8000-{index + 1:D12}");

    /// <summary>The key of the offered type <paramref name="typeId"/>.</summary>
    public string KeyOf(Guid typeId)
    {
        for (var index = 0; index < Offers.Count; index++)
        {
            if (Offers[index].TypeId == typeId)
            {
                return CaseTypes[index].Key;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(typeId), typeId, "Not an offered type.");
    }

    public static CaseProposalEvalSet Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        var path = Path.Combine(root, "questions.json");
        if (!File.Exists(path))
        {
            throw new CaseProposalEvalSetException(root, ["找不到 questions.json。"]);
        }

        var bytes = File.ReadAllBytes(path);
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new CaseProposalEvalSetException(root, [$"questions.json 不是正確的 JSON：{exception.Message}"]);
        }

        var problems = new List<string>();
        if (document?.Form is not { } form || string.IsNullOrWhiteSpace(form.Title) || string.IsNullOrWhiteSpace(form.Purpose))
        {
            problems.Add("form 需要 title 與 purpose。");
        }

        var types = document?.CaseTypes ?? [];
        if (types.Count == 0)
        {
            problems.Add("caseTypes 是空的。");
        }

        foreach (var type in types)
        {
            if (string.IsNullOrWhiteSpace(type.Key) || type.Key.Contains(':', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(type.Name))
            {
                problems.Add($"案件類型「{type.Key}」：需要 key（不含冒號）與 name。");
            }
        }

        foreach (var duplicate in types.GroupBy(type => type.Key).Where(group => group.Count() > 1))
        {
            problems.Add($"案件類型 key 重複：{duplicate.Key}");
        }

        var keys = types.Select(type => type.Key).ToHashSet(StringComparer.Ordinal);
        var questions = document?.Questions ?? [];
        if (questions.Count == 0)
        {
            problems.Add("questions 是空的。");
        }

        foreach (var question in questions)
        {
            var label = string.IsNullOrWhiteSpace(question.Id) ? "（沒有 id 的題目）" : question.Id;
            if (string.IsNullOrWhiteSpace(question.Id) || string.IsNullOrWhiteSpace(question.Question))
            {
                problems.Add($"{label}：需要 id 與 question。");
            }

            var expected = question.Expected ?? string.Empty;
            var typeKey = question.ExpectedTypeKey;
            if (expected is not (None or Form or Query) && (typeKey is null || !keys.Contains(typeKey)))
            {
                problems.Add($"{label}：expected 必須是 none、form、query 或 case:<caseTypes 的 key>。");
                continue;
            }

            var consistent = question.Category switch
            {
                CaseCategory => typeKey is not null,
                None => expected == None,
                Form => expected == Form,
                Query => expected == Query,
                Ambiguous => true,
                _ => false,
            };
            if (!consistent)
            {
                problems.Add($"{label}：category 必須是 case（expected case:<key>）、none、form、query（expected 同名）或 ambiguous。");
            }
        }

        foreach (var duplicate in questions.GroupBy(question => question.Id).Where(group => group.Count() > 1))
        {
            problems.Add($"題目 id 重複：{duplicate.Key}");
        }

        if (problems.Count > 0)
        {
            throw new CaseProposalEvalSetException(root, problems);
        }

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
        return new CaseProposalEvalSet(root, document!.Form!, types, questions, fingerprint);
    }

    private sealed record Document(FormRequestEvalForm? Form, List<CaseProposalEvalType>? CaseTypes, List<CaseProposalEvalQuestion>? Questions);
}

/// <summary>What a run answered one question with.</summary>
public enum CaseProposalEvalOutcomeKind
{
    /// <summary>No proposal: the answer pipeline would answer.</summary>
    None,

    /// <summary>The database query tools are offered (the query layer, before every proposal).</summary>
    Query,

    /// <summary>The sample form is shown.</summary>
    Form,

    /// <summary>A case proposal of <see cref="CaseProposalEvalOutcome.TypeKey"/>.</summary>
    Case,

    /// <summary>A model call this outcome depends on failed: not judged (production would fall back to the keywords).</summary>
    Error,
}

/// <summary>One outcome; a case also has its type's key and the drafted title.</summary>
public sealed record CaseProposalEvalOutcome(CaseProposalEvalOutcomeKind Kind, string? TypeKey = null, string? Title = null)
{
    public static readonly CaseProposalEvalOutcome None = new(CaseProposalEvalOutcomeKind.None);
    public static readonly CaseProposalEvalOutcome Query = new(CaseProposalEvalOutcomeKind.Query);
    public static readonly CaseProposalEvalOutcome Form = new(CaseProposalEvalOutcomeKind.Form);
    public static readonly CaseProposalEvalOutcome Error = new(CaseProposalEvalOutcomeKind.Error);

    public static CaseProposalEvalOutcome Case(string typeKey, string title) => new(CaseProposalEvalOutcomeKind.Case, typeKey, title);

    /// <summary>As the labels write it: <c>none</c>, <c>query</c>, <c>form</c>, <c>case:&lt;key&gt;</c> or <c>error</c>.</summary>
    public string Label => Kind switch
    {
        CaseProposalEvalOutcomeKind.Case => CaseProposalEvalSet.CasePrefix + TypeKey,
        CaseProposalEvalOutcomeKind.Query => CaseProposalEvalSet.Query,
        CaseProposalEvalOutcomeKind.Form => CaseProposalEvalSet.Form,
        CaseProposalEvalOutcomeKind.Error => "error",
        _ => CaseProposalEvalSet.None,
    };
}

/// <summary>What the form layer decided (keyword gate or the #164 selection call).</summary>
public enum CaseProposalEvalFormDecision
{
    Form,
    None,
    Error,
}

/// <summary>One trigger's decisions for one question: the case layer alone, and the whole stage.</summary>
/// <param name="CaseLayer">The case layer as if the layers before it had said no (the model's: the case-only call).</param>
/// <param name="Stage">Decision L: query → form → case → none (the model's: query → the combined selection, #286).</param>
/// <param name="CaseCallRejected">The model called a tool, but not <c>propose_case</c> with an offered id.</param>
/// <param name="SelectionCallRejected">The combined call named another tool or an unoffered id.</param>
public sealed record CaseProposalEvalDecisions(
    CaseProposalEvalOutcome CaseLayer, CaseProposalEvalOutcome Stage, bool CaseCallRejected = false, bool SelectionCallRejected = false);

/// <summary>The model's token usage for one question (<see langword="null"/> when the provider reported none):
/// the combined selection call (#286) and the case-only call.</summary>
public sealed record CaseProposalEvalUsage(long? SelectionInput, long? SelectionOutput, long? CaseInput, long? CaseOutput);

/// <summary>One question's decisions: the keyword trigger's, and the model's when it ran.</summary>
public sealed record CaseProposalEvalResult(
    CaseProposalEvalQuestion Question, CaseProposalEvalDecisions Keyword, CaseProposalEvalDecisions? Model, CaseProposalEvalUsage? Usage);

/// <summary>The rates of one trigger in one view. Rates are over judged questions (errors excluded); a
/// category with no judged question has a <see langword="null"/> rate.</summary>
/// <param name="Proposed">Case questions that got a case proposal (of any type): the wrong-type rate's denominator.</param>
/// <param name="FormRouted">Form questions that got the form (the stage view only).</param>
/// <param name="QueryRouted">Query questions the query layer took (the stage view only).</param>
public sealed record CaseProposalEvalSummary(
    string Trigger,
    bool Stage,
    int CaseQuestions,
    int Missed,
    int Proposed,
    int WrongType,
    int NoneQuestions,
    int NoneFalse,
    int FormQuestions,
    int FormFalse,
    int FormRouted,
    int QueryQuestions,
    int QueryFalse,
    int QueryRouted,
    int Ambiguous,
    int AmbiguousAgreed,
    int Errors)
{
    public int Negatives => NoneQuestions + FormQuestions + QueryQuestions;

    public int FalseTriggers => NoneFalse + FormFalse + QueryFalse;

    public double? MissedRate => CaseQuestions == 0 ? null : (double)Missed / CaseQuestions;

    public double? FalseTriggerRate => Negatives == 0 ? null : (double)FalseTriggers / Negatives;

    public double? WrongTypeRate => Proposed == 0 ? null : (double)WrongType / Proposed;

    public double? AmbiguousAgreement => Ambiguous == 0 ? null : (double)AmbiguousAgreed / Ambiguous;
}

/// <summary>Combining and judging (pure).</summary>
public static partial class CaseProposalEvalScoring
{
    /// <summary>The model's stage (#286): the query layer, then the combined selection's outcome (form, case or none).</summary>
    public static CaseProposalEvalOutcome Stage(bool asksForStatistics, CaseProposalEvalOutcome selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return asksForStatistics ? CaseProposalEvalOutcome.Query : selection;
    }

    /// <summary>Decision L by keyword: the query layer, then the form, then the case layer's outcome.</summary>
    public static CaseProposalEvalOutcome Stage(bool asksForStatistics, CaseProposalEvalFormDecision form, CaseProposalEvalOutcome caseLayer)
    {
        ArgumentNullException.ThrowIfNull(caseLayer);
        if (asksForStatistics)
        {
            return CaseProposalEvalOutcome.Query;
        }

        return form switch
        {
            CaseProposalEvalFormDecision.Error => CaseProposalEvalOutcome.Error,
            CaseProposalEvalFormDecision.Form => CaseProposalEvalOutcome.Form,
            _ => caseLayer,
        };
    }

    /// <summary>Whether <paramref name="outcome"/> is what <paramref name="question"/> is labelled. In
    /// the case-layer view only the case decision counts (a non-case label agrees with any non-case outcome).</summary>
    public static bool Agrees(CaseProposalEvalQuestion question, CaseProposalEvalOutcome outcome, bool stage)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(outcome);
        if (stage)
        {
            return outcome.Label == question.Expected;
        }

        return question.ExpectedTypeKey is { } key
            ? outcome.Kind == CaseProposalEvalOutcomeKind.Case && outcome.TypeKey == key
            : outcome.Kind != CaseProposalEvalOutcomeKind.Case;
    }

    public static CaseProposalEvalSummary Summarize(
        string trigger, bool stage, IEnumerable<(CaseProposalEvalQuestion Question, CaseProposalEvalOutcome Outcome)> decisions)
    {
        int cases = 0, missed = 0, proposed = 0, wrongType = 0, none = 0, noneFalse = 0, form = 0, formFalse = 0, formRouted = 0;
        int query = 0, queryFalse = 0, queryRouted = 0, ambiguous = 0, agreed = 0, errors = 0;
        foreach (var (question, outcome) in decisions)
        {
            if (outcome.Kind == CaseProposalEvalOutcomeKind.Error)
            {
                errors++;
                continue;
            }

            var isCase = outcome.Kind == CaseProposalEvalOutcomeKind.Case;
            switch (question.Category)
            {
                case CaseProposalEvalSet.CaseCategory:
                    cases++;
                    missed += isCase ? 0 : 1;
                    proposed += isCase ? 1 : 0;
                    wrongType += isCase && outcome.TypeKey != question.ExpectedTypeKey ? 1 : 0;
                    break;
                case CaseProposalEvalSet.None:
                    none++;
                    noneFalse += isCase ? 1 : 0;
                    break;
                case CaseProposalEvalSet.Form:
                    form++;
                    formFalse += isCase ? 1 : 0;
                    formRouted += outcome.Kind == CaseProposalEvalOutcomeKind.Form ? 1 : 0;
                    break;
                case CaseProposalEvalSet.Query:
                    query++;
                    queryFalse += isCase ? 1 : 0;
                    queryRouted += outcome.Kind == CaseProposalEvalOutcomeKind.Query ? 1 : 0;
                    break;
                default:
                    ambiguous++;
                    agreed += Agrees(question, outcome, stage) ? 1 : 0;
                    break;
            }
        }

        return new CaseProposalEvalSummary(
            trigger, stage, cases, missed, proposed, wrongType, none, noneFalse, form, formFalse, formRouted, query, queryFalse, queryRouted, ambiguous, agreed, errors);
    }

    /// <summary>
    /// The keyword rule's known false trigger, measured: the questions not labelled <c>case</c> that
    /// name an offered type whose name itself contains a case word (「採購申請」 has 申請), so
    /// <see cref="CaseProposalRules.KeywordProposal"/> proposes on any question about that type.
    /// </summary>
    public static IReadOnlyList<CaseProposalEvalResult> NamesTypeWithCaseWord(
        IReadOnlyList<CaseProposalEvalResult> results, IReadOnlyList<CaseProposalOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(offers);
        var names = offers.Where(offer => CaseProposalRules.AsksForCase(offer.Name)).Select(offer => Whitespace().Replace(offer.Name, string.Empty)).ToList();
        return
        [
            .. results.Where(result => result.Question.Category != CaseProposalEvalSet.CaseCategory
                && result.Question.ExpectedTypeKey is null
                && names.Any(name => Whitespace().Replace(result.Question.Question, string.Empty).Contains(name, StringComparison.Ordinal))),
        ];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>A finished run, as the report shows it.</summary>
public sealed record CaseProposalEvalRun(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string SetDisplayName,
    CaseProposalEvalSet Set,
    string? ChatProvider,
    string? ChatModel,
    IReadOnlyList<CaseProposalEvalResult> Results)
{
    public CaseProposalEvalSummary KeywordCaseLayer => Summary(EvalCaseProposalsCommand.Keyword, stage: false);

    public CaseProposalEvalSummary KeywordStage => Summary(EvalCaseProposalsCommand.Keyword, stage: true);

    public CaseProposalEvalSummary? ModelCaseLayer => ChatModel is null ? null : Summary(EvalCaseProposalsCommand.Model, stage: false);

    public CaseProposalEvalSummary? ModelStage => ChatModel is null ? null : Summary(EvalCaseProposalsCommand.Model, stage: true);

    private CaseProposalEvalSummary Summary(string trigger, bool stage) =>
        CaseProposalEvalScoring.Summarize(trigger, stage, Results.Select(result =>
        {
            var decisions = trigger == EvalCaseProposalsCommand.Keyword ? result.Keyword : result.Model!;
            return (result.Question, stage ? decisions.Stage : decisions.CaseLayer);
        }));
}

/// <summary>The Markdown report of <c>eval-case-proposals</c>, in Traditional Chinese like the rest of <c>docs/</c>.</summary>
public static class CaseProposalEvalReport
{
    public static string FileName(DateTimeOffset date, string? model)
    {
        var slug = new StringBuilder();
        foreach (var character in (model ?? "keyword").Trim().ToLowerInvariant())
        {
            slug.Append(character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' ? character : '-');
        }

        return $"{date:yyyy-MM-dd}-case-proposals-{slug.ToString().Trim('-')}.md";
    }

    public static string Render(CaseProposalEvalRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var set = run.Set;
        var hasModel = run.ChatModel is not null;
        var text = new StringBuilder();
        text.AppendLine("# 案件提議觸發評測（#257；#286 起整條提議階段走合成選擇）").AppendLine();
        text.AppendLine($"- 執行時間：{run.StartedAt:yyyy-MM-dd HH:mm:ss zzz}（{run.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} 秒）");
        text.AppendLine($"- 題庫：`{run.SetDisplayName}`（指紋 `{set.Fingerprint}`，{run.Results.Count} 題）");
        text.AppendLine($"- 範例表單：「{set.SampleForm.Title}」，收集目的：{set.SampleForm.Purpose}");
        text.AppendLine("- 可提議的案件類型（依序提供）：");
        foreach (var type in set.CaseTypes)
        {
            text.AppendLine($"  - `{type.Key}`＝「{type.Name}」：{type.Description}");
        }

        text.AppendLine(hasModel ? $"- 對話模型：{run.ChatModel}（{run.ChatProvider}）" : "- 對話模型：未執行（只評測關鍵字）");
        if (run.ChatProvider == "fake")
        {
            text.AppendLine("- **注意：對話模型是 `fake`，它在沒有指示詞時照關鍵字決定，模型欄的數字只證明流程可以執行，不代表真實模型的表現。**");
        }

        text.AppendLine("- 查詢層一律以 `DatabaseQueryTools.AsksForStatistics`（提供查詢工具的前置檢查）判定，不呼叫查詢模型。");
        if (hasModel)
        {
            text.AppendLine("- 模型的整條提議階段走正式環境的合成選擇呼叫（#286）：同時提供表單工具與案件工具，模型最多選一個；模型的案件層是只提供案件工具的單一呼叫（助理沒有表單時的正式路徑）。");
        }

        text.AppendLine().AppendLine("## 摘要：整條提議階段（決定 L：數據庫查詢 → 表單 → 案件）").AppendLine();
        AppendSummaryTable(text, [run.KeywordStage, run.ModelStage]);
        text.AppendLine().AppendLine("## 摘要：只看案件層（假設前面各層都沒有成立）").AppendLine();
        AppendSummaryTable(text, [run.KeywordCaseLayer, run.ModelCaseLayer]);

        text.AppendLine().AppendLine("漏觸＝應提議案件卻沒有提議；誤觸＝不應提議、應給表單或應走查詢的題目卻提議了案件；選錯類型的分母是有提議案件的應提議題。");

        var named = CaseProposalEvalScoring.NamesTypeWithCaseWord(run.Results, set.Offers);
        text.AppendLine().AppendLine("## 類型名稱本身含開案關鍵字").AppendLine();
        text.AppendLine(
            $"非案件題中，問題提到「名稱本身含開案關鍵字」的類型（{string.Join("、", set.Offers.Where(offer => CaseProposalRules.AsksForCase(offer.Name)).Select(offer => $"「{offer.Name}」"))}）的有 {named.Count} 題：" +
            $"關鍵字在案件層提議了 {named.Count(result => result.Keyword.CaseLayer.Kind == CaseProposalEvalOutcomeKind.Case)} 題、整條提議階段 {named.Count(result => result.Keyword.Stage.Kind == CaseProposalEvalOutcomeKind.Case)} 題" +
            (hasModel
                ? $"；模型在案件層 {named.Count(result => result.Model!.CaseLayer.Kind == CaseProposalEvalOutcomeKind.Case)} 題、整條提議階段 {named.Count(result => result.Model!.Stage.Kind == CaseProposalEvalOutcomeKind.Case)} 題。"
                : "。"));
        if (named.Count > 0)
        {
            text.AppendLine().AppendLine($"題目：{string.Join("、", named.Select(result => result.Question.Id))}。");
        }

        if (hasModel)
        {
            AppendUsage(text, run.Results);
        }

        text.AppendLine().AppendLine("## 逐題結果").AppendLine();
        text.AppendLine(hasModel
            ? "| id | 類別 | 標記 | 關鍵字：案件層 | 關鍵字：提議階段 | 模型：案件層 | 模型：提議階段 | tokens 合成（入／出） | tokens 案件（入／出） | 問題 |"
            : "| id | 類別 | 標記 | 關鍵字：案件層 | 關鍵字：提議階段 | 問題 |");
        text.AppendLine(hasModel ? "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |" : "| --- | --- | --- | --- | --- | --- |");
        foreach (var result in run.Results)
        {
            var question = result.Question;
            var row = new StringBuilder($"| {question.Id} | {question.Category} | {question.Expected} " +
                $"| {Mark(question, result.Keyword.CaseLayer, false)} | {Mark(question, result.Keyword.Stage, true)} |");
            if (result.Model is { } model)
            {
                var caseRejected = model.CaseCallRejected ? "（案件工具參數不合法）" : string.Empty;
                var selectionRejected = model.SelectionCallRejected ? "（合成呼叫的工具或參數不合法）" : string.Empty;
                row.Append($" {Mark(question, model.CaseLayer, false)}{caseRejected} | {Mark(question, model.Stage, true)}{selectionRejected} " +
                    $"| {Tokens(result.Usage?.SelectionInput, result.Usage?.SelectionOutput)} | {Tokens(result.Usage?.CaseInput, result.Usage?.CaseOutput)} |");
            }

            row.Append($" {Cell(question.Question)} |");
            text.AppendLine(row.ToString());
        }

        if (hasModel)
        {
            var drafted = run.Results.Where(result => result.Model!.CaseLayer.Kind == CaseProposalEvalOutcomeKind.Case).ToList();
            text.AppendLine().AppendLine("## 模型草擬的案件標題（案件層）").AppendLine();
            if (drafted.Count == 0)
            {
                text.AppendLine("（沒有提議）");
            }
            else
            {
                text.AppendLine("| id | 類型 | 標題草稿 |");
                text.AppendLine("| --- | --- | --- |");
                foreach (var result in drafted)
                {
                    text.AppendLine($"| {result.Question.Id} | {result.Model!.CaseLayer.TypeKey} | {Cell(result.Model.CaseLayer.Title ?? string.Empty)} |");
                }
            }
        }

        return text.ToString();
    }

    private static void AppendSummaryTable(StringBuilder text, IEnumerable<CaseProposalEvalSummary?> summaries)
    {
        text.AppendLine("| 觸發方式 | 漏觸 | 誤觸（合計） | 誤觸：不應提議 | 誤觸：應給表單 | 誤觸：應走查詢 | 選錯類型 | 表單題給表單 | 查詢題走查詢 | 模糊題與標記一致 | 模型呼叫失敗 |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var summary in summaries.OfType<CaseProposalEvalSummary>())
        {
            text.AppendLine(
                $"| {summary.Trigger} | {Ratio(summary.Missed, summary.CaseQuestions, summary.MissedRate)} " +
                $"| {Ratio(summary.FalseTriggers, summary.Negatives, summary.FalseTriggerRate)} " +
                $"| {summary.NoneFalse}/{summary.NoneQuestions} | {summary.FormFalse}/{summary.FormQuestions} | {summary.QueryFalse}/{summary.QueryQuestions} " +
                $"| {Ratio(summary.WrongType, summary.Proposed, summary.WrongTypeRate)} " +
                $"| {(summary.Stage ? $"{summary.FormRouted}/{summary.FormQuestions}" : "—")} | {(summary.Stage ? $"{summary.QueryRouted}/{summary.QueryQuestions}" : "—")} " +
                $"| {Ratio(summary.AmbiguousAgreed, summary.Ambiguous, summary.AmbiguousAgreement)} | {summary.Errors} |");
        }
    }

    private static void AppendUsage(StringBuilder text, IReadOnlyList<CaseProposalEvalResult> results)
    {
        static (long Total, int Reported) Sum(IEnumerable<long?> values)
        {
            var reported = values.Where(value => value is not null).Select(value => value!.Value).ToList();
            return (reported.Sum(), reported.Count);
        }

        var usages = results.Select(result => result.Usage).OfType<CaseProposalEvalUsage>().ToList();
        var selectionIn = Sum(usages.Select(usage => usage.SelectionInput));
        var selectionOut = Sum(usages.Select(usage => usage.SelectionOutput));
        var caseIn = Sum(usages.Select(usage => usage.CaseInput));
        var caseOut = Sum(usages.Select(usage => usage.CaseOutput));
        text.AppendLine().AppendLine("## Token 用量").AppendLine();
        text.AppendLine("| 呼叫 | 次數（有回報用量） | 輸入合計 | 輸出合計 | 每次平均（入／出） |");
        text.AppendLine("| --- | --- | --- | --- | --- |");
        text.AppendLine($"| 合成選擇（request_database_form＋propose_case） | {selectionIn.Reported} | {selectionIn.Total} | {selectionOut.Total} | {Average(selectionIn)}／{Average(selectionOut)} |");
        text.AppendLine($"| 案件選擇（只有 propose_case，案件層） | {caseIn.Reported} | {caseIn.Total} | {caseOut.Total} | {Average(caseIn)}／{Average(caseOut)} |");
        text.AppendLine($"| 合計 | {selectionIn.Reported + caseIn.Reported} | {selectionIn.Total + caseIn.Total} | {selectionOut.Total + caseOut.Total} | — |");
        text.AppendLine().AppendLine("正式環境中，同時有表單與可提議類型的助理每題只做一次合成選擇（統計問題連這次也不做）；案件選擇是為了單獨評測案件層才另外呼叫的。");
    }

    private static string Average((long Total, int Reported) value) =>
        value.Reported == 0 ? "—" : ((double)value.Total / value.Reported).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Tokens(long? input, long? output) =>
        input is null && output is null ? "—" : $"{input?.ToString(CultureInfo.InvariantCulture) ?? "—"}／{output?.ToString(CultureInfo.InvariantCulture) ?? "—"}";

    private static string Mark(CaseProposalEvalQuestion question, CaseProposalEvalOutcome outcome, bool stage) =>
        outcome.Kind == CaseProposalEvalOutcomeKind.Error
            ? "錯誤"
            : $"{outcome.Label} {(CaseProposalEvalScoring.Agrees(question, outcome, stage) ? "✓" : "✗")}";

    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static string Ratio(int count, int total, double? rate) =>
        $"{count}/{total}（{(rate is { } value ? (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—")}）";
}
