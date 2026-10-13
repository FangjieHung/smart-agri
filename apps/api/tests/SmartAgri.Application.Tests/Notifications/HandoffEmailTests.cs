using Shouldly;
using SmartAgri.Application.Notifications;
using SmartAgri.Domain.Notifications;

namespace SmartAgri.Application.Tests.Notifications;

/// <summary>The 轉真人 Email (#355): only the organization, assistant, channel, local time and admin
/// link; the job kind is a valid one.</summary>
public class HandoffEmailTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    // 2026-10-13 06:05 UTC is 14:05 in Taipei.
    private static readonly DateTimeOffset At = new(2026, 10, 13, 6, 5, 0, TimeSpan.Zero);

    [Fact]
    public void A_website_Email_names_the_assistant_channel_local_time_and_admin_link()
    {
        var message = HandoffEmail.Compose("owner@shop.example.org", "安心商行", "退貨小幫手", VisitorHandoffChannel.Website, At, Taipei, "https://assistant.example.org/");

        message.To.ShouldBe("owner@shop.example.org");
        message.Subject.ShouldBe("【轉真人】退貨小幫手：官網的訪客需要專人協助");
        message.Body.ShouldContain("安心商行的助理「退貨小幫手」在官網回覆了「找不到資料時的回覆」");
        message.Body.ShouldContain("助理：退貨小幫手\n");
        message.Body.ShouldContain("管道：官網\n");
        message.Body.ShouldContain("時間：2026-10-13 14:05（Asia/Taipei）\n");
        message.Body.ShouldContain("後台：https://assistant.example.org/app/handoffs\n");
        message.Body.ShouldContain("不會留下任何資料");
        message.Body.ShouldContain("我的通知");
    }

    [Fact]
    public void A_LINE_Email_points_to_LINE_Official_Account_Manager()
    {
        var message = HandoffEmail.Compose("owner@shop.example.org", "安心商行", "退貨小幫手", VisitorHandoffChannel.Line, At, Taipei, "https://assistant.example.org");

        message.Subject.ShouldBe("【轉真人】退貨小幫手：LINE 的訪客需要專人協助");
        message.Body.ShouldContain("在 LINE 回覆了");
        message.Body.ShouldContain("管道：LINE\n");
        message.Body.ShouldContain("LINE Official Account Manager");
    }

    [Fact]
    public void Without_an_admin_origin_there_is_no_link_line()
    {
        var message = HandoffEmail.Compose("owner@shop.example.org", "安心商行", "退貨小幫手", VisitorHandoffChannel.Website, At, Taipei, adminOrigin: null);

        message.Body.ShouldNotContain("後台：");
    }

    [Fact]
    public void A_name_with_line_breaks_cannot_break_the_subject()
    {
        var message = HandoffEmail.Compose("owner@shop.example.org", "安心\r\n商行", "退貨\r\nBcc: x@example.org", VisitorHandoffChannel.Website, At, Taipei, null);

        message.Subject.ShouldNotContain("\r");
        message.Subject.ShouldNotContain("\n");
        message.Subject.ShouldBe("【轉真人】退貨 Bcc: x@example.org：官網的訪客需要專人協助");
    }

    [Fact]
    public void The_job_kind_is_a_valid_kind()
    {
        Should.NotThrow(() => Domain.Jobs.BackgroundJob.RequireValidKind(SendHandoffEmailJob.Kind));
    }
}
