using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Observability;

namespace SmartAgri.Application.Answers;

/// <summary>
/// <b>The</b> answer pipeline (M3 plan §3; grounded-answers ADR): conversations (#77, streamed),
/// wizard trial answers (#78) and the answer evaluation (Slice 13) all answer through it.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Knowledge bases.</b> Of the profile's knowledge bases, only those its owner may
/// connect <i>now</i> (<see cref="IAnswerKnowledgeBases.FindConnectableAsync"/>).</item>
/// <item><b>Retrieval.</b> <see cref="IKnowledgeRetriever"/> with the profile's
/// <see cref="GroundedAnswerProfile.MinScore"/> (or the deployment's), never pending versions; the
/// query is the previous question plus this one (plan §7 D).</item>
/// <item><b>Threshold.</b> Below it, <c>company-data-only</c> answers <c>no-result</c> without
/// calling the model; <c>allow-general-knowledge</c> asks the model with no passage and answers
/// <c>general-knowledge</c> (plan §7 B).</item>
/// <item><b>Generation.</b> <see cref="GroundedAnswerPrompt"/>; one model call attributed
/// <see cref="ModelInvocationPurpose.GenerateAnswer"/> to the asker and assistant.</item>
/// <item><b>Validation.</b> <see cref="CitationMarkers"/> on the whole answer: a number outside
/// 1…k, no citation, or <see cref="ChatAnswerMarkers.CannotAnswer"/> make it <c>no-result</c>;
/// otherwise <c>company-data</c>, citing exactly the passages it marked, renumbered in order of
/// first citation.</item>
/// </list>
/// <para>
/// Failures: <see cref="KnowledgeEmbeddingException"/> when the question cannot be embedded, and
/// <see cref="ChatGenerationException"/> when the model is not configured
/// (<see cref="ChatGenerationException.ProviderNotConfigured"/>) or fails — before or during the
/// stream. Cancellation is rethrown as is. A <c>no-result</c> is not a failure. Every final reply
/// counts in <see cref="GroundedAnswerMetrics"/>, and each answer is one
/// <see cref="GroundedAnswerTelemetry.ActivityName"/> span carrying
/// <see cref="GroundedAnswerPrompt.Version"/>.
/// </para>
/// <para>Scoped, like the retriever and chat client it uses (both act for the scope's
/// organization).</para>
/// </remarks>
public sealed class GroundedAnswerService
{
    private readonly IAnswerKnowledgeBases _knowledgeBases;
    private readonly IKnowledgeRetriever _retriever;
    private readonly IChatClient _chat;
    private readonly GroundedAnswerMetrics _metrics;

    public GroundedAnswerService(
        IAnswerKnowledgeBases knowledgeBases,
        IKnowledgeRetriever retriever,
        IChatClient chat,
        GroundedAnswerMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBases);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(metrics);
        _knowledgeBases = knowledgeBases;
        _retriever = retriever;
        _chat = chat;
        _metrics = metrics;
    }

    /// <summary>
    /// Answers <paramref name="request"/> as a stream: <see cref="GroundedAnswerTextDelta"/>s as
    /// the model writes, then one <see cref="GroundedAnswerCompleted"/> or
    /// <see cref="GroundedAnswerRejected"/>. Retrieval happens before the first event, so a
    /// retrieval failure throws before anything is streamed.
    /// </summary>
    public async IAsyncEnumerable<GroundedAnswerEvent> StreamAsync(
        GroundedAnswerRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var question = Validate(request);
        using var activity = StartActivity(request.Profile);
        var plan = await PlanAsync(request, question, activity, cancellationToken);

        if (plan.Refusal is { } refusal)
        {
            yield return Finish(activity, refusal);
            yield break;
        }

        var answer = new StringBuilder();
        var sent = 0;
        var holding = plan.Grounded;
        await using var updates = StartStream(plan.Messages!, request, activity, cancellationToken);
        while (await MoveNextAsync(updates, activity, cancellationToken))
        {
            if (updates.Current.Text is not { Length: > 0 } text)
            {
                continue;
            }

            answer.Append(text);
            if (holding)
            {
                if (MayBeCannotAnswer(answer))
                {
                    continue;
                }

                holding = false;
            }

            var delta = answer.ToString(sent, answer.Length - sent);
            sent = answer.Length;
            yield return new GroundedAnswerTextDelta(delta);
        }

        yield return Finish(activity, Judge(plan, answer.ToString(), request.Profile));
    }

    /// <summary>Answers <paramref name="request"/> in one call (no streaming), with what retrieval
    /// found.</summary>
    public async Task<GroundedAnswerResult> AnswerAsync(GroundedAnswerRequest request, CancellationToken cancellationToken)
    {
        var question = Validate(request);
        using var activity = StartActivity(request.Profile);
        var plan = await PlanAsync(request, question, activity, cancellationToken);

        GroundedReply reply;
        if (plan.Refusal is { } refusal)
        {
            reply = refusal;
        }
        else
        {
            ChatResponse response;
            try
            {
                response = await _chat.GetResponseAsync(plan.Messages!, Options(request), cancellationToken);
            }
            catch (Exception exception) when (Wrap(exception, activity, cancellationToken) is { } wrapped)
            {
                throw wrapped;
            }

            reply = Judge(plan, response.Text, request.Profile);
        }

        Finish(activity, reply);
        return new GroundedAnswerResult(reply, plan.Retrieval);
    }

    /// <summary>What retrieval decided: the passages to ground on (k = their count) and the
    /// messages to send, or a refusal that needs no model call.</summary>
    private sealed record AnswerPlan(
        KnowledgeRetrievalResult Retrieval,
        IReadOnlyList<RetrievedKnowledgePassage> Passages,
        IReadOnlyDictionary<Guid, string> KnowledgeBaseNames,
        bool Grounded,
        IReadOnlyList<ChatMessage>? Messages,
        GroundedReply? Refusal);

    private async Task<AnswerPlan> PlanAsync(
        GroundedAnswerRequest request, string question, Activity? activity, CancellationToken cancellationToken)
    {
        var profile = request.Profile;

        // 1. The knowledge bases the owner may still connect.
        var connectable = profile.KnowledgeBaseIds.Count == 0
            ? []
            : await _knowledgeBases.FindConnectableAsync(profile.OwnerAccountId, profile.KnowledgeBaseIds, cancellationToken);
        var names = connectable.ToDictionary(knowledgeBase => knowledgeBase.Id, knowledgeBase => knowledgeBase.Name);

        // 2. Retrieval: the previous question joins this one; never pending versions.
        var retrieval = await _retriever.RetrieveAsync(
            new KnowledgeRetrievalQuery(
                RetrievalQuestion(request.History, question),
                [.. names.Keys],
                request.AccountId,
                request.AssistantId,
                IncludePending: false,
                MinScore: profile.MinScore),
            cancellationToken);

        // Only passages of the knowledge bases just checked, whatever the retriever returned.
        var relevant = retrieval.Relevant.Where(passage => names.ContainsKey(passage.KnowledgeBaseId)).ToList();
        activity?.SetTag(GroundedAnswerTelemetry.ThresholdTag, retrieval.Threshold);
        activity?.SetTag(GroundedAnswerTelemetry.RelevantPassagesTag, relevant.Count);

        // 3. Threshold.
        if (relevant.Count == 0)
        {
            return profile.KnowledgeScope == AssistantKnowledgeScope.CompanyDataOnly
                ? new AnswerPlan(retrieval, relevant, names, false, null,
                    GroundedReply.NoResult(profile.RefusalMessage, GroundedRejectionReason.BelowThreshold))
                : new AnswerPlan(retrieval, relevant, names, false,
                    GroundedAnswerPrompt.GeneralKnowledge(profile, request.History, question), null);
        }

        // 4. The grounded prompt.
        return new AnswerPlan(retrieval, relevant, names, true,
            GroundedAnswerPrompt.Grounded(profile, relevant, request.History, question), null);
    }

    /// <summary>5. The final reply for the model's whole <paramref name="answer"/>.</summary>
    private static GroundedReply Judge(AnswerPlan plan, string answer, GroundedAnswerProfile profile)
    {
        if (!plan.Grounded)
        {
            var general = CitationMarkers.Strip(answer);
            return general.Length == 0
                ? GroundedReply.NoResult(profile.RefusalMessage, GroundedRejectionReason.EmptyAnswer)
                : GroundedReply.GeneralKnowledge(general);
        }

        var analysis = CitationMarkers.Analyze(answer, plan.Passages.Count);
        if (analysis.CannotAnswer)
        {
            return GroundedReply.NoResult(profile.RefusalMessage, GroundedRejectionReason.CannotAnswer);
        }

        if (analysis.OutOfRange)
        {
            return GroundedReply.NoResult(profile.RefusalMessage, GroundedRejectionReason.CitationOutOfRange);
        }

        if (analysis.Cited.Count == 0)
        {
            return GroundedReply.NoResult(profile.RefusalMessage, GroundedRejectionReason.NoCitation);
        }

        var ordinals = new Dictionary<int, int>();
        var citations = new List<GroundedCitation>(analysis.Cited.Count);
        foreach (var number in analysis.Cited)
        {
            var ordinal = citations.Count + 1;
            ordinals[number] = ordinal;
            var passage = plan.Passages[number - 1];
            citations.Add(GroundedCitation.From(ordinal, passage, plan.KnowledgeBaseNames[passage.KnowledgeBaseId]));
        }

        return GroundedReply.CompanyData(CitationMarkers.Renumber(answer, ordinals).Trim(), citations);
    }

    /// <summary>The retrieval query: the last earlier question (if any) and this one (plan §7 D).</summary>
    internal static string RetrievalQuestion(IReadOnlyList<ConversationTurn> history, string question)
    {
        var previous = history.LastOrDefault(turn => turn.Author == ConversationAuthor.Account)?.Text?.Trim();
        return string.IsNullOrEmpty(previous) ? question : previous + "\n" + question;
    }

    private static string Validate(GroundedAnswerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Profile.KnowledgeBaseIds);
        ArgumentNullException.ThrowIfNull(request.History);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);
        return request.Question.Trim();
    }

    private static ChatOptions Options(GroundedAnswerRequest request) =>
        new ModelInvocationAttribution(ModelInvocationPurpose.GenerateAnswer, request.AccountId, request.AssistantId).ToChatOptions();

    /// <summary>Whether <paramref name="answer"/> so far could still be (or already is)
    /// <see cref="ChatAnswerMarkers.CannotAnswer"/>: held back instead of streamed, so the marker
    /// never reaches a screen.</summary>
    private static bool MayBeCannotAnswer(StringBuilder answer)
    {
        var text = answer.ToString().TrimStart();
        return text.Length == 0
            || ChatAnswerMarkers.CannotAnswer.StartsWith(text, StringComparison.Ordinal)
            || text.StartsWith(ChatAnswerMarkers.CannotAnswer, StringComparison.Ordinal);
    }

    private IAsyncEnumerator<ChatResponseUpdate> StartStream(
        IReadOnlyList<ChatMessage> messages, GroundedAnswerRequest request, Activity? activity, CancellationToken cancellationToken)
    {
        // The model call's own span (the recording middleware's) is a child of this answer's.
        Activity.Current = activity ?? Activity.Current;
        try
        {
            return _chat.GetStreamingResponseAsync(messages, Options(request), cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception) when (Wrap(exception, activity, cancellationToken) is { } wrapped)
        {
            throw wrapped;
        }
    }

    private static async Task<bool> MoveNextAsync(
        IAsyncEnumerator<ChatResponseUpdate> updates, Activity? activity, CancellationToken cancellationToken)
    {
        try
        {
            return await updates.MoveNextAsync();
        }
        catch (Exception exception) when (Wrap(exception, activity, cancellationToken) is { } wrapped)
        {
            throw wrapped;
        }
    }

    /// <summary>The exception to throw instead of <paramref name="exception"/>, or
    /// <see langword="null"/> to let it through unchanged: cancellation by the caller, and a
    /// <see cref="ChatGenerationException"/> already (e.g. no provider configured).</summary>
    private static ChatGenerationException? Wrap(Exception exception, Activity? activity, CancellationToken cancellationToken)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        if (exception is ChatGenerationException || (exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return null;
        }

        return new ChatGenerationException(providerNotConfigured: false, exception);
    }

    private static Activity? StartActivity(GroundedAnswerProfile profile)
    {
        var activity = SmartAgriActivitySource.Instance.StartActivity(GroundedAnswerTelemetry.ActivityName);
        activity?.SetTag(GroundedAnswerTelemetry.PromptVersionTag, GroundedAnswerPrompt.Version);
        activity?.SetTag(
            GroundedAnswerTelemetry.KnowledgeScopeTag,
            profile.KnowledgeScope == AssistantKnowledgeScope.CompanyDataOnly ? "company-data-only" : "allow-general-knowledge");
        return activity;
    }

    private GroundedAnswerEvent Finish(Activity? activity, GroundedReply reply)
    {
        _metrics.Record(reply);
        activity?.SetTag(GroundedAnswerTelemetry.ReplyKindTag, GroundedAnswerTelemetry.WireName(reply.Kind));
        activity?.SetTag(GroundedAnswerTelemetry.CitationsTag, reply.Citations.Count);
        if (reply.RejectionReason is { } reason)
        {
            activity?.SetTag(GroundedAnswerTelemetry.RejectionReasonTag, GroundedAnswerTelemetry.WireName(reason));
            return new GroundedAnswerRejected(reason, reply);
        }

        return new GroundedAnswerCompleted(reply);
    }
}
