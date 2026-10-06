namespace SmartAgri.Application.Ai;

/// <summary>
/// Test instructions a question can contain (anywhere in its text) to make
/// <c>FakeChatClient</c> (M3 plan, Slice 4) produce one specific kind of answer, so integration
/// tests can exercise every branch of the answer pipeline's citation validation (Slice 5)
/// deterministically, without a real model. Declared in Application so both the fake
/// (Infrastructure) and its tests (any layer) reference the same literal strings instead of
/// duplicating them.
/// </summary>
public static class FakeChatDirectives
{
    /// <summary>Answer citing a passage number past the last one actually supplied (<c>k+1</c>):
    /// the pipeline must reject it as <c>no-result</c>.</summary>
    public const string InvalidCitation = "#invalid-citation";

    /// <summary>Answer with prose but no <c>[n]</c> citation marker at all: the pipeline must
    /// reject it as <c>no-result</c> (an answer with no citation is not trusted, however
    /// plausible the prose).</summary>
    public const string NoMarker = "#no-marker";

    /// <summary>Answer with exactly <see cref="ChatAnswerMarkers.CannotAnswer"/>: the pipeline
    /// must turn this into the same <c>no-result</c> reply as a below-threshold retrieval.</summary>
    public const string CannotAnswer = "#cannot-answer";

    /// <summary>Stream one chunk of a legitimate-looking answer, then fail the call: the pipeline
    /// must not save (or count as successful) a partial answer, and the failed call is still
    /// recorded as a <see cref="ModelInvocationAttribution"/>-attributed
    /// <c>ModelInvocations</c> row.</summary>
    public const string FailMidway = "#fail-midway";

    /// <summary>When the call offers tools (M4 #149): call the tool and arguments given by the JSON
    /// object that follows — any tool name, including <c>request_database_form</c> (#164), e.g. <c>#query:{"name":"database_record_count","arguments":{"databaseId":"…","period":"this-month"}}</c>
    /// — names and values are sent exactly as written, so tests can send undefined ones.</summary>
    public const string Query = "#query:";

    /// <summary>When the call offers tools: call none and answer with text (the model decided the
    /// question is not about the databases).</summary>
    public const string NoQuery = "#query-none";

    /// <summary>When the call offers the form tool (M4 #164): call it with the first offered form,
    /// even if the question has no fill-in word (a model recognizing an intent the keywords miss).
    /// Without this or <see cref="NoForm"/>, the fake calls it exactly when the keyword gate would.</summary>
    public const string FormRequest = "#form-request";

    /// <summary>When the call offers the form tool (M4 #164): call none, even if the question has a
    /// fill-in word (a model seeing that it is not a request to fill anything in).</summary>
    public const string NoForm = "#form-none";

    /// <summary>When the call offers the case tool (M7-9 #254): call it with the first offered type and a
    /// title drafted from the question, even without a case word (a model recognizing an intent the keywords
    /// miss). Without this or <see cref="NoCase"/>, the fake calls it exactly when the keyword words match.</summary>
    public const string CaseProposal = "#case-proposal";

    /// <summary>When the call offers the case tool (M7-9 #254): call none, even with a case word.</summary>
    public const string NoCase = "#case-none";
}
