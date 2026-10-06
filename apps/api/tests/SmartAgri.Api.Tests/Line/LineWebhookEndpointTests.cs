using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Line;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Line;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Line;

/// <summary>
/// The LINE webhook endpoint and its background processor (M5b plan §3 C–E, §5 Slice 3, issue #231)
/// against real PostgreSQL and the host's fake LINE server. Every test adds its own assistant, LINE
/// channel and fake bot, so its replies and recorded requests are its own.
/// </summary>
/// <remarks>
/// The processor handles a delivery's events one after another, so a test that expects nothing to
/// happen ends its delivery with a <em>sentinel</em>: an <c>unfollow</c> of a chat whose history the
/// test seeded. Once that history is gone, every earlier event of the delivery has been handled.
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public class LineWebhookEndpointTests : IClassFixture<LineWebhookHostFixture>
{
    private const string Password = "Line-Webhook-Pass-1!";

    /// <summary>The secret <see cref="LineWebhookSignatureTests"/>' openssl values were computed with.</summary>
    private const string Secret = "8c9f1d2e3a4b5c6d7e8f90a1b2c3d4e5";

    private const string VerifyBody = """{"destination":"U0123456789abcdef0123456789abcdef","events":[]}""";
    private const string VerifySignature = "KqxlZTTVv23r68dKJUaSc9MAlERzNasoe0r1LCO003o=";
    private const string WelcomeMessage = "歡迎加入安心農場！有問題請直接問我。";

    private readonly LineWebhookHostFixture _host;
    private readonly HttpClient _client;

    public LineWebhookEndpointTests(LineWebhookHostFixture host)
    {
        _host = host;
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private FakeLineServer Line => _host.Line;

    private ILineConversationHistory History => _host.Factory.Services.GetRequiredService<ILineConversationHistory>();

    // --- Signature and refusals -------------------------------------------------------------------

    [Fact]
    public async Task The_signature_is_verified_over_the_raw_body_and_one_changed_byte_is_401()
    {
        // The bot of LINE's sample body, so the openssl-computed signature applies as is.
        var bot = new LineBot(NewToken(), "U0123456789abcdef0123456789abcdef", "@anxin-demo", "安心農場");
        Line.AddBot(bot);
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        LineWebhookSignature.Compute(Secret, Encoding.UTF8.GetBytes(VerifyBody)).ShouldBe(VerifySignature);

        var verify = await PostAsync(channel.AssistantId, VerifyBody, VerifySignature);

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await verify.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();

        var bytes = Encoding.UTF8.GetBytes(VerifyBody);
        foreach (var index in new[] { 0, 20, bytes.Length - 2, bytes.Length - 1 })
        {
            var changed = (byte[])bytes.Clone();
            changed[index] ^= 0x01;
            var response = await PostAsync(channel.AssistantId, changed, VerifySignature);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"byte {index} changed");
        }

        (await PostAsync(channel.AssistantId, VerifyBody + "\n", VerifySignature)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        Line.RequestsWith(bot.AccessToken).ShouldBeEmpty();
    }

    [Fact]
    public async Task No_assistant_no_channel_an_undecryptable_secret_and_a_bad_or_missing_signature_get_the_same_401()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var withoutChannel = await CreateAssistantAsync(await CreateOrganizationAsync());
        var unreadableSecret = await CreateChannelAsync(Line.AddBot("@anxin-other"), LineChannelState.Published, unreadableSecret: true);
        var body = Body(bot.UserId, Follow("U" + new string('1', 32), "reply-token-refused"));
        var signature = Sign(body);

        var refusals = new (string Case, HttpResponseMessage Response)[]
        {
            ("no such assistant", await PostAsync(Guid.NewGuid(), body, signature)),
            ("not a GUID", await PostAsync("not-an-assistant", body, signature)),
            ("no LINE channel", await PostAsync(withoutChannel.AssistantId, body, signature)),
            ("secret not decryptable", await PostAsync(unreadableSecret.AssistantId, body, signature)),
            ("wrong signature", await PostAsync(channel.AssistantId, body, Sign(body, "0000000000000000000000000000beef"))),
            ("missing signature", await PostAsync(channel.AssistantId, body, signature: null)),
            ("malformed signature", await PostAsync(channel.AssistantId, body, "not base64")),
        };

        var expected = await FingerprintAsync(refusals[0].Response);
        expected.ShouldStartWith("401\n");
        foreach (var (name, response) in refusals)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
            (await FingerprintAsync(response)).ShouldBe(expected, name);
        }

        (await PostAsync(channel.AssistantId, body, signature)).StatusCode.ShouldBe(HttpStatusCode.OK, "the same body, correctly signed");
    }

    [Fact]
    public async Task A_body_over_one_megabyte_is_413_before_anything_is_looked_up()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var body = new byte[LineWebhookEndpoints.MaxBodyBytes + 1];
        body.AsSpan().Fill((byte)' ');

        var response = await PostAsync(channel.AssistantId, body, LineWebhookSignature.Compute(Secret, body));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    // --- Destination, deduplication, the queue -------------------------------------------------------

    [Fact]
    public async Task Events_for_another_destination_are_ignored_with_200_and_not_remembered_as_seen()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var follow = Follow("U" + new string('2', 32), "reply-token-destination", eventId: "01JDEST000000000000000000A");

        var mismatched = await PostAsync(channel.AssistantId, Body("U" + new string('f', 32), follow), sign: true);

        mismatched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sentinel = NewSentinel(channel.AssistantId);
        (await PostAsync(channel.AssistantId, Body(bot.UserId, sentinel.Event), sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProcessedAsync(sentinel);
        Replies(bot).ShouldBeEmpty();

        // The same event to the right bot is handled: the ignored one was not marked as seen.
        (await PostAsync(channel.AssistantId, Body(bot.UserId, follow), sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EventuallyAsync(() => Replies(bot).Count == 1, "the welcome reply");
    }

    [Fact]
    public async Task The_same_webhook_event_id_is_handled_once_also_when_redelivered()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var userId = "U" + new string('3', 32);
        var original = Body(bot.UserId, Follow(userId, "reply-token-dedupe", eventId: "01JDEDUPE0000000000000000A"));

        (await PostAsync(channel.AssistantId, original, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EventuallyAsync(() => Replies(bot).Count == 1, "the welcome reply");

        // The very same request again, then LINE's redelivery (isRedelivery, same id and reply token).
        (await PostAsync(channel.AssistantId, original, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var sentinel = NewSentinel(channel.AssistantId);
        var redelivery = Body(
            bot.UserId,
            Follow(userId, "reply-token-dedupe", eventId: "01JDEDUPE0000000000000000A", redelivery: true),
            sentinel.Event);
        (await PostAsync(channel.AssistantId, redelivery, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProcessedAsync(sentinel);

        Replies(bot).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_endpoint_answers_200_at_once_while_the_processor_is_slow()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Questions.Behaviors[channel.AssistantId] = cancellationToken => release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        (await PostAsync(channel.AssistantId, Body(bot.UserId), sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK, "warm-up");

        var stopwatch = Stopwatch.StartNew();
        var response = await PostAsync(
            channel.AssistantId, Body(bot.UserId, Text(UserSource("U" + new string('4', 32)), "退貨期限是幾天？", "reply-token-slow")), sign: true);
        stopwatch.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "LINE expects 200 within about 2 seconds");
        await EventuallyAsync(() => _host.Questions.Started(channel.AssistantId) == 1, "the slow handler to start");
        _host.Questions.Finished(channel.AssistantId).ShouldBe(0, "the response came while the handler was still running");

        release.SetResult();
        await EventuallyAsync(() => _host.Questions.Finished(channel.AssistantId) == 1, "the slow handler to finish");
    }

    // --- Draft, follow/join, non-text, history, unknown ---------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_draft_channel_answers_the_webhook_test_with_200_and_replies_to_nothing(bool tested)
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Draft, tested: tested);

        // LINE's webhook test and Console's Verify: signed, no events. Before the first connection test
        // has passed there is no bot user id stored yet, so the destination cannot match; still 200.
        var test = await PostAsync(channel.AssistantId, Body(bot.UserId), sign: true);
        test.StatusCode.ShouldBe(HttpStatusCode.OK);

        var userId = "U" + new string('5', 32);
        var sentinel = NewSentinel(channel.AssistantId);
        var events = await PostAsync(
            channel.AssistantId,
            Body(
                bot.UserId,
                Follow(userId, "reply-token-draft-follow"),
                Message(UserSource(userId), """{"id":"m-draft-1","type":"image"}""", "reply-token-draft-image"),
                Text(UserSource(userId), "請問營業時間？", "reply-token-draft-text"),
                sentinel.Event),
            sign: true);

        events.StatusCode.ShouldBe(HttpStatusCode.OK);
        if (tested)
        {
            await ProcessedAsync(sentinel);
        }
        else
        {
            // Never tested: the events were ignored at the door (destination), the sentinel too.
            History.Get(sentinel.Chat).Count.ShouldBe(1);
        }

        Line.RequestsWith(bot.AccessToken).ShouldBeEmpty();
        _host.Questions.Started(channel.AssistantId).ShouldBe(0);
    }

    [Fact]
    public async Task Follow_and_join_reply_with_the_welcome_message_using_each_reply_token_once_and_write_nothing()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var before = await ChannelRowAsync(channel);

        var response = await PostAsync(
            channel.AssistantId,
            Body(
                bot.UserId,
                Follow("U" + new string('6', 32), "reply-token-follow"),
                Event("join", """{"type":"group","groupId":"Cgroup000000000000000000000000001"}""", "reply-token-join", null)),
            sign: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await EventuallyAsync(() => Replies(bot).Count == 2, "two welcome replies");
        var replies = Replies(bot);
        replies.Select(reply => reply.Json.GetProperty("replyToken").GetString()).Order().ShouldBe(["reply-token-follow", "reply-token-join"]);
        foreach (var reply in replies)
        {
            var messages = reply.Json.GetProperty("messages");
            messages.GetArrayLength().ShouldBe(1);
            messages[0].GetProperty("type").GetString().ShouldBe("text");
            messages[0].GetProperty("text").GetString().ShouldBe(WelcomeMessage);
        }

        // The channel row is only read.
        (await ChannelRowAsync(channel)).ShouldBe(before);
    }

    [Fact]
    public async Task A_paused_channel_greets_nobody()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Paused);
        var sentinel = NewSentinel(channel.AssistantId);

        await PostAsync(channel.AssistantId, Body(bot.UserId, Follow("U" + new string('7', 32), "reply-token-paused"), sentinel.Event), sign: true);

        await ProcessedAsync(sentinel);
        Replies(bot).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_non_text_message_gets_the_fixed_reply_in_a_one_to_one_chat_and_nothing_in_a_group_or_room()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var sentinel = NewSentinel(channel.AssistantId);

        var response = await PostAsync(
            channel.AssistantId,
            Body(
                bot.UserId,
                Message(UserSource("U" + new string('8', 32)), """{"id":"m-image","type":"image","contentProvider":{"type":"line"}}""", "reply-token-image"),
                Message(GroupSource("Cgroup000000000000000000000000002", "U" + new string('8', 32)), """{"id":"m-sticker","type":"sticker","packageId":"1","stickerId":"2"}""", "reply-token-group"),
                Message("""{"type":"room","roomId":"Rroom0000000000000000000000000001"}""", """{"id":"m-audio","type":"audio","duration":1000}""", "reply-token-room"),
                sentinel.Event),
            sign: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProcessedAsync(sentinel);
        var reply = Replies(bot).ShouldHaveSingleItem();
        reply.Json.GetProperty("replyToken").GetString().ShouldBe("reply-token-image");
        reply.Json.GetProperty("messages")[0].GetProperty("text").GetString().ShouldBe("目前只能回答文字問題。");
    }

    [Fact]
    public async Task Unfollow_and_leave_forget_the_chat_and_unsend_forgets_that_message()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var user = new LineChatKey(channel.AssistantId, "U" + new string('9', 32));
        var group = new LineChatKey(channel.AssistantId, "Cgroup000000000000000000000000003");
        var room = new LineChatKey(channel.AssistantId, "Rroom0000000000000000000000000003");
        History.Append(user, "m1", Turn(ConversationAuthor.Account, "退貨期限？"));
        History.Append(user, "m1", Turn(ConversationAuthor.Assistant, "七天內。"));
        History.Append(user, "m2", Turn(ConversationAuthor.Account, "運費？"));
        History.Append(group, "m3", Turn(ConversationAuthor.Account, "營業時間？"));
        History.Append(room, "m4", Turn(ConversationAuthor.Account, "地址？"));
        var sentinel = NewSentinel(channel.AssistantId);

        var response = await PostAsync(
            channel.AssistantId,
            Body(
                bot.UserId,
                Event("unsend", UserSource(user.ChatId), null, ""","unsend":{"messageId":"m1"}"""),
                Event("leave", $$"""{"type":"group","groupId":"{{group.ChatId}}"}""", null, null),
                sentinel.Event),
            sign: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProcessedAsync(sentinel);
        History.Get(user).Select(entry => entry.Turn.Text).ShouldBe(["運費？"]);
        History.Get(group).ShouldBeEmpty();
        History.Get(room).Count.ShouldBe(1);
        Replies(bot).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_ignored_and_standby_events_do_nothing_and_raise_no_error()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var userId = "U" + new string('a', 32);
        var sentinel = NewSentinel(channel.AssistantId);

        var response = await PostAsync(
            channel.AssistantId,
            Body(
                bot.UserId,
                Event("somethingNew", UserSource(userId), "reply-token-unknown", ""","payload":{"x":[1,2,3]}"""),
                Event("postback", UserSource(userId), "reply-token-postback", ""","postback":{"data":"a=1"}"""),
                Event("memberJoined", """{"type":"group","groupId":"Cgroup000000000000000000000000004"}""", "reply-token-member", null),
                Event("follow", UserSource(userId), "reply-token-standby", null, mode: "standby"),
                """{"type":"message"}""",
                "42",
                sentinel.Event),
            sign: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProcessedAsync(sentinel);
        Line.RequestsWith(bot.AccessToken).ShouldBeEmpty();
        _host.Questions.Started(channel.AssistantId).ShouldBe(0);
    }

    [Fact]
    public async Task A_text_message_is_handed_to_the_question_handler_of_a_published_or_paused_channel()
    {
        var bot = Line.AddBot("@anxin-demo");
        var channel = await CreateChannelAsync(bot, LineChannelState.Published);
        var userId = "U" + new string('b', 32);

        var response = await PostAsync(
            channel.AssistantId,
            Body(bot.UserId, Text(UserSource(userId), "退貨期限是幾天？", "reply-token-question", messageId: "m-question")),
            sign: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await EventuallyAsync(() => _host.Questions.Finished(channel.AssistantId) == 1, "the question handler");
        var context = _host.Questions.Received.Single(received => received.Assistant.Id == channel.AssistantId);
        context.Chat.ShouldBe(new LineChatKey(channel.AssistantId, userId));
        context.AccessToken.ShouldBe(bot.AccessToken);
        context.ServingState.ShouldBe(ChannelServingState.Serving);
        context.Channel.WelcomeMessage.ShouldBe(WelcomeMessage);
        context.Event.ReplyToken.ShouldBe("reply-token-question");
        context.Event.Message!.Id.ShouldBe("m-question");
        context.Event.Message.Text.ShouldBe("退貨期限是幾天？");
        Line.RequestsWith(bot.AccessToken).ShouldBeEmpty("the default handler of #231 answers nothing; #232 replaces it");

        var pausedBot = Line.AddBot("@anxin-paused");
        var paused = await CreateChannelAsync(pausedBot, LineChannelState.Paused);
        await PostAsync(paused.AssistantId, Body(pausedBot.UserId, Text(UserSource(userId), "在嗎？", "reply-token-paused-q")), sign: true);
        await EventuallyAsync(() => _host.Questions.Finished(paused.AssistantId) == 1, "the paused channel's question");
        _host.Questions.Received.Single(received => received.Assistant.Id == paused.AssistantId)
            .ServingState.ShouldBe(ChannelServingState.Paused);
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private sealed record TestChannel(Guid OrganizationId, Guid AssistantId);

    private sealed record Sentinel(LineChatKey Chat, string Event);

    private static string NewToken() => "fake-line-token-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];

    private async Task<(Organization Organization, Guid OwnerId)> CreateOrganizationAsync()
    {
        var organization = await _host.CreateOrganizationAsync("安心農場");
        var owner = await _host.CreateAccountAsync(
            organization, "owner", Password, AccountRole.SmbAdmin, "安心農場管理者", AccountPermission.ManageAssistants, AccountPermission.ManagePublishing);
        return (organization, owner.Id);
    }

    /// <summary>An assistant with one knowledge base its owner owns and a passed acceptance run.</summary>
    private async Task<TestChannel> CreateAssistantAsync((Organization Organization, Guid OwnerId) org)
    {
        var organizationId = org.Organization.Id;
        var now = _host.Clock.GetUtcNow().AddHours(-2);
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        var assistant = Assistant.Create(
            organizationId, org.OwnerId, "客服助理", "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: false, now);
        var knowledgeBase = KnowledgeBase.Create(organizationId, org.OwnerId, "客服知識庫", string.Empty, now);
        dbContext.Assistants.Add(assistant);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        dbContext.AssistantTestCases.Add(new AssistantTestCase(
            organizationId, assistant.Id, "退貨期限是幾天？", AssistantTestCaseCategory.Common,
            AssistantTestExpectedKind.CompanyData, [Guid.NewGuid()], null, ordinal: 1, now));
        var run = AssistantTestRun.Queue(organizationId, assistant.Id, AssistantTestRunTrigger.Manual, now);
        run.Start("test", AuthHostFixture.ChatModel, 0.3, now);
        run.Complete(1, 0, now);
        dbContext.AssistantTestRuns.Add(run);
        await dbContext.SaveChangesAsync(CancellationToken);
        return new TestChannel(organizationId, assistant.Id);
    }

    /// <summary>
    /// A LINE channel whose secret is <see cref="Secret"/> and token <paramref name="bot"/>'s, in
    /// <paramref name="state"/>: as #229/#230 leave it, set up directly. A published or paused channel
    /// (and a draft when <paramref name="tested"/>) has passed its connection test, which stored the
    /// bot user id. <paramref name="unreadableSecret"/> stores a secret that does not decrypt as a
    /// channel secret (protected under another purpose, as after a key-ring change).
    /// </summary>
    private async Task<TestChannel> CreateChannelAsync(
        LineBot bot, LineChannelState state, bool tested = true, bool unreadableSecret = false)
    {
        var org = await CreateOrganizationAsync();
        var created = await CreateAssistantAsync(org);
        var protector = _host.Factory.Services.GetRequiredService<ISecretProtector>();
        var now = _host.Clock.GetUtcNow();
        await using var dbContext = _host.Postgres.CreateDbContext(created.OrganizationId);
        var assistant = await dbContext.Assistants.SingleAsync(candidate => candidate.Id == created.AssistantId, CancellationToken);
        var channel = new AssistantLineChannel(
            assistant,
            "@anxin-demo",
            "1650000000",
            protector.Protect(
                unreadableSecret ? AssistantLineChannel.AccessTokenPurpose : AssistantLineChannel.ChannelSecretPurpose, Secret, now),
            protector.Protect(AssistantLineChannel.AccessTokenPurpose, bot.AccessToken, now),
            WelcomeMessage,
            now);
        if (tested || state != LineChannelState.Draft)
        {
            channel.RecordConnectionChecks(
                [.. LineConnectionCheck.All.Select(kind => new LineConnectionCheck(kind, LineConnectionCheckState.Passed, "通過。"))],
                bot.UserId,
                now);
        }

        if (state != LineChannelState.Draft)
        {
            channel.Publish(org.OwnerId, now);
        }

        if (state == LineChannelState.Paused)
        {
            channel.SetPaused(true, now);
        }

        dbContext.AssistantLineChannels.Add(channel);
        await dbContext.SaveChangesAsync(CancellationToken);
        return created;
    }

    private async Task<string> ChannelRowAsync(TestChannel channel)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(channel.OrganizationId);
        var row = await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(candidate => candidate.AssistantId == channel.AssistantId, CancellationToken);
        return $"{row.State}|{row.Revision}|{row.UpdatedAt:O}|{row.BotUserId}|{row.PushFallbackCount}|{row.ConnectionCheckedAt:O}";
    }

    private Sentinel NewSentinel(Guid assistantId)
    {
        var chat = new LineChatKey(assistantId, "U" + Guid.NewGuid().ToString("N"));
        History.Append(chat, null, Turn(ConversationAuthor.Account, "sentinel"));
        return new Sentinel(chat, Event("unfollow", UserSource(chat.ChatId), null, null));
    }

    private Task ProcessedAsync(Sentinel sentinel) =>
        EventuallyAsync(() => History.Get(sentinel.Chat).Count == 0, "the delivery to be processed up to its sentinel");

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new ShouldAssertException($"Timed out waiting for {what}.");
            }

            await Task.Delay(20, CancellationToken);
        }
    }

    private IReadOnlyList<LineRequest> Replies(LineBot bot) =>
        [.. Line.RequestsWith(bot.AccessToken).Where(request => request.Endpoint == FakeLineServer.Reply)];

    private static ConversationTurn Turn(ConversationAuthor author, string text) => new(author, text);

    private static string Sign(string body, string secret = Secret) => LineWebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(body));

    private Task<HttpResponseMessage> PostAsync(Guid assistantId, string body, bool sign) =>
        PostAsync(assistantId.ToString(), Encoding.UTF8.GetBytes(body), sign ? Sign(body) : null);

    private Task<HttpResponseMessage> PostAsync(Guid assistantId, string body, string? signature) =>
        PostAsync(assistantId.ToString(), Encoding.UTF8.GetBytes(body), signature);

    private Task<HttpResponseMessage> PostAsync(string assistantId, string body, string? signature) =>
        PostAsync(assistantId, Encoding.UTF8.GetBytes(body), signature);

    private Task<HttpResponseMessage> PostAsync(Guid assistantId, byte[] body, string? signature) =>
        PostAsync(assistantId.ToString(), body, signature);

    private async Task<HttpResponseMessage> PostAsync(string assistantId, byte[] body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/line/webhook/{assistantId}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("x-line-signature", signature);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    /// <summary>Status, every header but <c>Date</c>, and the body bytes.</summary>
    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .Select(header => $"{header.Key.ToLowerInvariant()}: {string.Join(",", header.Value)}")
            .Order(StringComparer.Ordinal);
        var body = await response.Content.ReadAsByteArrayAsync(CancellationToken);
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n{Convert.ToBase64String(body)}";
    }

    // LINE's documented event shapes (reference: "Webhook event objects").

    private static string Body(string destination, params string[] events) =>
        $$"""{"destination":"{{destination}}","events":[{{string.Join(",", events)}}]}""";

    private static string UserSource(string userId) => $$"""{"type":"user","userId":"{{userId}}"}""";

    private static string GroupSource(string groupId, string userId) => $$"""{"type":"group","groupId":"{{groupId}}","userId":"{{userId}}"}""";

    private static string Event(
        string type, string source, string? replyToken, string? extra, string? eventId = null, bool redelivery = false, string mode = "active") =>
        $$"""
        {"type":"{{type}}","mode":"{{mode}}","timestamp":1759737600000,"source":{{source}},
         "webhookEventId":"{{eventId ?? "01J" + Guid.NewGuid().ToString("N")[..23].ToUpperInvariant()}}",
         "deliveryContext":{"isRedelivery":{{(redelivery ? "true" : "false")}}}{{(replyToken is null ? "" : $$""","replyToken":"{{replyToken}}" """)}}{{extra}}}
        """;

    private static string Follow(string userId, string replyToken, string? eventId = null, bool redelivery = false) =>
        Event("follow", UserSource(userId), replyToken, ""","follow":{"isUnblocked":false}""", eventId, redelivery);

    private static string Message(string source, string message, string replyToken) =>
        Event("message", source, replyToken, $$""","message":{{message}}""");

    private static string Text(string source, string text, string replyToken, string messageId = "m-text") =>
        Message(source, JsonSerializer.Serialize(new { id = messageId, type = "text", quoteToken = "q", text }), replyToken);
}

/// <summary>The Api host of <see cref="LineWebhookEndpointTests"/>: the usual one, with a recording
/// <see cref="ILineQuestionHandler"/> in place of the one that answers nothing.</summary>
public sealed class LineWebhookHostFixture : AuthHostFixture
{
    public LineQuestionProbe Questions { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(Questions);
        services.AddScoped<ILineQuestionHandler, ProbeLineQuestionHandler>();
    }
}

/// <summary>What the recording question handler has seen, per assistant, and what it should do.</summary>
public sealed class LineQuestionProbe
{
    private readonly ConcurrentDictionary<Guid, int> _started = new();
    private readonly ConcurrentDictionary<Guid, int> _finished = new();

    public ConcurrentQueue<LineQuestionContext> Received { get; } = new();

    /// <summary>Run by the handler for that assistant's questions (e.g. waiting, to be slow).</summary>
    public ConcurrentDictionary<Guid, Func<CancellationToken, Task>> Behaviors { get; } = new();

    public int Started(Guid assistantId) => _started.GetValueOrDefault(assistantId);

    public int Finished(Guid assistantId) => _finished.GetValueOrDefault(assistantId);

    internal void Start(LineQuestionContext context)
    {
        Received.Enqueue(context);
        _started.AddOrUpdate(context.Assistant.Id, 1, (_, count) => count + 1);
    }

    internal void Finish(Guid assistantId) => _finished.AddOrUpdate(assistantId, 1, (_, count) => count + 1);
}

internal sealed class ProbeLineQuestionHandler(LineQuestionProbe probe) : ILineQuestionHandler
{
    public async Task HandleAsync(LineQuestionContext context, CancellationToken cancellationToken)
    {
        probe.Start(context);
        try
        {
            if (probe.Behaviors.TryGetValue(context.Assistant.Id, out var behavior))
            {
                await behavior(cancellationToken);
            }
        }
        finally
        {
            probe.Finish(context.Assistant.Id);
        }
    }
}
