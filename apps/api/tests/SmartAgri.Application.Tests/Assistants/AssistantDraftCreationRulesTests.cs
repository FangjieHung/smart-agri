using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

public class AssistantDraftCreationRulesTests
{
    private static readonly Guid ConnectableKnowledgeBaseId = Guid.CreateVersion7();

    private static readonly IReadOnlySet<Guid> OneConnectable = new HashSet<Guid> { ConnectableKnowledgeBaseId };

    [Fact]
    public void A_well_formed_draft_is_valid_and_normalized()
    {
        var payload = $$"""
            {
              "name": " 客服助理 ",
              "purpose": " 回答退換貨問題 ",
              "templateId": "answer-customer-questions",
              "tone": "friendly",
              "audience": "account-members",
              "roleInstructions": " 保持有禮貌 ",
              "sources": [{ "id": "{{ConnectableKnowledgeBaseId}}", "type": "knowledge-base" }],
              "rules": {
                "knowledgeScope": "company-data-only",
                "refusalMessage": " 目前的資料中找不到這個問題的答案。 ",
                "showCitations": true,
                "keepOwnConversations": false
              }
            }
            """;

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeTrue();
        result.Value.Name.ShouldBe("客服助理");
        result.Value.Purpose.ShouldBe("回答退換貨問題");
        result.Value.TemplateId.ShouldBe("answer-customer-questions");
        result.Value.Tone.ShouldBe(AssistantTone.Friendly);
        result.Value.RoleInstructions.ShouldBe("保持有禮貌");
        result.Value.KnowledgeScope.ShouldBe(AssistantKnowledgeScope.CompanyDataOnly);
        result.Value.RefusalMessage.ShouldBe("目前的資料中找不到這個問題的答案。");
        result.Value.ShowCitations.ShouldBeTrue();
        result.Value.KeepConversations.ShouldBeFalse();
        result.Value.KnowledgeBaseIds.ShouldBe([ConnectableKnowledgeBaseId]);
        result.Value.Audience.ShouldBe(AssistantAudience.AccountMembers);
    }

    [Fact]
    public void A_missing_audience_defaults_to_organization_internal_and_is_valid()
    {
        var payload = ValidPayload(audience: null);

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeTrue();
        result.Value.Audience.ShouldBe(AssistantAudience.AccountMembers);
    }

    [Theory]
    [InlineData("account-members", AssistantAudience.AccountMembers)]
    [InlineData("authorized-external-customers", AssistantAudience.AuthorizedExternalCustomers)]
    [InlineData("members-and-external-customers", AssistantAudience.MembersAndExternalCustomers)]
    public void Every_audience_is_accepted_including_external_ones(string audience, AssistantAudience expected)
    {
        // #224: external audiences are no longer "對外發布將於後續版本開放".
        var payload = ValidPayload(audience: audience);

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeTrue();
        result.Value.Audience.ShouldBe(expected);
    }

    [Theory]
    [InlineData("something-unknown")]
    [InlineData("Account-Members")]
    public void An_unknown_audience_is_rejected(string audience)
    {
        var payload = ValidPayload(audience: audience);

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure =>
            failure.Field == AssistantDraftCreationRules.AudienceField
            && failure.Message == AssistantDraftCreationRules.AudienceInvalidMessage);
    }

    [Fact]
    public void A_blank_name_is_rejected()
    {
        var payload = ValidPayload(name: "   ");

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantDraftCreationRules.NameField);
    }

    [Fact]
    public void A_blank_purpose_is_rejected()
    {
        var payload = ValidPayload(purpose: "");

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantDraftCreationRules.PurposeField);
    }

    [Fact]
    public void No_sources_is_rejected()
    {
        var payload = ValidPayload(sources: "[]");

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantDraftCreationRules.SourcesField);
    }

    [Fact]
    public void A_knowledge_base_id_the_caller_may_not_connect_is_rejected_with_422_shaped_failure()
    {
        var someoneElsesKnowledgeBase = Guid.CreateVersion7();
        var payload = ValidPayload(
            sources: $$"""[{ "id": "{{someoneElsesKnowledgeBase}}", "type": "knowledge-base" }]""");

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantDraftCreationRules.SourcesField);
    }

    [Fact]
    public void A_database_source_is_rejected_as_not_yet_available()
    {
        var payload = ValidPayload(sources: """[{ "id": "db-1", "type": "database" }]""");

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure =>
            failure.Field == AssistantDraftCreationRules.SourcesField
            && failure.Message == AssistantDraftCreationRules.DatabaseSourceNotAvailableMessage);
    }

    [Fact]
    public void A_blank_refusal_message_is_rejected()
    {
        var payload = ValidPayload(refusalMessage: "  ");

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantDraftCreationRules.RefusalMessageField);
    }

    [Fact]
    public void An_invalid_tone_or_knowledge_scope_is_rejected()
    {
        var badTone = AssistantDraftCreationRules.Validate(ValidPayload(tone: "sarcastic"), OneConnectable);
        badTone.IsValid.ShouldBeFalse();
        badTone.Failures.ShouldContain(failure => failure.Field == AssistantDraftCreationRules.ToneField);

        var badScope = AssistantDraftCreationRules.Validate(
            ValidPayload(knowledgeScope: "make-it-up"), OneConnectable);
        badScope.IsValid.ShouldBeFalse();
        badScope.Failures.ShouldContain(failure => failure.Field == AssistantDraftCreationRules.KnowledgeScopeField);
    }

    [Fact]
    public void Several_broken_fields_are_all_reported_at_once_and_nothing_is_written()
    {
        var payload = ValidPayload(name: "", purpose: "", sources: "[]");

        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
        result.Failures.Select(failure => failure.Field).ShouldBe(
            [
                AssistantDraftCreationRules.NameField,
                AssistantDraftCreationRules.PurposeField,
                AssistantDraftCreationRules.SourcesField,
            ],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void A_malformed_or_non_object_payload_is_rejected_without_throwing(string payload)
    {
        var result = AssistantDraftCreationRules.Validate(payload, OneConnectable);

        result.IsValid.ShouldBeFalse();
    }

    private static string ValidPayload(
        string? name = "客服助理",
        string? purpose = "回答退換貨問題",
        string? audience = "account-members",
        string? sources = null,
        string? refusalMessage = "目前的資料中找不到這個問題的答案。",
        string? tone = "friendly",
        string? knowledgeScope = "company-data-only")
    {
        sources ??= $$"""[{ "id": "{{ConnectableKnowledgeBaseId}}", "type": "knowledge-base" }]""";
        var audiencePart = audience is null ? "null" : $"\"{audience}\"";

        return $$"""
            {
              "name": {{Json(name)}},
              "purpose": {{Json(purpose)}},
              "tone": {{Json(tone)}},
              "audience": {{audiencePart}},
              "roleInstructions": "",
              "sources": {{sources}},
              "rules": {
                "knowledgeScope": {{Json(knowledgeScope)}},
                "refusalMessage": {{Json(refusalMessage)}},
                "showCitations": true,
                "keepOwnConversations": true
              }
            }
            """;
    }

    private static string Json(string? value) => value is null ? "null" : $"\"{value}\"";
}
