using Shouldly;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Validation;

namespace SmartAgri.Application.Tests.Cases;

/// <summary><see cref="CaseRules"/>: the request rules of <c>POST /api/v1/cases</c> (M7-3, #248).</summary>
public class CaseRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);

    private static readonly Guid TypeId = Guid.CreateVersion7();

    private static readonly Guid GroupId = Guid.CreateVersion7();

    [Fact]
    public void Valid_fields_are_trimmed_and_links_kept_as_pairs()
    {
        var databaseId = Guid.CreateVersion7();
        var submissionId = Guid.CreateVersion7();
        var previous = Guid.CreateVersion7();

        var result = CaseRules.ValidateCreate(
            TypeId, GroupId, Now, "  冷藏庫溫度異常  ", "\n請派人來看\n", databaseId, submissionId, null, null, previous);

        result.IsValid.ShouldBeTrue();
        (result.Value.Title, result.Value.Description, result.Value.TypeId, result.Value.GroupId, result.Value.DueAt)
            .ShouldBe(("冷藏庫溫度異常", "請派人來看", TypeId, GroupId, Now));
        (result.Value.Links.DatabaseId, result.Value.Links.SubmissionId, result.Value.Links.PreviousCaseId)
            .ShouldBe(((Guid?)databaseId, (Guid?)submissionId, (Guid?)previous));
        (result.Value.Links.ThreadAssistantId, result.Value.Links.ThreadId, result.Value.Links.AssistantIssueId)
            .ShouldBe(((Guid?)null, (Guid?)null, (Guid?)null));
    }

    [Fact]
    public void Every_field_failure_is_reported_at_once()
    {
        var result = CaseRules.ValidateCreate(
            null, Guid.Empty, null, "   ", new string('說', 4001), Guid.CreateVersion7(), null, null, Guid.CreateVersion7(), null);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldBe(
        [
            new ValidationFailure("typeId", "請選擇案件類型。"),
            new ValidationFailure("groupId", "請選擇承辦組。"),
            new ValidationFailure("dueAt", "請填寫處理時限。"),
            new ValidationFailure("title", "請輸入案件標題。"),
            new ValidationFailure("description", "說明請在 4,000 個字以內。"),
            new ValidationFailure("submissionId", "連結數據庫紀錄時，請同時提供數據庫與紀錄。"),
            new ValidationFailure("threadId", "連結對話時，請同時提供助理與對話。"),
        ]);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void The_title_is_one_to_120_characters(int length, bool valid)
    {
        var result = CaseRules.ValidateCreate(TypeId, GroupId, Now, new string('案', length), "", null, null, null, null, null);

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            result.Failures.ShouldHaveSingleItem().ShouldBe(new ValidationFailure("title", "案件標題請在 120 個字以內。"));
        }
    }

    [Theory]
    [InlineData(4000, true)]
    [InlineData(4001, false)]
    public void The_description_is_at_most_4000_characters(int length, bool valid) =>
        CaseRules.ValidateCreate(TypeId, GroupId, Now, "標題", new string('說', length), null, null, null, null, null)
            .IsValid.ShouldBe(valid);

    [Fact]
    public void A_due_time_may_be_now_but_not_earlier()
    {
        CaseRules.IsDueInPast(Now, Now).ShouldBeFalse();
        CaseRules.IsDueInPast(Now.AddHours(1), Now).ShouldBeFalse();
        CaseRules.IsDueInPast(Now.AddTicks(-1), Now).ShouldBeTrue();
        CaseRules.DueInPastMessage.ShouldBe("時限不能早於現在。");
    }
}
