using Shouldly;
using SmartAgri.Application.Cases;
using SmartAgri.Domain;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Tests.Cases;

/// <summary>
/// <see cref="CaseActionRules"/>: the action table (M7 plan §3 D, decisions I and J; issue #249), every
/// row with who may (「可以」) and who may not (「不可以」: <see cref="CaseActionCheck.NotYours"/> →
/// <c>403 case-action</c>), and the statuses where no one may (<see cref="CaseActionCheck.WrongStatus"/>
/// → <c>409 case-changed</c>). The endpoints' tests (<c>CaseActionEndpointsTests</c>) run the same
/// table over HTTP.
/// </summary>
public class CaseActionRulesTests
{
    private static readonly CaseActor Creator = new(IsCreator: true, IsOwner: false, IsGroupMember: false, IsManager: false);
    private static readonly CaseActor Owner = new(IsCreator: false, IsOwner: true, IsGroupMember: true, IsManager: false);
    private static readonly CaseActor FormerMemberOwner = new(IsCreator: false, IsOwner: true, IsGroupMember: false, IsManager: false);
    private static readonly CaseActor Member = new(IsCreator: false, IsOwner: false, IsGroupMember: true, IsManager: false);
    private static readonly CaseActor Manager = new(IsCreator: false, IsOwner: false, IsGroupMember: false, IsManager: true);
    private static readonly CaseActor Bystander = new(IsCreator: false, IsOwner: false, IsGroupMember: false, IsManager: false);

    public static TheoryData<string, string, string, string> Table => new()
    {
        // action, status, who, expected
        { "accept", "pending", "member", "allowed" },
        { "accept", "pending", "creator", "not-yours" },
        { "accept", "pending", "manager", "not-yours" },
        { "accept", "in-progress", "member", "wrong-status" },

        { "request-info", "in-progress", "owner", "allowed" },
        { "request-info", "in-progress", "former-member-owner", "allowed" },
        { "request-info", "in-progress", "member", "not-yours" },
        { "request-info", "in-progress", "creator", "not-yours" },
        { "request-info", "in-progress", "manager", "not-yours" },
        { "request-info", "awaiting-info", "owner", "wrong-status" },
        { "request-info", "pending", "member", "wrong-status" },

        { "resume", "awaiting-info", "owner", "allowed" },
        { "resume", "awaiting-info", "creator", "not-yours" },
        { "resume", "awaiting-info", "manager", "not-yours" },
        { "resume", "in-progress", "owner", "wrong-status" },

        { "complete", "in-progress", "owner", "allowed" },
        { "complete", "awaiting-info", "owner", "allowed" },
        { "complete", "in-progress", "former-member-owner", "allowed" },
        { "complete", "in-progress", "member", "not-yours" },
        { "complete", "in-progress", "manager", "not-yours" },
        { "complete", "in-progress", "creator", "not-yours" },
        { "complete", "pending", "member", "wrong-status" },

        { "cancel", "pending", "creator", "allowed" },
        { "cancel", "pending", "manager", "allowed" },
        { "cancel", "pending", "member", "not-yours" },
        { "cancel", "in-progress", "owner", "allowed" },
        { "cancel", "awaiting-info", "owner", "allowed" },
        { "cancel", "in-progress", "manager", "allowed" },
        { "cancel", "in-progress", "creator", "not-yours" },
        { "cancel", "in-progress", "member", "not-yours" },

        { "transfer", "in-progress", "owner", "allowed" },
        { "transfer", "awaiting-info", "owner", "allowed" },
        { "transfer", "in-progress", "manager", "allowed" },
        { "transfer", "pending", "manager", "allowed" },
        { "transfer", "pending", "member", "not-yours" },
        { "transfer", "pending", "creator", "not-yours" },
        { "transfer", "in-progress", "member", "not-yours" },
        { "transfer", "in-progress", "creator", "not-yours" },

        { "set-due", "in-progress", "owner", "allowed" },
        { "set-due", "awaiting-info", "owner", "allowed" },
        { "set-due", "in-progress", "manager", "not-yours" },
        { "set-due", "in-progress", "creator", "not-yours" },
        { "set-due", "pending", "member", "not-yours" },

        { "comment", "pending", "creator", "allowed" },
        { "comment", "in-progress", "creator", "allowed" },
        { "comment", "awaiting-info", "owner", "allowed" },
        { "comment", "in-progress", "member", "not-yours" },
        { "comment", "in-progress", "manager", "not-yours" },
        { "comment", "pending", "bystander", "not-yours" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_row_of_the_action_table(string action, string status, string who, string expected)
    {
        var check = CaseActionRules.Check(WireNames<CaseAction>.Parse(action), WireNames<CaseStatus>.Parse(status), Actor(who));

        check.ShouldBe(expected switch
        {
            "allowed" => CaseActionCheck.Allowed,
            "not-yours" => CaseActionCheck.NotYours,
            _ => CaseActionCheck.WrongStatus,
        });
    }

    [Fact]
    public void A_closed_case_admits_nothing_for_anyone()
    {
        var everyone = new CaseActor(IsCreator: true, IsOwner: true, IsGroupMember: true, IsManager: true);
        foreach (var status in CaseStatuses.Closed)
        {
            foreach (var action in CaseActionRules.All)
            {
                CaseActionRules.Check(action, status, everyone).ShouldBe(CaseActionCheck.WrongStatus, $"{action} {status}");
            }

            CaseActionRules.Allowed(status, everyone).ShouldBeEmpty();
        }
    }

    [Fact]
    public void The_detail_lists_what_each_person_may_do_now()
    {
        CaseActionRules.Allowed(CaseStatus.Pending, Member).ShouldBe([CaseAction.Accept]);
        CaseActionRules.Allowed(CaseStatus.Pending, Creator).ShouldBe([CaseAction.Cancel, CaseAction.Comment]);
        CaseActionRules.Allowed(CaseStatus.Pending, Manager).ShouldBe([CaseAction.Cancel, CaseAction.Transfer]);
        CaseActionRules.Allowed(CaseStatus.InProgress, Owner)
            .ShouldBe([CaseAction.RequestInfo, CaseAction.Complete, CaseAction.Cancel, CaseAction.Transfer, CaseAction.SetDue, CaseAction.Comment]);
        CaseActionRules.Allowed(CaseStatus.AwaitingInfo, Owner)
            .ShouldBe([CaseAction.Resume, CaseAction.Complete, CaseAction.Cancel, CaseAction.Transfer, CaseAction.SetDue, CaseAction.Comment]);
        CaseActionRules.Allowed(CaseStatus.InProgress, Bystander).ShouldBeEmpty();
        WireNames<CaseAction>.All.ShouldBe(["accept", "request-info", "resume", "complete", "cancel", "transfer", "set-due", "comment"]);
    }

    [Fact]
    public void Only_the_creator_before_acceptance_may_cancel_without_a_reason()
    {
        CaseActionRules.CancelReasonRequired(CaseStatus.Pending, Creator).ShouldBeFalse();
        CaseActionRules.CancelReasonRequired(CaseStatus.Pending, Manager).ShouldBeTrue();
        CaseActionRules.CancelReasonRequired(CaseStatus.Pending, Creator with { IsManager = true }).ShouldBeFalse();
        CaseActionRules.CancelReasonRequired(CaseStatus.InProgress, Owner).ShouldBeTrue();
        CaseActionRules.CancelReasonRequired(CaseStatus.InProgress, Owner with { IsCreator = true }).ShouldBeTrue();
    }

    [Fact]
    public void Only_the_creators_comment_on_an_awaiting_info_case_resumes_it()
    {
        CaseActionRules.CommentResumes(CaseStatus.AwaitingInfo, Creator).ShouldBeTrue();
        CaseActionRules.CommentResumes(CaseStatus.AwaitingInfo, Owner).ShouldBeFalse();
        CaseActionRules.CommentResumes(CaseStatus.AwaitingInfo, Owner with { IsCreator = true }).ShouldBeFalse();
        CaseActionRules.CommentResumes(CaseStatus.InProgress, Creator).ShouldBeFalse();
        CaseActionRules.CommentResumes(CaseStatus.Pending, Creator).ShouldBeFalse();
    }

    private static CaseActor Actor(string who) => who switch
    {
        "creator" => Creator,
        "owner" => Owner,
        "former-member-owner" => FormerMemberOwner,
        "member" => Member,
        "manager" => Manager,
        _ => Bystander,
    };
}
