using Shouldly;
using SmartAgri.Application.Accounts;
using SmartAgri.Domain.Accounts;

namespace SmartAgri.Application.Tests.Accounts;

/// <summary>
/// The rules of 停用成員並指定接手人 (issue #356): what the successor must hold, who may be the
/// successor, and when a member cannot be deactivated at all.
/// </summary>
public class MemberOffboardingRulesTests
{
    private static readonly Guid Caller = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Candidate = Guid.CreateVersion7();

    private static readonly MemberHoldings Nothing = new(0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void The_successor_needs_manage_assistants_for_assistants_or_drafts_and_manage_data_sources_for_sources()
    {
        MemberOffboardingRules.RequiredPermissions(Nothing).ShouldBeEmpty();
        MemberOffboardingRules.RequiredPermissions(Nothing with { Drafts = 1 }).ShouldBe([AccountPermission.ManageAssistants]);
        MemberOffboardingRules.RequiredPermissions(Nothing with { Databases = 2 }).ShouldBe([AccountPermission.ManageDataSources]);
        MemberOffboardingRules.RequiredPermissions(Nothing with { KnowledgeBases = 1, Assistants = 3 })
            .ShouldBe([AccountPermission.ManageAssistants, AccountPermission.ManageDataSources]);

        // Work items and borrowed sources need nothing of their own: they degrade instead.
        MemberOffboardingRules.RequiredPermissions(Nothing with { OpenIssues = 4, OpenCases = 2, BorrowedSources = 1 }).ShouldBeEmpty();
    }

    [Fact]
    public void An_internal_member_in_use_with_every_required_permission_is_eligible()
    {
        var required = new[] { AccountPermission.ManageAssistants, AccountPermission.ManageDataSources };

        foreach (var role in new[] { AccountRole.SmbAdmin, AccountRole.InternalEmployee })
        {
            var check = Check(role, deactivated: false, [AccountPermission.ManageDataSources, AccountPermission.ManageAssistants], required);
            (check.IsEligible, check.Reason, check.MissingPermissions.Count).ShouldBe((true, (SuccessorIneligibleReason?)null, 0), $"{role}");
        }

        Check(AccountRole.InternalEmployee, deactivated: false, [], []).IsEligible.ShouldBeTrue("nothing to manage, nothing required");
    }

    [Fact]
    public void External_deactivated_or_under_permissioned_accounts_are_not_eligible()
    {
        var required = new[] { AccountPermission.ManageAssistants, AccountPermission.ManageDataSources };
        var all = new[] { AccountPermission.ManageAssistants, AccountPermission.ManageDataSources };

        Check(AccountRole.ExternalCustomer, deactivated: false, all, required).Reason.ShouldBe(SuccessorIneligibleReason.ExternalCustomer);
        Check(AccountRole.InternalEmployee, deactivated: true, all, required).Reason.ShouldBe(SuccessorIneligibleReason.Deactivated);

        var missing = Check(AccountRole.InternalEmployee, deactivated: false, [AccountPermission.ManageAssistants], required);
        missing.Reason.ShouldBe(SuccessorIneligibleReason.MissingPermissions);
        missing.MissingPermissions.ShouldBe([AccountPermission.ManageDataSources]);
        missing.IsEligible.ShouldBeFalse();

        Should.Throw<ArgumentException>(() => MemberOffboardingRules.CheckSuccessor(
            Member, Member, AccountRole.SmbAdmin, false, new HashSet<AccountPermission>(), []));
    }

    [Fact]
    public void Nobody_deactivates_themselves_twice_or_the_last_manager_in_use()
    {
        MemberOffboardingRules.CheckDeactivation(Caller, Caller, AccountRole.InternalEmployee, false, 3).ShouldBe(MemberDeactivationBlock.Self);
        MemberOffboardingRules.CheckDeactivation(Caller, Member, AccountRole.InternalEmployee, true, 3).ShouldBe(MemberDeactivationBlock.AlreadyDeactivated);
        MemberOffboardingRules.CheckDeactivation(Caller, Member, AccountRole.SmbAdmin, false, 0).ShouldBe(MemberDeactivationBlock.LastAdmin);

        MemberOffboardingRules.CheckDeactivation(Caller, Member, AccountRole.SmbAdmin, false, 1).ShouldBeNull();
        MemberOffboardingRules.CheckDeactivation(Caller, Member, AccountRole.InternalEmployee, false, 0).ShouldBeNull("only a manager can be the last manager");

        foreach (var block in Enum.GetValues<MemberDeactivationBlock>())
        {
            MemberOffboardingRules.MessageOf(block).ShouldNotBeNullOrWhiteSpace();
        }
    }

    private static SuccessorCheck Check(
        AccountRole role, bool deactivated, IReadOnlyCollection<AccountPermission> granted, IReadOnlyList<AccountPermission> required) =>
        MemberOffboardingRules.CheckSuccessor(Member, Candidate, role, deactivated, granted.ToHashSet(), required);
}
