using SmartAgri.Api.Errors;
using SmartAgri.Application.Ai;

namespace SmartAgri.Api.Ai;

/// <summary>
/// Maps a <see cref="ChatGenerationException"/> to the API's <c>503</c> shape (M3 plan, Slice 4;
/// the same pattern as <c>KnowledgeRetrievalEndpoints</c>' <c>embedding-not-configured</c> /
/// <c>embedding-unavailable</c>). This slice has no public conversation endpoint yet — Slice 5's
/// answer pipeline and Slice 7's AG-UI endpoint are the callers — so this is exercised directly
/// by tests for now.
/// </summary>
public static class ChatErrors
{
    /// <summary>The <c>503</c> reason when the deployment has no chat provider at all.</summary>
    public const string ChatNotConfiguredReason = "chat-not-configured";

    /// <summary>The <c>503</c> reason when a configured chat provider's call failed.</summary>
    public const string ChatUnavailableReason = "chat-unavailable";

    /// <summary><c>503</c> with <paramref name="exception"/>'s reason and message.</summary>
    public static IResult ToApiResult(ChatGenerationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ApiErrors.WithReason(
            StatusCodes.Status503ServiceUnavailable,
            exception.ProviderNotConfigured ? ChatNotConfiguredReason : ChatUnavailableReason,
            exception.Message);
    }
}
