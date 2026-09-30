using Shouldly;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Domain.Tests;

public sealed class AssistantTestCaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Create_sets_every_field()
    {
        var organizationId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var followUpOfId = Guid.NewGuid();

        var testCase = new AssistantTestCase(
            organizationId, assistantId, "退貨期限是幾天？", AssistantTestCaseCategory.Common,
            AssistantTestExpectedKind.CompanyData, [documentId], followUpOfId, ordinal: 1, Now);

        testCase.OrganizationId.ShouldBe(organizationId);
        testCase.AssistantId.ShouldBe(assistantId);
        testCase.Question.ShouldBe("退貨期限是幾天？");
        testCase.Category.ShouldBe(AssistantTestCaseCategory.Common);
        testCase.ExpectedKind.ShouldBe(AssistantTestExpectedKind.CompanyData);
        testCase.ExpectedDocumentIds.ShouldBe([documentId]);
        testCase.FollowUpOfId.ShouldBe(followUpOfId);
        testCase.Ordinal.ShouldBe(1);
        testCase.CreatedAt.ShouldBe(Now);
        testCase.UpdatedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_question_is_rejected(string question)
    {
        Should.Throw<ArgumentException>(() => new AssistantTestCase(
            Guid.NewGuid(), Guid.NewGuid(), question, AssistantTestCaseCategory.Common,
            AssistantTestExpectedKind.GeneralKnowledge, [], null, 1, Now));
    }

    [Fact]
    public void A_question_over_the_limit_is_rejected()
    {
        Should.Throw<ArgumentException>(() => new AssistantTestCase(
            Guid.NewGuid(), Guid.NewGuid(), new string('問', AssistantTestCase.QuestionMaxLength + 1),
            AssistantTestCaseCategory.Common, AssistantTestExpectedKind.GeneralKnowledge, [], null, 1, Now));
    }

    [Fact]
    public void Apply_replaces_every_field_and_touches_updated_at()
    {
        var testCase = new AssistantTestCase(
            Guid.NewGuid(), Guid.NewGuid(), "原本的問題？", AssistantTestCaseCategory.Common,
            AssistantTestExpectedKind.GeneralKnowledge, [], null, 1, Now);

        var later = Now.AddMinutes(1);
        var documentId = Guid.NewGuid();
        testCase.Apply("改過的問題？", AssistantTestCaseCategory.ShouldRefuse, AssistantTestExpectedKind.CompanyData, [documentId], null, later);

        testCase.Question.ShouldBe("改過的問題？");
        testCase.Category.ShouldBe(AssistantTestCaseCategory.ShouldRefuse);
        testCase.ExpectedKind.ShouldBe(AssistantTestExpectedKind.CompanyData);
        testCase.ExpectedDocumentIds.ShouldBe([documentId]);
        testCase.UpdatedAt.ShouldBe(later);
    }
}
