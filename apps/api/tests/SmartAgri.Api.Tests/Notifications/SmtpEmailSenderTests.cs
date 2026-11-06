using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Notifications;
using SmartAgri.Infrastructure.Notifications;

namespace SmartAgri.Api.Tests.Notifications;

/// <summary>
/// The SMTP sender's display name (#391) against the in-process <see cref="FakeSmtpServer"/>: a message's own sender name
/// (a shop's, for its reservation Emails) replaces <c>Smtp:FromName</c> while the address stays <c>Smtp:From</c>; a
/// message without one keeps the deployment's name (the 轉真人 Email, #355); and no line break in a name can add a header
/// or a recipient.
/// </summary>
public sealed class SmtpEmailSenderTests : IAsyncDisposable
{
    private const string From = "reserve@example.org";

    private readonly FakeSmtpServer _smtp = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _smtp.DisposeAsync();

    [Fact]
    public async Task A_messages_own_sender_name_replaces_the_deployments_and_the_address_stays()
    {
        var to = Address();

        await Sender(fromName: "AI 助理工作台").SendAsync(new EmailMessage(to, "王記麵店：訂位已確認", "內文", FromName: "王記麵店"), CancellationToken);

        var message = _smtp.To(to).ShouldHaveSingleItem();
        message.From.ShouldBe(From, "the envelope sender is the platform's");
        message.FromHeader.ShouldBe($"王記麵店 <{From}>");
        message.Header("From")!.ShouldStartWith("=?utf-8?", customMessage: "a non-ASCII name is an encoded word");
        message.Header("From")!.All(char.IsAscii).ShouldBeTrue();
        message.Subject.ShouldBe("王記麵店：訂位已確認");
    }

    [Fact]
    public async Task Without_a_name_of_its_own_a_message_keeps_the_deployments_or_the_default()
    {
        var configured = Address();
        var fallback = Address();

        await Sender(fromName: "王記通知").SendAsync(new EmailMessage(configured, "主旨", "內文"), CancellationToken);
        await Sender(fromName: null).SendAsync(new EmailMessage(fallback, "主旨", "內文", FromName: " \t "), CancellationToken);

        _smtp.To(configured).ShouldHaveSingleItem().FromHeader.ShouldBe($"王記通知 <{From}>");
        _smtp.To(fallback).ShouldHaveSingleItem().FromHeader.ShouldBe($"AI 助理工作台 <{From}>");
    }

    [Theory]
    [InlineData("王記麵店\r\nBcc: {0}", "王記麵店 Bcc: {0}")]
    [InlineData("Wang Ji\r\nBcc: {0}", "Wang Ji Bcc: {0}")]
    [InlineData("Wang\nJi\r\n\r\nInjected body", "Wang Ji Injected body")]
    [InlineData("\"Wang\" \t Ji\u0000", "\"Wang\" Ji")]
    public async Task A_line_break_in_the_name_cannot_add_a_header_or_a_recipient(string name, string shown)
    {
        var to = Address();
        var victim = Address();

        await Sender(fromName: null).SendAsync(
            new EmailMessage(to, "主旨", "內文", FromName: string.Format(System.Globalization.CultureInfo.InvariantCulture, name, victim)), CancellationToken);

        var message = _smtp.To(to).ShouldHaveSingleItem();
        message.Recipients.ShouldBe([to]);
        _smtp.To(victim).ShouldBeEmpty();
        var headers = message.Data[..message.Data.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        headers.Split("\r\n").ShouldNotContain(line => line.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Injected", StringComparison.Ordinal));
        message.Header("Bcc").ShouldBeNull();
        message.Body.ShouldBe("內文");
        Unquoted(message.FromHeader).ShouldBe($"{string.Format(System.Globalization.CultureInfo.InvariantCulture, shown, victim)} <{From}>");
    }

    [Fact]
    public void The_display_name_is_one_trimmed_line_or_nothing()
    {
        SmtpEmailSender.DisplayName(null).ShouldBeNull();
        SmtpEmailSender.DisplayName(" \r\n\t").ShouldBeNull();
        SmtpEmailSender.DisplayName(" 王記\r\n 麵店 ").ShouldBe("王記 麵店");
        SmtpEmailSender.DisplayName("王記\u0007麵店").ShouldBe("王記 麵店");
    }

    private SmtpEmailSender Sender(string? fromName) => new(Options.Create(new SmtpOptions
    {
        Host = "127.0.0.1",
        Port = _smtp.Port,
        From = From,
        FromName = fromName,
        UseStartTls = false,
    }));

    private static string Address() => $"guest-{Guid.NewGuid():N}@example.com";

    /// <summary>An ASCII display name may be quoted (<c>"Wang Ji Bcc: x" &lt;a@b&gt;</c>), its own quotes escaped: the
    /// name as a person reads it.</summary>
    private static string Unquoted(string header)
    {
        var start = header.LastIndexOf(" <", StringComparison.Ordinal);
        var name = header[..start];
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
        {
            name = name[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return name + header[start..];
    }
}
