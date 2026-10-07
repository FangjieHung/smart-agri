using System.Reflection;
using Shouldly;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Chat;
using SmartAgri.Api.Databases;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>
/// Acceptance (#196, plan §3 D): the visitor endpoints' handlers compose only the answer pipeline —
/// no form request, database query or handoff service is reachable from them, structurally, not by a
/// flag — and they never touch a conversation row. Scans every type a handler declares or the
/// compiler generates for it (async state machines, closures): fields, parameters, return types,
/// properties and local variables, including generic arguments.
/// </summary>
public class VisitorEndpointArchitectureTests
{
    /// <summary>The services of the features visitors do not get, and the rows they must not write.</summary>
    private static readonly Type[] Forbidden =
    [
        typeof(AssistantFormRequests),
        typeof(ChatFormRequestTool),
        // M7-8: the proposal stage holds the form request now (and M7-9's case proposal).
        typeof(ChatProposalStage),
        typeof(IChatProposal),
        typeof(ChatProposalCandidate),
        typeof(ChatProposalReply),
        // M7-9: the case proposal, its tool, its endpoints and the case types an assistant may propose.
        typeof(ChatCaseProposal),
        typeof(ChatCaseProposalTool),
        typeof(ChatProposalSelectionTool),
        typeof(ChatProposalSelection),
        typeof(SmartAgri.Application.Chat.ProposalSelectionRules),
        typeof(ChatCaseProposalView),
        typeof(ChatCaseProposalEndpoints),
        typeof(ConfirmChatCaseProposalRequest),
        typeof(AssistantCaseType),
        typeof(SmartAgri.Domain.Chat.ChatCaseProposalSnapshot),
        typeof(SmartAgri.Domain.Cases.Case),
        typeof(SmartAgri.Domain.Cases.CaseType),
        typeof(ChatDatabaseQueries),
        typeof(IChatDatabaseQueryRunner),
        typeof(ChatDatabaseQueryScope),
        typeof(DatabaseSubmissionService),
        typeof(DatabaseFixedQueryService),
        typeof(AssistantIssue),
        typeof(AssistantIssueEvent),
        typeof(CreateAssistantHandoffRequest),
        typeof(SmartAgri.Domain.Chat.ChatThread),
        typeof(SmartAgri.Domain.Chat.ChatMessage),
        typeof(SmartAgri.Domain.Chat.ChatMessageCitation),
    ];

    private static readonly string[] ForbiddenNamespaces =
    [
        "SmartAgri.Api.Databases",
        "SmartAgri.Application.Databases",
        "SmartAgri.Domain.Databases",
    ];

    [Theory]
    [InlineData(typeof(VisitorChatRunEndpoints))]
    [InlineData(typeof(VisitorSessionEndpoints))]
    // M7-9: LINE answers through its own handler, which must not reach the proposal stage either.
    [InlineData(typeof(SmartAgri.Api.Line.LineQuestionHandler))]
    [InlineData(typeof(SmartAgri.Api.Line.LineWebhookProcessor))]
    public void The_visitor_handlers_depend_on_no_form_query_or_handoff_service(Type endpoints)
    {
        var offending = ReferencedTypes(endpoints)
            .Where(type => Forbidden.Contains(type) || ForbiddenNamespaces.Contains(type.Namespace))
            .Select(type => type.FullName)
            .Distinct()
            .Order()
            .ToList();

        offending.ShouldBeEmpty($"{endpoints.Name} must compose only the answer pipeline (plan §3 D)");
    }

    /// <summary>The scan itself finds what it looks for: the member endpoint does use them (the form
    /// services through the proposal stage since M7-8).</summary>
    [Fact]
    public void The_scan_finds_the_member_endpoints_form_and_query_services()
    {
        var referenced = ReferencedTypes(typeof(ChatRunEndpoints)).ToHashSet();

        referenced.ShouldContain(typeof(ChatProposalStage));
        referenced.ShouldContain(typeof(ChatProposalCandidate));
        referenced.ShouldContain(typeof(ChatDatabaseQueries));
        referenced.ShouldContain(typeof(SmartAgri.Domain.Chat.ChatThread));

        var proposals = ReferencedTypes(typeof(ChatFormRequestProposal)).ToHashSet();
        proposals.ShouldContain(typeof(AssistantFormRequests));
        proposals.ShouldContain(typeof(ChatFormRequestTool));

        // M7-9: the stage holds the case proposal after the form, and the case proposal its tool.
        ReferencedTypes(typeof(ChatProposalStage)).ShouldContain(typeof(ChatCaseProposal));
        ReferencedTypes(typeof(ChatCaseProposal)).ShouldContain(typeof(ChatCaseProposalTool));
        ReferencedTypes(typeof(ChatProposalStage)).ShouldContain(typeof(ChatProposalSelectionTool));
    }

    /// <summary>Every type <paramref name="root"/> and its nested (including compiler-generated)
    /// types mention in a signature, field, property or local, with generic arguments unwrapped.</summary>
    private static IEnumerable<Type> ReferencedTypes(Type root)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new HashSet<Type>();
        var pending = new Stack<Type>([root]);
        while (pending.TryPop(out var type))
        {
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                pending.Push(nested);
            }

            foreach (var field in type.GetFields(all))
            {
                Add(field.FieldType);
            }

            foreach (var property in type.GetProperties(all))
            {
                Add(property.PropertyType);
            }

            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                foreach (var parameter in method.GetParameters())
                {
                    Add(parameter.ParameterType);
                }

                if (method is MethodInfo info)
                {
                    Add(info.ReturnType);
                }

                foreach (var local in method.GetMethodBody()?.LocalVariables ?? [])
                {
                    Add(local.LocalType);
                }
            }
        }

        return found;

        void Add(Type type)
        {
            while (type.HasElementType)
            {
                type = type.GetElementType()!;
            }

            if (!found.Add(type))
            {
                return;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Add(argument);
                }
            }
        }
    }
}
