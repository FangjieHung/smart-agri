using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>What the 統籌助理 says (store-coordinator design §5 Slice 1 and Q24, #372).</summary>
public class CoordinatorRepliesTests
{
    [Fact]
    public void The_fixed_texts_are_the_designs()
    {
        CoordinatorReplies.Welcome.ShouldBe("歡迎使用統籌助理。請輸入老闆或平台營運者給你的綁定碼。");
        CoordinatorReplies.AskForCode.ShouldBe("請輸入綁定碼。");
        CoordinatorReplies.InvalidCode.ShouldBe("綁定碼無效或已過期，請向老闆或平台營運者索取新的綁定碼。");
        CoordinatorReplies.TooManyAttempts.ShouldBe("嘗試太多次，請 15 分鐘後再試。");
        CoordinatorReplies.OwnersOnly.ShouldBe("只有老闆可以產生店長綁定碼。");
        CoordinatorReplies.Menu.ShouldBe("我目前可以幫你：臨時店休／恢復營業／提早打烊／延後開店");
        CoordinatorReplies.Bound("王記麵店", CoordinatorRole.Owner).ShouldBe("已綁定 王記麵店（老闆）。");
        CoordinatorReplies.Bound("王記麵店", CoordinatorRole.Manager).ShouldBe("已綁定 王記麵店（店長）。");
        CoordinatorReplies.AlreadyBound("王記麵店", CoordinatorRole.Manager).ShouldBe("你已經綁定 王記麵店（店長）。");
    }

    [Fact]
    public void A_manager_code_shows_the_code_the_store_the_local_expiry_and_that_it_works_once()
    {
        var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

        var text = CoordinatorReplies.ManagerCode("ABCD2345", "王記麵店", new DateTimeOffset(2026, 11, 3, 6, 5, 0, TimeSpan.Zero), taipei);

        text.ShouldBe(
            "店長綁定碼：ABCD-2345\n店名：王記麵店\n有效期限：2026/11/03 14:05（台北時間）前\n這組碼只能使用一次，請交給店長加入統籌助理後輸入。");

        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        CoordinatorReplies.ManagerCode("ABCD2345", "王記麵店", new DateTimeOffset(2026, 11, 3, 6, 5, 0, TimeSpan.Zero), tokyo)
            .ShouldContain("2026/11/03 15:05（Asia/Tokyo）");
    }

    [Fact]
    public void The_store_choice_is_quick_reply_postbacks_with_short_labels_and_at_most_13()
    {
        var stores = Enumerable.Range(1, 15)
            .Select(index => (Guid.CreateVersion7(), index == 1 ? "超級無敵長的店名一二三四五六七八九十甲乙丙丁戊" : $"第{index}店"))
            .ToList();

        var message = CoordinatorReplies.StoreChoice(stores);

        message["type"]!.GetValue<string>().ShouldBe("text");
        message["text"]!.GetValue<string>().ShouldBe(CoordinatorReplies.ChooseStore);
        var items = message["quickReply"]!["items"]!.AsArray();
        items.Count.ShouldBe(13);
        for (var index = 0; index < items.Count; index++)
        {
            var action = items[index]!["action"]!;
            items[index]!["type"]!.GetValue<string>().ShouldBe("action");
            action["type"]!.GetValue<string>().ShouldBe("postback");
            action["label"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(20);
            action["data"]!.GetValue<string>().ShouldBe(CoordinatorMessageRouter.ManagerCodePostback(stores[index].Item1));
            CoordinatorMessageRouter.TryParseManagerCodePostback(action["data"]!.GetValue<string>(), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(stores[index].Item1);
        }

        items[0]!["action"]!["label"]!.GetValue<string>().ShouldBe("超級無敵長的店名一二三四五六七八九十甲…");
        items[1]!["action"]!["label"]!.GetValue<string>().ShouldBe("第2店");
    }

    [Fact]
    public void A_label_never_splits_a_surrogate_pair()
    {
        var name = new string('店', 18) + "😀😀";

        var label = CoordinatorReplies.Label(name);

        label.Length.ShouldBeLessThanOrEqualTo(20);
        label.ShouldBe(new string('店', 18) + "…");
        CoordinatorReplies.Label("王記麵店").ShouldBe("王記麵店");
    }
}
