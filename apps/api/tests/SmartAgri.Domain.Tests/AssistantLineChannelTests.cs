using Shouldly;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="AssistantLineChannel"/> (M5b plan §3 A, issue #229).</summary>
public class AssistantLineChannelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.CreateVersion7();

    private static Assistant NewAssistant() =>
        Assistant.Create(
            Guid.CreateVersion7(), Owner, "客服助理", "回答問題", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "找不到資料。", showCitations: true, keepConversations: false, T0);

    private static ProtectedSecret Secret(string marker, DateTimeOffset at) => new($"cipher-{marker}", marker, at);

    private static AssistantLineChannel NewChannel(Assistant? assistant = null) =>
        new(assistant ?? NewAssistant(), "@anxin-demo", "1650000000", Secret("s001", T0), Secret("t001", T0),
            AssistantLineChannel.DefaultWelcomeMessage, T0);

    private static IReadOnlyList<LineConnectionCheck> AllPassed() =>
        [.. LineConnectionCheck.All.Select(kind => new LineConnectionCheck(kind, LineConnectionCheckState.Passed, "通過。"))];

    /// <summary>A channel whose connection test passed, enabled by <see cref="Owner"/>.</summary>
    private static AssistantLineChannel PublishedChannel()
    {
        var channel = NewChannel();
        channel.RecordConnectionChecks(AllPassed(), "U" + new string('0', 32), T0.AddMinutes(1));
        channel.Publish(Owner, T0.AddMinutes(2)).ShouldBeTrue();
        return channel;
    }

    [Fact]
    public void A_new_channel_is_an_untested_draft_of_its_assistant_at_revision_1()
    {
        var assistant = NewAssistant();
        var channel = NewChannel(assistant);

        channel.AssistantId.ShouldBe(assistant.Id);
        channel.OrganizationId.ShouldBe(assistant.OrganizationId);
        channel.State.ShouldBe(LineChannelState.Draft);
        channel.Revision.ShouldBe(1);
        channel.ConnectionChecks.ShouldBeEmpty();
        channel.ConnectionCheckedAt.ShouldBeNull();
        channel.ConnectionChecksPassed.ShouldBeFalse();
        channel.BotUserId.ShouldBeNull();
        channel.PublishedAt.ShouldBeNull();
        channel.PushFallbackCount.ShouldBe(0);
        channel.WelcomeMessage.ShouldBe("您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。");
        channel.ChannelSecret.LastFour.ShouldBe("s001");
        channel.AccessToken.LastFour.ShouldBe("t001");
    }

    [Fact]
    public void Settings_apply_only_at_the_expected_revision()
    {
        var channel = NewChannel();

        channel.TryApplySettings("@other", "1650000001", null, null, "歡迎", 2, T0.AddMinutes(1)).ShouldBeFalse();
        channel.OfficialAccountId.ShouldBe("@anxin-demo");
        channel.Revision.ShouldBe(1);

        channel.TryApplySettings("@other", "1650000001", null, null, "歡迎", 1, T0.AddMinutes(1)).ShouldBeTrue();
        channel.OfficialAccountId.ShouldBe("@other");
        channel.ChannelId.ShouldBe("1650000001");
        channel.WelcomeMessage.ShouldBe("歡迎");
        channel.Revision.ShouldBe(2);
        channel.UpdatedAt.ShouldBe(T0.AddMinutes(1));
    }

    [Fact]
    public void Changing_only_the_welcome_message_keeps_the_credentials_the_test_and_the_publication()
    {
        var channel = PublishedChannel();
        var secret = channel.ChannelSecret;
        var token = channel.AccessToken;

        channel.TryApplySettings("@anxin-demo", "1650000000", null, null, "新的歡迎訊息", 1, T0.AddMinutes(3)).ShouldBeTrue();

        channel.ChannelSecret.ShouldBe(secret);
        channel.AccessToken.ShouldBe(token);
        channel.State.ShouldBe(LineChannelState.Published);
        channel.ConnectionChecksPassed.ShouldBeTrue();
        channel.ConnectionCheckedAt.ShouldBe(T0.AddMinutes(1));
        channel.BotUserId.ShouldNotBeNull();
        channel.PublishedAt.ShouldBe(T0.AddMinutes(2));
    }

    public static TheoryData<string> ConnectionChanges => ["token", "secret", "official-account-id", "channel-id"];

    [Theory]
    [MemberData(nameof(ConnectionChanges))]
    public void Any_credential_or_id_change_clears_the_test_and_sends_an_enabled_channel_back_to_draft(string change)
    {
        foreach (var paused in new[] { false, true })
        {
            var channel = PublishedChannel();
            if (paused)
            {
                channel.SetPaused(true, T0.AddMinutes(3)).ShouldBeTrue();
            }

            var later = T0.AddMinutes(4);
            channel.TryApplySettings(
                    change == "official-account-id" ? "@new-account" : "@anxin-demo",
                    change == "channel-id" ? "1650000009" : "1650000000",
                    change == "secret" ? Secret("s002", later) : null,
                    change == "token" ? Secret("t002", later) : null,
                    AssistantLineChannel.DefaultWelcomeMessage,
                    1,
                    later)
                .ShouldBeTrue();

            channel.State.ShouldBe(LineChannelState.Draft, change);
            channel.PublishedAt.ShouldBeNull(change);
            channel.PublishedByAccountId.ShouldBeNull(change);
            channel.ConnectionChecks.ShouldBeEmpty(change);
            channel.ConnectionCheckedAt.ShouldBeNull(change);
            channel.BotUserId.ShouldBeNull(change);
            channel.ConnectionChecksPassed.ShouldBeFalse(change);
            channel.Revision.ShouldBe(2, change);
        }
    }

    [Fact]
    public void A_replaced_credential_keeps_the_other_one()
    {
        var channel = NewChannel();
        var secret = channel.ChannelSecret;

        channel.TryApplySettings("@anxin-demo", "1650000000", null, Secret("t002", T0.AddMinutes(1)), "歡迎", 1, T0.AddMinutes(1))
            .ShouldBeTrue();

        channel.ChannelSecret.ShouldBe(secret);
        channel.AccessToken.LastFour.ShouldBe("t002");
        channel.AccessToken.SetAt.ShouldBe(T0.AddMinutes(1));
    }

    [Fact]
    public void Enabling_needs_every_connection_check_passed()
    {
        var channel = NewChannel();
        Should.Throw<InvalidOperationException>(() => channel.Publish(Owner, T0));

        channel.RecordConnectionChecks(
            [
                new LineConnectionCheck(LineConnectionCheckKind.AccessToken, LineConnectionCheckState.Failed, "權杖無效。"),
                new LineConnectionCheck(LineConnectionCheckKind.WebhookEndpoint, LineConnectionCheckState.Skipped, "未執行。"),
                new LineConnectionCheck(LineConnectionCheckKind.WebhookTest, LineConnectionCheckState.Skipped, "未執行。"),
            ],
            null,
            T0.AddMinutes(1));
        channel.ConnectionChecksPassed.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => channel.Publish(Owner, T0));

        // Only two of the three checks is not "all passed" either.
        channel.RecordConnectionChecks([.. AllPassed().Take(2)], null, T0.AddMinutes(2));
        channel.ConnectionChecksPassed.ShouldBeFalse();

        channel.RecordConnectionChecks(AllPassed(), "U" + new string('a', 32), T0.AddMinutes(3));
        channel.ConnectionChecksPassed.ShouldBeTrue();
        channel.Publish(Owner, T0.AddMinutes(4)).ShouldBeTrue();
        channel.State.ShouldBe(LineChannelState.Published);
        channel.PublishedByAccountId.ShouldBe(Owner);
        channel.Revision.ShouldBe(1);
    }

    [Fact]
    public void Pending_is_never_stored()
    {
        var channel = NewChannel();
        Should.Throw<ArgumentException>(() => channel.RecordConnectionChecks(
            [new LineConnectionCheck(LineConnectionCheckKind.AccessToken, LineConnectionCheckState.Pending, "尚未測試。")],
            null,
            T0));
        Should.Throw<ArgumentException>(() => channel.RecordConnectionChecks([], null, T0));
    }

    [Fact]
    public void Pause_resume_and_unpublish_keep_the_settings_the_test_and_the_revision()
    {
        var channel = PublishedChannel();

        channel.SetPaused(true, T0.AddMinutes(3)).ShouldBeTrue();
        channel.State.ShouldBe(LineChannelState.Paused);
        channel.SetPaused(true, T0.AddMinutes(3)).ShouldBeFalse();
        channel.Publish(Owner, T0.AddMinutes(4)).ShouldBeTrue();
        channel.State.ShouldBe(LineChannelState.Published);
        channel.PublishedAt.ShouldBe(T0.AddMinutes(2));

        channel.Unpublish(T0.AddMinutes(5)).ShouldBeTrue();
        channel.State.ShouldBe(LineChannelState.Draft);
        channel.PublishedAt.ShouldBeNull();
        channel.ConnectionChecksPassed.ShouldBeTrue();
        channel.Unpublish(T0.AddMinutes(6)).ShouldBeFalse();
        channel.Revision.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => channel.SetPaused(true, T0));
    }

    [Fact]
    public void Push_fallbacks_are_counted_per_month()
    {
        var channel = NewChannel();
        channel.PushFallbackCountIn("2026-10").ShouldBe(0);

        channel.RecordPushFallback("2026-10");
        channel.RecordPushFallback("2026-10");
        channel.PushFallbackCountIn("2026-10").ShouldBe(2);
        channel.PushFallbackCountIn("2026-11").ShouldBe(0);

        channel.RecordPushFallback("2026-11");
        channel.PushFallbackMonth.ShouldBe("2026-11");
        channel.PushFallbackCountIn("2026-11").ShouldBe(1);
        channel.PushFallbackCountIn("2026-10").ShouldBe(0);
    }
}
