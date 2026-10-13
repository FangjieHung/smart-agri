using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Notifications;

namespace SmartAgri.Domain.Tests;

public class VisitorHandoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_handoff_holds_the_assistant_channel_and_times_and_nothing_of_the_visitor()
    {
        var organization = Guid.CreateVersion7();
        var assistant = Guid.CreateVersion7();

        var handoff = VisitorHandoff.Open(organization, assistant, VisitorHandoffChannel.Line, Now);

        handoff.Id.ShouldNotBe(Guid.Empty);
        (handoff.OrganizationId, handoff.AssistantId, handoff.Channel, handoff.CreatedAt).ShouldBe((organization, assistant, VisitorHandoffChannel.Line, Now));
        handoff.IsPending.ShouldBeTrue();

        // #355: no visitor content and no visitor id — not even a string property to put one in.
        typeof(VisitorHandoff).GetProperties().Where(property => property.PropertyType == typeof(string)).ShouldBeEmpty();
        typeof(VisitorHandoff).GetProperties().Select(property => property.Name).ShouldBe(
            ["Id", "OrganizationId", "AssistantId", "Channel", "CreatedAt", "HandledAt", "HandledByAccountId", "EmailSentAt", "IsPending"],
            ignoreOrder: true);
    }

    [Fact]
    public void Marking_handled_twice_keeps_the_first_account_and_time()
    {
        var handoff = VisitorHandoff.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), VisitorHandoffChannel.Website, Now);
        var first = Guid.CreateVersion7();

        handoff.MarkHandled(first, Now.AddMinutes(5)).ShouldBeTrue();
        handoff.MarkHandled(Guid.CreateVersion7(), Now.AddMinutes(9)).ShouldBeFalse();

        handoff.IsPending.ShouldBeFalse();
        handoff.HandledByAccountId.ShouldBe(first);
        handoff.HandledAt.ShouldBe(Now.AddMinutes(5));
        Should.Throw<ArgumentException>(() => handoff.MarkHandled(Guid.Empty, Now));
    }

    [Fact]
    public void The_Email_is_recorded_once()
    {
        var handoff = VisitorHandoff.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), VisitorHandoffChannel.Website, Now);

        handoff.RecordEmailSent(Now.AddMinutes(1));
        handoff.RecordEmailSent(Now.AddMinutes(2));

        handoff.EmailSentAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void Every_id_is_required_and_the_channel_must_exist()
    {
        Should.Throw<ArgumentException>(() => VisitorHandoff.Open(Guid.Empty, Guid.CreateVersion7(), VisitorHandoffChannel.Line, Now));
        Should.Throw<ArgumentException>(() => VisitorHandoff.Open(Guid.CreateVersion7(), Guid.Empty, VisitorHandoffChannel.Line, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => VisitorHandoff.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), (VisitorHandoffChannel)9, Now));
    }

    [Fact]
    public void The_channels_wire_names_are_the_answer_outcome_channels_names()
    {
        WireNames<VisitorHandoffChannel>.All.ShouldBe(["website", "line"]);
        JsonSerializer.Serialize(VisitorHandoffChannel.Line).ShouldBe("\"line\"");
    }
}

public class AccountNotificationSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_account_has_no_address_and_handoff_Emails_on()
    {
        var settings = AccountNotificationSettings.CreateDefault(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        settings.Email.ShouldBeNull();
        settings.HandoffEmails.ShouldBeTrue();
        settings.HandoffEmailAddress.ShouldBeNull("no address, no Email");
    }

    [Theory]
    [InlineData("owner@shop.example.org", "owner@shop.example.org")]
    [InlineData("  owner.name+tag@shop.example.org  ", "owner.name+tag@shop.example.org")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_valid_address_is_trimmed_and_blank_clears_it(string? input, string? expected)
    {
        AccountNotificationSettings.TryNormalizeEmail(input, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("a@b")]
    [InlineData("a@.example.org")]
    [InlineData("Owner <owner@example.org>")]
    [InlineData("\"Owner\" owner@example.org")]
    [InlineData("a@example.org,b@example.org")]
    [InlineData("a@example.org; b@example.org")]
    [InlineData("a@example.org\r\nBcc: x@example.org")]
    [InlineData("a b@example.org")]
    public void Anything_but_one_plain_address_is_refused(string input)
    {
        AccountNotificationSettings.TryNormalizeEmail(input, out _).ShouldBeFalse(input);
        var settings = AccountNotificationSettings.CreateDefault(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        Should.Throw<ArgumentException>(() => settings.Update(input, true, Now)).Message.ShouldStartWith(AccountNotificationSettings.EmailInvalidMessage);
    }

    [Fact]
    public void An_address_longer_than_254_characters_is_refused()
    {
        var address = new string('a', 64) + "@" + string.Join('.', Enumerable.Repeat(new string('b', 60), 4)) + ".org";
        address.Length.ShouldBeGreaterThan(AccountNotificationSettings.EmailMaxLength);

        AccountNotificationSettings.TryNormalizeEmail(address, out _).ShouldBeFalse();
    }

    [Fact]
    public void Opting_out_keeps_the_address_but_sends_nothing()
    {
        var settings = AccountNotificationSettings.CreateDefault(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        settings.Update("owner@shop.example.org", handoffEmails: true, Now);
        settings.HandoffEmailAddress.ShouldBe("owner@shop.example.org");
        settings.Update("owner@shop.example.org", handoffEmails: false, Now.AddMinutes(1));

        settings.Email.ShouldBe("owner@shop.example.org");
        settings.HandoffEmailAddress.ShouldBeNull();
        settings.UpdatedAt.ShouldBe(Now.AddMinutes(1));
    }
}
