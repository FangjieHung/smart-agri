using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Chat;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Chat;

/// <summary>
/// Asking an assistant about a connected database's records in a conversation (M4 #149) against
/// real PostgreSQL with the <c>Fake</c> models: the model only chooses a fixed query tool and its
/// parameters (scripted with <c>#query:</c>); the server re-checks the assistant's connection, the
/// owner's and the <b>asker's</b> access on every request, runs the query as the asker, and answers
/// with the server's own numbers (period, metric, source); every refusal is the same and reveals
/// nothing; undefined parameters run nothing; too little data, a tool failure and a model failure
/// each have their own result; saving and usage follow the conversation's rules; the form request
/// (#148) and the query coexist; every query answer writes exactly one <c>database-query</c>
/// answer outcome with its result category only (#178).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class ChatDatabaseQueryEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Chat-Queries-Pass-1!";
    private const string AssistantsPath = "/api/v1/assistants";
    private const string DatabasesPath = "/api/v1/databases";
    private const string CountField = "field-completed-count";
    private const string DatabaseName = "回報資料庫";

    private readonly AuthHostFixture _host;

    public ChatDatabaseQueryEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(string instant) =>
        DateTimeOffset.Parse(instant, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();

    private static string Directive(string tool, object arguments) =>
        "#query:" + JsonSerializer.Serialize(new { name = tool, arguments });

    private static string CountDirective(Guid databaseId, string from = "2026-02-01", string to = "2026-02-28") =>
        Directive("database_record_count", new { databaseId, from, to });

    // --- Answers ------------------------------------------------------------------------------

    [Fact]
    public async Task A_designated_member_gets_the_servers_numbers_with_period_metric_and_source_and_the_usage_is_recorded()
    {
        var setup = await CreateSetupAsync(keepConversations: true);
        var (asker, assistantId, databaseId) = (setup.Internal, setup.AssistantId, setup.DatabaseId);

        // Count: the reply's figures are what the fixed query endpoint returns for the same parameters.
        var count = await RunAsync(asker, assistantId, $"二月有幾筆紀錄？ {CountDirective(databaseId)}");
        count.Status.ShouldBe(HttpStatusCode.OK, count.Body);
        var reply = count.Reply!.Value;
        OpenApiContract.AssertKeysMatchSchema(reply, "ChatMessageView");
        var view = Query(reply);
        OpenApiContract.AssertKeysMatchSchema(view, "ChatDatabaseQueryView");
        reply.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("database-query");
        view.GetProperty("status").GetString().ShouldBe("answered");
        (view.GetProperty("databaseId").GetGuid(), view.GetProperty("databaseName").GetString()).ShouldBe((databaseId, DatabaseName));
        (view.GetProperty("query").GetString(), view.GetProperty("queryLabel").GetString()).ShouldBe(("record-count", "紀錄筆數"));
        view.GetProperty("period").GetProperty("label").GetString().ShouldBe("2026-02-01 至 2026-02-28");
        var endpoint = await BodyJsonAsync(await asker.Spa.GetAsync(
            $"{DatabasesPath}/{databaseId}/queries/record-count?from=2026-02-01&to=2026-02-28", asker.Token));
        var figure = view.GetProperty("figures")[0];
        figure.GetProperty("value").GetDouble().ShouldBe(endpoint.GetProperty("count").GetInt32());
        figure.GetProperty("value").GetDouble().ShouldBe(3);
        figure.GetProperty("display").GetString().ShouldBe("3 筆");
        figure.GetProperty("changeLabel").GetString().ShouldBe(endpoint.GetProperty("changeLabel").GetString());
        var text = reply.GetProperty("reply").GetProperty("text").GetString()!;
        text.ShouldBe("根據「回報資料庫」的紀錄筆數查詢：2026-02-01 至 2026-02-28共有 3 筆有效紀錄；前一期（2026-01-04 至 2026-01-31）為 1 筆，變化 +2 筆。");
        var threadId = count.ThreadId!.Value;

        // Sum of a number field, same rule.
        var sum = await RunAsync(asker, assistantId,
            $"二月完成數量加總？ {Directive("database_field_sum", new { databaseId, from = "2026-02-01", to = "2026-02-28", fieldId = CountField })}",
            threadId.ToString());
        var sumView = Query(sum.Reply!.Value);
        var sumEndpoint = await BodyJsonAsync(await asker.Spa.GetAsync(
            $"{DatabasesPath}/{databaseId}/queries/field-sum?from=2026-02-01&to=2026-02-28&fieldId={CountField}", asker.Token));
        sumView.GetProperty("figures")[0].GetProperty("display").GetString().ShouldBe(sumEndpoint.GetProperty("field").GetProperty("display").GetString());
        sumView.GetProperty("figures")[0].GetProperty("display").GetString().ShouldBe("12 件");
        sum.Reply!.Value.GetProperty("reply").GetProperty("text").GetString()!.ShouldContain("「本期完成數量」加總為 12 件");

        // No directive: the fake model chooses by itself (record count, the named period); nothing
        // in the last 30 days is "no data", with the zero still stated.
        var empty = await RunAsync(asker, assistantId, "近30天有幾筆紀錄？", threadId.ToString());
        var emptyView = Query(empty.Reply!.Value);
        emptyView.GetProperty("status").GetString().ShouldBe("no-data");
        emptyView.GetProperty("period").GetProperty("period").GetString().ShouldBe("last-30-days");
        emptyView.GetProperty("figures")[0].GetProperty("display").GetString().ShouldBe("0 筆");

        // Saved and read back the same while the asker may still query the database.
        var chat = await BodyJsonAsync(await asker.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", asker.Token));
        var messages = chat.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(6);
        JsonNode.DeepEquals(JsonNode.Parse(messages[1].GetRawText()), JsonNode.Parse(reply.GetRawText())).ShouldBeTrue();

        // Usage: one selection call per question, purpose database-query, attributed, no answer call.
        await using (var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id))
        {
            var invocations = await dbContext.ModelInvocations.AsNoTracking().ToListAsync(CancellationToken);
            invocations.Count.ShouldBe(3);
            invocations.ShouldAllBe(invocation => invocation.Purpose == ModelInvocationPurpose.DatabaseQuery
                && invocation.AccountId == setup.InternalAccountId
                && invocation.AssistantId == assistantId
                && invocation.Succeeded);
            var stored = await dbContext.ChatMessages.AsNoTracking()
                .Where(message => message.ReplyKind == ChatReplyKind.DatabaseQuery).CountAsync(CancellationToken);
            stored.ShouldBe(3);
        }

        // A query answer is never handed off: its figures come from records only the asker may read.
        var handoff = await asker.Spa.PostAsync($"{AssistantsPath}/{assistantId}/chat/handoffs", asker.Token, new
        {
            threadId,
            questionMessageId = messages[0].GetProperty("id").GetGuid(),
            answerMessageId = messages[1].GetProperty("id").GetGuid(),
            confirmed = true,
        });
        handoff.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(handoff)).GetProperty("reason").GetString().ShouldBe("chat-thread");
    }

    // --- Refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task Every_refusal_is_the_same_reveals_nothing_and_takes_effect_on_the_next_request()
    {
        var setup = await CreateSetupAsync(keepConversations: true);
        var (asker, assistantId, databaseId) = (setup.Internal, setup.AssistantId, setup.DatabaseId);
        var answered = await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(databaseId)}");
        Query(answered.Reply!.Value).GetProperty("status").GetString().ShouldBe("answered");
        var threadId = answered.ThreadId!.Value.ToString();

        // A member who may use the assistant but not read the database: no model call, nothing named.
        var member = await RunAsync(setup.Member, assistantId, $"二月幾筆？ {CountDirective(databaseId)}");
        var refusal = AssertNotAvailable(member);

        // Another organization's database, a made-up one: the same reply.
        var orgB = await CreateOrganizationAsync("組織 B");
        var foreignDatabase = await CreateDatabaseAsync(await SignInAsync(orgB, "admin"));
        foreach (var other in new[] { foreignDatabase, Guid.NewGuid() })
        {
            AssertSameRefusal(await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(other)}", threadId), refusal);
        }

        // Designation removed: refused on the next request, and the saved answer reads back refused too.
        (await PutAccessAsync(setup.Admin, databaseId, [setup.Org.Admin.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertSameRefusal(await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(databaseId)}", threadId), refusal);
        var chat = await BodyJsonAsync(await asker.Spa.GetAsync($"{AssistantsPath}/{assistantId}/chat?conversation={threadId}", asker.Token));
        var hidden = chat.GetProperty("messages")[1].GetProperty("reply");
        hidden.GetProperty("text").GetString().ShouldBe(DatabaseQueryTools.NotAvailableText);
        hidden.GetProperty("databaseQuery").GetRawText().ShouldBe(refusal.GetRawText());
        chat.GetRawText().ShouldNotContain(DatabaseName);

        // Designated again but the read permission revoked: refused.
        (await PutAccessAsync(setup.Admin, databaseId, [setup.Org.Admin.Id, setup.InternalAccountId])).StatusCode.ShouldBe(HttpStatusCode.OK);
        Query((await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(databaseId)}", threadId)).Reply!.Value)
            .GetProperty("status").GetString().ShouldBe("answered");
        await SetPermissionsAsync(setup.Admin, setup.InternalAccountId, [AccountPermission.UseSharedAssistants]);
        AssertSameRefusal(await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(databaseId)}", threadId), refusal);

        // Permission back, database disconnected: the assistant has nothing to query, so the
        // question is answered as usual — no query reply, no database named.
        await SetPermissionsAsync(setup.Admin, setup.InternalAccountId, [AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions]);
        (await setup.Admin.Spa.DeleteAsync($"{AssistantsPath}/{assistantId}/sources/database/{databaseId}", setup.Admin.Token))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var disconnected = await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(databaseId)}", threadId);
        disconnected.Status.ShouldBe(HttpStatusCode.OK, disconnected.Body);
        disconnected.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldNotBe("database-query");
        disconnected.Body.ShouldNotContain(DatabaseName);

        // The member made no model call at all.
        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        (await dbContext.ModelInvocations.CountAsync(invocation => invocation.AccountId == setup.MemberAccountId, CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Parameters_outside_the_definitions_are_rejected_and_nothing_runs()
    {
        var setup = await CreateSetupAsync(keepConversations: false);
        var databaseId = setup.DatabaseId;
        var attempts = new (string Directive, bool NamesDatabase)[]
        {
            (Directive("database_record_count", new { databaseId, period = "this-month", sql = "SELECT * FROM \"DatabaseSubmissions\"" }), true),
            (Directive("database_record_count", new { databaseId, period = "this-month", fieldId = CountField }), true),
            (Directive("database_record_count", new { databaseId, period = "yesterday" }), true),
            (Directive("database_field_sum", new { databaseId, period = "this-month", fieldId = "field-report-date" }), true),
            (Directive("database_field_sum", new { databaseId, period = "this-month" }), true),
            (Directive("database_subject_comparison", new { databaseId, subjectId = Guid.NewGuid() }), true),
            (Directive("run_sql", new { databaseId, query = "SELECT 1" }), false),
        };

        foreach (var (directive, namesDatabase) in attempts)
        {
            var run = await RunAsync(setup.Internal, setup.AssistantId, $"統計一下 {directive}");
            run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
            var reply = run.Reply!.Value.GetProperty("reply");
            reply.GetProperty("text").GetString().ShouldBe(DatabaseQueryTools.RejectedText, directive);
            var view = reply.GetProperty("databaseQuery");
            view.GetProperty("status").GetString().ShouldBe("rejected", directive);
            view.GetProperty("figures").GetArrayLength().ShouldBe(0);
            view.GetProperty("period").ValueKind.ShouldBe(JsonValueKind.Null);
            view.GetProperty("databaseName").ValueKind.ShouldBe(namesDatabase ? JsonValueKind.String : JsonValueKind.Null, directive);
        }
    }

    [Fact]
    public async Task Too_few_records_to_compare_is_insufficient_data()
    {
        var setup = await CreateSetupAsync(keepConversations: false);
        var single = await CreateCustomerAsync(setup.Org, "customer-single");
        await SeedAsync(setup.Org, setup.DatabaseId, single, At("2026-02-10T10:00:00Z"), 4);

        var run = await RunAsync(setup.Internal, setup.AssistantId,
            $"這位的趨勢？ {Directive("database_subject_comparison", new { databaseId = setup.DatabaseId, subjectId = single })}");
        var view = Query(run.Reply!.Value);
        view.GetProperty("status").GetString().ShouldBe("insufficient-data");
        view.GetProperty("figures").GetArrayLength().ShouldBe(0);
        view.GetProperty("message").GetString().ShouldBe("目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。");
        run.Reply!.Value.GetProperty("reply").GetProperty("text").GetString()!.ShouldContain("資料不足");
    }

    // --- Failures -----------------------------------------------------------------------------

    [Fact]
    public async Task A_model_failure_ends_the_run_with_an_error_and_a_tool_failure_is_a_failed_reply()
    {
        var setup = await CreateSetupAsync(keepConversations: true);

        var failed = await RunAsync(setup.Internal, setup.AssistantId, "二月幾筆？ #fail-midway");
        failed.Status.ShouldBe(HttpStatusCode.OK);
        failed.Reply.ShouldBeNull();
        var error = failed.Events.Single(e => e.GetProperty("type").GetString() == "RUN_ERROR");
        error.GetProperty("code").GetString().ShouldBe("chat-unavailable");
        await using (var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id))
        {
            (await dbContext.ChatMessages.CountAsync(CancellationToken)).ShouldBe(1, "only the question");
            var invocation = await dbContext.ModelInvocations.SingleAsync(CancellationToken);
            (invocation.Purpose, invocation.Succeeded).ShouldBe((ModelInvocationPurpose.DatabaseQuery, false));

            // #178: a model failure is one failed database-query outcome, nothing else.
            var outcome = await dbContext.AnswerOutcomes.AsNoTracking().SingleAsync(CancellationToken);
            (outcome.ReplyKind, outcome.DatabaseQueryResult, outcome.AssistantId, outcome.Channel)
                .ShouldBe((AnswerReplyKind.DatabaseQuery, (AnswerDatabaseQueryResult?)AnswerDatabaseQueryResult.Failed, (Guid?)setup.AssistantId, AnswerOutcomeChannel.Chat));
        }

        await using var failing = _host.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddScoped<IChatDatabaseQueryRunner, FailingRunner>()));
        var spa = new SpaClient(failing.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = await spa.SignInAsync(setup.Org.Organization.Code, "internal", Password);
        var broken = await RunAsync(new SignedIn(spa, token.AccessToken), setup.AssistantId, $"二月幾筆？ {CountDirective(setup.DatabaseId)}");
        broken.Status.ShouldBe(HttpStatusCode.OK, broken.Body);
        var reply = broken.Reply!.Value.GetProperty("reply");
        reply.GetProperty("text").GetString().ShouldBe(DatabaseQueryTools.FailedText);
        reply.GetProperty("databaseQuery").GetProperty("status").GetString().ShouldBe("failed");
        reply.GetProperty("databaseQuery").GetProperty("figures").GetArrayLength().ShouldBe(0);

        // #178: a tool failure is a second failed database-query outcome.
        await using var outcomes = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        (await outcomes.AnswerOutcomes.AsNoTracking().Select(outcome => new { outcome.ReplyKind, outcome.DatabaseQueryResult }).ToListAsync(CancellationToken))
            .ShouldAllBe(outcome => outcome.ReplyKind == AnswerReplyKind.DatabaseQuery && outcome.DatabaseQueryResult == AnswerDatabaseQueryResult.Failed);
        (await outcomes.AnswerOutcomes.CountAsync(CancellationToken)).ShouldBe(2);
    }

    // --- Answer outcomes (#178) -----------------------------------------------------------------

    [Fact]
    public async Task Every_query_answer_records_exactly_one_database_query_outcome_with_its_result_only()
    {
        var setup = await CreateSetupAsync(keepConversations: false);
        var single = await CreateCustomerAsync(setup.Org, "customer-outcome");
        await SeedAsync(setup.Org, setup.DatabaseId, single, At("2026-02-10T10:00:00Z"), 4);

        var cases = new (SignedIn Asker, string Question, AnswerDatabaseQueryResult Expected)[]
        {
            (setup.Internal, $"二月幾筆？ {CountDirective(setup.DatabaseId)}", AnswerDatabaseQueryResult.Answered),
            // no-data (nothing in the last 30 days) and insufficient-data (too few to compare).
            (setup.Internal, "近30天有幾筆紀錄？", AnswerDatabaseQueryResult.InsufficientRecords),
            (setup.Internal, $"這位的趨勢？ {Directive("database_subject_comparison", new { databaseId = setup.DatabaseId, subjectId = single })}",
                AnswerDatabaseQueryResult.InsufficientRecords),
            // A member who may not read the records (no model call) and a database not offered.
            (setup.Member, $"二月幾筆？ {CountDirective(setup.DatabaseId)}", AnswerDatabaseQueryResult.NotPermitted),
            (setup.Internal, $"二月幾筆？ {CountDirective(Guid.NewGuid())}", AnswerDatabaseQueryResult.NotPermitted),
            // Outside the definitions (rejected) and a model failure.
            (setup.Internal, $"統計一下 {Directive("run_sql", new { databaseId = setup.DatabaseId, query = "SELECT 1" })}", AnswerDatabaseQueryResult.Failed),
            (setup.Internal, "二月幾筆？ #fail-midway", AnswerDatabaseQueryResult.Failed),
        };

        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        var seen = new HashSet<Guid>();
        foreach (var (asker, question, expected) in cases)
        {
            var run = await RunAsync(asker, setup.AssistantId, question);
            run.Status.ShouldBe(HttpStatusCode.OK, run.Body);

            var added = await dbContext.AnswerOutcomes.AsNoTracking()
                .Where(outcome => !seen.Contains(outcome.Id)).ToListAsync(CancellationToken);
            var outcome = added.ShouldHaveSingleItem(question);
            seen.Add(outcome.Id);
            (outcome.ReplyKind, outcome.DatabaseQueryResult, outcome.Channel, outcome.AssistantId)
                .ShouldBe((AnswerReplyKind.DatabaseQuery, (AnswerDatabaseQueryResult?)expected, AnswerOutcomeChannel.Chat, (Guid?)setup.AssistantId), question);
            (outcome.RejectionReason, outcome.CitedDocumentIds.Count).ShouldBe((null, 0), question);
        }

        // The model choosing no tool is not a query answer: no database-query outcome (the answer
        // pipeline it falls through to records its own kind).
        var fallback = await RunAsync(setup.Internal, setup.AssistantId, "本月有幾筆？ #query-none");
        fallback.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldNotBe("database-query");
        (await dbContext.AnswerOutcomes.CountAsync(
            outcome => !seen.Contains(outcome.Id) && outcome.ReplyKind == AnswerReplyKind.DatabaseQuery, CancellationToken)).ShouldBe(0);
    }

    // --- Saving and the form request -----------------------------------------------------------

    [Fact]
    public async Task Without_saved_conversations_nothing_is_written_but_the_usage()
    {
        var setup = await CreateSetupAsync(keepConversations: false);

        var run = await RunAsync(setup.Internal, setup.AssistantId, $"二月幾筆？ {CountDirective(setup.DatabaseId)}");
        Query(run.Reply!.Value).GetProperty("figures")[0].GetProperty("display").GetString().ShouldBe("3 筆");
        run.ThreadId.ShouldBeNull();

        await using var dbContext = _host.Postgres.CreateDbContext(setup.Org.Organization.Id);
        (await dbContext.ChatThreads.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.ChatMessages.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.DatabaseQuery, CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task A_statistics_question_queries_and_a_fill_in_request_asks_for_the_form()
    {
        var setup = await CreateSetupAsync(keepConversations: true);
        var target = await PatchRulesAsync(setup.Admin, setup.AssistantId, new { dataWriteDatabaseId = setup.DatabaseId.ToString(), dataWritePurpose = "彙整每週回報。" });
        target.StatusCode.ShouldBe(HttpStatusCode.OK, await target.Content.ReadAsStringAsync(CancellationToken));

        var form = await RunAsync(setup.Internal, setup.AssistantId, "我要回報這週的完成數量");
        form.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");

        // Both intents ("回報" and "幾筆"): the query comes first.
        var query = await RunAsync(setup.Internal, setup.AssistantId, "本月回報了幾筆？");
        query.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("database-query");
        Query(query.Reply!.Value).GetProperty("period").GetProperty("period").GetString().ShouldBe("this-month");

        // The model chooses no tool: the fill-in intent still gets its form.
        var fallback = await RunAsync(setup.Internal, setup.AssistantId, "本月回報了幾筆？ #query-none");
        fallback.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");

        // A member who may not read the records still gets the form, and the query is refused.
        (await RunAsync(setup.Member, setup.AssistantId, "我要回報這週的完成數量")).Reply!.Value
            .GetProperty("reply").GetProperty("kind").GetString().ShouldBe("form-request");
        AssertNotAvailable(await RunAsync(setup.Member, setup.AssistantId, "本月回報了幾筆？"));
    }

    // --- Helpers ------------------------------------------------------------------------------

    private sealed class FailingRunner : IChatDatabaseQueryRunner
    {
        public Task<DatabaseQueryOutcome<object>> RunAsync(
            DatabaseQueryKind kind, Guid accountId, Guid databaseId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database is unreachable.");
    }

    [Fact]
    public async Task An_archived_database_is_not_queried_in_chat_but_its_statistics_still_read_and_unarchiving_restores_it()
    {
        var setup = await CreateSetupAsync(keepConversations: false);
        var (asker, assistantId, databaseId) = (setup.Internal, setup.AssistantId, setup.DatabaseId);
        (await setup.Admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/archive", setup.Admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // #180: the connection is unusable, so the assistant has nothing to query — answered as usual,
        // the database never named (like a disconnected one).
        var archived = await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(databaseId)}");
        archived.Status.ShouldBe(HttpStatusCode.OK, archived.Body);
        archived.Reply!.Value.GetProperty("reply").GetProperty("kind").GetString().ShouldNotBe("database-query");
        archived.Body.ShouldNotContain(DatabaseName);

        // Reading is not affected: the fixed query endpoint (#147) answers the data manager as before.
        var endpoint = await asker.Spa.GetAsync(
            $"{DatabasesPath}/{databaseId}/queries/record-count?from=2026-02-01&to=2026-02-28", asker.Token);
        endpoint.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(endpoint)).GetProperty("count").GetInt32().ShouldBe(3);

        (await setup.Admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/unarchive", setup.Admin.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Query((await RunAsync(asker, assistantId, $"二月幾筆？ {CountDirective(databaseId)}")).Reply!.Value)
            .GetProperty("status").GetString().ShouldBe("answered");
    }

    private static JsonElement Query(JsonElement message) => message.GetProperty("reply").GetProperty("databaseQuery");

    private static JsonElement AssertNotAvailable(RecordedRun run)
    {
        run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
        var reply = run.Reply!.Value.GetProperty("reply");
        reply.GetProperty("kind").GetString().ShouldBe("database-query");
        reply.GetProperty("text").GetString().ShouldBe(DatabaseQueryTools.NotAvailableText);
        var view = reply.GetProperty("databaseQuery");
        view.GetProperty("status").GetString().ShouldBe("not-available");
        view.GetProperty("databaseId").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("figures").GetArrayLength().ShouldBe(0);
        run.Body.ShouldNotContain(DatabaseName);
        run.Body.ShouldNotContain(" 筆");
        return view.Clone();
    }

    private static void AssertSameRefusal(RecordedRun run, JsonElement refusal) =>
        AssertNotAvailable(run).GetRawText().ShouldBe(refusal.GetRawText());

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Member);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private sealed record Setup(
        TestOrganization Org, SignedIn Admin, SignedIn Internal, SignedIn Member, Guid AssistantId, Guid DatabaseId)
    {
        public Guid InternalAccountId => Org.Internal.Id;

        public Guid MemberAccountId => Org.Member.Id;
    }

    private sealed record RecordedRun(HttpStatusCode Status, string Body, IReadOnlyList<JsonElement> Events)
    {
        public JsonElement? Reply => Custom("smartagri.reply");

        public Guid? ThreadId => Custom("smartagri.thread")?.GetProperty("threadId").GetGuid();

        private JsonElement? Custom(string name) =>
            Events.Where(e => e.GetProperty("type").GetString() == "CUSTOM" && e.GetProperty("name").GetString() == name)
                .Select(e => (JsonElement?)e.GetProperty("value"))
                .SingleOrDefault();
    }

    /// <summary>The admin's database (the periodic-report template) designated to the admin and the
    /// internal employee, with records in January and February from a customer; the admin's
    /// assistant (with a knowledge base, so the database can be disconnected) connected to it and
    /// shared with the internal employee and a member who may not read records.</summary>
    private async Task<Setup> CreateSetupAsync(bool keepConversations)
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var customer = await CreateCustomerAsync(org, "customer");
        await SeedAsync(org, databaseId, customer, At("2026-01-20T02:00:00Z"), 7);
        await SeedAsync(org, databaseId, customer, At("2026-02-03T02:00:00Z"), 5);
        await SeedAsync(org, databaseId, customer, At("2026-02-14T02:00:00Z"), 7);
        await SeedAsync(org, databaseId, customer, At("2026-02-27T02:00:00Z"), null);
        var assistantId = await CreateAssistantAsync(org, keepConversations);
        var connected = await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{databaseId}", admin.Token, new { });
        connected.StatusCode.ShouldBe(HttpStatusCode.OK, await connected.Content.ReadAsStringAsync(CancellationToken));
        return new Setup(org, admin, await SignInAsync(org, "internal"), await SignInAsync(org, "member"), assistantId, databaseId);
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "查詢商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}成員", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, internalEmployee, member);
    }

    private async Task<Guid> CreateCustomerAsync(TestOrganization org, string loginName) =>
        (await _host.CreateAccountAsync(
            org.Organization, loginName, Password, AccountRole.ExternalCustomer, $"外部客戶 {loginName}",
            AccountPermission.SubmitAuthorizedForms)).Id;

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner)
    {
        var response = await owner.Spa.PostAsync(DatabasesPath, owner.Token, new { templateId = "template-periodic-report", name = DatabaseName });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateAssistantAsync(TestOrganization org, bool keepConversations)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Admin.Id, "回報小幫手", "協助查詢回報", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: keepConversations, now);
        dbContext.Assistants.Add(assistant);
        dbContext.AssistantShares.Add(new AssistantShare(assistant, org.Internal.Id));
        dbContext.AssistantShares.Add(new AssistantShare(assistant, org.Member.Id));
        var knowledgeBase = SmartAgri.Domain.Knowledge.KnowledgeBase.Create(org.Organization.Id, org.Admin.Id, "知識庫", string.Empty, now);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    /// <summary>A record of the periodic-report form as submitted at <paramref name="at"/> (a number,
    /// or the optional count left blank when <see langword="null"/>).</summary>
    private async Task SeedAsync(TestOrganization org, Guid databaseId, Guid accountId, DateTimeOffset at, double? count)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var database = await dbContext.Databases.SingleAsync(candidate => candidate.Id == databaseId, CancellationToken);
        var version = await dbContext.DatabaseFormVersions
            .Where(candidate => candidate.DatabaseId == databaseId)
            .OrderByDescending(candidate => candidate.VersionNumber)
            .FirstAsync(CancellationToken);
        var terms = new DatabaseConsentTerms(DatabaseName, "測試", "查詢商行（回報資料庫）", [], "請勿填寫敏感資料。");
        var submission = DatabaseSubmission.Create(
            database, version, accountId, Guid.NewGuid(), DatabaseSubmissionSource.FormLink, terms, at);
        dbContext.DatabaseSubmissions.Add(submission);
        for (var position = 0; position < version.Fields.Count; position++)
        {
            var field = version.Fields[position];
            dbContext.DatabaseSubmissionEntries.Add(field.Id switch
            {
                "field-report-date" => DatabaseSubmissionEntry.Create(submission, position, field, at.ToString("yyyy-MM-dd"), at.ToString("yyyy-MM-dd"), null, []),
                CountField when count is { } value =>
                    DatabaseSubmissionEntry.Create(submission, position, field, DatabaseAnswerRules.FormatNumber(value, field.Unit), null, value, []),
                _ => DatabaseSubmissionEntry.Create(submission, position, field, "未填寫", null, null, []),
            });
        }

        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static Task<HttpResponseMessage> PatchRulesAsync(SignedIn admin, Guid assistantId, object rules) =>
        admin.Spa.PatchAsync($"{AssistantsPath}/{assistantId}/settings", admin.Token, new { rules });

    private static Task<HttpResponseMessage> PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds) =>
        caller.Spa.PutAsync($"{DatabasesPath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });

    private static async Task SetPermissionsAsync(SignedIn admin, Guid accountId, AccountPermission[] permissions)
    {
        var wire = permissions.Select(SmartAgri.Domain.WireNames<AccountPermission>.ToWire).ToArray();
        var response = await admin.Spa.PutAsync($"/api/v1/team/members/{accountId}/permissions", admin.Token, new { permissions = wire });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<RecordedRun> RunAsync(SignedIn caller, Guid assistantId, string question, string? threadId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AssistantsPath}/{assistantId}/chat/runs")
        {
            Content = JsonContent.Create(new
            {
                threadId = threadId ?? string.Empty,
                runId = "run-query",
                state = new { },
                messages = new object[] { new { id = Guid.NewGuid().ToString(), role = "user", content = question } },
                tools = Array.Empty<object>(),
                context = Array.Empty<object>(),
                forwardedProps = new { },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await caller.Spa.Http.SendAsync(request, CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        IReadOnlyList<JsonElement> events = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ?
            [
                .. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                    .Select(record => string.Concat(record.Split('\n')
                        .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                        .Select(line => line["data: ".Length..])))
                    .Where(data => data.Length > 0)
                    .Select(data => JsonDocument.Parse(data).RootElement.Clone()),
            ]
            : [];
        return new RecordedRun(response.StatusCode, body, events);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
