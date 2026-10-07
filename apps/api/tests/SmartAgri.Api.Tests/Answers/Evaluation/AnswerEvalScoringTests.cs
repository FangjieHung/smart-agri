using Shouldly;
using SmartAgri.Api.Answers.Evaluation;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Api.Tests.Answers.Evaluation;

/// <summary>How <see cref="AnswerEvalScoring"/> judges and sums up replies (M3 plan Slice 13;
/// ticket #83). Pure: no database, no model.</summary>
public sealed class AnswerEvalScoringTests
{
    private static AnswerEvalQuestion CompanyData(string id, params string[] expectedDocuments) =>
        new(id, $"問題 {id}", AnswerEvalExpectedKind.CompanyData, expectedDocuments, FollowUpOf: null, Note: null);

    private static AnswerEvalQuestion NoResult(string id) =>
        new(id, $"問題 {id}", AnswerEvalExpectedKind.NoResult, [], FollowUpOf: null, Note: null);

    [Fact]
    public void A_company_data_question_answered_company_data_citing_an_expected_document_is_correct_and_a_hit()
    {
        var question = CompanyData("q1", "a.md", "b.md");
        var reply = new GroundedReply(GroundedReplyKind.CompanyData, "答案 [1]", [Citation("a.md")], null, [], null);

        var result = AnswerEvalScoring.Judge(question, reply, []);

        result.KindCorrect.ShouldBeTrue();
        result.CitationHit.ShouldBe(true);
        result.CitedDocuments.ShouldBe(["a.md"]);
    }

    [Fact]
    public void A_company_data_question_that_comes_back_no_result_is_a_kind_miss()
    {
        var question = CompanyData("q1", "a.md");
        var reply = new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold);

        var result = AnswerEvalScoring.Judge(question, reply, []);

        result.KindCorrect.ShouldBeFalse();
        result.CitationHit.ShouldBe(false);
    }

    [Fact]
    public void A_company_data_question_citing_only_the_wrong_document_is_a_kind_hit_but_a_citation_miss()
    {
        var question = CompanyData("q1", "a.md");
        var reply = new GroundedReply(GroundedReplyKind.CompanyData, "答案 [1]", [Citation("z.md")], null, [], null);

        var result = AnswerEvalScoring.Judge(question, reply, []);

        result.KindCorrect.ShouldBeTrue();
        result.CitationHit.ShouldBe(false);
    }

    [Fact]
    public void A_no_result_question_answered_no_result_is_correct_and_has_no_citation_hit_verdict()
    {
        var question = NoResult("q1");
        var reply = new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold);

        var result = AnswerEvalScoring.Judge(question, reply, []);

        result.KindCorrect.ShouldBeTrue();
        result.CitationHit.ShouldBeNull();
    }

    [Fact]
    public void The_result_keeps_the_closest_passages_score_whatever_the_threshold_and_none_when_nothing_was_retrieved()
    {
        var reply = new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold);

        AnswerEvalScoring.Judge(NoResult("q1"), reply, [Passage(0.31), Passage(0.337), Passage(0.2)]).TopScore.ShouldBe(0.337);
        AnswerEvalScoring.Judge(NoResult("q1"), reply, []).TopScore.ShouldBeNull();
    }

    [Fact]
    public void The_report_shows_every_questions_top_score_and_the_reply_text_of_answered_questions()
    {
        var results = new[]
        {
            AnswerEvalScoring.Judge(
                CompanyData("returns-05", "a.md"),
                new GroundedReply(GroundedReplyKind.CompanyData, "不可以，已超過七天。[1]\n請見 | 退貨辦法", [Citation("a.md")], null, [], null),
                [Passage(0.5404)]),
            AnswerEvalScoring.Judge(
                NoResult("trap-03"),
                new GroundedReply(GroundedReplyKind.NoResult, "目前的資料中找不到這個問題的答案。", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.CannotAnswer),
                [Passage(0.457)]),
            AnswerEvalScoring.Judge(
                NoResult("none-01"),
                new GroundedReply(GroundedReplyKind.NoResult, "目前的資料中找不到這個問題的答案。", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold),
                []),
        };

        var report = AnswerEvalReport.Render(new AnswerEvalRun(
            new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(8)), TimeSpan.FromSeconds(3), "fake", "fake-embedding", "fake", "fake-chat",
            0.406, GroundedAnswerPrompt.Version, "apps/api/eval/answers", new string('a', 64), 1, 1, results,
            AnswerEvalScoring.Summarize(results, null, null)));

        report.ShouldContain($"| 回答提示版本 | `{GroundedAnswerPrompt.Version}` |");
        report.ShouldContain("| 拒絕原因 | 最高分 | 回覆內容 |");
        report.ShouldContain("| — | 0.540 | 不可以，已超過七天。[1] 請見 \\| 退貨辦法 |", Case.Sensitive, "one line, the pipe escaped");
        report.ShouldContain("| `cannot-answer` | 0.457 | — |", Case.Sensitive, "a no-result reply is always the refusal message: not repeated");
        report.ShouldContain("| `below-threshold` | — | — |", Case.Sensitive, "nothing retrieved: no score");
    }

    [Fact]
    public void Summarize_computes_kind_accuracy_citation_hit_rate_and_the_rejection_reason_distribution()
    {
        var results = new[]
        {
            AnswerEvalScoring.Judge(CompanyData("q1", "a.md"), new GroundedReply(GroundedReplyKind.CompanyData, "x [1]", [Citation("a.md")], null, [], null), []),
            AnswerEvalScoring.Judge(CompanyData("q2", "a.md"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.CitationOutOfRange), []),
            AnswerEvalScoring.Judge(NoResult("q3"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold), []),
            AnswerEvalScoring.Judge(NoResult("q4"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold), []),
        };

        var summary = AnswerEvalScoring.Summarize(results, averageInputTokens: 12.5, averageOutputTokens: 3);

        (summary.Total, summary.ReplyKindCorrect).ShouldBe((4, 3));
        summary.ReplyKindAccuracy.ShouldBe(0.75);
        (summary.CompanyDataQuestions, summary.CitationHits).ShouldBe((2, 1));
        summary.CitationHitRate.ShouldBe(0.5);
        summary.RejectionReasons.ShouldBe([(GroundedRejectionReason.BelowThreshold, 2), (GroundedRejectionReason.CitationOutOfRange, 1)]);
        (summary.AverageInputTokens, summary.AverageOutputTokens).ShouldBe(((double?)12.5, (double?)3));
    }

    [Fact]
    public void Summarize_reports_no_citation_hit_rate_when_the_bank_has_no_company_data_question()
    {
        var results = new[] { AnswerEvalScoring.Judge(NoResult("q1"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold), []) };

        var summary = AnswerEvalScoring.Summarize(results, null, null);

        summary.CitationHitRate.ShouldBeNull();
        summary.CompanyDataQuestions.ShouldBe(0);
    }

    private static RetrievedKnowledgePassage Passage(double score) => new(
        ChunkId: Guid.NewGuid(),
        KnowledgeBaseId: Guid.NewGuid(),
        DocumentId: Guid.NewGuid(),
        DocumentName: "a.md",
        VersionId: Guid.NewGuid(),
        VersionNumber: 1,
        VersionState: KnowledgeVersionState.Effective,
        LocationLabel: "第 1 段",
        Text: "原文",
        Score: score,
        VersionEffectiveFrom: null);

    private static GroundedCitation Citation(string documentName) => new(
        Ordinal: 1,
        ChunkId: Guid.NewGuid(),
        KnowledgeBaseId: Guid.NewGuid(),
        KnowledgeBaseName: "知識庫",
        DocumentId: Guid.NewGuid(),
        DocumentName: documentName,
        VersionId: Guid.NewGuid(),
        VersionNumber: 1,
        LocationLabel: "第 1 頁",
        Excerpt: "節錄",
        Text: "原文",
        Score: 0.9,
        VersionEffectiveFrom: null);
}
