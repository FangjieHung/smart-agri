using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

public sealed class AssistantTestCaseRulesTests
{
    private static readonly HashSet<Guid> NoDocuments = [];

    private static readonly HashSet<Guid> NoExistingCases = [];

    [Fact]
    public void A_blank_question_is_invalid()
    {
        var result = AssistantTestCaseRules.Validate(
            "   ", "common", "general-knowledge", [], null, NoDocuments, NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Field == AssistantTestCaseRules.QuestionField);
    }

    [Fact]
    public void A_question_over_the_limit_is_invalid()
    {
        var result = AssistantTestCaseRules.Validate(
            new string('問', AssistantTestCase.QuestionMaxLength + 1), "common", "general-knowledge", [], null,
            NoDocuments, NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Field == AssistantTestCaseRules.QuestionField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-category")]
    public void An_unknown_category_is_invalid(string? category)
    {
        var result = AssistantTestCaseRules.Validate("問題？", category, "general-knowledge", [], null, NoDocuments, NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Field == AssistantTestCaseRules.CategoryField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-kind")]
    public void An_unknown_expected_kind_is_invalid(string? expectedKind)
    {
        var result = AssistantTestCaseRules.Validate("問題？", "common", expectedKind, [], null, NoDocuments, NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Field == AssistantTestCaseRules.ExpectedKindField);
    }

    [Theory]
    [InlineData("general-knowledge")]
    [InlineData("no-result")]
    public void Expected_documents_are_not_allowed_unless_the_kind_is_company_data(string expectedKind)
    {
        var documentId = Guid.NewGuid();
        var result = AssistantTestCaseRules.Validate(
            "問題？", "common", expectedKind, [documentId], null, [documentId], NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Field == AssistantTestCaseRules.ExpectedDocumentIdsField);
    }

    [Fact]
    public void A_document_not_among_the_connected_ones_is_invalid()
    {
        var connectedDocumentId = Guid.NewGuid();
        var unrelatedDocumentId = Guid.NewGuid();
        var result = AssistantTestCaseRules.Validate(
            "問題？", "common", "company-data", [unrelatedDocumentId], null, [connectedDocumentId], NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Field == AssistantTestCaseRules.ExpectedDocumentIdsField);
    }

    [Fact]
    public void A_follow_up_that_is_not_an_existing_case_of_the_assistant_is_invalid()
    {
        var result = AssistantTestCaseRules.Validate(
            "問題？", "common", "general-knowledge", [], Guid.NewGuid(), NoDocuments, NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Field == AssistantTestCaseRules.FollowUpOfField);
    }

    [Fact]
    public void A_valid_request_normalizes_and_trims()
    {
        var documentId = Guid.NewGuid();
        var followUpId = Guid.NewGuid();
        var result = AssistantTestCaseRules.Validate(
            "  退貨期限是幾天？  ", "exception", "company-data", [documentId, documentId], followUpId,
            [documentId], [followUpId]);

        result.IsValid.ShouldBeTrue();
        result.Value.Question.ShouldBe("退貨期限是幾天？");
        result.Value.Category.ShouldBe(AssistantTestCaseCategory.Exception);
        result.Value.ExpectedKind.ShouldBe(AssistantTestExpectedKind.CompanyData);
        result.Value.ExpectedDocumentIds.ShouldBe([documentId]);
        result.Value.FollowUpOfId.ShouldBe(followUpId);
    }

    [Fact]
    public void ForCreate_refuses_at_the_maximum_count_before_validating_anything_else()
    {
        var result = AssistantTestCaseRules.ForCreate(
            AssistantTestCaseRules.MaxCount, null, null, null, null, null, NoDocuments, NoExistingCases);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Message == AssistantTestCaseRules.TooManyTestCasesMessage);
    }

    [Fact]
    public void ForCreate_below_the_maximum_falls_through_to_field_validation()
    {
        var result = AssistantTestCaseRules.ForCreate(
            AssistantTestCaseRules.MaxCount - 1, "問題？", "common", "general-knowledge", [], null,
            NoDocuments, NoExistingCases);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ForUpdate_keeps_fields_that_are_not_provided()
    {
        var documentId = Guid.NewGuid();
        var current = new AssistantTestCaseDetails(
            "原本的問題？", AssistantTestCaseCategory.Common, AssistantTestExpectedKind.CompanyData, [documentId], null);

        var result = AssistantTestCaseRules.ForUpdate(
            current, question: "改過的問題？", category: null, expectedKind: null, expectedDocumentIds: null, followUpOfId: null,
            [documentId], NoExistingCases);

        result.IsValid.ShouldBeTrue();
        result.Value.Question.ShouldBe("改過的問題？");
        result.Value.Category.ShouldBe(AssistantTestCaseCategory.Common);
        result.Value.ExpectedKind.ShouldBe(AssistantTestExpectedKind.CompanyData);
        result.Value.ExpectedDocumentIds.ShouldBe([documentId]);
    }
}
