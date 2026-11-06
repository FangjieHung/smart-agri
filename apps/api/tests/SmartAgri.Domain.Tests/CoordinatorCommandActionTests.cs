using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>統籌助理 command actions (#375): how a new row starts, and what it refuses.</summary>
public class CoordinatorCommandActionTests
{
    private const string Owner = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");
    private static readonly Guid Command = Guid.Parse("0194a100-0000-7000-8000-0000000000d1");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_reminder_due_now_awaits_the_owner_and_is_reported_at_once()
    {
        var row = Create(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, ShopActionMode.Reminder, Now.AddMinutes(-5), image: "https://example.com/a.jpg");

        row.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual);
        row.DueAt.ShouldBe(Now, "never earlier than now");
        row.ReportedAt.ShouldBe(Now);
        row.ImageUrl.ShouldBeNull("only a LINE 群發 or an FB 貼文 carries the image");
        row.Attempts.ShouldBe(0);
        row.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void A_later_reminder_and_any_automatic_action_wait_for_their_job()
    {
        var later = Create(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Pause, ShopActionMode.Reminder, Now.AddMinutes(30));
        later.Status.ShouldBe(CoordinatorActionStatus.Scheduled);
        later.ReportedAt.ShouldBe(Now, "the report says when it comes");

        var automatic = Create(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Apply, ShopActionMode.Auto, Now, image: "https://example.com/a.jpg");
        automatic.Status.ShouldBe(CoordinatorActionStatus.Scheduled);
        automatic.ReportedAt.ShouldBeNull("its result is told when it has run");
        automatic.ImageUrl.ShouldBe("https://example.com/a.jpg");
    }

    [Fact]
    public void Kind_step_name_mode_and_undo_must_agree()
    {
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Apply, ShopActionMode.Reminder, Now));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Pause, ShopActionMode.Reminder, Now));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.Ordering, null, CoordinatorActionStep.Pause, ShopActionMode.Reminder, Now));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.LineBroadcast, "Xurf", CoordinatorActionStep.Apply, ShopActionMode.Reminder, Now));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Pause, ShopActionMode.Auto, Now));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Undo, ShopActionMode.Auto, Now));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Apply, ShopActionMode.Auto, Now, undoes: Guid.CreateVersion7()));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Pause, ShopActionMode.Reminder, Now, undoes: Guid.CreateVersion7()));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, ShopActionMode.Auto, Now, steps: " "));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, ShopActionMode.Auto, Now, steps: new string('步', 501)));
        Should.Throw<ArgumentException>(() => Create(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, ShopActionMode.Auto, Now, recipient: "not a LINE id"));

        Create(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Undo, ShopActionMode.Auto, Now, undoes: Guid.CreateVersion7()).Step.ShouldBe(CoordinatorActionStep.Undo);
        var resume = Create(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Resume, ShopActionMode.Reminder, Now, undoes: Guid.CreateVersion7());
        resume.UndoesActionId.ShouldNotBeNull("a reopen's resume names the pause it follows");
        resume.IsOrdering.ShouldBeTrue();
    }

    [Fact]
    public void A_failure_code_is_a_short_code_never_free_text()
    {
        CoordinatorCommandAction.IsValidFailureCode("rate-limited").ShouldBeTrue();
        CoordinatorCommandAction.IsValidFailureCode("e2").ShouldBeTrue();
        CoordinatorCommandAction.IsValidFailureCode(null).ShouldBeFalse();
        CoordinatorCommandAction.IsValidFailureCode(string.Empty).ShouldBeFalse();
        CoordinatorCommandAction.IsValidFailureCode("Rate limited").ShouldBeFalse();
        CoordinatorCommandAction.IsValidFailureCode("額度不足").ShouldBeFalse();
        CoordinatorCommandAction.IsValidFailureCode(new string('a', 33)).ShouldBeFalse();
    }

    [Fact]
    public void Steps_and_statuses_have_wire_names()
    {
        WireNames<CoordinatorActionStep>.All.ShouldBe(["apply", "undo", "pause", "resume"]);
        WireNames<CoordinatorActionStatus>.All.ShouldBe(["scheduled", "succeeded", "failed", "awaiting-manual", "done-manually", "cancelled"]);
    }

    private static CoordinatorCommandAction Create(
        ShopActionKind kind,
        string? name,
        CoordinatorActionStep step,
        ShopActionMode mode,
        DateTimeOffset dueAt,
        string steps = "步驟。",
        string? image = null,
        Guid? undoes = null,
        string recipient = Owner) =>
        CoordinatorCommandAction.Create(Organization, Command, 0, kind, name, step, mode, dueAt, steps, image, recipient, undoes, Now);
}
