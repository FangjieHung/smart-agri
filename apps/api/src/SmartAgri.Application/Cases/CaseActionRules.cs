using System.Text.Json.Serialization;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>
/// The actions of the action table (M7 plan §3 D; issue #249). Each is an endpoint
/// <c>POST /api/v1/cases/{id}:&lt;wire name&gt;</c>, except <see cref="Comment"/>, which is
/// <c>POST /api/v1/cases/{id}/comments</c>. The detail lists the ones the caller may do now
/// (<c>allowedActions</c>), so the screen shows only those.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaseAction>))]
public enum CaseAction
{
    /// <summary>受理.</summary>
    [JsonStringEnumMemberName("accept")]
    Accept,

    /// <summary>待補件 (a note is required).</summary>
    [JsonStringEnumMemberName("request-info")]
    RequestInfo,

    /// <summary>繼續處理 (decision J).</summary>
    [JsonStringEnumMemberName("resume")]
    Resume,

    /// <summary>完成 (a resolution is required).</summary>
    [JsonStringEnumMemberName("complete")]
    Complete,

    /// <summary>取消 (a reason is required unless the creator cancels before acceptance).</summary>
    [JsonStringEnumMemberName("cancel")]
    Cancel,

    /// <summary>轉組.</summary>
    [JsonStringEnumMemberName("transfer")]
    Transfer,

    /// <summary>調整時限.</summary>
    [JsonStringEnumMemberName("set-due")]
    SetDue,

    /// <summary>補充.</summary>
    [JsonStringEnumMemberName("comment")]
    Comment,
}

/// <summary>How the caller stands to a case: its creator, its current case owner, a member of its
/// current group, the organization's manager (any combination).</summary>
public sealed record CaseActor(bool IsCreator, bool IsOwner, bool IsGroupMember, bool IsManager);

/// <summary>The answer of <see cref="CaseActionRules.Check"/>.</summary>
public enum CaseActionCheck
{
    /// <summary>The caller may do it now.</summary>
    Allowed,

    /// <summary>No one may do this action in the case's status (a closed case, or e.g. accepting a case
    /// in progress): <c>409 case-changed</c> — the screen only offers what is possible, so someone else
    /// changed the case.</summary>
    WrongStatus,

    /// <summary>The status admits the action, but not for this caller: <c>403 case-action</c>.</summary>
    NotYours,
}

/// <summary>
/// The action table (M7 plan §3 D, decisions I and J; case ADR「狀態與流轉」) as one pure rule, so
/// each row's 「可以」 and 「不可以」 are unit tested and the endpoints and the detail's
/// <c>allowedActions</c> cannot disagree:
/// <list type="table">
/// <listheader><term>action</term><description>who — statuses</description></listheader>
/// <item><term>accept</term><description>a member of the current group — 待受理</description></item>
/// <item><term>request-info</term><description>the case owner — 處理中</description></item>
/// <item><term>resume</term><description>the case owner — 待補件</description></item>
/// <item><term>complete</term><description>the case owner — 處理中, 待補件</description></item>
/// <item><term>cancel</term><description>before acceptance (待受理) the creator or the manager; afterwards
/// the case owner or the manager — every open status</description></item>
/// <item><term>transfer</term><description>the case owner or the manager in 處理中 and 待補件; the manager
/// also in 待受理</description></item>
/// <item><term>set-due</term><description>the case owner — every open status (in 待受理 there is no owner)</description></item>
/// <item><term>comment</term><description>the creator or the case owner — every open status</description></item>
/// </list>
/// A closed case admits nothing (it is never reopened; 「另開新案」 links it instead).
/// </summary>
/// <remarks>
/// <para>
/// The case owner stays the case owner after leaving the group (until a transfer or closing), so
/// none of the owner's rows look at <see cref="CaseActor.IsGroupMember"/>.
/// </para>
/// <para>
/// The <c>eventCount</c> check comes first (the endpoint's): a stale screen is <c>409</c> whatever this
/// rule would say.
/// </para>
/// </remarks>
public static class CaseActionRules
{
    /// <summary>Every action, in the order the detail lists them.</summary>
    public static readonly IReadOnlyList<CaseAction> All = Enum.GetValues<CaseAction>();

    /// <summary>The statuses an action exists in for anyone (the table's 「允許的狀態」 column).</summary>
    public static IReadOnlyList<CaseStatus> StatusesOf(CaseAction action) => action switch
    {
        CaseAction.Accept => [CaseStatus.Pending],
        CaseAction.RequestInfo => [CaseStatus.InProgress],
        CaseAction.Resume => [CaseStatus.AwaitingInfo],
        CaseAction.Complete => [CaseStatus.InProgress, CaseStatus.AwaitingInfo],
        CaseAction.Cancel or CaseAction.Transfer or CaseAction.SetDue or CaseAction.Comment => CaseStatuses.Open,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a declared action."),
    };

    /// <summary>Whether <paramref name="actor"/> may do <paramref name="action"/> on a case in <paramref name="status"/>.</summary>
    public static CaseActionCheck Check(CaseAction action, CaseStatus status, CaseActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!StatusesOf(action).Contains(status))
        {
            return CaseActionCheck.WrongStatus;
        }

        var accepted = status != CaseStatus.Pending;
        var allowed = action switch
        {
            CaseAction.Accept => actor.IsGroupMember,
            CaseAction.RequestInfo or CaseAction.Resume or CaseAction.Complete or CaseAction.SetDue => actor.IsOwner,
            CaseAction.Cancel => actor.IsManager || (accepted ? actor.IsOwner : actor.IsCreator),
            CaseAction.Transfer => actor.IsManager || (accepted && actor.IsOwner),
            CaseAction.Comment => actor.IsCreator || actor.IsOwner,
            _ => false,
        };
        return allowed ? CaseActionCheck.Allowed : CaseActionCheck.NotYours;
    }

    /// <summary>What <paramref name="actor"/> may do now, in <see cref="All"/>'s order (empty for a closed case).</summary>
    public static IReadOnlyList<CaseAction> Allowed(CaseStatus status, CaseActor actor) =>
        [.. All.Where(action => Check(action, status, actor) == CaseActionCheck.Allowed)];

    /// <summary>Cancelling needs a reason, except from the creator before acceptance (the case ADR).</summary>
    public static bool CancelReasonRequired(CaseStatus status, CaseActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return !(status == CaseStatus.Pending && actor.IsCreator);
    }

    /// <summary>The creator's comment on a 待補件 case answers the request: the case goes back to 處理中.
    /// The case owner's comment never changes the status (also when the owner is the creator: they asked
    /// themselves, so only <c>resume</c> moves it on).</summary>
    public static bool CommentResumes(CaseStatus status, CaseActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return status == CaseStatus.AwaitingInfo && actor.IsCreator && !actor.IsOwner;
    }
}
