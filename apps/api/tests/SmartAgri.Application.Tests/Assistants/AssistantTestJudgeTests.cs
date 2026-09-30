using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

public sealed class AssistantTestJudgeTests
{
    private static readonly Guid DocumentA = Guid.NewGuid();
    private static readonly Guid DocumentB = Guid.NewGuid();

    [Fact]
    public void Company_data_citing_every_expected_document_passes_even_with_extra_citations()
    {
        AssistantTestJudge.Judge(AssistantTestExpectedKind.CompanyData, [DocumentA], AnswerReplyKind.CompanyData, [DocumentB, DocumentA])
            .ShouldBe(AssistantTestVerdict.Pass);
    }

    [Fact]
    public void Company_data_with_no_expected_documents_passes_on_any_citation()
    {
        AssistantTestJudge.Judge(AssistantTestExpectedKind.CompanyData, [], AnswerReplyKind.CompanyData, [DocumentB])
            .Passed.ShouldBeTrue();
    }

    [Fact]
    public void Company_data_citing_the_wrong_document_is_missing_document()
    {
        AssistantTestJudge.Judge(AssistantTestExpectedKind.CompanyData, [DocumentA], AnswerReplyKind.CompanyData, [DocumentB])
            .ShouldBe(AssistantTestVerdict.Fail(AssistantTestFailureReason.MissingDocument));
    }

    [Fact]
    public void Company_data_citing_only_some_expected_documents_is_missing_document()
    {
        AssistantTestJudge.Judge(AssistantTestExpectedKind.CompanyData, [DocumentA, DocumentB], AnswerReplyKind.CompanyData, [DocumentA])
            .FailureReason.ShouldBe(AssistantTestFailureReason.MissingDocument);
    }

    [Theory]
    [InlineData(AssistantTestExpectedKind.CompanyData, AnswerReplyKind.NoResult)]
    [InlineData(AssistantTestExpectedKind.CompanyData, AnswerReplyKind.GeneralKnowledge)]
    [InlineData(AssistantTestExpectedKind.NoResult, AnswerReplyKind.CompanyData)]
    [InlineData(AssistantTestExpectedKind.NoResult, AnswerReplyKind.GeneralKnowledge)]
    [InlineData(AssistantTestExpectedKind.GeneralKnowledge, AnswerReplyKind.CompanyData)]
    [InlineData(AssistantTestExpectedKind.GeneralKnowledge, AnswerReplyKind.NoResult)]
    public void Any_other_reply_kind_than_expected_is_kind_mismatch(AssistantTestExpectedKind expected, AnswerReplyKind actual)
    {
        AssistantTestJudge.Judge(expected, [], actual, actual == AnswerReplyKind.CompanyData ? [DocumentA] : [])
            .ShouldBe(AssistantTestVerdict.Fail(AssistantTestFailureReason.KindMismatch));
    }

    [Fact]
    public void Kind_mismatch_wins_over_missing_documents()
    {
        AssistantTestJudge.Judge(AssistantTestExpectedKind.CompanyData, [DocumentA], AnswerReplyKind.NoResult, [])
            .FailureReason.ShouldBe(AssistantTestFailureReason.KindMismatch);
    }

    [Theory]
    [InlineData(AssistantTestExpectedKind.NoResult, AnswerReplyKind.NoResult)]
    [InlineData(AssistantTestExpectedKind.GeneralKnowledge, AnswerReplyKind.GeneralKnowledge)]
    public void The_expected_non_company_kind_passes(AssistantTestExpectedKind expected, AnswerReplyKind actual)
    {
        AssistantTestJudge.Judge(expected, [], actual, []).ShouldBe(AssistantTestVerdict.Pass);
    }
}
