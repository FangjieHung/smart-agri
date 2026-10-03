using Shouldly;
using SmartAgri.Application.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

public sealed class AssistantFormRequestRulesTests
{
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly AssistantFormTarget None = new(null, string.Empty);

    [Theory]
    [InlineData("我要填寫客戶資料", true)]
    [InlineData("我 要 回 報 訂單問題", true)]
    [InlineData("請給我表單", true)]
    [InlineData("營業時間是幾點？", false)]
    public void Asks_for_a_form_only_when_the_question_says_so(string question, bool expected) =>
        AssistantFormRequestRules.AsksForForm(question).ShouldBe(expected);

    [Fact]
    public void A_target_must_be_a_usable_connected_database_with_a_purpose()
    {
        var valid = AssistantFormRequestRules.ForUpdate(None, A.ToString(), "  回訪紀錄  ", [A, B]);
        valid.Value.ShouldBe(new AssistantFormTarget(A, "回訪紀錄"));

        AssistantFormRequestRules.ForUpdate(None, A.ToString(), " ", [A]).Failures.Single().Field.ShouldBe("dataWritePurpose");
        AssistantFormRequestRules.ForUpdate(None, A.ToString(), new string('字', 501), [A]).Failures.Single().Field.ShouldBe("dataWritePurpose");
        AssistantFormRequestRules.ForUpdate(None, B.ToString(), "目的", [A]).Failures.Single().Field.ShouldBe("sources");
        AssistantFormRequestRules.ForUpdate(None, "not-a-guid", "目的", [A]).Failures.Single().Field.ShouldBe("sources");
    }

    [Fact]
    public void Absent_values_keep_the_current_ones_and_an_empty_id_clears_the_target()
    {
        var current = new AssistantFormTarget(A, "目的");

        AssistantFormRequestRules.ForUpdate(current, null, "新的目的", [A]).Value.ShouldBe(new AssistantFormTarget(A, "新的目的"));
        AssistantFormRequestRules.ForUpdate(current, B.ToString(), null, [A, B]).Value.ShouldBe(new AssistantFormTarget(B, "目的"));
        AssistantFormRequestRules.ForUpdate(current, string.Empty, "被忽略", [A]).Value.ShouldBe(None);
    }

    [Fact]
    public void The_receipt_text_names_the_recipient_and_number_but_no_answer() =>
        AssistantFormRequestRules.ReceiptText("安心商行（客戶資料庫）", "R-20261003-0000000001")
            .ShouldBe("已送出。資料只會交給 安心商行（客戶資料庫），回執編號 R-20261003-0000000001。");
}
