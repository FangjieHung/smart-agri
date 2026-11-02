using System.Text.Json;
using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>The 統籌助理's model tool (#374): its declaration, its prompts and reading the model's call.</summary>
public class CoordinatorCommandToolTests
{
    private static readonly ShopSettingsDocument Shop = CoordinatorTestShop.Settings();

    [Fact]
    public void The_tool_requires_every_parameter_and_allows_nothing_else()
    {
        var tool = CoordinatorCommandTool.Declaration();

        tool.Name.ShouldBe("submit_store_command");
        var schema = tool.JsonSchema;
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["scenario", "date", "time", "reason", "question"]);
        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        schema.GetProperty("properties").GetProperty("scenario").GetProperty("enum").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["temporary-closure", "reopen", "close-early", "open-late", "none"]);
        schema.GetProperty("properties").GetProperty("date").GetProperty("type").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["string", "null"]);
    }

    [Fact]
    public void The_prompt_gives_the_shops_local_now_a_14_day_calendar_its_hours_and_the_rules()
    {
        var prompt = CoordinatorCommandTool.Prompt(Shop, CoordinatorTestShop.Now, "  今天臨時店休  ");

        prompt.Count.ShouldBe(2);
        prompt[0].Role.ShouldBe(ChatRole.System);
        var system = prompt[0].Text;
        system.ShouldContain("店家「王記麵店」");
        system.ShouldContain("現在是店家當地時間 2026-11-02（週一）10:00，時區 Asia/Taipei。");
        system.ShouldContain("「下週」是 2026-11-09 到 2026-11-15");
        system.ShouldContain("- 2026-11-02（週一） 今天\n- 2026-11-03（週二） 明天\n- 2026-11-04（週三） 後天\n- 2026-11-05（週四）\n");
        system.ShouldContain("- 2026-11-15（週日）\n");
        system.ShouldNotContain("2026-11-16");
        system.ShouldContain("週三 公休");
        system.ShouldContain("週六 11:00–14:00、17:00–01:00");
        system.ShouldContain("絕對不要猜");
        system.ShouldContain("question");
        prompt[1].Role.ShouldBe(ChatRole.User);
        prompt[1].Text.ShouldBe("今天臨時店休");
    }

    [Fact]
    public void On_a_sunday_next_week_starts_tomorrow()
    {
        var sunday = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(8));

        CoordinatorCommandTool.Prompt(Shop, sunday, "下週一店休")[0].Text.ShouldContain("「下週」是 2026-11-09 到 2026-11-15");
        CoordinatorCommandTool.Prompt(Shop, CoordinatorTestShop.Now.AddDays(-1).AddHours(-12), "x")[0].Text
            .ShouldContain("現在是店家當地時間 2026-10-31（週六）22:00");
    }

    [Fact]
    public void A_long_message_is_cut_before_it_reaches_the_model()
    {
        var prompt = CoordinatorCommandTool.Prompt(Shop, CoordinatorTestShop.Now, new string('休', 800));

        prompt[1].Text!.Length.ShouldBe(CoordinatorCommandTool.MessageMaxLength);
    }

    [Fact]
    public void The_follow_up_prompt_says_what_is_pending_and_what_is_missing()
    {
        var prompt = CoordinatorCommandTool.FollowUpPrompt(
            Shop, CoordinatorTestShop.Now, new CoordinatorPendingDetails(ShopScenario.CloseEarly, new DateOnly(2026, 11, 3), null), "四點");

        prompt[0].Text.ShouldContain("上一個指令：close-early（提早打烊）；已知日期：2026-11-03；已知時間：未定；缺少：時間。");
        prompt[0].Text.ShouldContain("scenario 填 none");
        prompt[1].Text.ShouldBe("四點");
        prompt[0].Text.ShouldNotContain("你上一個追問是");
        var asked = CoordinatorCommandTool.FollowUpPrompt(
            Shop, CoordinatorTestShop.Now, new CoordinatorPendingDetails(ShopScenario.TemporaryClosure, null, null, "下週要休哪一天？"), "週四");
        asked[0].Text.ShouldContain("你上一個追問是「下週要休哪一天？」。上一個指令：temporary-closure（臨時店休）；已知日期：未定；已知時間：未定；缺少：日期。");
        new CoordinatorPendingDetails(ShopScenario.TemporaryClosure, null, null, "家裡有事嗎？").ToString().ShouldNotContain("家裡");
    }

    [Fact]
    public void A_call_is_read_from_strings_or_json_values()
    {
        var call = CoordinatorCommandTool.ParseCall(Call(new()
        {
            ["scenario"] = "close-early",
            ["date"] = JsonElement("\"2026-11-02\""),
            ["time"] = "16:00",
            ["reason"] = "  家裡\n有事  ",
            ["question"] = null,
        }));

        call.ShouldBe(new CoordinatorCommandCall(ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(16, 0), "家裡 有事", null));
        call.ToString().ShouldNotContain("家裡");
    }

    [Fact]
    public void An_ambiguous_command_comes_back_with_its_question()
    {
        var call = CoordinatorCommandTool.ParseCall(Call(new()
        {
            ["scenario"] = "temporary-closure", ["date"] = null, ["time"] = null, ["reason"] = null, ["question"] = "下週要休哪一天？",
        }));

        call.ShouldBe(new CoordinatorCommandCall(ShopScenario.TemporaryClosure, null, null, null, "下週要休哪一天？"));
    }

    [Theory]
    [InlineData("11/15")]
    [InlineData("2026-13-01")]
    [InlineData("2026/11/15")]
    [InlineData("")]
    public void A_date_that_is_not_yyyy_mm_dd_is_dropped(string date)
    {
        CoordinatorCommandTool.ParseCall(Call(new() { ["scenario"] = "temporary-closure", ["date"] = date })).Date.ShouldBeNull();
    }

    [Fact]
    public void A_time_is_kept_only_for_close_early_and_open_late_and_only_as_hh_mm()
    {
        CoordinatorCommandTool.ParseCall(Call(new() { ["scenario"] = "temporary-closure", ["date"] = "2026-11-02", ["time"] = "16:00" })).Time.ShouldBeNull();
        CoordinatorCommandTool.ParseCall(Call(new() { ["scenario"] = "open-late", ["time"] = "4pm" })).Time.ShouldBeNull();
        CoordinatorCommandTool.ParseCall(Call(new() { ["scenario"] = "open-late", ["time"] = "12:00" })).Time.ShouldBe(new TimeOnly(12, 0));
    }

    [Fact]
    public void A_reason_is_cut_to_50_characters_and_null_texts_are_none()
    {
        var call = CoordinatorCommandTool.ParseCall(Call(new()
        {
            ["scenario"] = "temporary-closure", ["reason"] = new string('因', 80), ["question"] = "null",
        }));

        call.Reason!.Length.ShouldBe(CoordinatorCommandTool.ReasonMaxLength);
        call.Question.ShouldBeNull();
    }

    [Fact]
    public void No_call_another_tool_none_or_an_unknown_scenario_is_not_a_command()
    {
        CoordinatorCommandTool.ParseCall(new ChatResponse(new ChatMessage(ChatRole.Assistant, "你好！"))).ShouldBe(CoordinatorCommandCall.None);
        CoordinatorCommandTool.ParseCall(Call(new() { ["scenario"] = "none" })).ShouldBe(CoordinatorCommandCall.None);
        CoordinatorCommandTool.ParseCall(Call(new() { ["scenario"] = "holiday", ["date"] = "2026-11-02" })).ShouldBe(CoordinatorCommandCall.None);
        CoordinatorCommandTool.ParseCall(Call(new() { ["date"] = "2026-11-02" })).ShouldBe(CoordinatorCommandCall.None);
        CoordinatorCommandTool.ParseCall(Call(new() { ["scenario"] = "temporary-closure" }, name: "database_record_count")).ShouldBe(CoordinatorCommandCall.None);
    }

    private static ChatResponse Call(Dictionary<string, object?> arguments, string name = CoordinatorCommandTool.ToolName) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", name, arguments)]));

    private static JsonElement JsonElement(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
