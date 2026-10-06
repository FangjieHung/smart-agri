using Shouldly;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Line;

/// <summary>The in-memory LINE conversation history (M5b plan §3 E, decision E; #231): per assistant
/// and chat, the newest messages only, forgotten after idling, bounded in conversations.</summary>
public class LineConversationHistoryTests
{
    private static readonly Guid AssistantA = Guid.Parse("01a10194-0000-7000-8000-00000000000a");
    private static readonly Guid AssistantB = Guid.Parse("01a10194-0000-7000-8000-00000000000b");

    private readonly ManualClock _clock = new();

    [Fact]
    public void Turns_are_kept_per_assistant_and_chat_oldest_first()
    {
        var history = Create();
        var user = new LineChatKey(AssistantA, "Uuser");
        history.Append(user, "m1", Question("退貨期限？"));
        history.Append(user, "m1", Answer("七天內。"));
        history.Append(new LineChatKey(AssistantA, "Cgroup"), "m2", Question("營業時間？"));
        history.Append(new LineChatKey(AssistantB, "Uuser"), "m3", Question("別的助理"));

        history.Get(user).Select(entry => entry.Turn.Text).ShouldBe(["退貨期限？", "七天內。"]);
        history.Get(new LineChatKey(AssistantA, "Cgroup")).Count.ShouldBe(1);
        history.Get(new LineChatKey(AssistantB, "Uuser")).Single().Turn.Text.ShouldBe("別的助理");
        history.Get(new LineChatKey(AssistantB, "Cgroup")).ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_newest_messages_are_kept()
    {
        var history = Create(maxMessages: 20);
        var chat = new LineChatKey(AssistantA, "Uuser");
        for (var index = 1; index <= 25; index++)
        {
            history.Append(chat, $"m{index}", Question($"q{index}"));
        }

        var kept = history.Get(chat);
        kept.Count.ShouldBe(20);
        kept[0].Turn.Text.ShouldBe("q6");
        kept[^1].Turn.Text.ShouldBe("q25");
    }

    [Fact]
    public void A_conversation_idle_for_30_minutes_is_forgotten_and_activity_keeps_it()
    {
        var history = Create();
        var chat = new LineChatKey(AssistantA, "Uuser");
        history.Append(chat, "m1", Question("q1"));

        _clock.Advance(TimeSpan.FromMinutes(29));
        history.Get(chat).Count.ShouldBe(1, "reading is activity");
        _clock.Advance(TimeSpan.FromMinutes(29));
        history.Get(chat).Count.ShouldBe(1);

        _clock.Advance(TimeSpan.FromMinutes(30));
        history.Get(chat).ShouldBeEmpty();
        history.Count.ShouldBe(0);
    }

    [Fact]
    public void Clear_forgets_the_whole_conversation_only()
    {
        var history = Create();
        var chat = new LineChatKey(AssistantA, "Cgroup");
        var other = new LineChatKey(AssistantA, "Uuser");
        history.Append(chat, "m1", Question("q1"));
        history.Append(other, "m2", Question("q2"));

        history.Clear(chat);
        history.Clear(new LineChatKey(AssistantB, "never-seen"));

        history.Get(chat).ShouldBeEmpty();
        history.Get(other).Count.ShouldBe(1);
    }

    [Fact]
    public void Removing_a_message_removes_the_question_and_its_answer()
    {
        var history = Create();
        var chat = new LineChatKey(AssistantA, "Uuser");
        history.Append(chat, "m1", Question("q1"));
        history.Append(chat, "m1", Answer("a1"));
        history.Append(chat, "m2", Question("q2"));
        history.Append(chat, "m2", Answer("a2"));

        history.RemoveMessage(chat, "m1").ShouldBe(2);
        history.RemoveMessage(chat, "m-unknown").ShouldBe(0);
        history.RemoveMessage(new LineChatKey(AssistantB, "Uuser"), "m2").ShouldBe(0);

        history.Get(chat).Select(entry => entry.Turn.Text).ShouldBe(["q2", "a2"]);
    }

    [Fact]
    public void Beyond_the_capacity_the_least_recently_used_conversation_goes_first()
    {
        var history = Create(maxConversations: 3);
        var first = new LineChatKey(AssistantA, "U1");
        var second = new LineChatKey(AssistantA, "U2");
        var third = new LineChatKey(AssistantA, "U3");
        history.Append(first, null, Question("1"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        history.Append(second, null, Question("2"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        history.Append(third, null, Question("3"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        history.Get(first).Count.ShouldBe(1, "used again: now the most recent");

        history.Append(new LineChatKey(AssistantA, "U4"), null, Question("4"));

        history.Count.ShouldBe(3);
        history.Get(second).ShouldBeEmpty();
        history.Get(first).Count.ShouldBe(1);
        history.Get(third).Count.ShouldBe(1);
    }

    [Fact]
    public void The_defaults_are_decision_Es()
    {
        LineConversationHistoryLimits.Default.ShouldBe(new LineConversationHistoryLimits(20, TimeSpan.FromMinutes(30), 10_000));
    }

    private InMemoryLineConversationHistory Create(int maxMessages = 20, int maxConversations = 10_000) =>
        new(_clock, new LineConversationHistoryLimits(maxMessages, TimeSpan.FromMinutes(30), maxConversations));

    private static ConversationTurn Question(string text) => new(ConversationAuthor.Account, text);

    private static ConversationTurn Answer(string text) => new(ConversationAuthor.Assistant, text);
}

/// <summary>A clock that moves only when told to.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
