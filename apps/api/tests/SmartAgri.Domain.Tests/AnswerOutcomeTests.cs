using Shouldly;
using SmartAgri.Domain.Answers;

namespace SmartAgri.Domain.Tests;

public class AnswerOutcomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Assistant = Guid.CreateVersion7();

    [Fact]
    public void The_entity_holds_no_content_at_all_not_even_provider_or_model_names()
    {
        var outcome = AnswerOutcome.Record(
            Organization, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [Guid.CreateVersion7()], Now);

        outcome.Id.ShouldNotBe(Guid.Empty);
        (outcome.OrganizationId, outcome.AssistantId, outcome.Channel, outcome.ReplyKind, outcome.RejectionReason, outcome.At)
            .ShouldBe((Organization, (Guid?)Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, (AnswerRejectionReason?)null, Now));

        // Issue #128's acceptance: no property that could hold text of any kind — not the
        // question, not the answer, not an account or thread id. Unlike ModelInvocation (which
        // may hold a provider/model name), this type has no string property at all.
        typeof(AnswerOutcome).GetProperties().Where(property => property.PropertyType == typeof(string)).ShouldBeEmpty();
    }

    [Fact]
    public void A_no_result_outcome_always_has_a_rejection_reason_and_nothing_else_does()
    {
        Should.Throw<ArgumentException>(() =>
            AnswerOutcome.Record(Organization, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.NoResult, null, [], Now));
        Should.Throw<ArgumentException>(() =>
            AnswerOutcome.Record(
                Organization, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, AnswerRejectionReason.BelowThreshold, [], Now));

        var rejected = AnswerOutcome.Record(
            Organization, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.NoResult, AnswerRejectionReason.BelowThreshold, [], Now);
        rejected.RejectionReason.ShouldBe(AnswerRejectionReason.BelowThreshold);
        rejected.CitedDocumentIds.ShouldBeEmpty();
    }

    [Fact]
    public void A_trial_answer_has_no_assistant()
    {
        var trial = AnswerOutcome.Record(
            Organization, null, AnswerOutcomeChannel.Trial, AnswerReplyKind.GeneralKnowledge, null, [], Now);
        trial.AssistantId.ShouldBeNull();
    }

    [Fact]
    public void Cited_document_ids_are_de_duplicated()
    {
        var documentId = Guid.CreateVersion7();
        var outcome = AnswerOutcome.Record(
            Organization, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [documentId, documentId], Now);
        outcome.CitedDocumentIds.ShouldBe([documentId]);
    }

    [Fact]
    public void An_empty_organization_or_a_zero_assistant_id_is_rejected()
    {
        Should.Throw<ArgumentException>(() =>
            AnswerOutcome.Record(Guid.Empty, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [], Now));
        Should.Throw<ArgumentException>(() =>
            AnswerOutcome.Record(Organization, Guid.Empty, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [], Now));
    }

    [Fact]
    public void A_database_query_outcome_holds_only_its_result_in_the_chat_channel()
    {
        var outcome = AnswerOutcome.RecordDatabaseQuery(Organization, Assistant, AnswerDatabaseQueryResult.InsufficientRecords, Now);

        (outcome.OrganizationId, outcome.AssistantId, outcome.Channel, outcome.ReplyKind, outcome.DatabaseQueryResult, outcome.RejectionReason, outcome.At)
            .ShouldBe((Organization, (Guid?)Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.DatabaseQuery,
                (AnswerDatabaseQueryResult?)AnswerDatabaseQueryResult.InsufficientRecords, (AnswerRejectionReason?)null, Now));
        outcome.CitedDocumentIds.ShouldBeEmpty();
    }

    [Fact]
    public void Only_a_database_query_outcome_has_a_query_result_and_it_needs_an_organization_and_assistant()
    {
        AnswerOutcome.Record(Organization, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [], Now)
            .DatabaseQueryResult.ShouldBeNull();
        Should.Throw<ArgumentException>(() =>
            AnswerOutcome.Record(Organization, Assistant, AnswerOutcomeChannel.Chat, AnswerReplyKind.DatabaseQuery, null, [], Now));
        Should.Throw<ArgumentException>(() =>
            AnswerOutcome.RecordDatabaseQuery(Guid.Empty, Assistant, AnswerDatabaseQueryResult.Answered, Now));
        Should.Throw<ArgumentException>(() =>
            AnswerOutcome.RecordDatabaseQuery(Organization, Guid.Empty, AnswerDatabaseQueryResult.Answered, Now));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            AnswerOutcome.RecordDatabaseQuery(Organization, Assistant, (AnswerDatabaseQueryResult)99, Now));
    }
}
