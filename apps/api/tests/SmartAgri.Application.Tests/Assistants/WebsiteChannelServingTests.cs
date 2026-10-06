using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;
using Acceptance = SmartAgri.Domain.Assistants.AssistantAcceptanceStatus;
using Serving = SmartAgri.Domain.Assistants.WebsiteServingState;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>Every row of M5a plan §3 C's serving-state table, in its order (issue #194).</summary>
public sealed class WebsiteChannelServingTests
{
    private static readonly AssistantAcceptanceSummary Passed = new(Acceptance.Passed, true);

    private static WebsiteChannelServingInput Input(
        WebsiteChannelState? state = WebsiteChannelState.Published,
        int domains = 1,
        AssistantStatus assistant = AssistantStatus.Ready,
        AssistantAcceptanceSummary? acceptance = null,
        int nonOwned = 0,
        bool quotaExceeded = false) =>
        new(state, domains, assistant, acceptance ?? Passed, nonOwned, quotaExceeded);

    [Fact]
    public void A_published_channel_with_a_domain_that_passed_acceptance_is_serving()
    {
        WebsiteChannelServing.Evaluate(Input()).ShouldBe(Serving.Serving);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(WebsiteChannelState.Draft)]
    public void Not_published_when_never_saved_or_a_draft(WebsiteChannelState? state)
    {
        WebsiteChannelServing.Evaluate(Input(state: state)).ShouldBe(Serving.NotPublished);
    }

    [Theory]
    [InlineData(WebsiteChannelState.Published)]
    [InlineData(WebsiteChannelState.Paused)]
    public void Not_published_without_an_allowed_domain_whatever_else_holds(WebsiteChannelState state)
    {
        WebsiteChannelServing.Evaluate(Input(
                state: state,
                domains: 0,
                assistant: AssistantStatus.Paused,
                acceptance: new AssistantAcceptanceSummary(Acceptance.Failed, false),
                nonOwned: 1,
                quotaExceeded: true))
            .ShouldBe(Serving.NotPublished);
    }

    [Fact]
    public void Paused_when_the_channel_is_paused_before_any_suspension()
    {
        WebsiteChannelServing.Evaluate(Input(
                state: WebsiteChannelState.Paused,
                acceptance: new AssistantAcceptanceSummary(Acceptance.Failed, false),
                nonOwned: 1,
                quotaExceeded: true))
            .ShouldBe(Serving.Paused);
    }

    [Fact]
    public void Paused_when_the_assistant_itself_is_paused()
    {
        WebsiteChannelServing.Evaluate(Input(assistant: AssistantStatus.Paused, nonOwned: 1))
            .ShouldBe(Serving.Paused);
    }

    [Theory]
    [InlineData(Acceptance.NotAccepted, null)]
    [InlineData(Acceptance.Failed, false)]
    [InlineData(Acceptance.Outdated, false)]
    public void Suspended_for_acceptance_when_failed_not_accepted_or_outdated_after_a_failed_run(
        Acceptance status, bool? latestPassed)
    {
        WebsiteChannelServing.Evaluate(Input(
                acceptance: new AssistantAcceptanceSummary(status, latestPassed), nonOwned: 1, quotaExceeded: true))
            .ShouldBe(Serving.SuspendedAcceptance);
    }

    [Fact]
    public void Outdated_after_a_fully_passed_run_keeps_serving()
    {
        WebsiteChannelServing.Evaluate(Input(acceptance: new AssistantAcceptanceSummary(Acceptance.Outdated, true)))
            .ShouldBe(Serving.Serving);
    }

    [Fact]
    public void Suspended_for_knowledge_even_when_outdated_acceptance_would_keep_serving()
    {
        // Connecting someone else's knowledge base makes acceptance outdated (still serving): the
        // ownership row must suspend it on its own (decision B).
        WebsiteChannelServing.Evaluate(Input(acceptance: new AssistantAcceptanceSummary(Acceptance.Outdated, true), nonOwned: 1))
            .ShouldBe(Serving.SuspendedKnowledge);
        WebsiteChannelServing.Evaluate(Input(nonOwned: 2, quotaExceeded: true)).ShouldBe(Serving.SuspendedKnowledge);
    }

    [Fact]
    public void Suspended_for_quota_when_the_organization_reached_its_limit()
    {
        WebsiteChannelServing.Evaluate(Input(quotaExceeded: true)).ShouldBe(Serving.SuspendedQuota);
    }

    [Fact]
    public void The_quota_defaults_to_not_exceeded()
    {
        WebsiteChannelServing.Evaluate(new WebsiteChannelServingInput(WebsiteChannelState.Published, 1, AssistantStatus.Ready, Passed, 0))
            .ShouldBe(Serving.Serving);
    }
}
