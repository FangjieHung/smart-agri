using System.Globalization;
using System.Text;

namespace SmartAgri.Api.Knowledge.Evaluation;

/// <summary>Everything a report says about one evaluation run.</summary>
/// <param name="StartedAt">In the machine's local time zone.</param>
/// <param name="Provider">The provider's stored name: <c>openai</c>, <c>openai-compatible</c>, <c>fake</c>, …</param>
/// <param name="SetName">How the set is shown: its path relative to the repository, when it is in it.</param>
public sealed record RetrievalEvalRun(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string Provider,
    string Model,
    string? Endpoint,
    string QueryPrefix,
    string DocumentPrefix,
    string SetName,
    string SetFingerprint,
    int KnowledgeBaseCount,
    int DocumentCount,
    int VersionCount,
    int ChunkCount,
    IReadOnlyList<EvalQuestionResult> Results,
    RetrievalEvalSummary Summary);

/// <summary>
/// The Markdown report of an <c>eval-retrieval</c> run (M2 plan Slice 16), in Traditional Chinese
/// like the rest of <c>docs/</c>: the run's settings, the summary (hit@5, the highest
/// should-find-nothing score, the lowest hit score, the suggested threshold), each category, the
/// threshold analysis, every question, and the top passages of each miss and of each
/// should-find-nothing question.
/// </summary>
public static class RetrievalEvalReport
{
    /// <summary>The report's second-level headings, in order (the tests check they are all there).</summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        "## 執行設定",
        "## 結果摘要",
        "## 各類別",
        "## 門檻分析",
        "## 逐題結果",
        "## 未命中與應查無結果的題目",
    ];

    /// <summary>The report's file name: <c>&lt;date&gt;-retrieval-&lt;model&gt;.md</c>, the model
    /// reduced to lower-case letters, digits, dots and dashes.</summary>
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
        return string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd}-retrieval-{(name.Length > 0 ? name : "model")}.md");
    }

    public static string Render(RetrievalEvalRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var summary = run.Summary;
        var text = new StringBuilder();

        text.AppendLine(CultureInfo.InvariantCulture, $"# 檢索評測：{run.Model}（{run.StartedAt:yyyy-MM-dd}）");
        text.AppendLine();
        text.AppendLine("> 由 `eval-retrieval` 產生（apps/api/README.md「Evaluating retrieval」；M2 計畫 Slice 16，#50）。");
        text.AppendLine("> 每題以 `KnowledgeRetriever` 在題庫的知識庫中檢索前 5 名，不含待確認版本；題庫與判定方式見 `apps/api/eval/retrieval/README.md`。");
        if (run.Provider == "fake")
        {
            text.AppendLine(">");
            text.AppendLine("> **這是 `Fake` 嵌入模型的結果：向量只是文字的雜湊，分數沒有語意，只證明評測流程能完整執行。不可用來判斷檢索品質或校正 `Retrieval:MinScore`。**");
        }

        text.AppendLine();
        text.AppendLine(Sections[0]);
        text.AppendLine();
        text.AppendLine("| 項目 | 值 |");
        text.AppendLine("| --- | --- |");
        Row(text, "嵌入提供者", $"`{run.Provider}`");
        Row(text, "模型", $"`{run.Model}`");
        if (run.Endpoint is not null)
        {
            Row(text, "端點", $"`{run.Endpoint}`");
        }

        Row(text, "問題前綴／段落前綴", $"{Quoted(run.QueryPrefix)}／{Quoted(run.DocumentPrefix)}");
        Row(text, "每題取回段落數", RetrievalEvalScoring.Top.ToString(CultureInfo.InvariantCulture));
        Row(text, "目前的 `Retrieval:MinScore`", Score(summary.AtCurrent.Value));
        Row(text, "題庫", $"`{run.SetName}`（{run.Results.Count} 題，其中 {summary.Unanswerable} 題應查無結果）");
        Row(text, "題庫指紋（SHA-256 前 12 碼）", $"`{run.SetFingerprint[..12]}`");
        Row(text, "資料", $"{run.KnowledgeBaseCount} 個知識庫、{run.DocumentCount} 份文件、{run.VersionCount} 個版本、{run.ChunkCount} 個可檢索段落");
        Row(text, "執行時間", string.Create(CultureInfo.InvariantCulture, $"{run.StartedAt:yyyy-MM-dd HH:mm:ss zzz}，耗時 {run.Duration.TotalSeconds:0.0} 秒"));
        text.AppendLine();

        text.AppendLine(Sections[1]);
        text.AppendLine();
        text.AppendLine("| 指標 | 值 |");
        text.AppendLine("| --- | --- |");
        Row(text, "前 5 名命中率（hit@5）", Rate(summary.Hits5, summary.Answerable));
        Row(text, "第 1 名命中率（hit@1）", Rate(summary.Hits1, summary.Answerable));
        Row(text, "「應查無結果」題的最高分", summary.MaxUnanswerable is { } highest ? $"{Score(highest.Score)}（{highest.QuestionId}）" : "—（都沒有取回段落）");
        Row(text, "正確命中的最低分", summary.MinHit is { } lowest ? $"{Score(lowest.Score)}（{lowest.QuestionId}）" : "—（沒有命中）");
        Row(text, "兩者可以用門檻分開", summary.Separable ? "是" : "否");
        Row(text, "建議門檻", summary.Suggested is { } suggested ? $"**{Score(suggested.Value)}**（正確判斷 {suggested.Correct}/{suggested.Total} 題）" : "—（沒有分數可依據）");
        Row(text, "目前門檻的正確判斷", $"{summary.AtCurrent.Correct}/{summary.AtCurrent.Total} 題");
        Row(text, "取回非有效版本的段落", summary.NonEffectivePassages.ToString(CultureInfo.InvariantCulture));
        Row(text, "取回預期文件其他版本的題目", summary.QuestionsWithOtherVersions.ToString(CultureInfo.InvariantCulture));
        Row(text, "hit@5 ≥ 90%（#50 的驗收標準）", summary.MeetsTarget ? "達成" : "未達成");
        text.AppendLine();

        text.AppendLine(Sections[2]);
        text.AppendLine();
        text.AppendLine("| 類別 | 題數 | hit@5 | hit@1 | 第 1 名的最高分 |");
        text.AppendLine("| --- | ---: | --- | --- | ---: |");
        foreach (var category in summary.Categories)
        {
            var name = RetrievalEvalSet.Categories.TryGetValue(category.Category, out var display) ? $"{display}（`{category.Category}`）" : category.Category;
            var unanswerable = category.Category == RetrievalEvalSet.Unanswerable;
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {name} | {category.Questions} | {(unanswerable ? "—" : Rate(category.Hits5, category.Questions))} | {(unanswerable ? "—" : Rate(category.Hits1, category.Questions))} | {(category.MaxTopScore is { } top ? Score(top) : "—")} |"));
        }

        text.AppendLine();
        text.AppendLine(Sections[3]);
        text.AppendLine();
        text.AppendLine("有答案的題目，要在前 5 名取回預期段落、而且該段落的分數達到門檻，才算判斷正確；應查無結果的題目，第 1 名的分數低於門檻才算正確（助理會直接回「查無結果」，不呼叫模型）。");
        text.AppendLine("相鄰兩個分數之間的門檻判斷結果都一樣，所以逐一比較每個區間，取正確題數最多的區間（同分取最寬的），建議值是區間的中點；兩組分數分得開時，就是「正確命中的最低分」與「應查無結果的最高分」的中點。");
        text.AppendLine();
        if (summary.Suggested is { } best)
        {
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- 建議門檻 {Score(best.Value)}：取自 {Score(best.Low)}–{Score(best.High)} 的區間，正確判斷 {best.Correct}/{best.Total} 題。"));
        }

        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- 目前的 `Retrieval:MinScore` {Score(summary.AtCurrent.Value)}：正確判斷 {summary.AtCurrent.Correct}/{summary.AtCurrent.Total} 題。"));
        text.AppendLine("- 校正時把選定的值同時寫進 `appsettings.json` 的 `Retrieval:MinScore` 與 `KnowledgeRetrievalSettings.DefaultMinScore`（`RetrievalOptionsTests` 會檢查兩者一致），並在 PR 說明引用這份報告。");
        text.AppendLine();

        text.AppendLine(Sections[4]);
        text.AppendLine();
        text.AppendLine("| 題號 | 類別 | 問題 | 預期 | 命中名次 | 命中分數 | 第 1 名 | 第 1 名分數 |");
        text.AppendLine("| --- | --- | --- | --- | ---: | ---: | --- | ---: |");
        foreach (var result in run.Results)
        {
            var expected = result.Question.ExpectsNothing
                ? "應查無結果"
                : string.Join("；", result.Question.Expected.Select(passage => $"{passage.Document} 第 {passage.Version} 版 {passage.Location}"));
            var first = result.Passages.Count > 0 ? Describe(result.Passages[0]) : "（無）";
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {result.Question.Id} | {result.Question.Category} | {Cell(result.Question.Question)} | {Cell(expected)} | {(result.HitRank is { } rank ? rank.ToString(CultureInfo.InvariantCulture) : result.Question.ExpectsNothing ? "—" : "未命中")} | {(result.HitScore is { } hit ? Score(hit) : "—")} | {Cell(first)} | {(result.TopScore is { } top ? Score(top) : "—")} |"));
        }

        text.AppendLine();
        text.AppendLine(Sections[5]);
        text.AppendLine();
        var details = run.Results.Where(result => !result.Hit).ToList();
        if (details.Count == 0)
        {
            text.AppendLine("（每題都命中，而且沒有應查無結果的題目。）");
        }

        foreach (var result in details)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### {result.Question.Id}：{result.Question.Question}");
            text.AppendLine();
            text.AppendLine(result.Question.ExpectsNothing ? "應查無結果。取回的段落：" : "預期段落不在前 5 名。取回的段落：");
            text.AppendLine();
            if (result.Passages.Count == 0)
            {
                text.AppendLine("（沒有取回任何段落。）");
            }

            for (var index = 0; index < result.Passages.Count; index++)
            {
                var passage = result.Passages[index];
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{index + 1}. {Score(passage.Score)} {Describe(passage)}：{Excerpt(passage.Text)}"));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static void Row(StringBuilder text, string name, string value) => text.AppendLine($"| {name} | {value} |");

    private static string Describe(EvalPassage passage) =>
        string.Create(CultureInfo.InvariantCulture, $"{passage.DocumentName} 第 {passage.VersionNumber} 版 {passage.LocationLabel}");

    /// <summary>Three decimals; a score that rounds to zero is 0.000, never -0.000.</summary>
    internal static string Score(double score) =>
        (Math.Abs(score) < 0.0005 ? 0 : score).ToString("0.000", CultureInfo.InvariantCulture);

    private static string Rate(int hits, int total) =>
        total == 0
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{hits}/{total} = {100.0 * hits / total:0.0}%");

    private static string Quoted(string prefix) => prefix.Length == 0 ? "（無）" : $"`{prefix}`";

    /// <summary>A table cell: no line breaks, and <c>|</c> escaped.</summary>
    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>The first 80 characters of a passage on one line, pipes escaped.</summary>
    private static string Excerpt(string text)
    {
        var line = Cell(text);
        var runes = line.EnumerateRunes().ToList();
        return runes.Count <= 80 ? line : string.Concat(runes.Take(80).Select(rune => rune.ToString())) + "…";
    }
}
