using Shouldly;
using SmartAgri.Api.Answers.Evaluation;
using SmartAgri.Application.Answers;

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

        var result = AnswerEvalScoring.Judge(question, reply);

        result.KindCorrect.ShouldBeTrue();
        result.CitationHit.ShouldBe(true);
        result.CitedDocuments.ShouldBe(["a.md"]);
    }

    [Fact]
    public void A_company_data_question_that_comes_back_no_result_is_a_kind_miss()
    {
        var question = CompanyData("q1", "a.md");
        var reply = new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold);

        var result = AnswerEvalScoring.Judge(question, reply);

        result.KindCorrect.ShouldBeFalse();
        result.CitationHit.ShouldBe(false);
    }

    [Fact]
    public void A_company_data_question_citing_only_the_wrong_document_is_a_kind_hit_but_a_citation_miss()
    {
        var question = CompanyData("q1", "a.md");
        var reply = new GroundedReply(GroundedReplyKind.CompanyData, "答案 [1]", [Citation("z.md")], null, [], null);

        var result = AnswerEvalScoring.Judge(question, reply);

        result.KindCorrect.ShouldBeTrue();
        result.CitationHit.ShouldBe(false);
    }

    [Fact]
    public void A_no_result_question_answered_no_result_is_correct_and_has_no_citation_hit_verdict()
    {
        var question = NoResult("q1");
        var reply = new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold);

        var result = AnswerEvalScoring.Judge(question, reply);

        result.KindCorrect.ShouldBeTrue();
        result.CitationHit.ShouldBeNull();
    }

    [Fact]
    public void Summarize_computes_kind_accuracy_citation_hit_rate_and_the_rejection_reason_distribution()
    {
        var results = new[]
        {
            AnswerEvalScoring.Judge(CompanyData("q1", "a.md"), new GroundedReply(GroundedReplyKind.CompanyData, "x [1]", [Citation("a.md")], null, [], null)),
            AnswerEvalScoring.Judge(CompanyData("q2", "a.md"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.CitationOutOfRange)),
            AnswerEvalScoring.Judge(NoResult("q3"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold)),
            AnswerEvalScoring.Judge(NoResult("q4"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold)),
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
        var results = new[] { AnswerEvalScoring.Judge(NoResult("q1"), new GroundedReply(GroundedReplyKind.NoResult, "查無結果", [], null, GroundedReply.NoResultNextSteps, GroundedRejectionReason.BelowThreshold)) };

        var summary = AnswerEvalScoring.Summarize(results, null, null);

        summary.CitationHitRate.ShouldBeNull();
        summary.CompanyDataQuestions.ShouldBe(0);
    }

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
        Score: 0.9);
}
