using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// The 統籌助理's webhook and binding (store-coordinator design §5 Slice 1, #372) through the real
/// endpoint, background processor and PostgreSQL, with the host's fake LINE server standing in for the
/// shared coordinator account. Every test uses its own LINE users, reply tokens and organizations, so
/// tests sharing the host never see each other's replies.
/// </summary>
/// <remarks>
/// The events of one delivery are handled one after another, so a test that expects nothing to happen
/// ends the delivery with a <em>sentinel</em> — a <c>follow</c> of a fresh, unbound user — and waits for
/// its welcome: by then every earlier event of the delivery has been handled.
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public sealed partial class CoordinatorWebhookTests : IClassFixture<CoordinatorHostFixture>
{
    private readonly CoordinatorHostFixture _host;
    private readonly HttpClient _client;

    public CoordinatorWebhookTests(CoordinatorHostFixture host)
    {
        _host = host;
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private FakeLineServer Line => _host.Line;

    private LineBot Bot => _host.Bot;

    // --- The door: signature, destination, groups, deduplication -------------------------------------

    [Fact]
    public async Task A_missing_or_wrong_signature_is_401_and_nothing_is_handled()
    {
        var user = NewUser();
        var body = Body(Bot.UserId, Follow(user, "rt-signature"));
        var bytes = Encoding.UTF8.GetBytes(body);
        var changed = (byte[])bytes.Clone();
        changed[^3] ^= 0x01;

        var refusals = new[]
        {
            await PostAsync(bytes, signature: null),
            await PostAsync(bytes, "not base64"),
            await PostAsync(bytes, Sign(body, "0000000000000000000000000000beef")),
            await PostAsync(changed, Sign(body)),
        };

        foreach (var refusal in refusals)
        {
            refusal.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await refusal.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
        }

        var accepted = await PostAsync(body, sign: true);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReplyTextAsync("rt-signature")).ShouldBe(CoordinatorReplies.Welcome);
        Replies("rt-signature").Count.ShouldBe(1, "only the signed request was handled");

        // The bot's own user id was asked for once and is kept (destination check).
        Line.RequestsWith(Bot.AccessToken).Count(request => request.Endpoint == FakeLineServer.BotInfo).ShouldBe(1);
    }

    [Fact]
    public async Task Verify_another_destination_and_group_or_room_events_are_200_and_ignored()
    {
        var verify = Body(Bot.UserId);
        (await PostAsync(verify, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var user = NewUser();
        var otherBot = Body("U" + new string('f', 32), Follow(user, "rt-other-bot"));
        (await PostAsync(otherBot, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var sentinel = Sentinel();
        var groups = Body(
            Bot.UserId,
            Event("message", GroupSource("Cgroup000000000000000000000000001", user), "rt-group", TextMessage("ABCD-2345")),
            Event("join", """{"type":"group","groupId":"Cgroup000000000000000000000000002"}""", "rt-join"),
            Event("message", """{"type":"room","roomId":"Rroom0000000000000000000000000001","userId":"U1"}""", "rt-room", TextMessage("你好")),
            sentinel.Event);
        (await PostAsync(groups, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await sentinel.HandledAsync(this);

        Replies("rt-other-bot").ShouldBeEmpty();
        Replies("rt-group").ShouldBeEmpty();
        Replies("rt-join").ShouldBeEmpty();
        Replies("rt-room").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_webhook_event_id_is_handled_once_also_when_redelivered()
    {
        var user = NewUser();
        var eventId = "01JCOORD" + Guid.NewGuid().ToString("N")[..18].ToUpperInvariant();
        var original = Body(Bot.UserId, Follow(user, "rt-dedupe", eventId));

        (await PostAsync(original, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReplyTextAsync("rt-dedupe")).ShouldBe(CoordinatorReplies.Welcome);

        (await PostAsync(original, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var sentinel = Sentinel();
        var redelivery = Body(Bot.UserId, Follow(user, "rt-dedupe", eventId, redelivery: true), sentinel.Event);
        (await PostAsync(redelivery, sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await sentinel.HandledAsync(this);

        Replies("rt-dedupe").Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_endpoint_answers_200_at_once()
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await PostAsync(Body(Bot.UserId, Follow(NewUser(), "rt-fast")), sign: true);
        stopwatch.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "LINE expects 200 within about 2 seconds");
        (await ReplyTextAsync("rt-fast")).ShouldBe(CoordinatorReplies.Welcome);
    }

    // --- Unbound people -------------------------------------------------------------------------------

    [Fact]
    public async Task The_unbound_are_welcomed_and_asked_for_a_code_whatever_they_send()
    {
        var user = NewUser();
        var sentinel = Sentinel();

        var response = await PostAsync(
            Body(
                Bot.UserId,
                Follow(user, "rt-unbound-follow"),
                Text(user, "你好", "rt-unbound-text"),
                Event("message", UserSource(user), "rt-unbound-sticker", """{"id":"m-s","type":"sticker","packageId":"446","stickerId":"1988"}"""),
                Text(user, CoordinatorMessageRouter.IssueManagerCodeCommand, "rt-unbound-command"),
                Text(user, "今天臨時店休", "rt-unbound-scenario"),
                Event("unfollow", UserSource(user), replyToken: null),
                sentinel.Event),
            sign: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await sentinel.HandledAsync(this);
        (await ReplyTextAsync("rt-unbound-follow")).ShouldBe(CoordinatorReplies.Welcome);
        (await ReplyTextAsync("rt-unbound-text")).ShouldBe(CoordinatorReplies.AskForCode);
        (await ReplyTextAsync("rt-unbound-sticker")).ShouldBe(CoordinatorReplies.AskForCode);
        (await ReplyTextAsync("rt-unbound-command")).ShouldBe(CoordinatorReplies.AskForCode);
        (await ReplyTextAsync("rt-unbound-scenario")).ShouldBe(CoordinatorReplies.AskForCode);
    }

    // --- Binding ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_operators_owner_code_binds_once_and_the_member_then_gets_the_menu()
    {
        var shop = await _host.CreateOrganizationAsync("王記麵店");
        var issued = await RunCommandAsync("--organization", shop.Code.ToUpperInvariant());
        issued.Exit.ShouldBe(CoordinatorBindingCodeCommand.ExitSuccess, issued.Error);
        issued.Output.ShouldContain(shop.Code);
        issued.Output.ShouldContain("王記麵店");
        var code = CodeIn(issued.Output);
        var row = (await CodesAsync(shop)).ShouldHaveSingleItem();
        row.Role.ShouldBe(CoordinatorRole.Owner);
        (row.ExpiresAt - row.CreatedAt).ShouldBe(TimeSpan.FromHours(168));
        row.IssuedByLineUserId.ShouldBeNull();
        row.CodeHash.ShouldBe(BindingCodes.Hash(code.Replace("-", "", StringComparison.Ordinal)));
        issued.Output.ShouldNotContain(row.CodeHash);

        var owner = NewUser();
        (await SendAsync(owner, code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)))
            .ShouldBe("已綁定 王記麵店（老闆）。");

        var member = (await MembersAsync(shop)).ShouldHaveSingleItem();
        member.LineUserId.ShouldBe(owner);
        member.Role.ShouldBe(CoordinatorRole.Owner);
        var used = (await CodesAsync(shop)).ShouldHaveSingleItem();
        used.UsedAt.ShouldNotBeNull();
        used.UsedByLineUserId.ShouldBe(owner);

        // A shop without settings has no commands yet (#374); a follow or a sticker still gets the menu.
        (await SendAsync(owner, "你好")).ShouldBe(CoordinatorCommandReplies.NotConfigured);
        (await SendAsync(owner, "今天臨時店休")).ShouldBe(CoordinatorCommandReplies.NotConfigured);
        await PostAsync(Body(Bot.UserId, Follow(owner, "rt-member-follow")), sign: true);
        (await ReplyTextAsync("rt-member-follow")).ShouldBe(CoordinatorReplies.Menu);
        await PostAsync(
            Body(Bot.UserId, Event("message", UserSource(owner), "rt-member-sticker", """{"id":"m-s2","type":"sticker","packageId":"446","stickerId":"1988"}""")),
            sign: true);
        (await ReplyTextAsync("rt-member-sticker")).ShouldBe(CoordinatorReplies.Menu);

        // Used: nobody else can bind with it.
        var other = NewUser();
        (await SendAsync(other, code)).ShouldBe(CoordinatorReplies.InvalidCode);
        (await MembersAsync(shop)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_and_unknown_codes_cannot_bind()
    {
        var shop = await _host.CreateOrganizationAsync("過期測試店");
        var expired = BindingCodes.Generate();
        var now = _host.Clock.GetUtcNow();
        await AddCodeAsync(shop, expired, CoordinatorRole.Owner, issuedAt: now - TimeSpan.FromHours(25), TimeSpan.FromHours(24));
        var user = NewUser();

        (await SendAsync(user, BindingCodes.Format(expired))).ShouldBe(CoordinatorReplies.InvalidCode);
        (await SendAsync(user, BindingCodes.Format(UnusedCode()))).ShouldBe(CoordinatorReplies.InvalidCode);

        (await MembersAsync(shop)).ShouldBeEmpty();
        (await CodesAsync(shop)).ShouldHaveSingleItem().UsedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Five_failed_attempts_block_the_sender_for_fifteen_minutes_without_using_a_good_code()
    {
        var shop = await _host.CreateOrganizationAsync("節流測試店");
        var good = await OwnerCodeAsync(shop);
        var user = NewUser();

        for (var attempt = 1; attempt <= CoordinatorBindingThrottle.MaxFailures; attempt++)
        {
            (await SendAsync(user, BindingCodes.Format(UnusedCode()))).ShouldBe(CoordinatorReplies.InvalidCode, $"attempt {attempt}");
        }

        (await SendAsync(user, good)).ShouldBe(CoordinatorReplies.TooManyAttempts);
        (await CodesAsync(shop)).ShouldHaveSingleItem().UsedAt.ShouldBeNull("a blocked attempt does not look at the code");
        (await SendAsync(NewUser(), BindingCodes.Format(UnusedCode()))).ShouldBe(CoordinatorReplies.InvalidCode, "others are not blocked");

        _host.Clock.Advance(CoordinatorBindingThrottle.Window);

        (await SendAsync(user, good)).ShouldBe("已綁定 節流測試店（老闆）。");
    }

    [Fact]
    public async Task A_member_sending_a_code_of_their_own_shop_is_told_so_and_the_code_stays_unused()
    {
        var shop = await _host.CreateOrganizationAsync("重複綁定店");
        var owner = NewUser();
        (await SendAsync(owner, await OwnerCodeAsync(shop))).ShouldBe("已綁定 重複綁定店（老闆）。");
        var second = await OwnerCodeAsync(shop);

        (await SendAsync(owner, second)).ShouldBe("你已經綁定 重複綁定店（老闆）。");

        var unused = (await CodesAsync(shop)).Where(code => code.UsedAt is null).ShouldHaveSingleItem();
        unused.CodeHash.ShouldBe(BindingCodes.Hash(second.Replace("-", "", StringComparison.Ordinal)));
        (await MembersAsync(shop)).Count.ShouldBe(1);

        var partner = NewUser();
        (await SendAsync(partner, second)).ShouldBe("已綁定 重複綁定店（老闆）。");
        (await MembersAsync(shop)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Of_two_people_sending_one_code_at_once_exactly_one_is_bound()
    {
        var shop = await _host.CreateOrganizationAsync("搶碼測試店");
        var code = await OwnerCodeAsync(shop);
        var first = NewUser();
        var second = NewUser();

        var posts = await Task.WhenAll(
            PostAsync(Body(Bot.UserId, Text(first, code, "rt-race-1")), sign: true),
            PostAsync(Body(Bot.UserId, Text(second, code, "rt-race-2")), sign: true));
        posts.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);

        var replies = new[] { await ReplyTextAsync("rt-race-1"), await ReplyTextAsync("rt-race-2") };
        replies.Count(reply => reply == "已綁定 搶碼測試店（老闆）。").ShouldBe(1);
        replies.Count(reply => reply == CoordinatorReplies.InvalidCode).ShouldBe(1);
        var member = (await MembersAsync(shop)).ShouldHaveSingleItem();
        (await CodesAsync(shop)).ShouldHaveSingleItem().UsedByLineUserId.ShouldBe(member.LineUserId);
    }

    // --- 店長 codes ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_gets_a_manager_code_that_binds_a_manager_who_cannot_issue_codes()
    {
        var shop = await _host.CreateOrganizationAsync("安心早餐店");
        var owner = NewUser();
        (await SendAsync(owner, await OwnerCodeAsync(shop))).ShouldBe("已綁定 安心早餐店（老闆）。");

        var reply = await SendAsync(owner, "產生店長綁定碼");

        reply.ShouldStartWith("店長綁定碼：");
        reply.ShouldContain("店名：安心早餐店");
        reply.ShouldContain("（台北時間）前");
        reply.ShouldContain("只能使用一次");
        var managerCode = CodeIn(reply);
        var row = (await CodesAsync(shop)).Single(code => code.UsedAt is null);
        row.Role.ShouldBe(CoordinatorRole.Manager);
        row.IssuedByLineUserId.ShouldBe(owner);
        (row.ExpiresAt - row.CreatedAt).ShouldBe(TimeSpan.FromHours(24));
        var expiry = TimeZoneInfo.ConvertTime(row.ExpiresAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));
        reply.ShouldContain(expiry.ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));

        var manager = NewUser();
        (await SendAsync(manager, managerCode)).ShouldBe("已綁定 安心早餐店（店長）。");
        (await MembersAsync(shop)).Single(member => member.LineUserId == manager).Role.ShouldBe(CoordinatorRole.Manager);

        (await SendAsync(manager, "產生店長綁定碼")).ShouldBe(CoordinatorReplies.OwnersOnly);
        (await CodesAsync(shop)).Count.ShouldBe(2, "the manager's request issued nothing");
    }

    [Fact]
    public async Task One_line_user_binds_two_shops_chooses_one_and_a_forged_choice_is_refused()
    {
        var first = await _host.CreateOrganizationAsync("甲記小館");
        var second = await _host.CreateOrganizationAsync("乙記小館");
        var foreign = await _host.CreateOrganizationAsync("丙記小館");
        var managed = await _host.CreateOrganizationAsync("丁記小館");
        var owner = NewUser();
        (await SendAsync(owner, await OwnerCodeAsync(first))).ShouldBe("已綁定 甲記小館（老闆）。");
        (await SendAsync(owner, await OwnerCodeAsync(second))).ShouldBe("已綁定 乙記小館（老闆）。");
        var managerCode = BindingCodes.Generate();
        await AddCodeAsync(managed, managerCode, CoordinatorRole.Manager, _host.Clock.GetUtcNow(), TimeSpan.FromHours(24));
        (await SendAsync(owner, BindingCodes.Format(managerCode))).ShouldBe("已綁定 丁記小館（店長）。");
        (await MembersAsync(first)).ShouldHaveSingleItem().LineUserId.ShouldBe(owner);
        (await MembersAsync(second)).ShouldHaveSingleItem().LineUserId.ShouldBe(owner);

        await PostAsync(Body(Bot.UserId, Text(owner, "產生店長綁定碼", "rt-choose")), sign: true);
        var choice = await ReplyMessageAsync("rt-choose");
        choice.GetProperty("text").GetString().ShouldBe(CoordinatorReplies.ChooseStore);
        var items = choice.GetProperty("quickReply").GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("action").GetProperty("label").GetString()).ShouldBe(["乙記小館", "甲記小館"], ignoreOrder: true);
        var secondData = items
            .Single(item => item.GetProperty("action").GetProperty("label").GetString() == "乙記小館")
            .GetProperty("action").GetProperty("data").GetString()!;
        secondData.ShouldBe(CoordinatorMessageRouter.ManagerCodePostback(second.Id));
        (await CodesAsync(first)).Count.ShouldBe(1, "a choice issues nothing yet");

        await PostAsync(Body(Bot.UserId, Postback(owner, secondData, "rt-chosen")), sign: true);
        var issued = await ReplyTextAsync("rt-chosen");
        issued.ShouldContain("店名：乙記小館");
        (await CodesAsync(second)).Single(code => code.UsedAt is null).Role.ShouldBe(CoordinatorRole.Manager);

        foreach (var (organization, replyToken) in new[] { (foreign, "rt-forged-foreign"), (managed, "rt-forged-managed") })
        {
            await PostAsync(Body(Bot.UserId, Postback(owner, CoordinatorMessageRouter.ManagerCodePostback(organization.Id), replyToken)), sign: true);
            (await ReplyTextAsync(replyToken)).ShouldBe(CoordinatorReplies.OwnersOnly, organization.Name);
        }

        (await CodesAsync(foreign)).ShouldBeEmpty();
        (await CodesAsync(managed)).Count.ShouldBe(1, "only the code the manager was bound with");

        // The same postback from someone bound nowhere just gets asked for a code.
        await PostAsync(Body(Bot.UserId, Postback(NewUser(), secondData, "rt-forged-unbound")), sign: true);
        (await ReplyTextAsync("rt-forged-unbound")).ShouldBe(CoordinatorReplies.AskForCode);
    }

    // --- The command ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_command_refuses_unknown_shops_and_bad_arguments_and_accepts_up_to_720_hours()
    {
        var shop = await _host.CreateOrganizationAsync("指令測試店");

        var unknown = await RunCommandAsync("--organization", "no-such-shop");
        unknown.Exit.ShouldBe(CoordinatorBindingCodeCommand.ExitFailed);
        unknown.Error.ShouldContain("找不到組織代碼");

        foreach (var args in new[]
                 {
                     Array.Empty<string>(), ["--organization"], ["--hours", "24"], ["--organization", shop.Code, "--hours", "0"],
                     ["--organization", shop.Code, "--hours", "721"], ["--organization", shop.Code, "--hours=abc"], ["--nope"],
                 })
        {
            var result = await RunCommandAsync(args);
            result.Exit.ShouldBe(CoordinatorBindingCodeCommand.ExitUsage, string.Join(' ', args));
            result.Error.ShouldContain("用法：coordinator-binding-code");
        }

        (await CodesAsync(shop)).ShouldBeEmpty();

        var longest = await RunCommandAsync("--organization=" + shop.Code, "--hours=720");
        longest.Exit.ShouldBe(CoordinatorBindingCodeCommand.ExitSuccess, longest.Error);
        var row = (await CodesAsync(shop)).ShouldHaveSingleItem();
        (row.ExpiresAt - row.CreatedAt).ShouldBe(TimeSpan.FromHours(720));

        var help = await RunCommandAsync("--help");
        help.Exit.ShouldBe(CoordinatorBindingCodeCommand.ExitSuccess);
        help.Output.ShouldStartWith("用法：coordinator-binding-code");
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private async Task<string> OwnerCodeAsync(Organization shop)
    {
        var result = await RunCommandAsync("--organization", shop.Code);
        result.Exit.ShouldBe(CoordinatorBindingCodeCommand.ExitSuccess, result.Error);
        return CodeIn(result.Output);
    }

    private async Task<(int Exit, string Output, string Error)> RunCommandAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CoordinatorBindingCodeCommand.RunAsync(_host.Factory.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private static string CodeIn(string text) => CodePattern().Match(text) is { Success: true } match
        ? match.Value
        : throw new ShouldAssertException($"No binding code in: {text}");

    [GeneratedRegex("[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}")]
    private static partial Regex CodePattern();

    /// <summary>A code that was never issued (the alphabet has 31^8 of them).</summary>
    private static string UnusedCode() => BindingCodes.Generate();

    private async Task AddCodeAsync(Organization shop, string code, CoordinatorRole role, DateTimeOffset issuedAt, TimeSpan validity)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        dbContext.CoordinatorBindingCodes.Add(CoordinatorBindingCode.Issue(shop.Id, BindingCodes.Hash(code), role, issuedAt, validity, null));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task<List<CoordinatorMember>> MembersAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorMembers.AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task<List<CoordinatorBindingCode>> CodesAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorBindingCodes.AsNoTracking().OrderBy(code => code.CreatedAt).ToListAsync(CancellationToken);
    }

    /// <summary>Sends <paramref name="text"/> as <paramref name="user"/> and returns the reply's text.</summary>
    private async Task<string> SendAsync(string user, string text)
    {
        var replyToken = "rt-" + Guid.NewGuid().ToString("N");
        var response = await PostAsync(Body(Bot.UserId, Text(user, text, replyToken)), sign: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReplyTextAsync(replyToken);
    }

    private IReadOnlyList<LineRequest> Replies(string replyToken) =>
        [.. Line.RequestsWith(Bot.AccessToken)
            .Where(request => request.Endpoint == FakeLineServer.Reply && request.Json.GetProperty("replyToken").GetString() == replyToken)];

    private async Task<JsonElement> ReplyMessageAsync(string replyToken)
    {
        await EventuallyAsync(() => Replies(replyToken).Count > 0, $"the reply to {replyToken}");
        var reply = Replies(replyToken).ShouldHaveSingleItem();
        return reply.Json.GetProperty("messages").EnumerateArray().ShouldHaveSingleItem();
    }

    private async Task<string> ReplyTextAsync(string replyToken) =>
        (await ReplyMessageAsync(replyToken)).GetProperty("text").GetString()!;

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

    private SentinelEvent Sentinel()
    {
        var replyToken = "rt-sentinel-" + Guid.NewGuid().ToString("N");
        return new SentinelEvent(Follow(NewUser(), replyToken), replyToken);
    }

    private sealed record SentinelEvent(string Event, string ReplyToken)
    {
        public async Task HandledAsync(CoordinatorWebhookTests tests) =>
            (await tests.ReplyTextAsync(ReplyToken)).ShouldBe(CoordinatorReplies.Welcome);
    }

    private static string NewUser() => "U" + Guid.NewGuid().ToString("N");

    private static string Sign(string body, string secret = CoordinatorHostFixture.Secret) =>
        LineWebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(body));

    private Task<HttpResponseMessage> PostAsync(string body, bool sign) =>
        PostAsync(Encoding.UTF8.GetBytes(body), sign ? Sign(body) : null);

    private async Task<HttpResponseMessage> PostAsync(byte[] body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CoordinatorWebhookEndpoints.Route)
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

    // LINE's documented event shapes (reference: "Webhook event objects").

    private static string Body(string destination, params string[] events) =>
        $$"""{"destination":"{{destination}}","events":[{{string.Join(",", events)}}]}""";

    private static string UserSource(string userId) => $$"""{"type":"user","userId":"{{userId}}"}""";

    private static string GroupSource(string groupId, string userId) => $$"""{"type":"group","groupId":"{{groupId}}","userId":"{{userId}}"}""";

    private static string TextMessage(string text) =>
        JsonSerializer.Serialize(new { id = "m-" + Guid.NewGuid().ToString("N")[..8], type = "text", quoteToken = "q", text });

    private static string Event(string type, string source, string? replyToken, string? message = null, string? eventId = null, bool redelivery = false, string? extra = null) =>
        $$"""
        {"type":"{{type}}","mode":"active","timestamp":1759737600000,"source":{{source}},
         "webhookEventId":"{{eventId ?? "01J" + Guid.NewGuid().ToString("N")[..23].ToUpperInvariant()}}",
         "deliveryContext":{"isRedelivery":{{(redelivery ? "true" : "false")}}}{{(replyToken is null ? "" : $$""","replyToken":"{{replyToken}}" """)}}{{(message is null ? "" : $$""","message":{{message}}""")}}{{extra}}}
        """;

    private static string Follow(string userId, string replyToken, string? eventId = null, bool redelivery = false) =>
        Event("follow", UserSource(userId), replyToken, eventId: eventId, redelivery: redelivery, extra: ""","follow":{"isUnblocked":false}""");

    private static string Text(string userId, string text, string replyToken) =>
        Event("message", UserSource(userId), replyToken, TextMessage(text));

    private static string Postback(string userId, string data, string replyToken) =>
        Event("postback", UserSource(userId), replyToken, extra: $$""","postback":{"data":{{JsonSerializer.Serialize(data)}}}""");
}

/// <summary>The Api host of <see cref="CoordinatorWebhookTests"/>: the usual one, with the 統籌助理
/// configured for a bot of the fake LINE server, and executors for the shops a test enrolls (#375) — LINE 群發's real
/// executor for the others (#376), whose shops' own accounts are bots of the same fake LINE server.</summary>
public sealed class CoordinatorHostFixture : AuthHostFixture
{
    public const string Secret = "c0ffee00c0ffee00c0ffee00c0ffee00";

    public LineBot Bot { get; } = new(
        "fake-coordinator-token-" + Guid.NewGuid().ToString("N"), "U" + Guid.NewGuid().ToString("N"), "@coordinator", "統籌助理");

    /// <summary>Every log entry of the host while <see cref="CoordinatorTestLog.Start"/>ed (#374).</summary>
    public CoordinatorTestLog Log { get; } = new();

    /// <summary>LINE 群發, Google and FB executors, available only for enrolled shops (#375).</summary>
    public FakeCoordinatorExecutors Executors { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        Line.AddBot(Bot);
        services.Configure<CoordinatorLineOptions>(options =>
        {
            options.ChannelSecret = Secret;
            options.ChannelAccessToken = Bot.AccessToken;
        });
        services.AddSingleton<ILoggerProvider>(Log);
        foreach (var kind in FakeCoordinatorExecutors.Kinds)
        {
            if (kind == ShopActionKind.LineBroadcast)
            {
                // The real one (#376) for every shop no test enrolled.
                services.AddScoped<ICoordinatorActionExecutor>(provider =>
                    Executors.For(kind, provider.GetRequiredService<CoordinatorLineBroadcastExecutor>()));
            }
            else
            {
                services.AddSingleton(Executors.For(kind));
            }
        }

        services.Configure<LoggerFilterOptions>(options =>
            options.Rules.Add(new LoggerFilterRule(typeof(CoordinatorTestLog).FullName, categoryName: null, LogLevel.Trace, filter: null)));
    }
}

/// <summary>Every log entry of every category at every level — message, structured values, scopes and exception —
/// between <see cref="Start"/> and <see cref="Stop"/> (the tests of a class run one at a time).</summary>
public sealed class CoordinatorTestLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();
    private volatile bool _capturing;

    public IReadOnlyList<string> Lines => [.. _lines];

    public void Start()
    {
        _lines.Clear();
        _capturing = true;
    }

    public void Stop() => _capturing = false;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, CoordinatorTestLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (log._capturing)
            {
                log._lines.Enqueue($"{category} scope: {Describe(state)}");
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => log._capturing;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (log._capturing)
            {
                log._lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} | {Describe(state)} | {exception}");
            }
        }

        private static string Describe<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> values
                ? string.Join("; ", values.Select(pair => $"{pair.Key}={pair.Value}"))
                : state?.ToString() ?? string.Empty;
    }
}
