using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Line;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Setup;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Line;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Observability;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Line;

/// <summary>
/// LINE answers end to end (M5b plan §3 D–G, §5 Slice 4, issue #232): signed webhook events in, the
/// real <c>LineQuestionHandler</c> with the <c>Fake</c> models and real PostgreSQL, and the host's fake
/// LINE server out. Every test adds its own organization, knowledge, assistant, LINE channel and fake
/// bot, so its LINE requests and model calls are its own.
/// </summary>
/// <remarks>
/// Events of one delivery are handled one after another, so a test that expects nothing to happen ends
/// its delivery with a <em>sentinel</em>: an <c>unfollow</c> of a chat whose history the test seeded.
/// Once that history is gone, every earlier event of the delivery has been handled.
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class LineAnswerTests : IClassFixture<LineAnswerHostFixture>
{
    private const string Password = "Line-Answer-Pass-1!";
    private const string Secret = "8c9f1d2e3a4b5c6d7e8f90a1b2c3d4e5";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";
    private const string RelatedQuestion = "收到商品後幾天內可以退貨？退貨運費由誰負擔？";
    private const string BotMention = "@安心客服";
    private const string FakeAnswerText = "根據資料回答，本回答引用段落（來源 1）。";

    private readonly LineAnswerHostFixture _host;

    public LineAnswerTests(LineAnswerHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private FakeLineServer Line => _host.Line;

    // --- One-to-one and group answers ---------------------------------------------------------------

    /// <summary>Acceptance: loading, then one reply with the answer and the Flex sources; the database
    /// gains one <c>line-answer</c> call without an account and one <c>line</c> outcome, and no
    /// conversation.</summary>
    [Fact]
    public async Task A_one_to_one_question_shows_loading_then_replies_with_the_answer_and_its_sources_and_keeps_no_conversation()
    {
        var setup = await SetUpAsync();
        var userId = NewUserId();
        var before = await CountsAsync(setup);

        await PostAsync(setup, Text(UserSource(userId), RelatedQuestion, "reply-token-1", "m-1"));

        await EventuallyAsync(() => Replies(setup).Count == 1, "the answer");
        var requests = Line.RequestsWith(setup.Bot.AccessToken);
        requests.Select(request => request.Endpoint).ShouldBe([FakeLineServer.StartLoading, FakeLineServer.Reply]);
        var loading = requests[0].Json;
        loading.GetProperty("chatId").GetString().ShouldBe(userId);
        loading.GetProperty("loadingSeconds").GetInt32().ShouldBe(50);

        var reply = requests[1].Json;
        reply.GetProperty("replyToken").GetString().ShouldBe("reply-token-1");
        var messages = reply.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(2);
        messages[0].GetProperty("type").GetString().ShouldBe("text");
        messages[0].GetProperty("text").GetString().ShouldBe(FakeAnswerText);
        messages[1].GetProperty("type").GetString().ShouldBe("flex");
        messages[1].GetProperty("altText").GetString().ShouldBe("參考來源：退貨政策.md");
        var bubble = messages[1].GetProperty("contents").GetProperty("contents").EnumerateArray().ShouldHaveSingleItem();
        var texts = bubble.GetProperty("body").GetProperty("contents").EnumerateArray().Select(text => text.GetProperty("text").GetString()).ToList();
        texts[0].ShouldBe("來源 1");
        texts[1].ShouldBe("對話知識庫");
        texts[2].ShouldBe("退貨政策.md");
        texts[3].ShouldNotBeNull().ShouldContain("七天內可申請退貨");

        // The model was asked the question, under the assistant's organization (the pinned scope).
        var call = _host.Model.CallsFor(setup.AssistantId).ShouldHaveSingleItem();
        call.Messages[^1].Text.ShouldBe(RelatedQuestion);

        await using var dbContext = _host.Postgres.CreateDbContext(setup.OrganizationId);
        var after = await CountsAsync(setup);
        after.Threads.ShouldBe(before.Threads);
        after.Messages.ShouldBe(before.Messages);
        var invocation = await dbContext.ModelInvocations.AsNoTracking()
            .Where(row => row.AssistantId == setup.AssistantId && row.Purpose != ModelInvocationPurpose.EmbedQuery)
            .SingleAsync(CancellationToken);
        invocation.Purpose.ShouldBe(ModelInvocationPurpose.LineAnswer);
        invocation.AccountId.ShouldBeNull();
        invocation.OrganizationId.ShouldBe(setup.OrganizationId);
        var outcome = await dbContext.AnswerOutcomes.AsNoTracking().SingleAsync(row => row.AssistantId == setup.AssistantId, CancellationToken);
        outcome.Channel.ShouldBe(AnswerOutcomeChannel.Line);
        outcome.ReplyKind.ShouldBe(AnswerReplyKind.CompanyData);

        // No LINE id anywhere in those rows.
        JsonSerializer.Serialize(invocation).ShouldNotContain(userId);
        JsonSerializer.Serialize(outcome).ShouldNotContain(userId);
        _host.History.Get(new LineChatKey(setup.AssistantId, userId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_assistant_that_hides_its_sources_replies_with_the_answer_only()
    {
        var setup = await SetUpAsync(showCitations: false);

        await PostAsync(setup, Text(UserSource(NewUserId()), RelatedQuestion, "reply-token-hidden"));

        await EventuallyAsync(() => Replies(setup).Count == 1, "the answer");
        var messages = Replies(setup)[0].Json.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(1);
        messages[0].GetProperty("text").GetString().ShouldBe("根據資料回答，本回答引用段落。");
        Replies(setup)[0].Body.ShouldNotBeNull().ShouldNotContain("退貨政策");
    }

    /// <summary>Acceptance: in a group, a message that does not mention the bot gets nothing; one that
    /// does is answered without the mention and without the loading animation.</summary>
    [Fact]
    public async Task A_group_answers_only_a_mention_of_the_bot_with_the_mention_removed_and_no_loading()
    {
        var setup = await SetUpAsync();
        var groupId = "C" + Guid.NewGuid().ToString("N");
        var member = NewUserId();
        var sentinel = NewSentinel(setup.AssistantId);

        await PostAsync(
            setup,
            Text(GroupSource(groupId, member), RelatedQuestion, "reply-token-plain"),
            Text(GroupSource(groupId, member), "@小明 " + RelatedQuestion, "reply-token-other", mentionees: """[{"index":0,"length":3,"type":"user","userId":"Uxm"}]"""),
            sentinel.Event);
        await ProcessedAsync(sentinel);
        Line.RequestsWith(setup.Bot.AccessToken).ShouldBeEmpty();
        _host.Model.CallsFor(setup.AssistantId).ShouldBeEmpty();

        await PostAsync(
            setup,
            Text(GroupSource(groupId, member), BotMention + " " + RelatedQuestion, "reply-token-mention", mentionees: """[{"index":0,"length":5,"type":"user","userId":"Ubot","isSelf":true}]"""));

        await EventuallyAsync(() => Replies(setup).Count == 1, "the group answer");
        Line.RequestsWith(setup.Bot.AccessToken).Select(request => request.Endpoint).ShouldBe([FakeLineServer.Reply], "no loading in a group");
        Replies(setup)[0].Json.GetProperty("replyToken").GetString().ShouldBe("reply-token-mention");
        var question = _host.Model.CallsFor(setup.AssistantId).ShouldHaveSingleItem().Messages[^1].Text;
        question.ShouldBe(RelatedQuestion);
        question.ShouldNotContain(BotMention);
        _host.History.Get(new LineChatKey(setup.AssistantId, groupId)).Count.ShouldBe(2, "the group shares one conversation");
    }

    // --- History --------------------------------------------------------------------------------------

    /// <summary>Acceptance: a follow-up carries the previous turn; <c>unsend</c> removes a question
    /// and its answer; after 30 idle minutes (the host's clock) nothing is remembered.</summary>
    [Fact]
    public async Task A_follow_up_carries_the_earlier_turns_unsend_forgets_one_and_30_idle_minutes_forget_all()
    {
        var setup = await SetUpAsync();
        var userId = NewUserId();
        // Both close to the document, so retrieval finds it and the model is called every time.
        const string FirstQuestion = RelatedQuestion;
        const string SecondQuestion = "請再說明一次：" + RelatedQuestion;
        const string RememberedAnswer = "根據資料回答，本回答引用段落。";

        await AskAsync(setup, userId, FirstQuestion, "m-1", expectedReplies: 1);
        await AskAsync(setup, userId, SecondQuestion, "m-2", expectedReplies: 2);
        History(_host.Model.CallsFor(setup.AssistantId)[1]).ShouldBe(
        [
            (ChatRole.User, FirstQuestion),
            (ChatRole.Assistant, RememberedAnswer),
        ]);

        await PostAsync(setup, Event("unsend", UserSource(userId), null, ""","unsend":{"messageId":"m-1"}"""));
        await EventuallyAsync(() => _host.History.Get(new LineChatKey(setup.AssistantId, userId)).Count == 2, "the unsent turn to be forgotten");
        await AskAsync(setup, userId, FirstQuestion, "m-3", expectedReplies: 3);
        History(_host.Model.CallsFor(setup.AssistantId)[2]).ShouldBe(
        [
            (ChatRole.User, SecondQuestion),
            (ChatRole.Assistant, RememberedAnswer),
        ]);

        _host.Clock.Advance(TimeSpan.FromMinutes(31));
        await AskAsync(setup, userId, SecondQuestion, "m-4", expectedReplies: 4);
        History(_host.Model.CallsFor(setup.AssistantId)[3]).ShouldBeEmpty();
    }

    // --- Not serving ----------------------------------------------------------------------------------

    /// <summary>Acceptance: paused, acceptance not passed, monthly tokens used up → 「目前暫停服務」 and
    /// no model call.</summary>
    [Theory]
    [InlineData("paused")]
    [InlineData("acceptance-failed")]
    [InlineData("quota-exceeded")]
    public async Task A_channel_that_is_not_serving_replies_paused_and_never_calls_the_model(string why)
    {
        var setup = await SetUpAsync(state: why == "paused" ? LineChannelState.Paused : LineChannelState.Published);
        await using (var dbContext = _host.Postgres.CreateDbContext(setup.OrganizationId))
        {
            if (why == "acceptance-failed")
            {
                var run = AssistantTestRun.Queue(setup.OrganizationId, setup.AssistantId, AssistantTestRunTrigger.Manual, _host.Clock.GetUtcNow().AddMinutes(-5));
                run.Start("test", AuthHostFixture.ChatModel, 0.3, _host.Clock.GetUtcNow().AddMinutes(-5));
                run.Complete(0, 1, _host.Clock.GetUtcNow().AddMinutes(-5));
                dbContext.AssistantTestRuns.Add(run);
            }

            if (why == "quota-exceeded")
            {
                (await dbContext.Organizations.SingleAsync(row => row.Id == setup.OrganizationId, CancellationToken)).SetMonthlyTokenLimit(0);
            }

            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await PostAsync(setup, Text(UserSource(NewUserId()), RelatedQuestion, "reply-token-paused"));

        await EventuallyAsync(() => Replies(setup).Count == 1, "the paused notice");
        Line.RequestsWith(setup.Bot.AccessToken).Select(request => request.Endpoint).ShouldBe([FakeLineServer.Reply], "no loading either");
        var messages = Replies(setup)[0].Json.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(1);
        messages[0].GetProperty("text").GetString().ShouldBe("目前暫停服務");
        _host.Model.CallsFor(setup.AssistantId).ShouldBeEmpty();
        await using var check = _host.Postgres.CreateDbContext(setup.OrganizationId);
        (await check.ModelInvocations.CountAsync(row => row.Purpose == ModelInvocationPurpose.LineAnswer, CancellationToken)).ShouldBe(0);
        (await check.AnswerOutcomes.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- Rate limits ----------------------------------------------------------------------------------

    /// <summary>Acceptance: each partition, over its limit, gets one notice and then silence, and the
    /// model is not called for any refused question. Each case runs its own host with a limit of 2.</summary>
    [Theory]
    [InlineData("LineQuestionsPerUserPerMinute", "user")]
    [InlineData("LineQuestionsPerUserPerHour", "user")]
    [InlineData("LineQuestionsPerGroupPerMinute", "group")]
    [InlineData("LineQuestionsPerAssistantPerMinute", "assistant")]
    public async Task Over_a_rate_limit_a_chat_is_told_once_and_then_nothing_and_the_model_is_not_called(string setting, string partition)
    {
        await using var limited = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting($"PublicChannels:RateLimits:{setting}", "2"));
        var setup = await SetUpAsync();
        var groupId = "C" + Guid.NewGuid().ToString("N");
        var sameUser = NewUserId();
        var events = Enumerable.Range(1, 4).Select(index => partition switch
        {
            "user" => Text(UserSource(sameUser), RelatedQuestion, $"reply-token-{index}"),
            "group" => Text(
                GroupSource(groupId, NewUserId()), BotMention + " " + RelatedQuestion, $"reply-token-{index}",
                mentionees: """[{"index":0,"length":5,"type":"user","isSelf":true}]"""),
            _ => Text(UserSource(NewUserId()), RelatedQuestion, $"reply-token-{index}"),
        }).ToList();
        var history = limited.Services.GetRequiredService<ILineConversationHistory>();
        var sentinel = NewSentinel(setup.AssistantId, history);

        await PostAsync(limited.CreateClient(), setup, [.. events, sentinel.Event]);

        await EventuallyAsync(() => history.Get(sentinel.Chat).Count == 0, "the delivery to be processed");
        var replies = Replies(setup);
        replies.Select(reply => reply.Json.GetProperty("replyToken").GetString()).ShouldBe(["reply-token-1", "reply-token-2", "reply-token-3"]);
        replies[2].Json.GetProperty("messages").GetArrayLength().ShouldBe(1);
        replies[2].Json.GetProperty("messages")[0].GetProperty("text").GetString().ShouldBe("問題太頻繁了，請稍後再試");
        _host.Model.CallsFor(setup.AssistantId).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Over_the_assistants_concurrent_answers_one_chat_is_told_once_and_the_rest_get_nothing()
    {
        await using var limited = _host.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("PublicChannels:RateLimits:LineMaxConcurrentQuestionsPerAssistant", "1"));
        var client = limited.CreateClient();
        var history = limited.Services.GetRequiredService<ILineConversationHistory>();
        var setup = await SetUpAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Model.Hooks[setup.AssistantId] = cancellationToken => release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        await PostAsync(client, setup, [Text(UserSource(NewUserId()), RelatedQuestion, "reply-token-first")]);
        await EventuallyAsync(() => _host.Model.CallsFor(setup.AssistantId).Count == 1, "the first answer to be generating");

        var sentinel = NewSentinel(setup.AssistantId, history);
        await PostAsync(
            client,
            setup,
            [
                Text(UserSource(NewUserId()), RelatedQuestion, "reply-token-second"),
                Text(UserSource(NewUserId()), RelatedQuestion, "reply-token-third"),
                sentinel.Event,
            ]);
        await EventuallyAsync(() => history.Get(sentinel.Chat).Count == 0, "the second delivery to be processed");
        var notice = Replies(setup).ShouldHaveSingleItem();
        notice.Json.GetProperty("replyToken").GetString().ShouldBe("reply-token-second");
        notice.Json.GetProperty("messages")[0].GetProperty("text").GetString().ShouldBe("問題太頻繁了，請稍後再試");

        release.SetResult();
        await EventuallyAsync(() => Replies(setup).Count == 2, "the first answer");
        Replies(setup)[1].Json.GetProperty("replyToken").GetString().ShouldBe("reply-token-first");
        _host.Model.CallsFor(setup.AssistantId).Count.ShouldBe(1);
    }

    // --- What a failure writes to the log ---------------------------------------------------------------

    /// <summary>Acceptance (#307): a model provider's error can quote the question it rejected, so a failed
    /// answer logs the exception types and the HTTP status, never a message — in no log, at no level. The
    /// user still gets the fixed 「目前無法回答」. (The same rule for website visitors is in
    /// <c>VisitorEndpointsTests</c>.)</summary>
    [Fact]
    public async Task A_model_error_that_quotes_the_question_never_reaches_the_log()
    {
        const string echoed = "SECRET-ECHO-9f3a7";
        var telemetry = new TelemetryCapture();
        await using var observed = _host.Factory.WithWebHostBuilder(telemetry.Attach);
        var history = observed.Services.GetRequiredService<ILineConversationHistory>();
        var setup = await SetUpAsync();
        _host.Model.Hooks[setup.AssistantId] = _ =>
            Task.FromException(new HttpRequestException($"Invalid request: {RelatedQuestion} {echoed}", inner: null, HttpStatusCode.BadRequest));
        var sentinel = NewSentinel(setup.AssistantId, history);

        await PostAsync(observed.CreateClient(), setup, [Text(UserSource(NewUserId()), RelatedQuestion, "reply-token-quoted"), sentinel.Event]);

        await EventuallyAsync(() => history.Get(sentinel.Chat).Count == 0, "the delivery to be processed");
        var reply = Replies(setup).ShouldHaveSingleItem();
        reply.Json.GetProperty("messages")[0].GetProperty("text").GetString().ShouldBe(LineAnswerMessages.FailedReply);
        var logs = telemetry.Logs.Concat(telemetry.OpenTelemetryLogs).ToList();
        logs.ShouldNotContain(line => line.Contains(echoed, StringComparison.Ordinal));
        logs.ShouldNotContain(line => line.Contains(RelatedQuestion, StringComparison.Ordinal));
        var failures = logs.Where(line => line.Contains("could not be answered", StringComparison.Ordinal)).ToList();
        failures.ShouldNotBeEmpty("the failure is logged (by the plain logger and by OpenTelemetry's)");
        failures.ShouldAllBe(line => line.Contains("HttpRequestException (HTTP 400)", StringComparison.Ordinal));
    }

    // --- Reply deadline and push ----------------------------------------------------------------------

    /// <summary>Acceptance: past the reply deadline a one-to-one answer is pushed and counted on the
    /// channel (「本月補送次數」) and as a metric; a group's is not pushed (decision A).</summary>
    [Fact]
    public async Task Past_the_reply_deadline_a_one_to_one_answer_is_pushed_and_counted_and_a_groups_is_dropped()
    {
        var setup = await SetUpAsync();
        var userId = NewUserId();
        _host.Model.Hooks[setup.AssistantId] = _ =>
        {
            // The model takes longer than the 50-second deadline.
            _host.Clock.Advance(TimeSpan.FromSeconds(51));
            return Task.CompletedTask;
        };
        long pushMetrics = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == SmartAgriMeter.Name && instrument.Name == LineAnswerMetrics.PushFallbacksInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref pushMetrics, value));
        listener.Start();

        await PostAsync(setup, Text(UserSource(userId), RelatedQuestion, "reply-token-late-1"));
        await EventuallyAsync(() => Pushes(setup).Count == 1, "the pushed answer");
        await PostAsync(setup, Text(UserSource(userId), RelatedQuestion, "reply-token-late-2"));
        await EventuallyAsync(() => Pushes(setup).Count == 2, "the second pushed answer");

        var requests = Line.RequestsWith(setup.Bot.AccessToken);
        requests.Select(request => request.Endpoint).ShouldBe(
            [FakeLineServer.StartLoading, FakeLineServer.Push, FakeLineServer.StartLoading, FakeLineServer.Push]);
        var push = requests[1].Json;
        push.GetProperty("to").GetString().ShouldBe(userId);
        push.GetProperty("messages").GetArrayLength().ShouldBe(2);
        push.GetProperty("messages")[0].GetProperty("text").GetString().ShouldBe(FakeAnswerText);
        await EventuallyAsync(() => Interlocked.Read(ref pushMetrics) >= 2, "the push fallback metric");

        await using (var dbContext = _host.Postgres.CreateDbContext(setup.OrganizationId))
        {
            var channel = await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == setup.AssistantId, CancellationToken);
            channel.PushFallbackCount.ShouldBe(2);
            channel.PushFallbackMonth.ShouldNotBeNull().ShouldMatch(@"^\d{4}-\d{2}$");
        }

        // A group: no reply (too late) and no push.
        var groupId = "C" + Guid.NewGuid().ToString("N");
        var sentinel = NewSentinel(setup.AssistantId);
        await PostAsync(
            setup,
            Text(GroupSource(groupId, userId), BotMention + " " + RelatedQuestion, "reply-token-late-group",
                mentionees: """[{"index":0,"length":5,"type":"user","isSelf":true}]"""),
            sentinel.Event);
        await ProcessedAsync(sentinel);
        _host.Model.CallsFor(setup.AssistantId).Count.ShouldBe(3, "the group question was answered, too late");
        Line.RequestsWith(setup.Bot.AccessToken).Count.ShouldBe(4, "nothing sent for the group");
        _host.History.Get(new LineChatKey(setup.AssistantId, groupId)).ShouldBeEmpty("an answer nobody saw is not remembered");
        await using (var dbContext = _host.Postgres.CreateDbContext(setup.OrganizationId))
        {
            (await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == setup.AssistantId, CancellationToken))
                .PushFallbackCount.ShouldBe(2);
        }
    }

    [Fact]
    public async Task A_refused_reply_token_falls_back_to_push_but_lines_429_is_dropped_without_a_retry()
    {
        var setup = await SetUpAsync();
        var userId = NewUserId();
        Line.Script(setup.Bot.AccessToken, FakeLineServer.Reply, LineBehavior.Answer(HttpStatusCode.BadRequest, """{"message":"Invalid reply token"}"""));

        await PostAsync(setup, Text(UserSource(userId), RelatedQuestion, "reply-token-expired"));
        await EventuallyAsync(() => Pushes(setup).Count == 1, "the pushed answer");
        Line.RequestsWith(setup.Bot.AccessToken).Select(request => request.Endpoint)
            .ShouldBe([FakeLineServer.StartLoading, FakeLineServer.Reply, FakeLineServer.Push]);

        Line.Script(setup.Bot.AccessToken, FakeLineServer.Reply, LineBehavior.RateLimited);
        var sentinel = NewSentinel(setup.AssistantId);
        await PostAsync(setup, Text(UserSource(userId), RelatedQuestion, "reply-token-429"), sentinel.Event);
        await ProcessedAsync(sentinel);
        Line.RequestsWith(setup.Bot.AccessToken).Select(request => request.Endpoint).Skip(3)
            .ShouldBe([FakeLineServer.StartLoading, FakeLineServer.Reply], "dropped: no push, no second reply");

        await using var dbContext = _host.Postgres.CreateDbContext(setup.OrganizationId);
        (await dbContext.AssistantLineChannels.AsNoTracking().SingleAsync(row => row.AssistantId == setup.AssistantId, CancellationToken))
            .PushFallbackCount.ShouldBe(1);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private sealed record Setup(Guid OrganizationId, Guid AssistantId, LineBot Bot);

    private sealed record Sentinel(LineChatKey Chat, string Event);

    private static string NewUserId() => "U" + Guid.NewGuid().ToString("N");

    private static List<(ChatRole Role, string? Text)> History(RecordedModelCall call) =>
        [.. call.Messages.Skip(1).SkipLast(1).Select(message => (message.Role, (string?)message.Text))];

    private async Task AskAsync(Setup setup, string userId, string question, string messageId, int expectedReplies)
    {
        await PostAsync(setup, Text(UserSource(userId), question, "reply-token-" + messageId, messageId));
        await EventuallyAsync(() => Replies(setup).Count == expectedReplies, $"the answer to {messageId}");
    }

    /// <summary>An organization whose admin owns a knowledge base with one approved document, an
    /// assistant connected to it whose acceptance passed, and a LINE channel in
    /// <paramref name="state"/> (tested, so its bot user id is stored).</summary>
    private async Task<Setup> SetUpAsync(bool showCitations = true, LineChannelState state = LineChannelState.Published)
    {
        var organization = await _host.CreateOrganizationAsync("LINE 農場");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "LINE 農場管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManagePublishing, AccountPermission.ManageDataSources);
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;

        var created = await spa.PostAsync("/api/v1/knowledge-bases", token, new { name = "對話知識庫", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = (await BodyJsonAsync(created)).GetProperty("id").GetGuid();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"# 退貨政策\n\n{ReturnClause}\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "退貨政策.md");
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-bases/{knowledgeBaseId}/documents") { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var uploaded = await spa.Http.SendAsync(upload, CancellationToken);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var versionId = (await BodyJsonAsync(uploaded)).GetProperty("latestVersionId").GetGuid();
        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        var approved = await spa.PostAsync(
            $"/api/v1/knowledge-bases/{knowledgeBaseId}/versions/approve", token, new { versionIds = new[] { versionId.ToString() } });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));

        var bot = Line.AddBot("@anxin-line");
        var protector = _host.Factory.Services.GetRequiredService<ISecretProtector>();
        var now = _host.Clock.GetUtcNow().AddHours(-2);
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var assistant = Assistant.Create(
            organization.Id, admin.Id, "退貨小幫手", "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: showCitations, keepConversations: true, now);
        dbContext.Assistants.Add(assistant);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(row => row.Id == knowledgeBaseId, CancellationToken);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        dbContext.AssistantTestCases.Add(new AssistantTestCase(
            organization.Id, assistant.Id, "退貨期限是幾天？", AssistantTestCaseCategory.Common,
            AssistantTestExpectedKind.CompanyData, [Guid.NewGuid()], null, ordinal: 1, now));
        var run = AssistantTestRun.Queue(organization.Id, assistant.Id, AssistantTestRunTrigger.Manual, now);
        run.Start("test", AuthHostFixture.ChatModel, 0.3, now);
        run.Complete(1, 0, now);
        dbContext.AssistantTestRuns.Add(run);
        var channel = new AssistantLineChannel(
            assistant, "@anxin-line", "1650000000",
            protector.Protect(AssistantLineChannel.ChannelSecretPurpose, Secret, now),
            protector.Protect(AssistantLineChannel.AccessTokenPurpose, bot.AccessToken, now),
            "歡迎！", AssistantLineChannel.DefaultNonTextReply, now);
        channel.RecordConnectionChecks(
            [.. LineConnectionCheck.All.Select(kind => new LineConnectionCheck(kind, LineConnectionCheckState.Passed, "通過。"))], bot.UserId, now);
        if (state != LineChannelState.Draft)
        {
            channel.Publish(admin.Id, now);
        }

        if (state == LineChannelState.Paused)
        {
            channel.SetPaused(true, now);
        }

        dbContext.AssistantLineChannels.Add(channel);
        await dbContext.SaveChangesAsync(CancellationToken);
        return new Setup(organization.Id, assistant.Id, bot);
    }

    private async Task<(int Threads, int Messages)> CountsAsync(Setup setup)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(setup.OrganizationId);
        return (await dbContext.ChatThreads.CountAsync(CancellationToken), await dbContext.ChatMessages.CountAsync(CancellationToken));
    }

    private Sentinel NewSentinel(Guid assistantId, ILineConversationHistory? history = null)
    {
        var chat = new LineChatKey(assistantId, NewUserId());
        (history ?? _host.History).Append(chat, null, new ConversationTurn(ConversationAuthor.Account, "sentinel"));
        return new Sentinel(chat, Event("unfollow", UserSource(chat.ChatId), null, null));
    }

    private Task ProcessedAsync(Sentinel sentinel) =>
        EventuallyAsync(() => _host.History.Get(sentinel.Chat).Count == 0, "the delivery to be processed up to its sentinel");

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new ShouldAssertException($"Timed out waiting for {what}.");
            }

            await Task.Delay(20, CancellationToken);
        }
    }

    private IReadOnlyList<LineRequest> Replies(Setup setup) =>
        [.. Line.RequestsWith(setup.Bot.AccessToken).Where(request => request.Endpoint == FakeLineServer.Reply)];

    private IReadOnlyList<LineRequest> Pushes(Setup setup) =>
        [.. Line.RequestsWith(setup.Bot.AccessToken).Where(request => request.Endpoint == FakeLineServer.Push)];

    private Task PostAsync(Setup setup, params string[] events) => PostAsync(_host.Factory.CreateClient(), setup, events);

    private static async Task PostAsync(HttpClient client, Setup setup, string[] events)
    {
        var body = $$"""{"destination":"{{setup.Bot.UserId}}","events":[{{string.Join(",", events)}}]}""";
        var bytes = Encoding.UTF8.GetBytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/line/webhook/{setup.AssistantId}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("x-line-signature", LineWebhookSignature.Compute(Secret, bytes));
        var response = await client.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();

    // LINE's documented event shapes (reference: "Webhook event objects").

    private static string UserSource(string userId) => $$"""{"type":"user","userId":"{{userId}}"}""";

    private static string GroupSource(string groupId, string userId) => $$"""{"type":"group","groupId":"{{groupId}}","userId":"{{userId}}"}""";

    private static string Event(string type, string source, string? replyToken, string? extra) =>
        $$"""
        {"type":"{{type}}","mode":"active","timestamp":1759737600000,"source":{{source}},
         "webhookEventId":"{{"01J" + Guid.NewGuid().ToString("N")[..23].ToUpperInvariant()}}",
         "deliveryContext":{"isRedelivery":false}{{(replyToken is null ? "" : $$""","replyToken":"{{replyToken}}" """)}}{{extra}}}
        """;

    private static string Text(string source, string text, string replyToken, string? messageId = null, string? mentionees = null)
    {
        var message = mentionees is null
            ? JsonSerializer.Serialize(new { id = messageId ?? "m-" + Guid.NewGuid().ToString("N"), type = "text", quoteToken = "q", text })
            : $$"""{"id":"{{messageId ?? "m-" + Guid.NewGuid().ToString("N")}}","type":"text","quoteToken":"q","text":{{JsonSerializer.Serialize(text)}},"mention":{"mentionees":"""
              + mentionees + "}}";
        return Event("message", source, replyToken, $$""","message":{{message}}""");
    }
}

/// <summary>The Api host of <see cref="LineAnswerTests"/>: the usual one, with its chat model wrapped
/// by <see cref="RecordingChatModel"/> (the real per-organization client underneath).</summary>
public sealed class LineAnswerHostFixture : AuthHostFixture
{
    public RecordingChatModel Model { get; } = new();

    public ILineConversationHistory History => Factory.Services.GetRequiredService<ILineConversationHistory>();

    protected override void ConfigureServices(IServiceCollection services)
    {
        var original = services.Last(descriptor => descriptor.ServiceType == typeof(IChatClient));
        services.Remove(original);
        services.AddScoped<IChatClient>(provider =>
            new RecordingChatClient((IChatClient)original.ImplementationFactory!(provider), Model));
    }
}

/// <summary>One model call: the assistant it was attributed to and the messages it was given.</summary>
public sealed record RecordedModelCall(Guid? AssistantId, IReadOnlyList<ChatMessage> Messages);

/// <summary>The model calls of a host, per assistant, and what each assistant's calls should do first
/// (e.g. wait, or move the clock).</summary>
public sealed class RecordingChatModel
{
    private readonly ConcurrentQueue<RecordedModelCall> _calls = new();

    public ConcurrentDictionary<Guid, Func<CancellationToken, Task>> Hooks { get; } = new();

    public IReadOnlyList<RecordedModelCall> CallsFor(Guid assistantId) => [.. _calls.Where(call => call.AssistantId == assistantId)];

    internal async Task RecordAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var assistantId = ModelInvocationAttribution.From(options)?.AssistantId;
        _calls.Enqueue(new RecordedModelCall(assistantId, [.. messages]));
        if (assistantId is { } id && Hooks.TryGetValue(id, out var hook))
        {
            await hook(cancellationToken);
        }
    }
}

internal sealed class RecordingChatClient(IChatClient inner, RecordingChatModel model) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        await model.RecordAsync(list, options, cancellationToken);
        return await base.GetResponseAsync(list, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        await model.RecordAsync(list, options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            yield return update;
        }
    }
}
