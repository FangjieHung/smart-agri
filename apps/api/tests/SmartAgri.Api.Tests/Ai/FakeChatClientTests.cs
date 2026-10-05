using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// <c>FakeChatClient</c>'s scripted answers (M3 plan, Slice 4): reproducible, no network, and
/// able to exercise every branch the answer pipeline (Slice 5) will validate against.
/// </summary>
public sealed class FakeChatClientTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_no_directive_it_cites_the_first_passage()
    {
        using var client = new FakeChatClient("fake-chat");

        var response = await client.GetResponseAsync(WithPassages(2, "問題？"), cancellationToken: CancellationToken);

        response.Text.ShouldContain("[1]");
        response.Usage.ShouldNotBeNull();
        response.Usage!.InputTokenCount.ShouldNotBeNull().ShouldBeGreaterThan(0);
        response.Usage.OutputTokenCount.ShouldNotBeNull().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task With_no_passages_and_no_directive_it_cites_nothing()
    {
        using var client = new FakeChatClient("fake-chat");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "問題？")], cancellationToken: CancellationToken);

        response.Text.ShouldNotContain("[");
    }

    [Fact]
    public async Task Invalid_citation_directive_cites_past_the_last_supplied_passage()
    {
        using var client = new FakeChatClient("fake-chat");

        var response = await client.GetResponseAsync(WithPassages(2, $"問題？ {FakeChatDirectives.InvalidCitation}"), cancellationToken: CancellationToken);

        response.Text.ShouldContain("[3]");
    }

    [Fact]
    public async Task No_marker_directive_answers_with_no_citation_at_all()
    {
        using var client = new FakeChatClient("fake-chat");

        var response = await client.GetResponseAsync(WithPassages(1, $"問題？ {FakeChatDirectives.NoMarker}"), cancellationToken: CancellationToken);

        response.Text.ShouldNotContain("[");
        response.Text.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Cannot_answer_directive_answers_with_exactly_the_marker()
    {
        using var client = new FakeChatClient("fake-chat");

        var response = await client.GetResponseAsync(WithPassages(1, $"問題？ {FakeChatDirectives.CannotAnswer}"), cancellationToken: CancellationToken);

        response.Text.ShouldBe(ChatAnswerMarkers.CannotAnswer);
    }

    [Fact]
    public async Task Fail_midway_directive_fails_the_non_streaming_call_and_reports_no_usage()
    {
        using var client = new FakeChatClient("fake-chat");

        await Should.ThrowAsync<InvalidOperationException>(
            () => client.GetResponseAsync(WithPassages(1, $"問題？ {FakeChatDirectives.FailMidway}"), cancellationToken: CancellationToken));
    }

    [Fact]
    public async Task Fail_midway_directive_streams_one_chunk_then_throws_before_any_usage()
    {
        using var client = new FakeChatClient("fake-chat");
        var received = new List<ChatResponseUpdate>();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(WithPassages(1, $"問題？ {FakeChatDirectives.FailMidway}"), cancellationToken: CancellationToken))
            {
                received.Add(update);
            }
        });

        exception.Message.ShouldContain(FakeChatDirectives.FailMidway);
        received.ShouldNotBeEmpty();
        received.SelectMany(update => update.Contents).OfType<UsageContent>().ShouldBeEmpty("no usage is ever seen for a call that fails midway");
    }

    [Fact]
    public async Task Streaming_splits_the_citation_marker_across_two_chunks_and_reports_usage_at_the_end()
    {
        using var client = new FakeChatClient("fake-chat");
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in client.GetStreamingResponseAsync(WithPassages(1, "問題？"), cancellationToken: CancellationToken))
        {
            updates.Add(update);
        }

        updates.Count.ShouldBeGreaterThanOrEqualTo(3, "at least two text chunks plus a final usage-only chunk");
        var textChunks = updates.Where(update => !string.IsNullOrEmpty(update.Text)).Select(update => update.Text).ToList();
        textChunks.Any(chunk => chunk.EndsWith('[')).ShouldBeTrue("one chunk ends right at the marker's opening bracket");
        textChunks.Any(chunk => chunk.StartsWith("1]", StringComparison.Ordinal)).ShouldBeTrue("the next chunk starts with the digit and closing bracket");
        string.Concat(textChunks).ShouldContain("[1]");

        var usage = updates.SelectMany(update => update.Contents).OfType<UsageContent>().ShouldHaveSingleItem();
        usage.Details.OutputTokenCount.ShouldNotBeNull().ShouldBeGreaterThan(0);
        usage.Details.InputTokenCount.ShouldNotBeNull().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Cancellation_before_the_call_throws_without_answering()
    {
        using var client = new FakeChatClient("fake-chat");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => client.GetResponseAsync(WithPassages(1, "問題？"), cancellationToken: cancelled.Token));
    }

    [Fact]
    public void It_reports_the_configured_model()
    {
        using var client = new FakeChatClient("fake-chat");

        var metadata = client.GetService<ChatClientMetadata>().ShouldNotBeNull();

        (metadata.ProviderName, metadata.DefaultModelId).ShouldBe(("fake", "fake-chat"));
    }

    [Fact]
    public async Task With_tools_it_calls_the_directed_tool_or_chooses_deterministically_or_none()
    {
        using var client = new FakeChatClient("fake-chat");
        var databaseId = Guid.NewGuid();
        var source = new SmartAgri.Application.Databases.DatabaseQueryToolSource(
            databaseId, "回報資料庫", [new("field-count", "數量", SmartAgri.Domain.Databases.DatabaseFieldType.Number, "件")]);
        var options = new ChatOptions { Tools = [.. SmartAgri.Application.Databases.DatabaseQueryTools.Declarations([source])] };

        var directed = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, $"幾筆？ {FakeChatDirectives.Query}{{\"name\":\"run_sql\",\"arguments\":{{\"sql\":\"SELECT 1\"}}}}")],
            options, CancellationToken);
        var call = directed.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ShouldHaveSingleItem();
        call.Name.ShouldBe("run_sql");
        ((System.Text.Json.JsonElement)call.Arguments!["sql"]!).GetString().ShouldBe("SELECT 1");

        var chosen = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "上個月的數量加總？")], options, CancellationToken);
        var sum = chosen.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ShouldHaveSingleItem();
        sum.Name.ShouldBe("database_field_sum");
        sum.Arguments!.ShouldBe(new Dictionary<string, object?> { ["databaseId"] = databaseId.ToString(), ["period"] = "last-month", ["fieldId"] = "field-count" }, ignoreOrder: true);

        var none = await client.GetResponseAsync([new ChatMessage(ChatRole.User, $"幾筆？ {FakeChatDirectives.NoQuery}")], options, CancellationToken);
        none.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ShouldBeEmpty();
        none.Usage.ShouldNotBeNull();
    }

    [Fact]
    public async Task With_the_form_tool_it_follows_the_keyword_gate_unless_a_directive_says_otherwise()
    {
        using var client = new FakeChatClient("fake-chat");
        var formId = Guid.NewGuid();
        var options = new ChatOptions
        {
            Tools = [SmartAgri.Application.Assistants.AssistantFormRequestRules.Declaration([new(formId, "田間異常回報", "記錄異常")])],
        };

        async Task<FunctionCallContent?> CallAsync(string question) =>
            (await client.GetResponseAsync([new ChatMessage(ChatRole.User, question)], options, CancellationToken))
                .Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().SingleOrDefault();

        var keyword = (await CallAsync("我要回報病蟲害")).ShouldNotBeNull();
        keyword.Name.ShouldBe("request_database_form");
        keyword.Arguments!.ShouldBe(new Dictionary<string, object?> { ["databaseId"] = formId.ToString() });
        (await CallAsync("番茄葉子黃了是什麼原因？")).ShouldBeNull();
        (await CallAsync($"葉子出現黃斑 {FakeChatDirectives.FormRequest}")).ShouldNotBeNull().Name.ShouldBe("request_database_form");
        (await CallAsync($"我要回報 {FakeChatDirectives.NoForm}")).ShouldBeNull();
        // #query-none is about the query tools only.
        (await CallAsync($"我要回報 {FakeChatDirectives.NoQuery}")).ShouldNotBeNull();
    }

    private static List<ChatMessage> WithPassages(int count, string question)
    {
        var passages = string.Join('\n', Enumerable.Range(1, count).Select(index => $"[{index}] 段落內容 {index}"));
        return
        [
            new ChatMessage(ChatRole.System, $"以下是可用的段落：\n{passages}\n只能根據段落回答，每句事實標註 [n]。"),
            new ChatMessage(ChatRole.User, question),
        ];
    }
}
