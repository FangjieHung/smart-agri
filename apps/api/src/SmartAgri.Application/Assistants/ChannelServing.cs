using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>Everything <see cref="ChannelServing.Evaluate"/> decides from: the channel's own two
/// facts and the conditions every public channel shares.</summary>
/// <param name="ChannelUsable">The channel is configured and usable: published or paused by its owner,
/// and its own prerequisite holds (website: at least one allowed domain; LINE: every connection check
/// passed). See <see cref="WebsiteChannelServing"/> and <see cref="LineChannelServing"/>.</param>
/// <param name="ChannelPaused">The owner paused the channel.</param>
/// <param name="AssistantStatus">The assistant's own status (paused pauses every channel).</param>
/// <param name="Acceptance">From <see cref="AssistantAcceptanceRules.Summarize"/>.</param>
/// <param name="NonOwnedKnowledgeBaseCount">Connected knowledge bases not owned by the assistant's owner (decision B).</param>
/// <param name="QuotaExceeded">Whether the organization's monthly token limit is reached (M5a plan §3 F).</param>
public sealed record ChannelServingInput(
    bool ChannelUsable,
    bool ChannelPaused,
    AssistantStatus AssistantStatus,
    AssistantAcceptanceSummary Acceptance,
    int NonOwnedKnowledgeBaseCount,
    bool QuotaExceeded = false);

/// <summary>
/// Whether a public channel answers right now (「實際服務狀態」, M5a plan §3 C; shared by the website
/// and LINE channels since M5b plan §4). Derived on every read — admin views, every visitor session
/// and question, every LINE event — and never stored, so a rerun that fails suspends it and one that
/// passes restores it without publishing again.
/// </summary>
public static class ChannelServing
{
    /// <summary>
    /// The first row of the plan's table that applies, in its order: not published (or not usable)
    /// → paused (the channel or the assistant) → acceptance (failed, not accepted, or outdated with a
    /// failed latest completed run) → knowledge ownership → quota → serving.
    /// </summary>
    /// <remarks>
    /// Knowledge ownership is checked separately from acceptance on purpose: connecting a new
    /// knowledge base only makes the acceptance status outdated, which keeps serving, so without
    /// its own row someone else's knowledge base would reach the public until the rerun completed.
    /// </remarks>
    public static ChannelServingState Evaluate(ChannelServingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.ChannelUsable)
        {
            return ChannelServingState.NotPublished;
        }

        if (input.ChannelPaused || input.AssistantStatus == AssistantStatus.Paused)
        {
            return ChannelServingState.Paused;
        }

        if (!AcceptanceAllowsServing(input.Acceptance))
        {
            return ChannelServingState.SuspendedAcceptance;
        }

        if (input.NonOwnedKnowledgeBaseCount > 0)
        {
            return ChannelServingState.SuspendedKnowledge;
        }

        return input.QuotaExceeded ? ChannelServingState.SuspendedQuota : ChannelServingState.Serving;
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

/// <summary>Everything <see cref="WebsiteChannelServing.Evaluate"/> decides from.</summary>
/// <param name="ChannelState">The owner's choice; <see langword="null"/> when no settings were ever saved.</param>
/// <param name="AllowedDomainCount">How many domains may embed it.</param>
/// <param name="AssistantStatus">The assistant's own status (paused pauses every channel).</param>
/// <param name="Acceptance">From <see cref="AssistantAcceptanceRules.Summarize"/>.</param>
/// <param name="NonOwnedKnowledgeBaseCount">Connected knowledge bases not owned by the assistant's owner (decision B).</param>
/// <param name="QuotaExceeded">Whether the organization's monthly token limit is reached (M5a plan §3 F).</param>
public sealed record WebsiteChannelServingInput(
    WebsiteChannelState? ChannelState,
    int AllowedDomainCount,
    AssistantStatus AssistantStatus,
    AssistantAcceptanceSummary Acceptance,
    int NonOwnedKnowledgeBaseCount,
    bool QuotaExceeded = false);

/// <summary>The website channel's serving state: usable when published or paused with at least one
/// allowed domain, then <see cref="ChannelServing.Evaluate"/>.</summary>
public static class WebsiteChannelServing
{
    public static ChannelServingState Evaluate(WebsiteChannelServingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ChannelServing.Evaluate(new ChannelServingInput(
            ChannelUsable: input.ChannelState is WebsiteChannelState.Published or WebsiteChannelState.Paused
                && input.AllowedDomainCount > 0,
            ChannelPaused: input.ChannelState == WebsiteChannelState.Paused,
            input.AssistantStatus,
            input.Acceptance,
            input.NonOwnedKnowledgeBaseCount,
            input.QuotaExceeded));
    }
}

/// <summary>Everything <see cref="LineChannelServing.Evaluate"/> decides from.</summary>
/// <param name="ChannelState">The owner's choice; <see langword="null"/> when no settings were ever saved.</param>
/// <param name="ConnectionChecksPassed">The last connection test passed every check
/// (<see cref="AssistantLineChannel.ConnectionChecksPassed"/>).</param>
/// <param name="AssistantStatus">The assistant's own status (paused pauses every channel).</param>
/// <param name="Acceptance">From <see cref="AssistantAcceptanceRules.Summarize"/>.</param>
/// <param name="NonOwnedKnowledgeBaseCount">Connected knowledge bases not owned by the assistant's owner (decision B).</param>
/// <param name="QuotaExceeded">Whether the organization's monthly token limit is reached (M5a plan §3 F).</param>
public sealed record LineChannelServingInput(
    LineChannelState? ChannelState,
    bool ConnectionChecksPassed,
    AssistantStatus AssistantStatus,
    AssistantAcceptanceSummary Acceptance,
    int NonOwnedKnowledgeBaseCount,
    bool QuotaExceeded = false);

/// <summary>The LINE channel's serving state (M5b plan §4): usable when published or paused and its
/// last connection test passed every check, then <see cref="ChannelServing.Evaluate"/>.</summary>
public static class LineChannelServing
{
    public static ChannelServingState Evaluate(LineChannelServingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ChannelServing.Evaluate(new ChannelServingInput(
            ChannelUsable: input.ChannelState is LineChannelState.Published or LineChannelState.Paused
                && input.ConnectionChecksPassed,
            ChannelPaused: input.ChannelState == LineChannelState.Paused,
            input.AssistantStatus,
            input.Acceptance,
            input.NonOwnedKnowledgeBaseCount,
            input.QuotaExceeded));
    }
}
