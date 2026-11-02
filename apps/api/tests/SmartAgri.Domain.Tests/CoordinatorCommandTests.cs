using Shouldly;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>統籌助理 commands (#374): what a new one holds, and what it refuses.</summary>
public class CoordinatorCommandTests
{
    private const string User = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_preview_is_version_1_and_open_for_30_minutes()
    {
        var command = CoordinatorCommand.Preview(
            Organization, User, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(16, 0), "提早打烊", Now);

        command.Status.ShouldBe(CoordinatorCommandStatus.Previewed);
        command.Version.ShouldBe(1);
        command.ExpiresAt.ShouldBe(Now + TimeSpan.FromMinutes(30));
        command.IsExpired(Now + TimeSpan.FromMinutes(29)).ShouldBeFalse();
        command.IsExpired(Now + TimeSpan.FromMinutes(30)).ShouldBeTrue();
        command.ConfirmedAt.ShouldBeNull();
        command.CorrectionBroadcast.ShouldBeFalse();
        command.ReopensCommandId.ShouldBeNull();
    }

    [Fact]
    public void Waiting_for_details_keeps_what_is_known_and_never_a_time_for_a_closure()
    {
        var closure = CoordinatorCommand.AwaitDetails(Organization, User, ShopScenario.TemporaryClosure, null, new TimeOnly(16, 0), Now);
        closure.Status.ShouldBe(CoordinatorCommandStatus.AwaitingDetails);
        closure.Date.ShouldBeNull();
        closure.Time.ShouldBeNull();
        closure.AnnouncementText.ShouldBeNull();

        var early = CoordinatorCommand.AwaitDetails(Organization, User, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), null, Now);
        early.Date.ShouldBe(new DateOnly(2026, 11, 2));

        Should.Throw<ArgumentException>(() => CoordinatorCommand.AwaitDetails(Organization, User, ShopScenario.Reopen, null, null, Now));
    }

    [Fact]
    public void A_reopen_names_what_it_undoes_and_may_have_no_announcement()
    {
        var undone = Guid.CreateVersion7();
        var reopen = CoordinatorCommand.Preview(Organization, User, ShopScenario.Reopen, new DateOnly(2026, 11, 15), null, null, Now, undone);

        reopen.ReopensCommandId.ShouldBe(undone);
        reopen.AnnouncementText.ShouldBeNull();
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(Organization, User, ShopScenario.Reopen, new DateOnly(2026, 11, 15), null, null, Now));
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(
            Organization, User, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 15), null, "公告", Now, undone));
    }

    [Fact]
    public void A_preview_needs_its_time_its_announcement_and_a_valid_sender()
    {
        var date = new DateOnly(2026, 11, 2);
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(Organization, User, ShopScenario.CloseEarly, date, null, "公告", Now));
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(Organization, User, ShopScenario.TemporaryClosure, date, new TimeOnly(16, 0), "公告", Now));
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(Organization, User, ShopScenario.TemporaryClosure, date, null, " ", Now));
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(
            Organization, User, ShopScenario.TemporaryClosure, date, null, new string('公', CoordinatorCommand.AnnouncementMaxLength + 1), Now));
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(Guid.Empty, User, ShopScenario.TemporaryClosure, date, null, "公告", Now));
        Should.Throw<ArgumentException>(() => CoordinatorCommand.Preview(Organization, "U 1", ShopScenario.TemporaryClosure, date, null, "公告", Now));
        CoordinatorCommand.Preview(
                Organization, User, ShopScenario.TemporaryClosure, date, null, new string('公', CoordinatorCommand.AnnouncementMaxLength), Now)
            .AnnouncementText!.Length.ShouldBe(1000);
    }

    [Fact]
    public void Statuses_and_scenarios_have_wire_names_and_display_names()
    {
        WireNames<CoordinatorCommandStatus>.All.ShouldBe(["awaiting-details", "previewed", "confirmed", "cancelled"]);
        Enum.GetValues<ShopScenario>().Select(ShopScenarioNames.Display).ShouldBe(["臨時店休", "提早打烊", "延後開店", "恢復營業"]);
    }
}
