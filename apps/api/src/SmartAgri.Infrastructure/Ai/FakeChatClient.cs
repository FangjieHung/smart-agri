using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Reports;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// <c>Ai:Chat:Provider=Fake</c>: a scripted, reproducible answer generator, no model and no
/// network, for local development and tests only (<see cref="ChatModelOptions.FakeEnvironments"/>;
/// any other environment refuses to start with it — <see cref="ChatModelOptions"/>).
/// </summary>
/// <remarks>
/// <para>
/// Reads the highest passage number <c>[k]</c> found anywhere in the messages it is given (the
/// generation prompt numbers the passages it hands the model, grounded-answers ADR §3) and the
/// last user message's text. If that text contains one of <see cref="FakeChatDirectives"/>, it
/// answers exactly as that directive says (see each constant's doc); otherwise it answers with a
/// generic sentence citing the first passage, <c>[1]</c>, if any passage was supplied.
/// </para>
/// <para>
/// Streaming always splits the answer into at least two chunks, and — whenever the answer
/// contains a <c>[n]</c> citation marker — splits one marker across two chunks (right after its
/// <c>[</c>), so the answer pipeline's citation scanner (M3 Slice 5) is exercised against a
/// marker that arrives in two pieces, not just whole ones in a single chunk.
/// </para>
/// <para>
/// Usage is always reported (one token per Unicode scalar of the input / of the answer, the same
/// convention as <see cref="FakeEmbeddingGenerator"/>), except for
/// <see cref="FakeChatDirectives.FailMidway"/>: no usage is ever seen for that call, the same as
/// a real provider failing before it reports any.
/// </para>
/// </remarks>
public sealed class FakeChatClient : IChatClient
{
    private static readonly Regex PassageMarker = new(@"[\[［【](\d+)[\]］】]", RegexOptions.Compiled);

    private readonly string _model;
    private readonly ChatClientMetadata _metadata;

    public FakeChatClient(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _model = model;
        _metadata = new ChatClientMetadata("fake", providerUri: null, model);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var script = FakeChatScript.Build(messages);
        if (script.Directive == FakeChatDirectives.FailMidway)
        {
            return Task.FromException<ChatResponse>(FailMidwayException());
        }

        var message = new ChatMessage(ChatRole.Assistant, script.Answer);
        var response = new ChatResponse(message)
        {
            ModelId = _model,
            FinishReason = ChatFinishReason.Stop,
            Usage = script.Usage,
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var script = FakeChatScript.Build(messages);
        foreach (var piece in script.StreamPieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece) { ModelId = _model };
            await Task.Yield();

            if (script.Directive == FakeChatDirectives.FailMidway)
            {
                throw FailMidwayException();
            }
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(script.Usage)])
        {
            ModelId = _model,
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null
            : serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
    }

    private static InvalidOperationException FailMidwayException() =>
        new($"FakeChatClient: '{FakeChatDirectives.FailMidway}' requested a mid-response failure.");

    /// <summary>What one call answers with, computed once from the messages it was given.</summary>
    private sealed record FakeChatScript(string Directive, string Answer, IReadOnlyList<string> StreamPieces, UsageDetails Usage)
    {
        public static FakeChatScript Build(IEnumerable<ChatMessage> messages)
        {
            var all = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
            var fullText = string.Join('\n', all.Select(message => message.Text));
            var lastPassage = PassageMarker.Matches(fullText)
                .Select(match => int.Parse(match.Groups[1].Value))
                .DefaultIfEmpty(0)
                .Max();

            var question = all.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
            var directive = new[]
                {
                    FakeChatDirectives.InvalidCitation,
                    FakeChatDirectives.NoMarker,
                    FakeChatDirectives.CannotAnswer,
                    FakeChatDirectives.FailMidway,
                }
                .FirstOrDefault(question.Contains);

            var answer = directive switch
            {
                FakeChatDirectives.InvalidCitation => $"根據資料回答，本回答引用段落 [{lastPassage + 1}]。",
                FakeChatDirectives.NoMarker => "以下是我的回答，內容沒有引用任何段落。",
                FakeChatDirectives.CannotAnswer => ChatAnswerMarkers.CannotAnswer,
                _ when fullText.Contains(ReportSummaryPrompt.Marker, StringComparison.Ordinal) => ReportSummary(question),
                _ when lastPassage > 0 => "根據資料回答，本回答引用段落 [1]。",
                _ => "以下是我的回答，內容沒有引用任何段落。",
            };

            var inputTokens = all.Sum(message => (message.Text ?? string.Empty).EnumerateRunes().Count());
            var outputTokens = answer.EnumerateRunes().Count();
            var usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
            };

            return new FakeChatScript(directive ?? string.Empty, answer, SplitForStreaming(answer), usage);
        }

        /// <summary>A periodic report's AI summary (M4 #150): one sentence made of the first statistics line
        /// of the facts, word for word — so it only contains numbers the facts have.</summary>
        private static string ReportSummary(string facts)
        {
            var line = facts.Split('\n').Select(candidate => candidate.Trim()).FirstOrDefault(candidate => candidate.StartsWith("- ", StringComparison.Ordinal));
            return line is null ? "這一期的統計請見上方。" : $"整體來看，{line[2..]}。";
        }

        /// <summary>At least two pieces; when <paramref name="answer"/> has a <c>[n]</c> marker,
        /// splits right after the first marker's opening bracket so the marker itself crosses a
        /// chunk boundary.</summary>
        private static IReadOnlyList<string> SplitForStreaming(string answer)
        {
            var match = PassageMarker.Match(answer);
            var splitAt = match.Success ? match.Index + 1 : answer.Length / 2;
            splitAt = Math.Clamp(splitAt, 1, Math.Max(1, answer.Length - 1));

            return answer.Length <= 1
                ? [answer]
                : [answer[..splitAt], answer[splitAt..]];
        }
    }
}
