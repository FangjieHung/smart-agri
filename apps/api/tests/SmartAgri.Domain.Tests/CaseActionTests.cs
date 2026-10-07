using Shouldly;
using SmartAgri.Domain.Cases;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// <see cref="Case"/>'s actions (M7-4, #249): each moves the status as the action table says, writes
/// exactly one numbered event with the values it changed, and refuses a status it does not belong to
/// — a closed case refuses everything. Who may act is <c>CaseActionRulesTests</c> (Application).
/// </summary>
public class CaseActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 2, 0, 0, TimeSpan.Zero);

    private static readonly Guid Creator = Guid.CreateVersion7();

    private static readonly Guid Aming = Guid.CreateVersion7();

    [Fact]
    public void Accepting_makes_the_member_the_case_owner_and_records_them_on_the_event()
    {
        var (item, _, _) = NewCase();

        var accepted = item.Accept(Aming, Now.AddMinutes(5));

        (item.Status, item.OwnerAccountId, item.AcceptedAt, item.EventCount, item.UpdatedAt)
            .ShouldBe((CaseStatus.InProgress, (Guid?)Aming, (DateTimeOffset?)Now.AddMinutes(5), 2, Now.AddMinutes(5)));
        (accepted.Ordinal, accepted.Action, accepted.ActorAccountId, accepted.OwnerAccountId, accepted.Status, accepted.At)
            .ShouldBe((2, CaseEventAction.Accepted, (Guid?)Aming, (Guid?)Aming, (CaseStatus?)CaseStatus.InProgress, Now.AddMinutes(5)));
        Should.Throw<InvalidOperationException>(() => item.Accept(Creator, Now), "only a pending case is accepted");
    }

    [Fact]
    public void Requesting_information_resuming_and_commenting_move_between_in_progress_and_awaiting_info()
    {
        var (item, _, _) = NewCase();
        Should.Throw<InvalidOperationException>(() => item.RequestInfo(Aming, "請補照片", Now), "pending");
        item.Accept(Aming, Now);

        var requested = item.RequestInfo(Aming, "  請補上溫度紀錄的照片  ", Now);
        (item.Status, requested.Action, requested.Note, requested.Status)
            .ShouldBe((CaseStatus.AwaitingInfo, CaseEventAction.InfoRequested, "請補上溫度紀錄的照片", (CaseStatus?)CaseStatus.AwaitingInfo));
        Should.Throw<InvalidOperationException>(() => item.RequestInfo(Aming, "再補", Now), "already awaiting");

        // The owner's comment keeps the status; the creator's answer resumes it (one event either way).
        var ownersNote = item.Comment(Aming, "已打電話提醒", resumes: false, Now);
        (item.Status, ownersNote.Status, ownersNote.Note).ShouldBe((CaseStatus.AwaitingInfo, (CaseStatus?)null, "已打電話提醒"));
        var answer = item.Comment(Creator, "照片已上傳到共用資料夾", resumes: true, Now);
        (item.Status, answer.Action, answer.Status).ShouldBe((CaseStatus.InProgress, CaseEventAction.Commented, (CaseStatus?)CaseStatus.InProgress));
        Should.Throw<InvalidOperationException>(() => item.Comment(Creator, "再補一張", resumes: true, Now), "resumes only from awaiting-info");

        Should.Throw<InvalidOperationException>(() => item.Resume(Aming, null, Now), "in progress");
        item.RequestInfo(Aming, "還缺型號", Now);
        var resumed = item.Resume(Aming, "  ", Now);
        (item.Status, resumed.Action, resumed.Note, resumed.Status)
            .ShouldBe((CaseStatus.InProgress, CaseEventAction.Resumed, (string?)null, (CaseStatus?)CaseStatus.InProgress));
        item.EventCount.ShouldBe(7);
    }

    [Fact]
    public void Completing_needs_a_resolution_and_works_from_in_progress_and_awaiting_info()
    {
        var (item, _, _) = NewCase();
        Should.Throw<InvalidOperationException>(() => item.Complete(Aming, "已修好", Now), "pending");
        item.Accept(Aming, Now);
        item.RequestInfo(Aming, "請補照片", Now);
        Should.Throw<ArgumentException>(() => item.Complete(Aming, "   ", Now));
        Should.Throw<ArgumentException>(() => item.Complete(Aming, new string('結', CaseEvent.NoteMaxLength + 1), Now));
        item.Status.ShouldBe(CaseStatus.AwaitingInfo, "a refused completion changes nothing");
        item.EventCount.ShouldBe(3);

        var completed = item.Complete(Aming, " 已更換壓縮機 ", Now.AddHours(1));

        (item.Status, item.Resolution, item.CompletedAt).ShouldBe((CaseStatus.Completed, "已更換壓縮機", (DateTimeOffset?)Now.AddHours(1)));
        (completed.Action, completed.Note, completed.Status).ShouldBe((CaseEventAction.Completed, "已更換壓縮機", (CaseStatus?)CaseStatus.Completed));
    }

    [Fact]
    public void Cancelling_keeps_its_optional_reason()
    {
        var (item, _, _) = NewCase();
        var cancelled = item.Cancel(Creator, null, Now);
        (item.Status, item.CancelReason, item.CancelledAt, cancelled.Note, cancelled.Status)
            .ShouldBe((CaseStatus.Cancelled, (string?)null, (DateTimeOffset?)Now, (string?)null, (CaseStatus?)CaseStatus.Cancelled));

        var (other, _, _) = NewCase();
        other.Accept(Aming, Now);
        other.Cancel(Aming, "  客戶撤回需求 ", Now);
        other.CancelReason.ShouldBe("客戶撤回需求");
    }

    [Fact]
    public void A_transfer_moves_to_another_group_clears_the_owner_and_records_both_groups()
    {
        var (item, equipment, purchasing) = NewCase();
        item.Accept(Aming, Now);

        var transferred = item.Transfer(Aming, purchasing, " 需要採購零件 ", Now);

        (item.Status, item.GroupId, item.OwnerAccountId).ShouldBe((CaseStatus.Pending, purchasing.Id, (Guid?)null));
        (transferred.Action, transferred.FromGroupId, transferred.ToGroupId, transferred.Status, transferred.Note, transferred.OwnerAccountId)
            .ShouldBe((CaseEventAction.Transferred, (Guid?)equipment.Id, (Guid?)purchasing.Id, (CaseStatus?)CaseStatus.Pending, "需要採購零件", (Guid?)null));

        var archived = CaseGroup.Create(item.OrganizationId, "舊倉儲組", Now);
        archived.Archive(Now);
        Should.Throw<InvalidOperationException>(() => item.Transfer(Aming, purchasing, null, Now), "the same group");
        Should.Throw<InvalidOperationException>(() => item.Transfer(Aming, archived, null, Now));
        Should.Throw<ArgumentException>(() => item.Transfer(Aming, CaseGroup.Create(Guid.CreateVersion7(), "別家", Now), null, Now));
        item.EventCount.ShouldBe(3);
    }

    [Fact]
    public void A_new_due_time_is_never_earlier_than_now_and_a_refused_one_changes_nothing()
    {
        var (item, _, _) = NewCase();
        var due = item.DueAt;

        Should.Throw<ArgumentOutOfRangeException>(() => item.SetDue(Aming, Now.AddTicks(-10), null, Now));
        (item.DueAt, item.EventCount).ShouldBe((due, 1));

        var changed = item.SetDue(Aming, Now, "等廠商報價", Now);
        (item.DueAt, changed.Action, changed.DueAt, changed.Note).ShouldBe((Now, CaseEventAction.DueChanged, (DateTimeOffset?)Now, "等廠商報價"));
    }

    [Fact]
    public void The_operations_command_may_move_the_due_time_into_the_past_with_an_event_that_has_no_actor()
    {
        var (item, _, _) = NewCase();
        var past = Now.AddHours(-1);

        var changed = item.SetDueByOperations(past, Now.AddMinutes(1));

        (item.DueAt, item.EventCount, item.UpdatedAt, item.Status).ShouldBe((past, 2, Now.AddMinutes(1), CaseStatus.Pending));
        (changed.Ordinal, changed.Action, changed.ActorAccountId, changed.DueAt, changed.Note, changed.Status)
            .ShouldBe((2, CaseEventAction.DueChanged, (Guid?)null, (DateTimeOffset?)past, (string?)null, (CaseStatus?)null));

        item.Cancel(Creator, null, Now);
        Should.Throw<InvalidOperationException>(() => item.SetDueByOperations(past, Now), "a closed case is never changed");
        item.EventCount.ShouldBe(3);
    }

    [Fact]
    public void A_closed_case_refuses_every_action()
    {
        var (completed, _, purchasing) = NewCase();
        completed.Accept(Aming, Now);
        completed.Complete(Aming, "已修好", Now);
        var (cancelled, _, _) = NewCase();
        cancelled.Cancel(Creator, null, Now);

        foreach (var item in new[] { completed, cancelled })
        {
            var count = item.EventCount;
            var actions = new Action[]
            {
                () => item.Accept(Aming, Now),
                () => item.RequestInfo(Aming, "補件", Now),
                () => item.Resume(Aming, null, Now),
                () => item.Comment(Creator, "補充", resumes: false, Now),
                () => item.Complete(Aming, "再完成一次", Now),
                () => item.Cancel(Aming, "取消", Now),
                () => item.Transfer(Aming, purchasing, null, Now),
                () => item.SetDue(Aming, Now.AddDays(1), null, Now),
            };
            foreach (var action in actions)
            {
                Should.Throw<InvalidOperationException>(action);
            }

            item.EventCount.ShouldBe(count);
        }
    }

    private static (Case Case, CaseGroup Equipment, CaseGroup Purchasing) NewCase()
    {
        var organizationId = Guid.CreateVersion7();
        var equipment = CaseGroup.Create(organizationId, "設備組", Now);
        var purchasing = CaseGroup.Create(organizationId, "採購組", Now);
        var type = CaseType.Create(organizationId, "設備故障報修", "", equipment, 72, true, Now);
        var (item, _) = Case.Create(CaseOrigin.Manual, type, equipment, Creator, "冷藏庫溫度異常", "", Now.AddHours(72), CaseLinks.None, Now);
        return (item, equipment, purchasing);
    }
}
