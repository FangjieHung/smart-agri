using System.Text;
using Shouldly;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Line;

/// <summary>Reading a verified webhook delivery leniently (M5b plan §3 C–D, #231), with LINE's
/// documented event shapes.</summary>
public class LineWebhookPayloadTests
{
    [Fact]
    public void The_verify_request_has_a_destination_and_no_events()
    {
        var payload = Parse(LineWebhookSignatureTests.VerifyBody).ShouldNotBeNull();

        payload.Destination.ShouldBe("U0123456789abcdef0123456789abcdef");
        payload.Events.ShouldBeEmpty();
    }

    [Fact]
    public void A_text_message_in_a_group_that_mentions_the_bot()
    {
        var payload = Parse("""
            {"destination":"Uaaaa","events":[{
              "type":"message","mode":"active","timestamp":1727000000000,
              "source":{"type":"group","groupId":"Cgroup","userId":"Uuser"},
              "webhookEventId":"01J0000000000000000000000A",
              "deliveryContext":{"isRedelivery":true},
              "replyToken":"reply-1",
              "message":{"id":"468789577898262530","type":"text","quoteToken":"q","text":"@安心客服 退貨期限？",
                "mention":{"mentionees":[{"index":0,"length":5,"type":"user","userId":"Ubot","isSelf":true}]}}
            }]}
            """).ShouldNotBeNull();

        var lineEvent = payload.Events.ShouldHaveSingleItem();
        lineEvent.Type.ShouldBe("message");
        lineEvent.WebhookEventId.ShouldBe("01J0000000000000000000000A");
        lineEvent.IsRedelivery.ShouldBeTrue();
        lineEvent.Timestamp.ShouldBe(1727000000000);
        lineEvent.IsStandby.ShouldBeFalse();
        lineEvent.ReplyToken.ShouldBe("reply-1");
        lineEvent.Source.IsOneToOne.ShouldBeFalse();
        lineEvent.ChatId.ShouldBe("Cgroup");
        var message = lineEvent.Message.ShouldNotBeNull();
        message.IsText.ShouldBeTrue();
        message.Id.ShouldBe("468789577898262530");
        message.Text.ShouldBe("@安心客服 退貨期限？");
        message.MentionsSelf.ShouldBeTrue();
        message.SelfMentions.ShouldBe([new LineTextSpan(0, 5)]);
        message.QuestionText.ShouldBe("退貨期限？");
    }

    [Theory]
    [InlineData("@安心客服 退貨期限？", """[{"index":0,"length":5,"type":"user","isSelf":true}]""", "退貨期限？")]
    [InlineData("請問 @安心客服 退貨期限？", """[{"index":3,"length":5,"type":"user","isSelf":true}]""", "請問 退貨期限？")]
    [InlineData("退貨期限？@安心客服", """[{"index":5,"length":5,"type":"user","isSelf":true}]""", "退貨期限？")]
    // Another member's mention stays; only the bot's is removed.
    [InlineData("@小明 @安心客服 運費誰付？", """[{"index":0,"length":3,"type":"user","userId":"Uxm"},{"index":4,"length":5,"type":"user","isSelf":true}]""", "@小明 運費誰付？")]
    // The bot mentioned twice, listed out of order.
    [InlineData("@安心客服 營業時間？ @安心客服", """[{"index":12,"length":5,"type":"user","isSelf":true},{"index":0,"length":5,"type":"user","isSelf":true}]""", "營業時間？")]
    // A span outside the text, or without a position, removes nothing.
    [InlineData("營業時間？", """[{"index":40,"length":5,"type":"user","isSelf":true}]""", "營業時間？")]
    [InlineData("@安心客服 營業時間？", """[{"type":"user","isSelf":true}]""", "@安心客服 營業時間？")]
    // A surrogate pair before the mention: positions are UTF-16 code units.
    [InlineData("🍎 @安心客服 價格？", """[{"index":3,"length":5,"type":"user","isSelf":true}]""", "🍎 價格？")]
    public void The_question_is_the_text_without_the_bots_own_mentions(string text, string mentionees, string expected)
    {
        var payload = Parse(
            """{"events":[{"type":"message","source":{"type":"group","groupId":"Cg"},"message":{"id":"1","type":"text","text":"""
            + System.Text.Json.JsonSerializer.Serialize(text) + ""","mention":{"mentionees":""" + mentionees + "}}}]}")
            .ShouldNotBeNull();

        var message = payload.Events.ShouldHaveSingleItem().Message.ShouldNotBeNull();
        message.MentionsSelf.ShouldBeTrue();
        message.QuestionText.ShouldBe(expected);
    }

    [Fact]
    public void A_mention_only_message_has_an_empty_question_and_a_one_to_one_text_is_its_own_question()
    {
        var payload = Parse("""
            {"events":[
              {"type":"message","source":{"type":"group","groupId":"Cg"},"message":{"id":"1","type":"text","text":"@安心客服","mention":{"mentionees":[{"index":0,"length":5,"type":"user","isSelf":true}]}}},
              {"type":"message","source":{"type":"user","userId":"Uu"},"message":{"id":"2","type":"text","text":"  退貨期限？  "}}
            ]}
            """).ShouldNotBeNull();

        payload.Events[0].Message!.QuestionText.ShouldBe(string.Empty);
        payload.Events[1].Message!.MentionsSelf.ShouldBeFalse();
        payload.Events[1].Message!.QuestionText.ShouldBe("退貨期限？");
    }

    [Fact]
    public void Non_text_messages_have_no_text_and_sources_name_their_chat()
    {
        var payload = Parse("""
            {"destination":"Uaaaa","events":[
              {"type":"message","source":{"type":"user","userId":"Uuser"},"message":{"id":"1","type":"sticker","packageId":"1","stickerId":"1"}},
              {"type":"follow","source":{"type":"user","userId":"Uuser"},"replyToken":"r","follow":{"isUnblocked":false}},
              {"type":"join","source":{"type":"room","roomId":"Rroom"},"replyToken":"r"},
              {"type":"unsend","source":{"type":"user","userId":"Uuser"},"unsend":{"messageId":"325708"}},
              {"type":"message","mode":"standby","source":{"type":"user","userId":"Uuser"},"message":{"id":"2","type":"text","text":"hi"}}
            ]}
            """).ShouldNotBeNull();

        payload.Events.Count.ShouldBe(5);
        payload.Events[0].Message!.IsText.ShouldBeFalse();
        payload.Events[0].Message!.Text.ShouldBeNull();
        payload.Events[0].Source.IsOneToOne.ShouldBeTrue();
        payload.Events[0].ChatId.ShouldBe("Uuser");
        payload.Events[0].IsRedelivery.ShouldBeFalse();
        payload.Events[1].Type.ShouldBe("follow");
        payload.Events[2].ChatId.ShouldBe("Rroom");
        payload.Events[3].UnsentMessageId.ShouldBe("325708");
        payload.Events[4].IsStandby.ShouldBeTrue();
        payload.Events[4].Message!.MentionsSelf.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_events_and_properties_are_kept_or_ignored_never_an_error()
    {
        var payload = Parse("""
            {"destination":"Uaaaa","future":{"x":1},"events":[
              {"type":"somethingNew","source":{"type":"spaceship","id":"S1"},"payload":[1,2,3]},
              42,
              {"source":"not an object","message":"nor this","deliveryContext":{"isRedelivery":"yes"},"timestamp":"soon"}
            ]}
            """).ShouldNotBeNull();

        payload.Events.Count.ShouldBe(2);
        payload.Events[0].Type.ShouldBe("somethingNew");
        payload.Events[0].ChatId.ShouldBeNull();
        payload.Events[1].Type.ShouldBeNull();
        payload.Events[1].Message.ShouldBeNull();
        payload.Events[1].IsRedelivery.ShouldBeFalse();
        payload.Events[1].Timestamp.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"destination\":")]
    public void A_body_that_is_not_a_json_object_is_null(string body)
    {
        Parse(body).ShouldBeNull();
    }

    [Fact]
    public void Missing_or_non_array_events_are_no_events()
    {
        Parse("""{"destination":"U1"}""")!.Events.ShouldBeEmpty();
        Parse("""{"events":{}}""")!.Events.ShouldBeEmpty();
        Parse("""{"events":[]}""")!.Destination.ShouldBeNull();
    }

    [Fact]
    public void Logging_an_event_shows_no_ids_token_or_text()
    {
        var lineEvent = Parse("""
            {"events":[{"type":"message","replyToken":"secret-reply-token","webhookEventId":"01JEVENT",
              "source":{"type":"user","userId":"Usecretuser"},"message":{"id":"m1","type":"text","text":"我的電話是 0912"}}]}
            """)!.Events[0];

        var text = $"{lineEvent} {new LineChatKey(Guid.Empty, "Usecretuser")}";

        text.ShouldContain("message");
        foreach (var hidden in new[] { "secret-reply-token", "01JEVENT", "Usecretuser", "m1", "0912" })
        {
            text.ShouldNotContain(hidden);
        }
    }

    private static LineWebhookPayload? Parse(string body) => LineWebhookPayload.TryParse(Encoding.UTF8.GetBytes(body));
}
