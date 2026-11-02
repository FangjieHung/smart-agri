using System.Diagnostics;
using Microsoft.Extensions.AI;
using SmartAgri.Api.Tenancy;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Observability;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Observability;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Coordinator;

/// <summary>How a command model call went.</summary>
public enum CoordinatorInterpretationOutcome
{
    /// <summary>The model answered (<see cref="CoordinatorInterpretation.Call"/>, possibly
    /// <see cref="CoordinatorCommandCall.None"/>).</summary>
    Understood,

    /// <summary>The shop's monthly token limit is reached: no call was made.</summary>
    QuotaExceeded,

    /// <summary>The call failed (provider error, timeout, no model configured).</summary>
    Failed,
}

public sealed record CoordinatorInterpretation(CoordinatorInterpretationOutcome Outcome, CoordinatorCommandCall Call)
{
    public static readonly CoordinatorInterpretation QuotaExceeded = new(CoordinatorInterpretationOutcome.QuotaExceeded, CoordinatorCommandCall.None);

    public static readonly CoordinatorInterpretation Failed = new(CoordinatorInterpretationOutcome.Failed, CoordinatorCommandCall.None);
}

/// <summary>
/// Asks the model what a command means (store-coordinator design decision D, §5 Slice 3, #374): one call with
/// <see cref="CoordinatorCommandTool"/>, <b>charged to the shop</b> the command is for. The 統籌助理's own scope
/// acts for no organization, so the call runs in a scope of its own pinned to that shop (as the LINE channel's
/// processor does), where <see cref="IChatClient"/> is the shop's model wrapped in the recording middleware —
/// recorded as <see cref="ModelInvocationPurpose.CoordinatorCommand"/>, without an account, and counted toward
/// the shop's monthly limit, which is checked first (<see cref="OrganizationTokenUsage"/>).
/// </summary>
/// <remarks>
/// <c>Ai:Chat:ReasoningEffort</c> and <c>MaxOutputTokens</c> apply as for every call (the provider's defaults;
/// nothing here overrides them). Like the other tool calls, the tool is offered with
/// <see cref="ChatToolMode.Auto"/> (some OpenAI-compatible servers refuse a forced tool); no call reads as
/// "not a command". A failure is logged with its exception type only: the message and the model's text are
/// what a person wrote, and are never logged.
/// </remarks>
public sealed class CoordinatorCommandInterpreter
{
    public const string ActivityName = "smartagri.coordinator.command";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrganizationTokenUsage _tokenUsage;
    private readonly ILogger<CoordinatorCommandInterpreter> _logger;

    public CoordinatorCommandInterpreter(
        IServiceScopeFactory scopeFactory, OrganizationTokenUsage tokenUsage, ILogger<CoordinatorCommandInterpreter> logger)
    {
        _scopeFactory = scopeFactory;
        _tokenUsage = tokenUsage;
        _logger = logger;
    }

    /// <summary>Sends <paramref name="prompt"/> (<see cref="CoordinatorCommandTool.Prompt"/> or
    /// <see cref="CoordinatorCommandTool.FollowUpPrompt"/>) to <paramref name="organizationId"/>'s model.</summary>
    public async Task<CoordinatorInterpretation> InterpretAsync(Guid organizationId, IReadOnlyList<ChatMessage> prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        using var activity = SmartAgriActivitySource.Instance.StartActivity(ActivityName);
        if ((await _tokenUsage.GetAsync(organizationId, cancellationToken)).State == TokenUsageState.Exceeded)
        {
            activity?.SetTag("smartagri.coordinator.command.status", "quota");
            return CoordinatorInterpretation.QuotaExceeded;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ClaimsOrganizationContext>().Pin(organizationId);
            if (scope.ServiceProvider.GetRequiredService<IOrganizationContext>().OrganizationId != organizationId)
            {
                throw new InvalidOperationException("A 統籌助理 model call's scope does not act for the shop.");
            }

            var options = new ModelInvocationAttribution(ModelInvocationPurpose.CoordinatorCommand, AccountId: null, AssistantId: null).ToChatOptions();
            options.Tools = [CoordinatorCommandTool.Declaration()];
            options.ToolMode = ChatToolMode.Auto;
            options.AllowMultipleToolCalls = false;
            var response = await scope.ServiceProvider.GetRequiredService<IChatClient>().GetResponseAsync(prompt, options, cancellationToken);
            var call = CoordinatorCommandTool.ParseCall(response);
            activity?.SetTag("smartagri.coordinator.command.status", call.Scenario is null ? "none" : "understood");
            return new CoordinatorInterpretation(CoordinatorInterpretationOutcome.Understood, call);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Only types: the provider's error can quote the message it was given.
            _logger.LogWarning("A 統籌助理 command could not be understood by the model: {Failure}", ExceptionSummary.Of(exception));
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("smartagri.coordinator.command.status", "failed");
            return CoordinatorInterpretation.Failed;
        }
    }
}
