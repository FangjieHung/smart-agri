using System.Text;
using System.Text.Json;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>What the 統籌助理 does with each kind of LINE event (store-coordinator design §5 Slice 1, #372).</summary>
public class CoordinatorMessageRouterTests
{
    private const string UserId = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");

    [Theory]
    [InlineData(false, CoordinatorIntentKind.Welcome)]
    [InlineData(true, CoordinatorIntentKind.Menu)]
    public void Follow_welcomes_the_unbound_and_shows_members_the_menu(bool bound, CoordinatorIntentKind expected)
    {
        Route(Event("follow", UserSource()), bound).Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_that_looks_like_a_code_is_a_bind_attempt_bound_or_not(bool bound)
    {
        var intent = Route(Text("ａｂｃｄ－２３４５"), bound);

        intent.Kind.ShouldBe(CoordinatorIntentKind.Bind);
        intent.Code.ShouldBe("ABCD2345");
        intent.ToString().ShouldNotContain("ABCD2345");
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("產生店長綁定碼")]
    [InlineData("今天臨時店休")]
    [InlineData("ABCD-234O")]
    public void The_unbound_are_asked_for_a_code_whatever_else_they_write(string text)
    {
        Route(Text(text), bound: false).Kind.ShouldBe(CoordinatorIntentKind.AskForCode);
    }

    [Theory]
    [InlineData("""{"id":"m1","type":"sticker","packageId":"446","stickerId":"1988"}""", false, CoordinatorIntentKind.AskForCode)]
    [InlineData("""{"id":"m1","type":"image"}""", false, CoordinatorIntentKind.AskForCode)]
    [InlineData("""{"id":"m1","type":"sticker","packageId":"446","stickerId":"1988"}""", true, CoordinatorIntentKind.Menu)]
    [InlineData("""{"id":"m1","type":"location","latitude":25,"longitude":121}""", true, CoordinatorIntentKind.Menu)]
    public void A_message_that_is_not_text_asks_for_a_code_or_shows_the_menu(string message, bool bound, CoordinatorIntentKind expected)
    {
        Route(Event("message", UserSource(), $$""","message":{{message}}"""), bound).Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData("產生店長綁定碼")]
    [InlineData("  產生店長綁定碼\n")]
    public void A_member_asking_for_a_manager_code(string text)
    {
        Route(Text(text), bound: true).Kind.ShouldBe(CoordinatorIntentKind.IssueManagerCode);
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("今天臨時店休")]
    [InlineData("產生 店長綁定碼")]
    [InlineData("請問營業時間")]
    public void Anything_else_from_a_member_gets_the_menu(string text)
    {
        Route(Text(text), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Menu);
    }

    [Fact]
    public void The_store_choice_postback_names_the_organization_and_needs_a_member()
    {
        var data = CoordinatorMessageRouter.ManagerCodePostback(Organization);
        var postback = Event("postback", UserSource(), $$""","postback":{"data":"{{data}}"}""");

        var bound = Route(postback, bound: true);
        bound.Kind.ShouldBe(CoordinatorIntentKind.IssueManagerCodeFor);
        bound.OrganizationId.ShouldBe(Organization);
        Route(postback, bound: false).Kind.ShouldBe(CoordinatorIntentKind.AskForCode);
    }

    [Theory]
    [InlineData("coordinator:manager-code:")]
    [InlineData("coordinator:manager-code:not-a-guid")]
    [InlineData("coordinator:manager-code:00000000000000000000000000000000")]
    [InlineData("action=buy&item=1")]
    public void Any_other_postback_is_ignored(string data)
    {
        Route(Event("postback", UserSource(), $$""","postback":{"data":"{{data}}"}"""), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Theory]
    [InlineData("""{"type":"group","groupId":"Cgroup","userId":"Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"type":"room","roomId":"Rroom","userId":"Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"type":"user"}""")]
    public void Groups_rooms_and_events_without_a_user_are_ignored(string source)
    {
        Route(Event("message", source, ""","message":{"id":"m","type":"text","text":"ABCD-2345"}"""), bound: true).Kind
            .ShouldBe(CoordinatorIntentKind.Ignore);
        Route(Event("follow", source), bound: false).Kind.ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Theory]
    [InlineData("unfollow")]
    [InlineData("unsend")]
    [InlineData("memberJoined")]
    [InlineData("videoPlayComplete")]
    public void Other_event_types_are_ignored(string type)
    {
        Route(Event(type, UserSource()), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Fact]
    public void Standby_mode_is_ignored()
    {
        Route(Event("message", UserSource(), ""","message":{"id":"m","type":"text","text":"你好"}""", mode: "standby"), bound: true).Kind
            .ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Fact]
    public void Text_is_read_as_nfkc_and_trimmed()
    {
        CoordinatorMessageRouter.NormalizeText("  ＡＢＣ　１２３ ").ShouldBe("ABC 123");
        CoordinatorMessageRouter.NormalizeText("bad\uD800").ShouldBe("bad\uD800");
    }

    private static CoordinatorIntent Route(LineWebhookEvent lineEvent, bool bound) => CoordinatorMessageRouter.Route(lineEvent, bound);

    private static string UserSource() => $$"""{"type":"user","userId":"{{UserId}}"}""";

    private static LineWebhookEvent Text(string text) =>
        Event("message", UserSource(), $$""","message":{"id":"m","type":"text","text":{{JsonSerializer.Serialize(text)}}}""");

    private static LineWebhookEvent Event(string type, string source, string extra = "", string mode = "active")
    {
        var body = $$"""
            {"destination":"Ubot","events":[{"type":"{{type}}","mode":"{{mode}}","timestamp":1727000000000,
              "source":{{source}},"webhookEventId":"01J0000000000000000000000R","replyToken":"reply"{{extra}}}]}
            """;
        return LineWebhookPayload.TryParse(Encoding.UTF8.GetBytes(body))!.Events.ShouldHaveSingleItem();
    }
}
