using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;
using Acceptance = SmartAgri.Domain.Assistants.AssistantAcceptanceStatus;
using Serving = SmartAgri.Domain.Assistants.ChannelServingState;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>
/// The serving state shared by both public channels (M5b plan §4, issue #229): <see cref="ChannelServing"/>
/// and the LINE channel's adapter. The website adapter keeps every row of
/// <see cref="WebsiteChannelServingTests"/>.
/// </summary>
public sealed class LineChannelServingTests
{
    private static readonly AssistantAcceptanceSummary Passed = new(Acceptance.Passed, true);

    private static LineChannelServingInput Input(
        LineChannelState? state = LineChannelState.Published,
        bool checksPassed = true,
        AssistantStatus assistant = AssistantStatus.Ready,
        AssistantAcceptanceSummary? acceptance = null,
        int nonOwned = 0,
        bool quotaExceeded = false) =>
        new(state, checksPassed, assistant, acceptance ?? Passed, nonOwned, quotaExceeded);

    [Fact]
    public void An_enabled_tested_channel_that_passed_acceptance_is_serving()
    {
        LineChannelServing.Evaluate(Input()).ShouldBe(Serving.Serving);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LineChannelState.Draft)]
    public void Not_published_when_never_saved_or_a_draft_even_if_tested(LineChannelState? state)
    {
        LineChannelServing.Evaluate(Input(state: state)).ShouldBe(Serving.NotPublished);
    }

    [Theory]
    [InlineData(LineChannelState.Published)]
    [InlineData(LineChannelState.Paused)]
    public void Not_published_while_the_connection_checks_have_not_all_passed_whatever_else_holds(LineChannelState state)
    {
        LineChannelServing.Evaluate(Input(
                state: state,
                checksPassed: false,
                assistant: AssistantStatus.Paused,
                acceptance: new AssistantAcceptanceSummary(Acceptance.Failed, false),
                nonOwned: 1,
                quotaExceeded: true))
            .ShouldBe(Serving.NotPublished);
    }

    [Fact]
    public void The_shared_conditions_follow_in_the_website_channels_order()
    {
        LineChannelServing.Evaluate(Input(state: LineChannelState.Paused, nonOwned: 1)).ShouldBe(Serving.Paused);
        LineChannelServing.Evaluate(Input(assistant: AssistantStatus.Paused, quotaExceeded: true)).ShouldBe(Serving.Paused);
        LineChannelServing.Evaluate(Input(acceptance: new AssistantAcceptanceSummary(Acceptance.Outdated, false), nonOwned: 1))
            .ShouldBe(Serving.SuspendedAcceptance);
        LineChannelServing.Evaluate(Input(acceptance: new AssistantAcceptanceSummary(Acceptance.Outdated, true))).ShouldBe(Serving.Serving);
        LineChannelServing.Evaluate(Input(nonOwned: 2, quotaExceeded: true)).ShouldBe(Serving.SuspendedKnowledge);
        LineChannelServing.Evaluate(Input(quotaExceeded: true)).ShouldBe(Serving.SuspendedQuota);
    }

    [Fact]
    public void Both_channels_give_the_shared_evaluation_the_same_inputs()
    {
        var acceptances = new[]
        {
            Passed,
            new AssistantAcceptanceSummary(Acceptance.Failed, false),
            new AssistantAcceptanceSummary(Acceptance.Outdated, true),
            new AssistantAcceptanceSummary(Acceptance.NotAccepted, null),
        };
        foreach (var paused in new[] { false, true })
        foreach (var assistant in new[] { AssistantStatus.Ready, AssistantStatus.Paused })
        foreach (var acceptance in acceptances)
        foreach (var nonOwned in new[] { 0, 1 })
        foreach (var quota in new[] { false, true })
        {
            var shared = ChannelServing.Evaluate(new ChannelServingInput(true, paused, assistant, acceptance, nonOwned, quota));
            WebsiteChannelServing.Evaluate(new WebsiteChannelServingInput(
                    paused ? WebsiteChannelState.Paused : WebsiteChannelState.Published, 1, assistant, acceptance, nonOwned, quota))
                .ShouldBe(shared);
            LineChannelServing.Evaluate(new LineChannelServingInput(
                    paused ? LineChannelState.Paused : LineChannelState.Published, true, assistant, acceptance, nonOwned, quota))
                .ShouldBe(shared);
        }
    }
}
