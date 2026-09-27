namespace SmartAgri.Application.Ai;

/// <summary>
/// Fixed strings the generation prompt (M3 Slice 5) asks the model to output as an escape hatch,
/// and that <c>FakeChatClient</c> (M3 Slice 4) reproduces so every validation branch of the
/// answer pipeline can be exercised without a real model. Application-level so both the prompt
/// builder and the fake live in code that can reference this without a circular dependency.
/// </summary>
public static class ChatAnswerMarkers
{
    /// <summary>The exact text the prompt instructs the model to answer with when the supplied
    /// passages do not contain enough to answer the question (grounded-answers ADR). The answer
    /// pipeline treats any answer containing this marker as <c>no-result</c>, the same as a
    /// citation that fails validation.</summary>
    public const string CannotAnswer = "[[SMARTAGRI_CANNOT_ANSWER]]";
}
