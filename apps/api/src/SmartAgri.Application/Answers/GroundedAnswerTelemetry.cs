using System.Diagnostics.Metrics;
using SmartAgri.Domain.Observability;

namespace SmartAgri.Application.Answers;

/// <summary>
/// Span and metric names of the answer pipeline. Never content: no question, passage or answer
/// text, and no account names (observability ADR).
/// </summary>
public static class GroundedAnswerTelemetry
{
    /// <summary>The span around one answer (retrieval, the model call and validation are inside it).</summary>
    public const string ActivityName = "smartagri.grounded_answer";

    /// <summary><see cref="GroundedAnswerPrompt.Version"/>.</summary>
    public const string PromptVersionTag = "smartagri.answer.prompt_version";

    /// <summary><c>company-data-only</c> or <c>allow-general-knowledge</c>.</summary>
    public const string KnowledgeScopeTag = "smartagri.answer.knowledge_scope";

    /// <summary>The relevance threshold used.</summary>
    public const string ThresholdTag = "smartagri.answer.threshold";

    /// <summary>How many passages reached the threshold (k).</summary>
    public const string RelevantPassagesTag = "smartagri.answer.relevant_passages";

    /// <summary>The candidate threshold in effect (#302), on an answer that asked the model with
    /// candidate passages because none reached <see cref="ThresholdTag"/>; absent otherwise.</summary>
    public const string CandidateThresholdTag = "smartagri.answer.candidate_threshold";

    /// <summary>How many candidate passages the model was asked with (#302); absent unless it was.</summary>
    public const string CandidatePassagesTag = "smartagri.answer.candidate_passages";

    /// <summary>How many passages the reply cites.</summary>
    public const string CitationsTag = "smartagri.answer.citations";

    /// <summary><see cref="GroundedReplyKind"/>'s wire name.</summary>
    public const string ReplyKindTag = "smartagri.answer.reply_kind";

    /// <summary><see cref="GroundedRejectionReason"/>'s wire name, on no-result replies.</summary>
    public const string RejectionReasonTag = "smartagri.answer.rejection_reason";

    /// <summary>Counter: final replies, tagged <c>reply_kind</c>.</summary>
    public const string RepliesInstrument = "smartagri.answer.replies";

    /// <summary>Counter: no-result replies, tagged <c>reason</c> — the distribution plan §7 risk 2
    /// tunes the prompt by.</summary>
    public const string RejectionsInstrument = "smartagri.answer.rejections";

    public const string ReplyKindMetricTag = "reply_kind";

    public const string ReasonMetricTag = "reason";

    internal static string WireName(GroundedReplyKind kind) => kind switch
    {
        GroundedReplyKind.CompanyData => "company-data",
        GroundedReplyKind.GeneralKnowledge => "general-knowledge",
        GroundedReplyKind.NoResult => "no-result",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    internal static string WireName(GroundedRejectionReason reason) => reason switch
    {
        GroundedRejectionReason.BelowThreshold => "below-threshold",
        GroundedRejectionReason.CitationOutOfRange => "citation-out-of-range",
        GroundedRejectionReason.NoCitation => "no-citation",
        GroundedRejectionReason.CannotAnswer => "cannot-answer",
        GroundedRejectionReason.EmptyAnswer => "empty-answer",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}

/// <summary>The answer pipeline's counters, created once per host from its
/// <see cref="IMeterFactory"/> on the application's meter (<see cref="SmartAgriMeter"/>).</summary>
public sealed class GroundedAnswerMetrics
{
    private readonly Counter<long> _replies;
    private readonly Counter<long> _rejections;

    public GroundedAnswerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SmartAgriMeter.Name);
        _replies = meter.CreateCounter<long>(
            GroundedAnswerTelemetry.RepliesInstrument, unit: "{reply}", description: "Final answer replies, by kind.");
        _rejections = meter.CreateCounter<long>(
            GroundedAnswerTelemetry.RejectionsInstrument, unit: "{reply}", description: "Answers turned into no-result, by reason.");
    }

    internal void Record(GroundedReply reply)
    {
        _replies.Add(1, new KeyValuePair<string, object?>(GroundedAnswerTelemetry.ReplyKindMetricTag, GroundedAnswerTelemetry.WireName(reply.Kind)));
        if (reply.RejectionReason is { } reason)
        {
            _rejections.Add(1, new KeyValuePair<string, object?>(GroundedAnswerTelemetry.ReasonMetricTag, GroundedAnswerTelemetry.WireName(reason)));
        }
    }
}
