using System.Globalization;
using System.Text;
using SmartAgri.Application.Answers;

namespace SmartAgri.Api.Answers.Evaluation;

/// <summary>Everything a report says about one <c>eval-answers</c> run.</summary>
/// <param name="StartedAt">In the machine's local time zone.</param>
/// <param name="EmbeddingProvider">The retrieval embedding's stored provider name.</param>
/// <param name="ChatProvider">The chat model's stored provider name (<c>openai</c>, <c>fake</c>, …).</param>
/// <param name="SetName">How the set is shown: its path relative to the repository, when it is in it.</param>
/// <param name="PromptVersion">The answer prompt's <see cref="GroundedAnswerPrompt.Version"/>, so
/// runs before and after a wording change can be told apart.</param>
public sealed record AnswerEvalRun(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string EmbeddingProvider,
    string EmbeddingModel,
    string ChatProvider,
    string ChatModel,
    double MinScore,
    string PromptVersion,
    string SetName,
    string SetFingerprint,
    int KnowledgeBaseCount,
    int DocumentCount,
    IReadOnlyList<AnswerEvalQuestionResult> Results,
    AnswerEvalSummary Summary);

/// <summary>
/// The Markdown report of an <c>eval-answers</c> run (M3 plan Slice 13; ticket #83), in
/// Traditional Chinese like the rest of <c>docs/</c>: the run's settings, the summary (reply-kind
/// accuracy, citation hit rate, average token usage), the rejection reason distribution, and every
/// question with its expected and actual reply, the closest passage's score and the reply text
/// (#303: whether a conclusion is negative, or a refusal came from the threshold or from the model,
/// is judged from these).
/// </summary>
public static class AnswerEvalReport
{
    /// <summary>The report's second-level headings, in order (the tests check they are all there).</summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        "## 執行設定",
        "## 結果摘要",
        "## 拒絕原因分布",
        "## 逐題結果",
    ];

    /// <summary>The report's file name: <c>&lt;date&gt;-answers-&lt;chat model&gt;.md</c>, the
    /// model reduced to lower-case letters, digits, dots and dashes.</summary>
    public static string FileName(DateTimeOffset date, string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var slug = new StringBuilder(model.Length);
        foreach (var character in model.Trim().ToLowerInvariant())
        {
            var keep = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-';
            if (keep)
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var name = slug.ToString().Trim('-', '.');
        return string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd}-answers-{(name.Length > 0 ? name : "model")}.md");
    }

    public static string Render(AnswerEvalRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var summary = run.Summary;
        var text = new StringBuilder();

        text.AppendLine(CultureInfo.InvariantCulture, $"# 回答評測：{run.ChatModel}（{run.StartedAt:yyyy-MM-dd}）");
        text.AppendLine();
        text.AppendLine("> 由 `eval-answers` 產生（apps/api/README.md「Evaluating answers」；M3 計畫 Slice 13，#83）。");
        text.AppendLine("> 每題以 `GroundedAnswerService.AnswerAsync`（`company-data-only`）回答，題庫與判定方式見 `apps/api/eval/answers/README.md`。");
        if (run.ChatProvider == "fake" || run.EmbeddingProvider == "fake")
        {
            text.AppendLine(">");
            text.AppendLine(
                "> **這是 `Fake` 模型的結果：嵌入向量只是文字的雜湊、對話模型是照樣板回答，分數與引用命中沒有語意，只證明評測流程能完整、可重現地執行。" +
                "真實模型的評測與 `Retrieval:MinScore` 校正待補做（需要負責人提供 OpenAI 金鑰）。**");
        }

        text.AppendLine();
        text.AppendLine(Sections[0]);
        text.AppendLine();
        text.AppendLine("| 項目 | 值 |");
        text.AppendLine("| --- | --- |");
        Row(text, "嵌入提供者／模型", $"`{run.EmbeddingProvider}` / `{run.EmbeddingModel}`");
        Row(text, "對話提供者／模型", $"`{run.ChatProvider}` / `{run.ChatModel}`");
        Row(text, "Retrieval:MinScore", Score(run.MinScore));
        Row(text, "回答提示版本", $"`{run.PromptVersion}`");
        Row(text, "題庫", $"`{run.SetName}`（{run.Results.Count} 題）");
        Row(text, "題庫指紋（SHA-256 前 12 碼）", $"`{run.SetFingerprint[..12]}`");
        Row(text, "資料", $"{run.KnowledgeBaseCount} 個知識庫、{run.DocumentCount} 份文件");
        Row(text, "執行時間", string.Create(CultureInfo.InvariantCulture, $"{run.StartedAt:yyyy-MM-dd HH:mm:ss zzz}，耗時 {run.Duration.TotalSeconds:0.0} 秒"));
        text.AppendLine();

        text.AppendLine(Sections[1]);
        text.AppendLine();
        text.AppendLine("| 指標 | 值 |");
        text.AppendLine("| --- | --- |");
        Row(text, "回覆類型正確率", Rate(summary.ReplyKindCorrect, summary.Total));
        Row(text, "引用命中率（company-data 題）", summary.CitationHitRate is { } rate
            ? Rate(summary.CitationHits, summary.CompanyDataQuestions)
            : "—（題庫沒有 company-data 題）");
        Row(text, "平均輸入 token", summary.AverageInputTokens is { } input ? input.ToString("0.0", CultureInfo.InvariantCulture) : "—");
        Row(text, "平均輸出 token", summary.AverageOutputTokens is { } output ? output.ToString("0.0", CultureInfo.InvariantCulture) : "—");
        text.AppendLine();

        text.AppendLine(Sections[2]);
        text.AppendLine();
        if (summary.RejectionReasons.Count == 0)
        {
            text.AppendLine("（沒有 `no-result` 回覆。）");
        }
        else
        {
            text.AppendLine("| 拒絕原因 | 次數 |");
            text.AppendLine("| --- | ---: |");
            foreach (var (reason, count) in summary.RejectionReasons)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| `{WireName(reason)}` | {count} |"));
            }
        }

        text.AppendLine();

        text.AppendLine(Sections[3]);
        text.AppendLine();
        text.AppendLine("| 題號 | 追問 | 問題 | 預期類型 | 實際類型 | 類型正確 | 預期引用 | 實際引用 | 引用命中 | 拒絕原因 | 最高分 | 回覆內容 |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | ---: | --- |");
        foreach (var result in run.Results)
        {
            var question = result.Question;
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {question.Id} | {question.FollowUpOf ?? "—"} | {Cell(question.Question)} | {WireName(question.ExpectedKind)} | " +
                $"{WireName(result.ActualKind)} | {(result.KindCorrect ? "是" : "**否**")} | {Cell(string.Join("、", question.ExpectedCitedDocuments))} | " +
                $"{Cell(string.Join("、", result.CitedDocuments))} | {(result.CitationHit is { } hit ? (hit ? "是" : "**否**") : "—")} | " +
                $"{(result.RejectionReason is { } reason ? $"`{WireName(reason)}`" : "—")} | " +
                $"{(result.TopScore is { } top ? Score(top) : "—")} | {ReplyCell(result)} |"));
        }

        return text.ToString();
    }

    private static void Row(StringBuilder text, string name, string value) => text.AppendLine($"| {name} | {value} |");

    /// <summary>Three decimals; a score that rounds to zero is 0.000, never -0.000.</summary>
    private static string Score(double score) =>
        (Math.Abs(score) < 0.0005 ? 0 : score).ToString("0.000", CultureInfo.InvariantCulture);

    private static string Rate(int hits, int total) =>
        total == 0
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{hits}/{total} = {100.0 * hits / total:0.0}%");

    /// <summary>The reply text, or <c>—</c> for a <c>no-result</c> reply (always the profile's
    /// refusal message, whichever the reason).</summary>
    private static string ReplyCell(AnswerEvalQuestionResult result) =>
        result.ActualKind == GroundedReplyKind.NoResult || string.IsNullOrWhiteSpace(result.ReplyText)
            ? "—"
            : Cell(result.ReplyText.Trim());

    /// <summary>A table cell: no line breaks, and <c>|</c> escaped.</summary>
    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

    private static string WireName(AnswerEvalExpectedKind kind) => kind switch
    {
        AnswerEvalExpectedKind.CompanyData => "company-data",
        AnswerEvalExpectedKind.NoResult => "no-result",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string WireName(GroundedReplyKind kind) => kind switch
    {
        GroundedReplyKind.CompanyData => "company-data",
        GroundedReplyKind.GeneralKnowledge => "general-knowledge",
        GroundedReplyKind.NoResult => "no-result",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string WireName(GroundedRejectionReason reason) => reason switch
    {
        GroundedRejectionReason.BelowThreshold => "below-threshold",
        GroundedRejectionReason.CitationOutOfRange => "citation-out-of-range",
        GroundedRejectionReason.NoCitation => "no-citation",
        GroundedRejectionReason.CannotAnswer => "cannot-answer",
        GroundedRejectionReason.EmptyAnswer => "empty-answer",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
