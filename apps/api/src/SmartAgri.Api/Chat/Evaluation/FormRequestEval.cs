using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartAgri.Api.Chat.Evaluation;

/// <summary>One labelled question of the form-request trigger set (M4 #164).</summary>
/// <param name="Expected"><c>form</c> or <c>none</c>: whether the question should get the form.</param>
/// <param name="Category"><c>positive</c> (expected <c>form</c>), <c>negative</c> (expected <c>none</c>)
/// or <c>ambiguous</c> (either, the labeller's best judgement; reported on its own).</param>
public sealed record FormRequestEvalQuestion(string Id, string Question, string Expected, string Category, string? Note)
{
    public bool ExpectsForm => Expected == FormRequestEvalSet.Form;
}

/// <summary>The sample form the set is judged against: what the model is offered.</summary>
public sealed record FormRequestEvalForm(string Title, string Purpose);

public sealed class FormRequestEvalSetException(string directory, IReadOnlyList<string> problems)
    : Exception($"表單觸發評測題庫「{directory}」無法使用：\n- " + string.Join("\n- ", problems));

/// <summary>
/// The labelled question set of <c>eval-form-requests</c> (M4 #164): <c>questions.json</c> in
/// <c>apps/api/eval/form-requests/</c>, copied next to the Api assembly (<see cref="DefaultDirectory"/>);
/// its README describes the format.
/// </summary>
public sealed class FormRequestEvalSet
{
    public const string Form = "form";
    public const string None = "none";
    public const string Positive = "positive";
    public const string Negative = "negative";
    public const string Ambiguous = "ambiguous";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private FormRequestEvalSet(string directory, FormRequestEvalForm form, IReadOnlyList<FormRequestEvalQuestion> questions, string fingerprint)
    {
        Directory = directory;
        SampleForm = form;
        Questions = questions;
        Fingerprint = fingerprint;
    }

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "eval", "form-requests");

    public string Directory { get; }

    public FormRequestEvalForm SampleForm { get; }

    public IReadOnlyList<FormRequestEvalQuestion> Questions { get; }

    /// <summary>The first 12 hex digits of <c>questions.json</c>'s SHA-256: which set a report judged.</summary>
    public string Fingerprint { get; }

    public static FormRequestEvalSet Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        var path = Path.Combine(root, "questions.json");
        if (!File.Exists(path))
        {
            throw new FormRequestEvalSetException(root, ["找不到 questions.json。"]);
        }

        var bytes = File.ReadAllBytes(path);
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormRequestEvalSetException(root, [$"questions.json 不是正確的 JSON：{exception.Message}"]);
        }

        var problems = new List<string>();
        if (document?.Form is not { } form || string.IsNullOrWhiteSpace(form.Title) || string.IsNullOrWhiteSpace(form.Purpose))
        {
            problems.Add("form 需要 title 與 purpose。");
        }

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

            if (question.Expected is not (Form or None))
            {
                problems.Add($"{label}：expected 必須是 form 或 none。");
            }

            var consistent = question.Category switch
            {
                Positive => question.Expected == Form,
                Negative => question.Expected == None,
                Ambiguous => true,
                _ => false,
            };
            if (!consistent)
            {
                problems.Add($"{label}：category 必須是 positive（expected form）、negative（expected none）或 ambiguous。");
            }
        }

        foreach (var duplicate in questions.GroupBy(question => question.Id).Where(group => group.Count() > 1))
        {
            problems.Add($"題目 id 重複：{duplicate.Key}");
        }

        if (problems.Count > 0)
        {
            throw new FormRequestEvalSetException(root, problems);
        }

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
        return new FormRequestEvalSet(root, document!.Form!, questions, fingerprint);
    }

    private sealed record Document(FormRequestEvalForm? Form, List<FormRequestEvalQuestion>? Questions);
}

/// <summary>What one trigger decided for one question.</summary>
public enum FormRequestEvalDecision
{
    /// <summary>The form would be shown.</summary>
    Form,

    /// <summary>No form (including a model call naming another tool or an unoffered id).</summary>
    None,

    /// <summary>The model call failed: not judged (production would fall back to the keywords).</summary>
    Error,

    /// <summary>The combined selection (#286) proposed a case instead: no form.</summary>
    Case,
}

/// <summary>One question's decisions: the keyword gate's, and the model's when it ran.</summary>
public sealed record FormRequestEvalResult(
    FormRequestEvalQuestion Question,
    FormRequestEvalDecision Keyword,
    FormRequestEvalDecision? Model,
    bool ModelCallRejected,
    FormRequestEvalDecision? Combined = null,
    string? CombinedCaseKey = null,
    bool CombinedCallRejected = false,
    bool CombinedNoMatch = false);

/// <summary>The rates of one trigger. Rates are over judged questions (errors excluded); a category
/// with no judged question has a <see langword="null"/> rate.</summary>
public sealed record FormRequestEvalSummary(
    string Trigger,
    int Positives,
    int Missed,
    int Negatives,
    int FalseTriggers,
    int Ambiguous,
    int AmbiguousAgreed,
    int Errors)
{
    public double? MissedRate => Positives == 0 ? null : (double)Missed / Positives;

    public double? FalseTriggerRate => Negatives == 0 ? null : (double)FalseTriggers / Negatives;

    public double? AmbiguousAgreement => Ambiguous == 0 ? null : (double)AmbiguousAgreed / Ambiguous;

    /// <summary>Over positives and negatives only.</summary>
    public double? Accuracy => Positives + Negatives == 0 ? null : (double)(Positives - Missed + Negatives - FalseTriggers) / (Positives + Negatives);
}

/// <summary>Judging and summarizing (pure).</summary>
public static class FormRequestEvalScoring
{
    public static FormRequestEvalSummary Summarize(string trigger, IEnumerable<(FormRequestEvalQuestion Question, FormRequestEvalDecision Decision)> decisions)
    {
        int positives = 0, missed = 0, negatives = 0, falseTriggers = 0, ambiguous = 0, agreed = 0, errors = 0;
        foreach (var (question, decision) in decisions)
        {
            if (decision == FormRequestEvalDecision.Error)
            {
                errors++;
                continue;
            }

            var gaveForm = decision == FormRequestEvalDecision.Form;
            switch (question.Category)
            {
                case FormRequestEvalSet.Positive:
                    positives++;
                    missed += gaveForm ? 0 : 1;
                    break;
                case FormRequestEvalSet.Negative:
                    negatives++;
                    falseTriggers += gaveForm ? 1 : 0;
                    break;
                default:
                    ambiguous++;
                    agreed += gaveForm == question.ExpectsForm ? 1 : 0;
                    break;
            }
        }

        return new FormRequestEvalSummary(trigger, positives, missed, negatives, falseTriggers, ambiguous, agreed, errors);
    }
}

/// <summary>A finished run, as the report shows it.</summary>
public sealed record FormRequestEvalRun(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string SetDisplayName,
    string SetFingerprint,
    FormRequestEvalForm SampleForm,
    string? ChatProvider,
    string? ChatModel,
    IReadOnlyList<FormRequestEvalResult> Results,
    FormRequestEvalSummary Keyword,
    FormRequestEvalSummary? Model,
    double? AverageInputTokens,
    double? AverageOutputTokens,
    FormRequestEvalSummary? Combined = null,
    double? CombinedAverageInputTokens = null,
    double? CombinedAverageOutputTokens = null,
    IReadOnlyList<CaseProposalEvalType>? CaseTypes = null);

/// <summary>The Markdown report of <c>eval-form-requests</c>, in Traditional Chinese like the rest of <c>docs/</c>.</summary>
public static class FormRequestEvalReport
{
    public static string FileName(DateTimeOffset date, string? model)
    {
        var slug = new StringBuilder();
        foreach (var character in (model ?? "keyword").Trim().ToLowerInvariant())
        {
            slug.Append(character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' ? character : '-');
        }

        return $"{date:yyyy-MM-dd}-form-requests-{slug.ToString().Trim('-')}.md";
    }

    public static string Render(FormRequestEvalRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var text = new StringBuilder();
        text.AppendLine("# 表單請求觸發評測（#164）").AppendLine();
        text.AppendLine($"- 執行時間：{run.StartedAt:yyyy-MM-dd HH:mm:ss zzz}（{run.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} 秒）");
        text.AppendLine($"- 題庫：`{run.SetDisplayName}`（指紋 `{run.SetFingerprint}`，{run.Results.Count} 題）");
        text.AppendLine($"- 範例表單：「{run.SampleForm.Title}」，收集目的：{run.SampleForm.Purpose}");
        text.AppendLine(run.Model is null
            ? "- 對話模型：未執行（只評測關鍵字門檻）"
            : $"- 對話模型：{run.ChatModel}（{run.ChatProvider}）");
        if (run.ChatProvider == "fake")
        {
            text.AppendLine("- **注意：對話模型是 `fake`，它在沒有指示詞時照關鍵字門檻決定，模型欄的數字只證明流程可以執行，不代表真實模型的表現。**");
        }

        if (run.CaseTypes is { } caseTypes)
        {
            text.AppendLine($"- 合成呼叫（#286）：表單工具之外，同時提供這些可提議的案件類型（正式環境中助理同時有表單與可提議類型時的路徑），以及明確的「都不符合」工具 `{SmartAgri.Application.Cases.CaseProposalRules.NoMatchToolName}`（#297）：");
            foreach (var type in caseTypes)
            {
                text.AppendLine($"  - `{type.Key}`＝「{type.Name}」：{type.Description}");
            }
        }

        text.AppendLine().AppendLine("## 摘要").AppendLine();
        text.AppendLine("| 觸發方式 | 漏觸（應給表單卻沒給） | 誤觸（不應給卻給了） | 正反題正確率 | 模糊題與標記一致 | 模型呼叫失敗 |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var summary in new[] { run.Keyword, run.Model, run.Combined }.OfType<FormRequestEvalSummary>())
        {
            text.AppendLine(
                $"| {summary.Trigger} | {summary.Missed}/{summary.Positives}（{Percent(summary.MissedRate)}） " +
                $"| {summary.FalseTriggers}/{summary.Negatives}（{Percent(summary.FalseTriggerRate)}） " +
                $"| {Percent(summary.Accuracy)} | {summary.AmbiguousAgreed}/{summary.Ambiguous}（{Percent(summary.AmbiguousAgreement)}） | {summary.Errors} |");
        }

        if (run.AverageInputTokens is { } input && run.AverageOutputTokens is { } output)
        {
            text.AppendLine().AppendLine(
                $"模型每題平均用量：輸入 {input.ToString("0.0", CultureInfo.InvariantCulture)}、輸出 {output.ToString("0.0", CultureInfo.InvariantCulture)} tokens。");
        }

        if (run.Combined is not null)
        {
            var cases = run.Results.Where(result => result.Combined == FormRequestEvalDecision.Case).ToList();
            text.AppendLine().AppendLine(
                $"合成呼叫改提議案件（沒有給表單）的有 {cases.Count} 題：正題 {cases.Count(result => result.Question.Category == FormRequestEvalSet.Positive)}、" +
                $"反題 {cases.Count(result => result.Question.Category == FormRequestEvalSet.Negative)}、模糊題 {cases.Count(result => result.Question.Category == FormRequestEvalSet.Ambiguous)}" +
                (cases.Count > 0 ? $"（{string.Join("、", cases.Select(result => $"{result.Question.Id}→{result.CombinedCaseKey}"))}）。" : "。"));
            var noMatch = run.Results.Where(result => result.CombinedNoMatch).ToList();
            text.AppendLine().AppendLine(
                $"合成呼叫明確選「都不符合」的有 {noMatch.Count} 題：正題 {noMatch.Count(result => result.Question.Category == FormRequestEvalSet.Positive)}、" +
                $"反題 {noMatch.Count(result => result.Question.Category == FormRequestEvalSet.Negative)}、模糊題 {noMatch.Count(result => result.Question.Category == FormRequestEvalSet.Ambiguous)}。");
            if (run.CombinedAverageInputTokens is { } combinedInput && run.CombinedAverageOutputTokens is { } combinedOutput)
            {
                text.AppendLine().AppendLine(
                    $"合成呼叫每題平均用量：輸入 {combinedInput.ToString("0.0", CultureInfo.InvariantCulture)}、輸出 {combinedOutput.ToString("0.0", CultureInfo.InvariantCulture)} tokens。");
            }
        }

        text.AppendLine().AppendLine("## 逐題結果").AppendLine();
        text.AppendLine(run.Model is null ? "| id | 類別 | 標記 | 關鍵字 | 問題 |"
            : run.Combined is null ? "| id | 類別 | 標記 | 關鍵字 | 模型 | 問題 |"
            : "| id | 類別 | 標記 | 關鍵字 | 模型 | 合成呼叫 | 問題 |");
        text.AppendLine(run.Model is null ? "| --- | --- | --- | --- | --- |"
            : run.Combined is null ? "| --- | --- | --- | --- | --- | --- |"
            : "| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var result in run.Results)
        {
            var question = result.Question;
            var model = result.Model is { } decision
                ? $" {Mark(question, decision)}{(result.ModelCallRejected ? "（工具參數不合法）" : string.Empty)} |"
                : string.Empty;
            var combined = result.Combined is { } combinedDecision
                ? $" {Mark(question, combinedDecision, result.CombinedCaseKey)}{(result.CombinedCallRejected ? "（工具參數不合法）" : result.CombinedNoMatch ? "（都不符合）" : string.Empty)} |"
                : string.Empty;
            text.AppendLine(
                $"| {question.Id} | {question.Category} | {question.Expected} | {Mark(question, result.Keyword)} |{model}{combined} {question.Question.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        return text.ToString();
    }

    private static string Mark(FormRequestEvalQuestion question, FormRequestEvalDecision decision, string? caseKey = null) => decision switch
    {
        FormRequestEvalDecision.Error => "錯誤",
        // A case instead of the form: a miss for a form question; flagged (not a form) for any other.
        FormRequestEvalDecision.Case => question.ExpectsForm ? $"case:{caseKey} ✗" : $"case:{caseKey} ⚠",
        FormRequestEvalDecision.Form => question.ExpectsForm ? "form ✓" : "form ✗",
        _ => question.ExpectsForm ? "none ✗" : "none ✓",
    };

    private static string Percent(double? rate) =>
        rate is { } value ? (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
}
