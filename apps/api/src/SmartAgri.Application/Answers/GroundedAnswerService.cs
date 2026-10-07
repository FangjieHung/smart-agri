using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Observability;
using SmartAgri.Domain.Organizations;

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
/// <item><b>Candidates</b> (pre-launch plan §3 B, #302). When no passage reaches the threshold but
/// some reach the deployment's <see cref="KnowledgeRetrievalSettings.CandidateMinScore"/>
/// (<see cref="KnowledgeRetrievalSettings.CandidateFloor"/>), <c>company-data-only</c> asks the
/// model with those candidates (at most <c>Top</c>, closest first) instead of refusing: the
/// model's <see cref="ChatAnswerMarkers.CannotAnswer"/> then refuses (<c>cannot-answer</c>), and an
/// answer is <c>company-data</c> as usual. The outcome row records that candidates were used.
/// <c>allow-general-knowledge</c> keeps the general-knowledge answer instead: a candidate refusal
/// there would replace an answer it gives today with <c>no-result</c>.</item>
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
    private readonly IAnswerOutcomeRecorder _outcomes;
    private readonly IOrganizationContext _organization;
    private readonly TimeProvider _clock;

    public GroundedAnswerService(
        IAnswerKnowledgeBases knowledgeBases,
        IKnowledgeRetriever retriever,
        IChatClient chat,
        GroundedAnswerMetrics metrics,
        IAnswerOutcomeRecorder outcomes,
        IOrganizationContext organization,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBases);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(clock);
        _knowledgeBases = knowledgeBases;
        _retriever = retriever;
        _chat = chat;
        _metrics = metrics;
        _outcomes = outcomes;
        _organization = organization;
        _clock = clock;
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
            yield return await FinishAsync(activity, refusal, plan, request, cancellationToken);
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

        yield return await FinishAsync(activity, Judge(plan, answer.ToString(), request.Profile), plan, request, cancellationToken);
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

        await FinishAsync(activity, reply, plan, request, cancellationToken);
        return new GroundedAnswerResult(reply, plan.Retrieval, plan.UsedCandidates);
    }

    /// <summary>What retrieval decided: the passages to ground on (k = their count) and the
    /// messages to send, or a refusal that needs no model call. <paramref name="UsedCandidates"/>:
    /// the passages are candidates below the threshold (#302).</summary>
    private sealed record AnswerPlan(
        KnowledgeRetrievalResult Retrieval,
        IReadOnlyList<RetrievedKnowledgePassage> Passages,
        IReadOnlyDictionary<Guid, string> KnowledgeBaseNames,
        bool Grounded,
        IReadOnlyList<ChatMessage>? Messages,
        GroundedReply? Refusal,
        bool UsedCandidates = false);

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

        // 3. Threshold, then (#302) candidates: company-data-only only, and only when none is relevant.
        if (relevant.Count == 0
            && profile.KnowledgeScope == AssistantKnowledgeScope.CompanyDataOnly
            && _retriever.Settings.CandidateFloor(retrieval.Threshold) is { } candidateFloor)
        {
            var candidates = retrieval.Passages
                .Where(passage => passage.Score >= candidateFloor && names.ContainsKey(passage.KnowledgeBaseId))
                .ToList();
            if (candidates.Count > 0)
            {
                activity?.SetTag(GroundedAnswerTelemetry.CandidateThresholdTag, candidateFloor);
                activity?.SetTag(GroundedAnswerTelemetry.CandidatePassagesTag, candidates.Count);
                return new AnswerPlan(retrieval, candidates, names, true,
                    GroundedAnswerPrompt.Grounded(profile, candidates, request.History, question), null, UsedCandidates: true);
            }
        }

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
        new ModelInvocationAttribution(request.Purpose, request.AccountId, request.AssistantId).ToChatOptions();

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

    /// <summary>Finalizes a confirmed reply: metrics, the span's tags, and (M3.5 plan §3,
    /// Slice 6) one <see cref="AnswerOutcome"/> row. Only ever called with a reply that is
    /// actually going to reach the caller — never on a mid-stream failure or cancellation, so an
    /// outcome is written exactly when the answer it is about is (M3.5 issue #128).</summary>
    private async Task<GroundedAnswerEvent> FinishAsync(
        Activity? activity, GroundedReply reply, AnswerPlan plan, GroundedAnswerRequest request, CancellationToken cancellationToken)
    {
        _metrics.Record(reply);
        activity?.SetTag(GroundedAnswerTelemetry.ReplyKindTag, GroundedAnswerTelemetry.WireName(reply.Kind));
        activity?.SetTag(GroundedAnswerTelemetry.CitationsTag, reply.Citations.Count);
        if (reply.RejectionReason is { } reason)
        {
            activity?.SetTag(GroundedAnswerTelemetry.RejectionReasonTag, GroundedAnswerTelemetry.WireName(reason));
            await RecordOutcomeAsync(reply, plan.UsedCandidates, request, cancellationToken);
            return new GroundedAnswerRejected(reason, reply);
        }

        await RecordOutcomeAsync(reply, plan.UsedCandidates, request, cancellationToken);
        return new GroundedAnswerCompleted(reply);
    }

    /// <summary>Writes the outcome row. <see cref="IAnswerOutcomeRecorder"/> never throws for its
    /// caller — a failure to write is its own concern (logged there), never the conversation's or
    /// trial answer's.</summary>
    private async Task RecordOutcomeAsync(
        GroundedReply reply, bool usedCandidates, GroundedAnswerRequest request, CancellationToken cancellationToken)
    {
        if (_organization.OrganizationId is not { } organizationId)
        {
            // No organization in scope (anonymous / design-time / a background job that did not
            // set one): there is nowhere to file the row. Not expected for a real answer.
            return;
        }

        await _outcomes.RecordAsync(
            organizationId,
            request.AssistantId,
            ChannelFor(request.Purpose),
            AnswerKinds.ToReplyKind(reply.Kind),
            reply.RejectionReason is { } reason ? AnswerKinds.ToRejectionReason(reason) : null,
            [.. reply.Citations.Select(citation => citation.DocumentId).Distinct()],
            usedCandidates,
            _clock.GetUtcNow(),
            cancellationToken);
    }

    private static AnswerOutcomeChannel ChannelFor(ModelInvocationPurpose purpose) => purpose switch
    {
        ModelInvocationPurpose.GenerateAnswer => AnswerOutcomeChannel.Chat,
        ModelInvocationPurpose.TrialAnswer => AnswerOutcomeChannel.Trial,
        ModelInvocationPurpose.AssistantTest => AnswerOutcomeChannel.TestRun,
        ModelInvocationPurpose.PublicAnswer => AnswerOutcomeChannel.Website,
        ModelInvocationPurpose.LineAnswer => AnswerOutcomeChannel.Line,
        _ => throw new ArgumentOutOfRangeException(
            nameof(purpose), purpose, "This purpose has no answer-outcome channel yet."),
    };
}
