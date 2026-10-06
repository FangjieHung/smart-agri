using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>Everything <see cref="WebsiteChannelServing.Evaluate"/> decides from.</summary>
/// <param name="ChannelState">The owner's choice; <see langword="null"/> when no settings were ever saved.</param>
/// <param name="AllowedDomainCount">How many domains may embed it.</param>
/// <param name="AssistantStatus">The assistant's own status (paused pauses every channel).</param>
/// <param name="Acceptance">From <see cref="AssistantAcceptanceRules.Summarize"/>.</param>
/// <param name="NonOwnedKnowledgeBaseCount">Connected knowledge bases not owned by the assistant's owner (decision B).</param>
/// <param name="QuotaExceeded">Whether the organization's monthly token limit is reached (M5a plan §3 F;
/// wired by Slice 3 — until then always <see langword="false"/>).</param>
public sealed record WebsiteChannelServingInput(
    WebsiteChannelState? ChannelState,
    int AllowedDomainCount,
    AssistantStatus AssistantStatus,
    AssistantAcceptanceSummary Acceptance,
    int NonOwnedKnowledgeBaseCount,
    bool QuotaExceeded = false);

/// <summary>
/// Whether an assistant's website channel answers visitors right now (「實際服務狀態」, M5a plan
/// §3 C). Derived on every read — admin views now, every visitor session and question from Slice 4
/// on — and never stored, so a rerun that fails suspends it and one that passes restores it without
/// publishing again.
/// </summary>
public static class WebsiteChannelServing
{
    /// <summary>
    /// The first row of the plan's table that applies, in its order: not published (or no allowed
    /// domain) → paused (the channel or the assistant) → acceptance (failed, not accepted, or
    /// outdated with a failed latest completed run) → knowledge ownership → quota → serving.
    /// </summary>
    /// <remarks>
    /// Knowledge ownership is checked separately from acceptance on purpose: connecting a new
    /// knowledge base only makes the acceptance status outdated, which keeps serving, so without
    /// its own row someone else's knowledge base would reach visitors until the rerun completed.
    /// </remarks>
    public static WebsiteServingState Evaluate(WebsiteChannelServingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ChannelState is not (WebsiteChannelState.Published or WebsiteChannelState.Paused)
            || input.AllowedDomainCount == 0)
        {
            return WebsiteServingState.NotPublished;
        }

        if (input.ChannelState == WebsiteChannelState.Paused || input.AssistantStatus == AssistantStatus.Paused)
        {
            return WebsiteServingState.Paused;
        }

        if (!AcceptanceAllowsServing(input.Acceptance))
        {
            return WebsiteServingState.SuspendedAcceptance;
        }

        if (input.NonOwnedKnowledgeBaseCount > 0)
        {
            return WebsiteServingState.SuspendedKnowledge;
        }

        return input.QuotaExceeded ? WebsiteServingState.SuspendedQuota : WebsiteServingState.Serving;
    }

    /// <summary><see cref="AssistantAcceptanceStatus.Passed"/>, or outdated while the latest
    /// completed run passed every case (「過期」照常服務).</summary>
    private static bool AcceptanceAllowsServing(AssistantAcceptanceSummary acceptance) =>
        acceptance.Status switch
        {
            AssistantAcceptanceStatus.Passed => true,
            AssistantAcceptanceStatus.Outdated => acceptance.LatestCompletedRunPassed == true,
            _ => false,
        };
}
