using Shouldly;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="AssistantWebsiteChannel"/> and <see cref="AssistantWebsiteDomain"/> (issue #194).</summary>
public class AssistantWebsiteChannelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.CreateVersion7();

    private static Assistant NewAssistant() =>
        Assistant.Create(
            Guid.CreateVersion7(), Owner, "客服助理", "回答問題", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "找不到資料。", showCitations: true, keepConversations: false, T0);

    private static AssistantWebsiteChannel NewChannel(Assistant? assistant = null) =>
        new(assistant ?? NewAssistant(), "安心客服", "您好！", WebsiteBrandColor.Forest, WebsiteLauncherPosition.BottomRight, T0);

    [Fact]
    public void A_new_channel_is_a_draft_of_its_assistant_at_revision_1()
    {
        var assistant = NewAssistant();
        var channel = NewChannel(assistant);

        channel.AssistantId.ShouldBe(assistant.Id);
        channel.OrganizationId.ShouldBe(assistant.OrganizationId);
        channel.State.ShouldBe(WebsiteChannelState.Draft);
        channel.Revision.ShouldBe(1);
        channel.PublishedAt.ShouldBeNull();
        channel.PublishedByAccountId.ShouldBeNull();
        channel.UpdatedAt.ShouldBe(T0);
    }

    [Fact]
    public void Settings_apply_only_at_the_expected_revision()
    {
        var channel = NewChannel();

        channel.TryApplySettings("新名稱", "歡迎", WebsiteBrandColor.Plum, WebsiteLauncherPosition.BottomLeft, 2, T0.AddMinutes(1))
            .ShouldBeFalse();
        channel.DisplayName.ShouldBe("安心客服");
        channel.Revision.ShouldBe(1);

        channel.TryApplySettings("新名稱", "歡迎", WebsiteBrandColor.Plum, WebsiteLauncherPosition.BottomLeft, 1, T0.AddMinutes(1))
            .ShouldBeTrue();
        channel.DisplayName.ShouldBe("新名稱");
        channel.BrandColor.ShouldBe(WebsiteBrandColor.Plum);
        channel.Position.ShouldBe(WebsiteLauncherPosition.BottomLeft);
        channel.Revision.ShouldBe(2);
        channel.UpdatedAt.ShouldBe(T0.AddMinutes(1));
    }

    [Fact]
    public void Publish_pause_resume_and_unpublish_keep_the_settings_and_not_the_revision()
    {
        var channel = NewChannel();
        var publisher = Guid.CreateVersion7();

        channel.Publish(publisher, T0.AddMinutes(1)).ShouldBeTrue();
        channel.State.ShouldBe(WebsiteChannelState.Published);
        channel.PublishedAt.ShouldBe(T0.AddMinutes(1));
        channel.PublishedByAccountId.ShouldBe(publisher);
        channel.Publish(publisher, T0.AddMinutes(2)).ShouldBeFalse();

        channel.SetPaused(true, T0.AddMinutes(3)).ShouldBeTrue();
        channel.State.ShouldBe(WebsiteChannelState.Paused);
        channel.SetPaused(true, T0.AddMinutes(4)).ShouldBeFalse();

        // Publishing a paused channel resumes it, keeping the original publication.
        channel.Publish(publisher, T0.AddMinutes(5)).ShouldBeTrue();
        channel.State.ShouldBe(WebsiteChannelState.Published);
        channel.PublishedAt.ShouldBe(T0.AddMinutes(1));

        channel.Unpublish(T0.AddMinutes(6)).ShouldBeTrue();
        channel.State.ShouldBe(WebsiteChannelState.Draft);
        channel.PublishedAt.ShouldBeNull();
        channel.PublishedByAccountId.ShouldBeNull();
        channel.Unpublish(T0.AddMinutes(7)).ShouldBeFalse();

        channel.DisplayName.ShouldBe("安心客服");
        channel.Revision.ShouldBe(1);
    }

    [Fact]
    public void A_draft_cannot_be_paused_or_resumed()
    {
        var channel = NewChannel();

        Should.Throw<InvalidOperationException>(() => channel.SetPaused(true, T0));
        Should.Throw<InvalidOperationException>(() => channel.SetPaused(false, T0));
    }

    [Theory]
    [InlineData("", "歡迎")]
    [InlineData("名稱", " ")]
    public void Blank_text_is_refused(string displayName, string welcomeMessage)
    {
        Should.Throw<ArgumentException>(() => new AssistantWebsiteChannel(
            NewAssistant(), displayName, welcomeMessage, WebsiteBrandColor.Forest, WebsiteLauncherPosition.BottomRight, T0));
    }

    [Fact]
    public void Text_over_the_limits_is_refused()
    {
        Should.Throw<ArgumentException>(() => new AssistantWebsiteChannel(
            NewAssistant(), new string('名', AssistantWebsiteChannel.DisplayNameMaxLength + 1), "歡迎",
            WebsiteBrandColor.Forest, WebsiteLauncherPosition.BottomRight, T0));
        Should.Throw<ArgumentException>(() => new AssistantWebsiteChannel(
            NewAssistant(), "名稱", new string('歡', AssistantWebsiteChannel.WelcomeMessageMaxLength + 1),
            WebsiteBrandColor.Forest, WebsiteLauncherPosition.BottomRight, T0));
    }

    [Fact]
    public void A_domain_belongs_to_its_channel_and_must_be_lower_case()
    {
        var channel = NewChannel();
        var domain = new AssistantWebsiteDomain(channel, "shop.example.com", T0);

        domain.AssistantId.ShouldBe(channel.AssistantId);
        domain.OrganizationId.ShouldBe(channel.OrganizationId);
        domain.AddedAt.ShouldBe(T0);
        domain.LastSeenAt.ShouldBeNull();
        Should.Throw<ArgumentException>(() => new AssistantWebsiteDomain(channel, "Shop.example.com", T0));
        Should.Throw<ArgumentException>(() => new AssistantWebsiteDomain(channel, " ", T0));
    }

    [Fact]
    public void Being_seen_moves_last_seen_forward_only()
    {
        var domain = new AssistantWebsiteDomain(NewChannel(), "shop.example.com", T0);

        domain.MarkSeen(T0.AddMinutes(5)).ShouldBeTrue();
        domain.LastSeenAt.ShouldBe(T0.AddMinutes(5));
        domain.MarkSeen(T0.AddMinutes(1)).ShouldBeFalse();
        domain.MarkSeen(T0.AddMinutes(5)).ShouldBeFalse();
        domain.LastSeenAt.ShouldBe(T0.AddMinutes(5));
        domain.MarkSeen(T0.AddMinutes(9)).ShouldBeTrue();
        domain.LastSeenAt.ShouldBe(T0.AddMinutes(9));
    }
}
