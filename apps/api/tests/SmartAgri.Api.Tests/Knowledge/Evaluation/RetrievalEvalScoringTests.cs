using Shouldly;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Application.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge.Evaluation;

/// <summary>
/// How <c>eval-retrieval</c> judges passages and sums a run up (<see cref="RetrievalEvalScoring"/>),
/// and what its report contains (<see cref="RetrievalEvalReport"/>), on made-up scores.
/// </summary>
public sealed class RetrievalEvalScoringTests
{
    private static readonly EvalExpectedPassage Page2 = new("退換貨辦法.pdf", 2, "第 2 頁", "七天內");

    [Fact]
    public void A_passage_matches_on_document_version_location_and_phrase_only()
    {
        RetrievalEvalScoring.Matches(Page2, Passage("退換貨辦法.pdf", 2, "第 2 頁", "收到商品後七天內可申請退貨")).ShouldBeTrue();
        RetrievalEvalScoring.Matches(Page2, Passage("退換貨辦法.pdf", 1, "第 2 頁", "收到商品後七天內可申請退貨")).ShouldBeFalse("another version");
        RetrievalEvalScoring.Matches(Page2, Passage("商品使用指南.docx", 2, "第 2 頁", "七天內")).ShouldBeFalse("another document");
        RetrievalEvalScoring.Matches(Page2, Passage("退換貨辦法.pdf", 2, "第 3 頁", "七天內")).ShouldBeFalse("another page");
        RetrievalEvalScoring.Matches(Page2, Passage("退換貨辦法.pdf", 2, "第 2 頁", "十天內")).ShouldBeFalse("without the phrase");

        var sheet = new EvalExpectedPassage("運費與配送時間表.xlsx", 1, "工作表『運費』", null);
        RetrievalEvalScoring.Matches(sheet, Passage("運費與配送時間表.xlsx", 1, "工作表『運費』第 2–5 列", "…")).ShouldBeTrue("the location is contained in the label");
    }

    [Fact]
    public void The_hit_rank_is_the_first_passage_matching_any_expected_one()
    {
        var question = Question("q", "returns", Page2, new EvalExpectedPassage("退換貨辦法.pdf", 2, "第 1 頁", null));

        var result = RetrievalEvalScoring.Judge(question,
        [
            Passage("常見問題.md", 1, "常見問題", "…", 0.9),
            Passage("退換貨辦法.pdf", 2, "第 1 頁", "本版的主要變更", 0.8),
            Passage("退換貨辦法.pdf", 2, "第 2 頁", "七天內", 0.7),
        ]);

        (result.HitRank, result.HitScore, result.TopScore).ShouldBe((2, 0.8, 0.9));
        RetrievalEvalScoring.Judge(question, [Passage("常見問題.md", 1, "常見問題", "…", 0.9)]).Hit.ShouldBeFalse();
        RetrievalEvalScoring.Judge(Question("n", "unanswerable"), [Passage("常見問題.md", 1, "常見問題", "…", 0.4)]).HitRank.ShouldBeNull();
    }

    [Fact]
    public void Separable_scores_suggest_the_midpoint_between_the_lowest_hit_and_the_highest_should_find_nothing_score()
    {
        IReadOnlyList<EvalQuestionResult> results =
        [
            Hit("returns-01", "returns", rank: 1, score: 0.62),
            Hit("returns-02", "returns", rank: 3, score: 0.48),
            Hit("faq-01", "faq", rank: 1, score: 0.71),
            Miss("faq-02", "faq", top: 0.5),
            Nothing("none-01", top: 0.31),
            Nothing("none-02", top: 0.12),
        ];

        var summary = RetrievalEvalScoring.Summarize(results, currentThreshold: 0.3);

        (summary.Answerable, summary.Hits5, summary.Hits1, summary.Unanswerable).ShouldBe((4, 3, 2, 2));
        summary.HitRate5.ShouldBe(0.75);
        summary.MeetsTarget.ShouldBeFalse();
        summary.MaxUnanswerable.ShouldBe(("none-01", 0.31));
        summary.MinHit.ShouldBe(("returns-02", 0.48));
        summary.Separable.ShouldBeTrue();
        var suggested = summary.Suggested.ShouldNotBeNull();
        suggested.Value.ShouldBe((0.31 + 0.48) / 2, 1e-9);
        (suggested.Correct, suggested.Total, suggested.Low, suggested.High).ShouldBe((5, 6, 0.31, 0.48), "every question but the miss");
        (summary.AtCurrent.Value, summary.AtCurrent.Correct).ShouldBe((0.3, 4), "0.3 lets none-01 through");
        summary.Categories.Select(category => (category.Category, category.Questions, category.Hits5, category.Hits1)).ShouldBe(
        [
            ("returns", 2, 2, 1),
            ("faq", 2, 1, 1),
            ("unanswerable", 2, 0, 0),
        ]);
        summary.Categories.Single(category => category.Category == "unanswerable").MaxTopScore.ShouldBe(0.31);
    }

    [Fact]
    public void Overlapping_scores_suggest_the_threshold_judging_most_questions_correctly_widest_gap_first()
    {
        IReadOnlyList<EvalQuestionResult> results =
        [
            Hit("a", "returns", rank: 1, score: 0.80),
            Hit("b", "returns", rank: 1, score: 0.70),
            Hit("c", "returns", rank: 1, score: 0.40),
            Nothing("x", top: 0.50),
            Nothing("y", top: 0.15),
        ];

        var summary = RetrievalEvalScoring.Summarize(results, currentThreshold: 0.9);

        summary.Separable.ShouldBeFalse();
        // Thresholds in (0.15, 0.4] and in (0.5, 0.7] both judge 4 of 5 correctly; the first gap is wider.
        var suggested = summary.Suggested.ShouldNotBeNull();
        suggested.Correct.ShouldBe(4);
        (suggested.Low, suggested.High).ShouldBe((0.15, 0.4));
        suggested.Value.ShouldBe(0.275, 1e-9);
        RetrievalEvalScoring.CorrectAt(results, 0.6).ShouldBe(4);
        RetrievalEvalScoring.CorrectAt(results, 0.45).ShouldBe(3);
        summary.AtCurrent.Correct.ShouldBe(2, "0.9 rejects everything");

        IReadOnlyList<EvalQuestionResult> wider =
        [
            Hit("a", "returns", rank: 1, score: 0.95),
            Hit("c", "returns", rank: 1, score: 0.40),
            Nothing("x", top: 0.50),
            Nothing("y", top: 0.35),
        ];
        var widest = RetrievalEvalScoring.SuggestThreshold(wider).ShouldNotBeNull();
        (widest.Correct, widest.Low, widest.High).ShouldBe((3, 0.5, 0.95), "the widest of the best gaps");
    }

    [Fact]
    public void Without_any_score_there_is_nothing_to_suggest()
    {
        IReadOnlyList<EvalQuestionResult> results = [Miss("a", "returns", top: null), Nothing("x", top: null)];

        var summary = RetrievalEvalScoring.Summarize(results, 0.3);

        summary.Suggested.ShouldBeNull();
        summary.MaxUnanswerable.ShouldBeNull();
        summary.MinHit.ShouldBeNull();
        summary.Separable.ShouldBeFalse();
        summary.AtCurrent.Correct.ShouldBe(1, "a question that should find nothing and found nothing is right");
    }

    [Fact]
    public void Passages_of_other_versions_and_non_effective_passages_are_counted()
    {
        var question = Question("q", "returns", Page2);
        var result = RetrievalEvalScoring.Judge(question,
        [
            Passage("退換貨辦法.pdf", 1, "第 2 頁", "十天內", 0.9, KnowledgeVersionState.Archived),
            Passage("退換貨辦法.pdf", 2, "第 2 頁", "七天內", 0.8),
        ]);

        result.OtherVersionPassages.ShouldBe(1);
        var summary = RetrievalEvalScoring.Summarize([result], 0.3);
        (summary.NonEffectivePassages, summary.QuestionsWithOtherVersions).ShouldBe((1, 1));
    }

    [Fact]
    public void The_report_has_every_section_warns_about_fake_scores_and_escapes_table_cells()
    {
        IReadOnlyList<EvalQuestionResult> results =
        [
            Hit("delivery-01", "delivery", rank: 2, score: 0.5),
            RetrievalEvalScoring.Judge(Question("none-01", "unanswerable"), [Passage("運費與配送時間表.xlsx", 1, "工作表『運費』第 2–5 列", "配送方式 | 運費\n本島常溫 | 100", 0.2)]),
        ];
        var run = new RetrievalEvalRun(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(8)),
            TimeSpan.FromSeconds(3),
            "fake",
            "fake-dev",
            null,
            string.Empty,
            string.Empty,
            "apps/api/eval/retrieval",
            new string('a', 64),
            3,
            4,
            5,
            20,
            results,
            RetrievalEvalScoring.Summarize(results, 0.3));

        var report = RetrievalEvalReport.Render(run);

        report.ShouldStartWith("# 檢索評測：fake-dev（2026-09-27）");
        foreach (var section in RetrievalEvalReport.Sections)
        {
            report.ShouldContain("\n" + section + "\n", Case.Sensitive);
        }

        report.ShouldContain("Fake", Case.Sensitive);
        report.ShouldContain("不可用來判斷檢索品質", Case.Sensitive);
        report.ShouldContain("配送方式 \\| 運費 本島常溫 \\| 100", Case.Sensitive, "a passage's pipes and line breaks never break the Markdown");
        report.ShouldContain("| 前 5 名命中率（hit@5） | 1/1 = 100.0% |", Case.Sensitive);
        RetrievalEvalReport.FileName(run.StartedAt, "intfloat/multilingual-e5-large").ShouldBe("2026-09-27-retrieval-intfloat-multilingual-e5-large.md");
        RetrievalEvalReport.FileName(run.StartedAt, "text-embedding-3-small").ShouldBe("2026-09-27-retrieval-text-embedding-3-small.md");
    }

    private static EvalPassage Passage(
        string document,
        int version,
        string location,
        string text,
        double score = 0.5,
        KnowledgeVersionState state = KnowledgeVersionState.Effective) =>
        new(document, version, state, location, text, score);

    private static EvalQuestion Question(string id, string category, params EvalExpectedPassage[] expected) =>
        new(id, category, $"問題 {id}", expected, null);

    /// <summary>An answerable question whose expected passage is at <paramref name="rank"/> with
    /// <paramref name="score"/> (the passages above it score a little more).</summary>
    private static EvalQuestionResult Hit(string id, string category, int rank, double score)
    {
        var expected = new EvalExpectedPassage($"{id}.md", 1, "全文", null);
        var passages = Enumerable.Range(1, rank - 1)
            .Select(above => Passage("其他.md", 1, "全文", "…", score + (0.01 * (rank - above))))
            .Append(Passage($"{id}.md", 1, "全文", "…", score))
            .ToList();
        return RetrievalEvalScoring.Judge(Question(id, category, expected), passages);
    }

    private static EvalQuestionResult Miss(string id, string category, double? top) =>
        RetrievalEvalScoring.Judge(
            Question(id, category, new EvalExpectedPassage($"{id}.md", 1, "全文", null)),
            top is { } score ? [Passage("其他.md", 1, "全文", "…", score)] : []);

    private static EvalQuestionResult Nothing(string id, double? top) =>
        RetrievalEvalScoring.Judge(Question(id, "unanswerable"), top is { } score ? [Passage("其他.md", 1, "全文", "…", score)] : []);
}
