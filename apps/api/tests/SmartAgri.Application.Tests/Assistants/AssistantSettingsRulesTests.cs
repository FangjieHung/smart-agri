using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

public class AssistantSettingsRulesTests
{
    private static readonly AssistantSettingsDetails Current = new(
        "客服助理", "回答退換貨問題", AssistantTone.Friendly, "保持有禮貌",
        AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
        ShowCitations: true, KeepConversations: true);

    [Fact]
    public void A_null_field_keeps_its_current_value()
    {
        var result = AssistantSettingsRules.ForUpdate(Current, null, null, null, null, null, null, null, null);

        result.IsValid.ShouldBeTrue();
        result.Value.ShouldBe(Current);
    }

    [Fact]
    public void Fields_are_trimmed_and_only_the_sent_ones_change()
    {
        var result = AssistantSettingsRules.ForUpdate(
            Current, " 新名稱 ", null, null, null, null, null, null, null);

        result.IsValid.ShouldBeTrue();
        result.Value.Name.ShouldBe("新名稱");
        result.Value.Purpose.ShouldBe(Current.Purpose);
    }

    [Fact]
    public void A_blank_name_is_rejected()
    {
        var result = AssistantSettingsRules.ForUpdate(Current, "   ", null, null, null, null, null, null, null);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure =>
            failure.Field == AssistantSettingsRules.NameField && failure.Message == AssistantSettingsRules.NameRequiredMessage);
    }

    [Fact]
    public void A_blank_purpose_is_rejected()
    {
        var result = AssistantSettingsRules.ForUpdate(Current, null, "  ", null, null, null, null, null, null);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantSettingsRules.PurposeField);
    }

    [Fact]
    public void An_unknown_tone_is_rejected()
    {
        var result = AssistantSettingsRules.ForUpdate(Current, null, null, "sarcastic", null, null, null, null, null);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure =>
            failure.Field == AssistantSettingsRules.ToneField && failure.Message == AssistantSettingsRules.ToneInvalidMessage);
    }

    [Fact]
    public void A_known_tone_is_parsed()
    {
        var result = AssistantSettingsRules.ForUpdate(Current, null, null, "concise", null, null, null, null, null);

        result.IsValid.ShouldBeTrue();
        result.Value.Tone.ShouldBe(AssistantTone.Concise);
    }

    [Fact]
    public void An_unknown_knowledge_scope_is_rejected()
    {
        var result = AssistantSettingsRules.ForUpdate(Current, null, null, null, null, "anything-goes", null, null, null);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure =>
            failure.Field == AssistantSettingsRules.KnowledgeScopeField
            && failure.Message == AssistantSettingsRules.KnowledgeScopeInvalidMessage);
    }

    [Fact]
    public void A_blank_refusal_message_is_rejected()
    {
        var result = AssistantSettingsRules.ForUpdate(Current, null, null, null, null, null, "   ", null, null);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantSettingsRules.RefusalMessageField);
    }

    [Fact]
    public void One_bad_field_alongside_a_good_one_reports_only_the_bad_one_and_nothing_is_applied()
    {
        // Mirrors the acceptance criterion: a legitimate field change plus an invalid one is
        // 422 as a whole, so the caller (AssistantEndpoints) never applies the good half.
        var result = AssistantSettingsRules.ForUpdate(Current, "新名稱", null, "not-a-tone", null, null, null, null, null);

        result.IsValid.ShouldBeFalse();
        result.Failures.Select(failure => failure.Field).ShouldBe([AssistantSettingsRules.ToneField]);
    }

    [Fact]
    public void Too_long_fields_are_each_reported()
    {
        var result = AssistantSettingsRules.ForUpdate(
            Current,
            new string('名', Assistant.NameMaxLength + 1),
            new string('用', Assistant.PurposeMaxLength + 1),
            null,
            new string('角', Assistant.RoleInstructionsMaxLength + 1),
            null,
            new string('拒', Assistant.RefusalMessageMaxLength + 1),
            null,
            null);

        result.IsValid.ShouldBeFalse();
        result.Failures.Select(failure => failure.Field).ShouldBe(
            [
                AssistantSettingsRules.NameField,
                AssistantSettingsRules.PurposeField,
                AssistantSettingsRules.RoleInstructionsField,
                AssistantSettingsRules.RefusalMessageField,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Boolean_flags_default_to_the_current_value_and_can_be_toggled()
    {
        var toggled = AssistantSettingsRules.ForUpdate(Current, null, null, null, null, null, null, false, false);

        toggled.IsValid.ShouldBeTrue();
        toggled.Value.ShowCitations.ShouldBeFalse();
        toggled.Value.KeepConversations.ShouldBeFalse();
    }

    [Theory]
    [InlineData("account-members", AssistantAudience.AccountMembers)]
    [InlineData("authorized-external-customers", AssistantAudience.AuthorizedExternalCustomers)]
    [InlineData("members-and-external-customers", AssistantAudience.MembersAndExternalCustomers)]
    public void The_audience_can_be_changed_to_any_of_the_three(string audience, AssistantAudience expected)
    {
        var result = AssistantSettingsRules.ForUpdate(
            Current, null, null, null, null, null, null, null, null, audience: audience);

        result.IsValid.ShouldBeTrue();
        result.Value.Audience.ShouldBe(expected);
        result.Value.Name.ShouldBe(Current.Name);
    }

    [Fact]
    public void A_null_audience_keeps_the_current_one()
    {
        var current = Current with { Audience = AssistantAudience.MembersAndExternalCustomers };

        var result = AssistantSettingsRules.ForUpdate(current, "新名稱", null, null, null, null, null, null, null);

        result.IsValid.ShouldBeTrue();
        result.Value.Audience.ShouldBe(AssistantAudience.MembersAndExternalCustomers);
    }

    [Fact]
    public void An_unknown_audience_is_rejected_with_the_other_failures()
    {
        var result = AssistantSettingsRules.ForUpdate(
            Current, "   ", null, null, null, null, null, null, null, audience: "everyone");

        result.IsValid.ShouldBeFalse();
        result.Failures.Select(failure => failure.Field).ShouldBe(
            [AssistantSettingsRules.NameField, AssistantSettingsRules.AudienceField], ignoreOrder: true);
        result.Failures.ShouldContain(failure => failure.Message == AssistantSettingsRules.AudienceInvalidMessage);
    }
}
