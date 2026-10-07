using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Tests.Setup;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>
/// What a failed visitor run writes to the log (pre-launch plan §2.3, issue #307): the model
/// provider's own error can quote the question it rejected, so the log gets the exception types and
/// the HTTP status, never a message. The same rule for LINE is in <c>LineAnswerTests</c>.
/// </summary>
public sealed partial class VisitorEndpointsTests
{
    private const string EchoedByProvider = "SECRET-ECHO-9f3a7";

    /// <summary>Acceptance (#307): a provider error that quotes the visitor's question reaches neither
    /// the log nor the telemetry's logs; what stays is the error's type and HTTP status (and the
    /// control: the run did fail, and the failure was logged).</summary>
    [Fact]
    public async Task A_model_error_that_quotes_the_question_never_reaches_the_log()
    {
        var telemetry = new TelemetryCapture();
        await using var factory = _host.Factory.WithWebHostBuilder(builder =>
        {
            telemetry.Attach(builder);
            builder.ConfigureTestServices(services =>
                services.AddScoped<IChatClient>(_ => new QuotingFailureChatClient($"{RelatedQuestion} {EchoedByProvider}")));
        });
        var org = await CreateOrganizationWithKnowledgeAsync();
        var assistantId = await PublishedAssistantAsync(org, keepConversations: false);
        var visitor = VisitorClient(factory);
        var token = await SessionTokenAsync(visitor, assistantId);

        var run = await RunAsync(visitor, assistantId, token, RunInput(RelatedQuestion));

        run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
        run.Body.ShouldContain("RUN_ERROR");
        var logs = telemetry.Logs.Concat(telemetry.OpenTelemetryLogs).ToList();
        logs.ShouldNotContain(line => line.Contains(EchoedByProvider, StringComparison.Ordinal));
        logs.ShouldNotContain(line => line.Contains(RelatedQuestion, StringComparison.Ordinal));
        var failures = logs.Where(line => line.Contains("A visitor run's model call failed", StringComparison.Ordinal)).ToList();
        failures.ShouldNotBeEmpty("the failure is logged (by the plain logger and by OpenTelemetry's)");
        failures.ShouldAllBe(line => line.Contains("HttpRequestException (HTTP 400)", StringComparison.Ordinal));
    }

    /// <summary>A model whose every call fails with a provider-style error that quotes the request.</summary>
    private sealed class QuotingFailureChatClient(string quoted) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw Failure();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw Failure();
#pragma warning disable CS0162 // Unreachable: makes this an iterator.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private HttpRequestException Failure() =>
            new($"Invalid request: {quoted}", inner: null, HttpStatusCode.BadRequest);
    }
}
