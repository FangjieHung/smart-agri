using Shouldly;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

public class AssistantTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_assistant_is_ready_trimmed_and_owned_by_its_creator()
    {
        var assistant = Assistant.Create(
            Organization, Owner, " 客服助理 ", " 回答退換貨問題 ", "answer-customer-questions",
            AssistantTone.Friendly, " 保持有禮貌 ", AssistantKnowledgeScope.CompanyDataOnly,
            " 目前的資料中找不到這個問題的答案。 ", showCitations: true, keepConversations: true, Created);

        assistant.Id.ShouldNotBe(Guid.Empty);
        assistant.OrganizationId.ShouldBe(Organization);
        assistant.OwnerAccountId.ShouldBe(Owner);
        assistant.Name.ShouldBe("客服助理");
        assistant.Purpose.ShouldBe("回答退換貨問題");
        assistant.TemplateId.ShouldBe("answer-customer-questions");
        assistant.Tone.ShouldBe(AssistantTone.Friendly);
        assistant.RoleInstructions.ShouldBe("保持有禮貌");
        assistant.KnowledgeScope.ShouldBe(AssistantKnowledgeScope.CompanyDataOnly);
        assistant.RefusalMessage.ShouldBe("目前的資料中找不到這個問題的答案。");
        assistant.ShowCitations.ShouldBeTrue();
        assistant.KeepConversations.ShouldBeTrue();
        assistant.MinScore.ShouldBeNull();
        assistant.Status.ShouldBe(AssistantStatus.Ready);
        assistant.CreatedAt.ShouldBe(Created);
        assistant.UpdatedAt.ShouldBe(Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_rejected(string name)
    {
        Should.Throw<ArgumentException>(() => CreateWith(name: name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_purpose_is_rejected(string purpose)
    {
        Should.Throw<ArgumentException>(() => CreateWith(purpose: purpose));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_refusal_message_is_rejected(string refusalMessage)
    {
        Should.Throw<ArgumentException>(() => CreateWith(refusalMessage: refusalMessage));
    }

    [Fact]
    public void Over_long_fields_and_empty_ids_are_rejected()
    {
        Should.Throw<ArgumentException>(() => CreateWith(name: new string('名', Assistant.NameMaxLength + 1)));
        Should.Throw<ArgumentException>(() => CreateWith(purpose: new string('用', Assistant.PurposeMaxLength + 1)));
        Should.Throw<ArgumentException>(() => CreateWith(roleInstructions: new string('角', Assistant.RoleInstructionsMaxLength + 1)));
        Should.Throw<ArgumentException>(() => CreateWith(refusalMessage: new string('拒', Assistant.RefusalMessageMaxLength + 1)));
        Should.Throw<ArgumentException>(() => Assistant.Create(
            Guid.Empty, Owner, "名稱", "用途", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "拒絕文字", true, true, Created));
        Should.Throw<ArgumentException>(() => Assistant.Create(
            Organization, Guid.Empty, "名稱", "用途", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "拒絕文字", true, true, Created));
    }

    [Fact]
    public void An_undeclared_tone_or_knowledge_scope_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => CreateWith(tone: (AssistantTone)99));
        Should.Throw<ArgumentOutOfRangeException>(() => CreateWith(knowledgeScope: (AssistantKnowledgeScope)99));
    }

    [Fact]
    public void Applying_settings_reports_the_changed_fields_and_bumps_updated_at_only_when_something_changed()
    {
        var assistant = CreateWith();
        var later = Created.AddHours(1);

        assistant.ApplySettings(
                assistant.Name, assistant.Purpose, assistant.Tone, assistant.RoleInstructions, assistant.KnowledgeScope,
                assistant.RefusalMessage, assistant.ShowCitations, assistant.KeepConversations, later)
            .ShouldBeEmpty();
        assistant.UpdatedAt.ShouldBe(Created);

        var changed = assistant.ApplySettings(
            "新名稱", assistant.Purpose, AssistantTone.Concise, assistant.RoleInstructions, assistant.KnowledgeScope,
            assistant.RefusalMessage, showCitations: false, assistant.KeepConversations, later);

        changed.ShouldBe(["name", "tone", "showCitations"], ignoreOrder: true);
        assistant.Name.ShouldBe("新名稱");
        assistant.Tone.ShouldBe(AssistantTone.Concise);
        assistant.ShowCitations.ShouldBeFalse();
        assistant.UpdatedAt.ShouldBe(later);
    }

    [Fact]
    public void A_knowledge_base_connection_must_share_the_assistants_organization()
    {
        var assistant = CreateWith();
        var otherOrganizationKnowledgeBase = KnowledgeBase.Create(Guid.CreateVersion7(), Owner, "別組織的知識庫", string.Empty, Created);

        Should.Throw<ArgumentException>(() => new AssistantKnowledgeBase(assistant, otherOrganizationKnowledgeBase, Created));
    }

    [Fact]
    public void A_knowledge_base_connection_carries_the_assistants_organization_and_the_connection_time()
    {
        var assistant = CreateWith();
        var knowledgeBase = KnowledgeBase.Create(Organization, Owner, "知識庫", string.Empty, Created);

        var link = new AssistantKnowledgeBase(assistant, knowledgeBase, Created.AddMinutes(5));

        link.AssistantId.ShouldBe(assistant.Id);
        link.KnowledgeBaseId.ShouldBe(knowledgeBase.Id);
        link.OrganizationId.ShouldBe(Organization);
        link.ConnectedAt.ShouldBe(Created.AddMinutes(5));
    }

    private static Assistant CreateWith(
        string name = "客服助理",
        string purpose = "回答退換貨問題",
        AssistantTone tone = AssistantTone.Friendly,
        string roleInstructions = "",
        AssistantKnowledgeScope knowledgeScope = AssistantKnowledgeScope.CompanyDataOnly,
        string refusalMessage = "目前的資料中找不到這個問題的答案。") =>
        Assistant.Create(
            Organization, Owner, name, purpose, null, tone, roleInstructions, knowledgeScope, refusalMessage,
            showCitations: true, keepConversations: true, Created);
}
