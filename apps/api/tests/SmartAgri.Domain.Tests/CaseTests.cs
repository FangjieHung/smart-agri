using Shouldly;
using SmartAgri.Domain.Cases;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="Case"/> creation and its <c>created</c> event (M7-3, #248). The request rules are in
/// <c>SmartAgri.Application.Tests/Cases/CaseRulesTests.cs</c>; who sees a case in <c>CaseVisibilityTests</c>.</summary>
public class CaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);

    private static readonly Guid Creator = Guid.CreateVersion7();

    [Fact]
    public void A_new_case_is_pending_in_its_group_and_its_created_event_records_the_starting_values()
    {
        var (type, group) = TypeAndGroup();
        var links = new CaseLinks(ThreadAssistantId: Guid.CreateVersion7(), ThreadId: Guid.CreateVersion7());

        var (item, created) = Case.Create(
            CaseOrigin.Manual, type, group, Creator, "冷藏庫溫度降不下來", "請派人來看", Now.AddHours(72), links, Now);

        (item.OrganizationId, item.TypeId, item.GroupId, item.Status, item.Origin, item.CreatedByAccountId, item.OwnerAccountId)
            .ShouldBe((type.OrganizationId, type.Id, group.Id, CaseStatus.Pending, CaseOrigin.Manual, (Guid?)Creator, (Guid?)null));
        (item.Title, item.Description, item.DueAt, item.CreatedAt, item.UpdatedAt, item.EventCount)
            .ShouldBe(("冷藏庫溫度降不下來", "請派人來看", Now.AddHours(72), Now, Now, 1));
        (item.ThreadAssistantId, item.ThreadId).ShouldBe((links.ThreadAssistantId, links.ThreadId));
        (item.DatabaseId, item.SubmissionId, item.AssistantIssueId, item.PreviousCaseId).ShouldBe(((Guid?)null, (Guid?)null, (Guid?)null, (Guid?)null));

        (created.CaseId, created.OrganizationId, created.Ordinal, created.Action, created.ActorAccountId, created.At)
            .ShouldBe((item.Id, item.OrganizationId, 1, CaseEventAction.Created, (Guid?)Creator, Now));
        (created.Status, created.ToGroupId, created.DueAt, created.OwnerAccountId, created.FromGroupId, created.Note)
            .ShouldBe(((CaseStatus?)CaseStatus.Pending, (Guid?)group.Id, (DateTimeOffset?)Now.AddHours(72), (Guid?)null, (Guid?)null, (string?)null));
    }

    [Fact]
    public void The_due_time_may_be_now_but_not_earlier()
    {
        var (type, group) = TypeAndGroup();

        Case.Create(CaseOrigin.Manual, type, group, Creator, "標題", "", Now, CaseLinks.None, Now).Case.DueAt.ShouldBe(Now);
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Case.Create(CaseOrigin.Manual, type, group, Creator, "標題", "", Now.AddTicks(-10), CaseLinks.None, Now));
    }

    [Fact]
    public void An_inactive_type_an_archived_group_or_another_organizations_group_is_refused()
    {
        var (type, group) = TypeAndGroup();
        var inactive = CaseType.Create(type.OrganizationId, "停用類型", "", group, 24, isActive: false, Now);
        var archived = CaseGroup.Create(type.OrganizationId, "舊組", Now);
        archived.Archive(Now);
        var foreign = CaseGroup.Create(Guid.CreateVersion7(), "別組", Now);

        Should.Throw<InvalidOperationException>(() =>
            Case.Create(CaseOrigin.Manual, inactive, group, Creator, "標題", "", Now.AddHours(1), CaseLinks.None, Now));
        Should.Throw<InvalidOperationException>(() =>
            Case.Create(CaseOrigin.Manual, type, archived, Creator, "標題", "", Now.AddHours(1), CaseLinks.None, Now));
        Should.Throw<ArgumentException>(() =>
            Case.Create(CaseOrigin.Manual, type, foreign, Creator, "標題", "", Now.AddHours(1), CaseLinks.None, Now));
    }

    [Fact]
    public void Only_a_case_opened_from_a_database_submission_has_no_creator()
    {
        var (type, group) = TypeAndGroup();
        var record = new CaseLinks(DatabaseId: Guid.CreateVersion7(), SubmissionId: Guid.CreateVersion7());

        var (automatic, created) = Case.Create(
            CaseOrigin.DatabaseSubmission, type, group, null, "客戶資料庫：新紀錄", "", Now.AddHours(1), record, Now);
        automatic.CreatedByAccountId.ShouldBeNull();
        created.ActorAccountId.ShouldBeNull();

        Should.Throw<ArgumentException>(() =>
            Case.Create(CaseOrigin.DatabaseSubmission, type, group, Creator, "標題", "", Now.AddHours(1), record, Now));
        foreach (var origin in new[] { CaseOrigin.Manual, CaseOrigin.ChatProposal, CaseOrigin.AssistantIssue })
        {
            Should.Throw<ArgumentException>(() =>
                Case.Create(origin, type, group, null, "標題", "", Now.AddHours(1), CaseLinks.None, Now), origin.ToString());
        }
    }

    [Fact]
    public void A_link_pair_is_both_ids_or_none_and_the_text_is_trimmed_and_bounded()
    {
        var (type, group) = TypeAndGroup();

        foreach (var links in new[]
                 {
                     new CaseLinks(ThreadAssistantId: Guid.CreateVersion7()),
                     new CaseLinks(ThreadId: Guid.CreateVersion7()),
                     new CaseLinks(DatabaseId: Guid.CreateVersion7()),
                     new CaseLinks(SubmissionId: Guid.CreateVersion7()),
                     new CaseLinks(DatabaseId: Guid.Empty, SubmissionId: Guid.CreateVersion7()),
                 })
        {
            Should.Throw<ArgumentException>(() =>
                Case.Create(CaseOrigin.Manual, type, group, Creator, "標題", "", Now.AddHours(1), links, Now));
        }

        foreach (var (title, description) in new[]
                 {
                     ("", ""), (" 標題", ""), (new string('案', Case.TitleMaxLength + 1), ""),
                     ("標題", new string('說', Case.DescriptionMaxLength + 1)), ("標題", "說明 "),
                 })
        {
            Should.Throw<ArgumentException>(() =>
                Case.Create(CaseOrigin.Manual, type, group, Creator, title, description, Now.AddHours(1), CaseLinks.None, Now));
        }

        Case.Create(
                CaseOrigin.Manual, type, group, Creator, new string('案', Case.TitleMaxLength),
                new string('說', Case.DescriptionMaxLength), Now.AddHours(1), CaseLinks.None, Now)
            .Case.Title.Length.ShouldBe(Case.TitleMaxLength);
    }

    [Fact]
    public void Open_and_closed_statuses_split_the_five()
    {
        CaseStatuses.Open.Concat(CaseStatuses.Closed).ShouldBe(Enum.GetValues<CaseStatus>(), ignoreOrder: true);
        CaseStatuses.Open.ShouldAllBe(status => status.IsOpen());
        CaseStatuses.Closed.ShouldAllBe(status => !status.IsOpen());
        WireNames<CaseStatus>.All.ShouldBe(["pending", "in-progress", "awaiting-info", "completed", "cancelled"]);
        WireNames<CaseOrigin>.All.ShouldBe(["manual", "chat-proposal", "database-submission", "assistant-issue"]);
        WireNames<CaseEventAction>.All.ShouldBe(
            ["created", "accepted", "info-requested", "commented", "resumed", "completed", "cancelled", "transferred", "due-changed"]);
    }

    private static (CaseType Type, CaseGroup Group) TypeAndGroup()
    {
        var organizationId = Guid.CreateVersion7();
        var group = CaseGroup.Create(organizationId, "設備組", Now);
        return (CaseType.Create(organizationId, "設備故障報修", "", group, 72, true, Now), group);
    }
}
