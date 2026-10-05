using Shouldly;
using SmartAgri.Domain.Reports;

namespace SmartAgri.Domain.Tests;

/// <summary>A periodic report's states (M4 #150): a snapshot whose AI summary is kept apart, moves only
/// along pending → ready / failed / discarded, and can be retried only from failed or discarded.</summary>
public class DatabaseReportTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Database = Guid.CreateVersion7();
    private static readonly Guid Assistant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 28);
    private static readonly DateOnly To = new(2026, 10, 4);

    private static DatabaseReport Generated(ReportDataState state) =>
        DatabaseReport.Generated(Organization, Database, Assistant, " 客服小幫手 ", ReportFrequency.Weekly, From, To, state, "{\"recordCount\":3}", Now);

    [Fact]
    public void A_report_with_enough_records_waits_for_its_summary_and_one_without_never_gets_one()
    {
        var report = Generated(ReportDataState.Sufficient);
        (report.Status, report.DataState, report.SummaryStatus, report.SkipReason).ShouldBe(
            (ReportStatus.Generated, ReportDataState.Sufficient, ReportSummaryStatus.Pending, null));
        report.AssistantName.ShouldBe("客服小幫手");
        report.StatisticsJson.ShouldBe("{\"recordCount\":3}");

        var few = Generated(ReportDataState.InsufficientRecords);
        few.SummaryStatus.ShouldBe(ReportSummaryStatus.NotRequested);
        Should.Throw<InvalidOperationException>(() => few.SummaryReady("text", "model", Now));
        few.RetrySummary(Now).ShouldBeFalse();
    }

    [Fact]
    public void A_skipped_period_holds_no_statistics_and_no_summary()
    {
        var skipped = DatabaseReport.Skipped(Organization, Database, Assistant, "助理", ReportFrequency.Monthly, From, To, ReportSkipReason.NotConnected, Now);

        (skipped.Status, skipped.SkipReason, skipped.StatisticsJson, skipped.DataState, skipped.SummaryStatus).ShouldBe(
            (ReportStatus.Skipped, ReportSkipReason.NotConnected, null, null, ReportSummaryStatus.NotRequested));
        Should.Throw<InvalidOperationException>(() => skipped.SummaryFailed("x", Now));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            DatabaseReport.Skipped(Organization, Database, Assistant, "助理", ReportFrequency.Monthly, From, To, (ReportSkipReason)9, Now));
    }

    [Fact]
    public void The_summary_moves_from_pending_and_a_retry_only_from_failed_or_discarded_leaving_the_statistics_alone()
    {
        var report = Generated(ReportDataState.Sufficient);
        var statistics = report.StatisticsJson;

        report.SummaryFailed("模型失敗", Now.AddMinutes(1));
        (report.SummaryStatus, report.SummaryText, report.SummaryNote).ShouldBe((ReportSummaryStatus.Failed, null, "模型失敗"));
        Should.Throw<InvalidOperationException>(() => report.SummaryDiscarded("x", Now), "only a pending summary can be settled");

        report.RetrySummary(Now.AddMinutes(2)).ShouldBeTrue();
        report.SummaryStatus.ShouldBe(ReportSummaryStatus.Pending);
        report.SummaryNote.ShouldBeNull();
        report.RetrySummary(Now.AddMinutes(3)).ShouldBeFalse("already pending");

        report.SummaryDiscarded("有不明數字", Now.AddMinutes(4));
        (report.SummaryStatus, report.SummaryText, report.SummaryModel).ShouldBe((ReportSummaryStatus.Discarded, null, null));
        report.RetrySummary(Now.AddMinutes(5)).ShouldBeTrue();

        report.SummaryReady("整體來看，本期 3 筆。", "gpt-x", Now.AddMinutes(6));
        (report.SummaryStatus, report.SummaryText, report.SummaryModel, report.SummaryNote).ShouldBe(
            (ReportSummaryStatus.Ready, "整體來看，本期 3 筆。", "gpt-x", null));
        report.RetrySummary(Now).ShouldBeFalse("a ready summary is kept");
        report.StatisticsJson.ShouldBe(statistics, "the summary never touches the statistics");
    }

    [Fact]
    public void Bad_input_is_refused()
    {
        Should.Throw<ArgumentException>(() => DatabaseReport.Generated(Guid.Empty, Database, Assistant, "助理", ReportFrequency.Weekly, From, To, ReportDataState.Sufficient, "{}", Now));
        Should.Throw<ArgumentException>(() => DatabaseReport.Generated(Organization, Database, Assistant, "  ", ReportFrequency.Weekly, From, To, ReportDataState.Sufficient, "{}", Now));
        Should.Throw<ArgumentException>(() => DatabaseReport.Generated(Organization, Database, Assistant, "助理", ReportFrequency.Weekly, To, From, ReportDataState.Sufficient, "{}", Now));
        Should.Throw<ArgumentException>(() => DatabaseReport.Generated(Organization, Database, Assistant, "助理", ReportFrequency.Weekly, From, To, ReportDataState.Sufficient, " ", Now));
        Should.Throw<ArgumentOutOfRangeException>(() => DatabaseReport.Generated(Organization, Database, Assistant, "助理", (ReportFrequency)9, From, To, ReportDataState.Sufficient, "{}", Now));
        Should.Throw<ArgumentException>(() => ReportSchedule.Create(Organization, Guid.Empty, Database, ReportFrequency.Weekly, From, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => ReportSchedule.Create(Organization, Assistant, Database, (ReportFrequency)9, From, Now));
    }

    [Fact]
    public void Enums_have_the_wire_names_the_frontend_uses()
    {
        WireNames<ReportFrequency>.All.ShouldBe(["weekly", "monthly"], ignoreOrder: true);
        WireNames<ReportStatus>.All.ShouldBe(["generated", "skipped"], ignoreOrder: true);
        WireNames<ReportSkipReason>.All.ShouldBe(["not-connected", "owner-cannot-read"], ignoreOrder: true);
        WireNames<ReportDataState>.All.ShouldBe(["sufficient", "insufficient-records"], ignoreOrder: true);
        WireNames<ReportSummaryStatus>.All.ShouldBe(["not-requested", "pending", "ready", "failed", "discarded"], ignoreOrder: true);
        WireNames<SmartAgri.Domain.Ai.ModelInvocationPurpose>.ToWire(SmartAgri.Domain.Ai.ModelInvocationPurpose.GenerateReportSummary).ShouldBe("generate-report-summary");
    }
}
