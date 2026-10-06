using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Reports;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Reports;

/// <summary>
/// Periodic reports (M4 #150) against real PostgreSQL with the <c>Fake</c> models: who may schedule a
/// report, what the schedule's jobs produce (right period in the statistics time zone, right numbers,
/// once per period however often a job is delivered), what happens with too few records, with the owner
/// losing access or the connection going away, how the AI summary is kept apart from the statistics and
/// guarded against changed numbers, what a failing model leaves behind, that a withdrawal changes later
/// reports but never an existing one, and that only readers of the database's records see any of it.
/// </summary>
/// <remarks>
/// Time is driven by the jobs themselves: a schedule's job is queued for the end of its period, so a test
/// makes it due (<see cref="MakeJobsDueAsync"/>) and runs <see cref="JobRunner"/>; the next period's job is
/// queued for the future and stays put.
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public sealed partial class PeriodicReportEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Periodic-Report-Pass-1!";
    private const string AssistantsPath = "/api/v1/assistants";
    private const string DatabasesPath = "/api/v1/databases";
    private const string SubmissionsPath = "/api/v1/submissions";
    private const string CountField = "field-completed-count";
    private const string Purpose = "每週回報完成數量。";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly AuthHostFixture _host;

    public PeriodicReportEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Setting -----------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_connected_database_the_owner_may_use_can_be_scheduled()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var owner2 = await SignInAsync(org, "owner2");
        var own = await CreateDatabaseAsync(admin, "自己的資料庫");
        var shared = await CreateDatabaseAsync(owner2, "別人的資料庫");
        (await PutAccessAsync(owner2, shared, [org.Owner2.Id, org.Admin.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var assistantId = await CreateAssistantAsync(org, withKnowledgeBase: true);

        // No form target yet: there is nothing to report on.
        var noTarget = await PatchRulesAsync(admin, assistantId, new { periodicReport = "weekly" });
        noTarget.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(noTarget)).GetProperty("errors").GetProperty("periodicReport")[0].GetString()
            .ShouldBe(ReportScheduleRules.NeedsTargetMessage);

        await ConnectAsTargetAsync(admin, assistantId, own);
        var invalid = await PatchRulesAsync(admin, assistantId, new { periodicReport = "daily" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(invalid)).GetProperty("errors").GetProperty("periodicReport")[0].GetString()
            .ShouldBe(ReportScheduleRules.InvalidMessage);
        await AssertNoScheduleAsync(org);

        // Another account's assistant settings are not the caller's to change, and say nothing more.
        var foreignAttempt = await PatchRulesAsync(await SignInAsync(org, "internal"), assistantId, new { periodicReport = "weekly" });
        foreignAttempt.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var weekly = await PatchRulesAsync(admin, assistantId, new { periodicReport = "weekly" });
        weekly.StatusCode.ShouldBe(HttpStatusCode.OK, await weekly.Content.ReadAsStringAsync(CancellationToken));
        var settings = await BodyJsonAsync(weekly);
        OpenApiContract.AssertKeysMatchSchema(settings, "AssistantSettingsView");
        settings.GetProperty("rules").GetProperty("periodicReport").GetString().ShouldBe("weekly");
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var schedule = await dbContext.ReportSchedules.AsNoTracking().SingleAsync(CancellationToken);
            (schedule.AssistantId, schedule.DatabaseId, schedule.Frequency).ShouldBe((assistantId, own, ReportFrequency.Weekly));
            var thisWeek = ReportPeriods.Containing(ReportFrequency.Weekly, TodayInTaipei());
            schedule.NextPeriodFrom.ShouldBe(thisWeek.From);
            var job = await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(candidate => candidate.Kind == GenerateDatabaseReportJob.Kind, CancellationToken);
            job.RunAfter.ShouldBe(ReportPeriods.DueAt(thisWeek, Taipei), "the first report is made when the current week is over");
        }

        // A setting that is not changed changes nothing (no second schedule, no second job).
        (await PatchRulesAsync(admin, assistantId, new { periodicReport = "weekly" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountAsync(org, dbContext => dbContext.ReportSchedules.CountAsync(CancellationToken))).ShouldBe(1);
        (await CountAsync(org, dbContext => dbContext.BackgroundJobs.CountAsync(job => job.Kind == GenerateDatabaseReportJob.Kind, CancellationToken))).ShouldBe(1);

        // The owner may no longer use the database (designation removed): a new setting is refused.
        var sharedTarget = await CreateAssistantAsync(org, withKnowledgeBase: true, name: "第二個助理");
        await ConnectAsTargetAsync(admin, sharedTarget, shared);
        (await PutAccessAsync(owner2, shared, [org.Owner2.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var unusable = await PatchRulesAsync(admin, sharedTarget, new { periodicReport = "monthly" });
        unusable.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(unusable)).GetProperty("errors").GetProperty("periodicReport")[0].GetString()
            .ShouldBe(ReportScheduleRules.TargetNotUsableMessage);

        // Off removes the schedule; the queued job then finds nothing to do.
        var off = await BodyJsonAsync(await PatchRulesAsync(admin, assistantId, new { periodicReport = "off" }));
        off.GetProperty("rules").GetProperty("periodicReport").GetString().ShouldBe("off");
        await AssertNoScheduleAsync(org);
        await MakeJobsDueAsync(org);
        await RunJobsAsync();
        (await CountAsync(org, dbContext => dbContext.DatabaseReports.CountAsync(CancellationToken))).ShouldBe(0);
    }

    [Fact]
    public async Task Changing_the_frequency_or_the_target_replaces_the_schedule_and_a_job_for_the_old_one_does_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var first = await CreateDatabaseAsync(admin, "第一個資料庫");
        var second = await CreateDatabaseAsync(admin, "第二個資料庫");
        var assistantId = await CreateAssistantAsync(org, withKnowledgeBase: true);
        await ConnectAsTargetAsync(admin, assistantId, first);
        (await PatchRulesAsync(admin, assistantId, new { periodicReport = "weekly" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var oldId = (await ScheduleAsync(org)).Id;

        var monthly = await PatchRulesAsync(admin, assistantId, new { periodicReport = "monthly" });
        monthly.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replaced = await ScheduleAsync(org);
        replaced.Id.ShouldNotBe(oldId);
        (replaced.Frequency, replaced.DatabaseId).ShouldBe((ReportFrequency.Monthly, first));
        replaced.NextPeriodFrom.ShouldBe(ReportPeriods.Containing(ReportFrequency.Monthly, TodayInTaipei()).From);

        // Moving the form target moves the report with it.
        (await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{second}", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatchRulesAsync(admin, assistantId, new { dataWriteDatabaseId = second.ToString() })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var moved = await ScheduleAsync(org);
        (moved.Frequency, moved.DatabaseId).ShouldBe((ReportFrequency.Monthly, second));

        // Both stale jobs (weekly, then monthly on the first database) find no such schedule.
        await MakeJobsDueAsync(org);
        await RunJobsAsync();
        (await CountAsync(org, dbContext => dbContext.DatabaseReports.CountAsync(CancellationToken))).ShouldBe(1, "only the live schedule's job reports");
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseReports.AsNoTracking().SingleAsync(CancellationToken)).DatabaseId.ShouldBe(second);

        // Clearing the target turns the report off with it.
        (await PatchRulesAsync(admin, assistantId, new { dataWriteDatabaseId = "" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await AssertNoScheduleAsync(org);
    }

    // --- Generation --------------------------------------------------------------------------

    [Fact]
    public async Task The_schedule_saves_the_right_period_with_the_right_numbers_once_and_queues_the_next_one()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var databaseId = await CreateDatabaseAsync(admin, "回報資料庫");
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var assistantId = await ScheduleAsync(org, admin, databaseId, "weekly");
        var week = await SeedTwoWeeksAsync(org, databaseId);

        await MakeJobsDueAsync(org);
        await RunJobsAsync();

        var list = await BodyJsonAsync(await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports", reader.Token));
        OpenApiContract.AssertKeysMatchSchema(list, "DatabaseReportListView");
        var item = list.GetProperty("reports").EnumerateArray().Single();
        item.GetProperty("assistantName").GetString().ShouldBe("客服小幫手");
        (item.GetProperty("frequency").GetString(), item.GetProperty("status").GetString(), item.GetProperty("dataState").GetString())
            .ShouldBe(("weekly", "generated", "sufficient"));
        (item.GetProperty("periodFrom").GetString(), item.GetProperty("periodTo").GetString())
            .ShouldBe((Day(week.From), Day(week.To)));

        var report = await BodyJsonAsync(await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports/{item.GetProperty("id").GetGuid()}", reader.Token));
        OpenApiContract.AssertKeysMatchSchema(report, "DatabaseReportView");
        var statistics = report.GetProperty("statistics");

        // The Taipei calendar days decide which week a record belongs to (Sunday 23:30 +08 is last week,
        // Monday 00:30 +08 this week, though both are the same UTC day).
        statistics.GetProperty("period").GetProperty("from").GetString().ShouldBe(Day(week.From));
        statistics.GetProperty("previousPeriod").GetProperty("from").GetString().ShouldBe(Day(week.From.AddDays(-7)));
        statistics.GetProperty("recordCount").GetInt32().ShouldBe(3);
        statistics.GetProperty("previousRecordCount").GetInt32().ShouldBe(2);
        var sum = statistics.GetProperty("sums").EnumerateArray().Single(candidate => candidate.GetProperty("fieldId").GetString() == CountField);
        (sum.GetProperty("sum").GetDouble(), sum.GetProperty("previousSum").GetDouble(), sum.GetProperty("recordCount").GetInt32()).ShouldBe((15, 10, 3));

        // The AI summary is its own, labelled part; the statistics stay as the query returned them.
        var summary = report.GetProperty("aiSummary");
        (summary.GetProperty("label").GetString(), summary.GetProperty("status").GetString()).ShouldBe(("AI 摘要", "ready"));
        summary.GetProperty("text").GetString().ShouldNotBeNullOrWhiteSpace();
        summary.GetProperty("disclaimer").GetString().ShouldBe(ReportDataRules.SummaryDisclaimer);

        // Saved as the fixed query's own result, exactly.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var saved = await dbContext.DatabaseReports.AsNoTracking().SingleAsync(CancellationToken);
            var service = JsonSerializer.Deserialize<DatabasePeriodSummaryResult>(saved.StatisticsJson!, JsonSerializerOptions.Web)!;
            service.RecordCount.ShouldBe(3);
            saved.SummaryStatus.ShouldBe(ReportSummaryStatus.Ready);
            saved.SummaryModel.ShouldBe(AuthHostFixture.ChatModel);

            // The model call is recorded like every other (purpose, organization, owner, assistant; no content).
            var call = await dbContext.ModelInvocations.AsNoTracking().SingleAsync(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateReportSummary, CancellationToken);
            (call.AccountId, call.AssistantId, call.Succeeded).ShouldBe((org.Admin.Id, assistantId, true));
        }

        // Exactly one job for the next period, for the day after it is over, and the schedule moved on.
        var next = ReportPeriods.After(ReportFrequency.Weekly, week);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await ScheduleAsync(org)).NextPeriodFrom.ShouldBe(next.From);
            var queued = await dbContext.BackgroundJobs.AsNoTracking()
                .Where(job => job.Kind == GenerateDatabaseReportJob.Kind && job.Status == BackgroundJobStatus.Queued)
                .ToListAsync(CancellationToken);
            var only = queued.ShouldHaveSingleItem();
            only.RunAfter.ShouldBe(ReportPeriods.DueAt(next, Taipei));
            JsonSerializer.Deserialize<GenerateDatabaseReportJob>(only.Payload, JsonSerializerOptions.Web)!.PeriodFrom.ShouldBe(next.From);
        }

        // The list also names the schedule and the day its next report is made.
        var schedule = list.GetProperty("schedules").EnumerateArray().Single();
        (schedule.GetProperty("nextPeriodFrom").GetString(), schedule.GetProperty("nextReportDate").GetString())
            .ShouldBe((Day(next.From), Day(next.To.AddDays(1))));
    }

    [Fact]
    public async Task A_job_delivered_twice_or_retried_saves_one_report_and_queues_one_next_job()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        await ScheduleAsync(org, admin, databaseId, "weekly");
        await SeedTwoWeeksAsync(org, databaseId);
        var schedule = await ScheduleAsync(org);

        await MakeJobsDueAsync(org);
        await RunJobsAsync();
        (await CountAsync(org, dbContext => dbContext.DatabaseReports.CountAsync(CancellationToken))).ShouldBe(1);

        // The queue delivers the same job again (at least once): a copy of the first one.
        var duplicate = BackgroundJob.Create(
            org.Organization.Id, GenerateDatabaseReportJob.Kind, new GenerateDatabaseReportJob(schedule.Id, schedule.NextPeriodFrom.AddDays(-7)),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            dbContext.BackgroundJobs.Add(duplicate);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await RunJobsAsync();

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.DatabaseReports.CountAsync(CancellationToken)).ShouldBe(1);
            (await dbContext.BackgroundJobs.CountAsync(job => job.Kind == GenerateDatabaseReportJob.Kind && job.Status == BackgroundJobStatus.Queued, CancellationToken))
                .ShouldBe(1, "the stale delivery queued no further job");
            (await dbContext.BackgroundJobs.CountAsync(job => job.Kind == SummarizeDatabaseReportJob.Kind, CancellationToken)).ShouldBe(1);
        }

        // A retry after the report was saved but before the chain moved on (a crash between the two
        // cannot happen — one transaction — but a report existing is all the handler looks at): put the
        // schedule back to the period and deliver again; the report is kept, the chain moves on once.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var current = await dbContext.ReportSchedules.SingleAsync(CancellationToken);
            await dbContext.ReportSchedules.ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.NextPeriodFrom, current.NextPeriodFrom.AddDays(-7)), CancellationToken);
            dbContext.BackgroundJobs.Add(BackgroundJob.Create(
                org.Organization.Id, GenerateDatabaseReportJob.Kind, new GenerateDatabaseReportJob(current.Id, current.NextPeriodFrom.AddDays(-7)),
                DateTimeOffset.UtcNow.AddMinutes(-1)));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await RunJobsAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.DatabaseReports.CountAsync(CancellationToken)).ShouldBe(1, "the unique period kept one report");
            (await dbContext.BackgroundJobs.CountAsync(job => job.Kind == SummarizeDatabaseReportJob.Kind, CancellationToken)).ShouldBe(1, "no second summary");
        }
    }

    [Fact]
    public async Task Too_few_records_are_saved_as_insufficient_records_with_no_trend_and_no_summary()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        await ScheduleAsync(org, admin, databaseId, "weekly");

        // No records at all.
        await MakeJobsDueAsync(org);
        await RunJobsAsync();

        // Records this week but none the week before: the counts are real, the change is not.
        var week = ReportPeriods.Containing(ReportFrequency.Weekly, TodayInTaipei());
        var second = ReportPeriods.After(ReportFrequency.Weekly, week);
        await SeedAsync(org, databaseId, org.Customer.Id, DayStart(second.From).AddHours(10), 4);
        await MakeJobsDueAsync(org);
        await RunJobsAsync();

        var list = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports", admin.Token));
        var reports = list.GetProperty("reports").EnumerateArray().ToList();
        reports.Count.ShouldBe(2);
        reports.ShouldAllBe(report => report.GetProperty("dataState").GetString() == "insufficient-records");
        reports.ShouldAllBe(report => report.GetProperty("summaryStatus").GetString() == "not-requested");
        reports[1].GetProperty("dataMessage").GetString().ShouldStartWith("紀錄不足：這一期有 0 筆、前一期有 0 筆");
        reports[0].GetProperty("dataMessage").GetString().ShouldStartWith("紀錄不足：這一期有 1 筆、前一期有 0 筆");

        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports/{reports[0].GetProperty("id").GetGuid()}", admin.Token));
        detail.GetProperty("statistics").GetProperty("recordCount").GetInt32().ShouldBe(1);
        var summary = detail.GetProperty("aiSummary");
        (summary.GetProperty("status").GetString(), summary.GetProperty("text").ValueKind).ShouldBe(("not-requested", JsonValueKind.Null));
        (await CountAsync(org, dbContext => dbContext.BackgroundJobs.CountAsync(job => job.Kind == SummarizeDatabaseReportJob.Kind, CancellationToken)))
            .ShouldBe(0, "a report with too few records is never sent to the model");
        (await CountAsync(org, dbContext => dbContext.ModelInvocations.CountAsync(CancellationToken))).ShouldBe(0);
    }

    // --- AI summary --------------------------------------------------------------------------

    [Fact]
    public async Task A_summary_with_a_changed_number_is_discarded_and_the_statistics_are_untouched()
    {
        var model = new ScriptedSummaryModel(facts => FirstLine(facts) + "，比前一期成長 999%。");
        await using var factory = ScriptedHost(model);
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        await ScheduleAsync(org, admin, databaseId, "weekly");
        await SeedTwoWeeksAsync(org, databaseId);

        await MakeJobsDueAsync(org);
        await RunJobsAsync(factory);

        var (reportId, before) = await SavedReportAsync(org);
        before.SummaryStatus.ShouldBe(ReportSummaryStatus.Discarded);
        before.SummaryText.ShouldBeNull("the altered text is thrown away, never stored");
        before.SummaryNote.ShouldBe(ReportDataRules.SummaryDiscardedNote);
        var view = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}", admin.Token));
        view.GetProperty("aiSummary").GetProperty("text").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("aiSummary").GetProperty("status").GetString().ShouldBe("discarded");
        view.GetProperty("statistics").GetProperty("recordCount").GetInt32().ShouldBe(3, "the report is complete without the summary");
        (await ModelCallsAsync(org)).ShouldBe([true], "the call itself succeeded and is recorded");

        // A faithful retry replaces it; the statistics columns never change.
        model.Reply = facts => "整體來看，" + FirstLine(facts) + "。";
        var retry = await admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}/summary", admin.Token, new { });
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(retry)).GetProperty("aiSummary").GetProperty("status").GetString().ShouldBe("pending");
        var twice = await admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}/summary", admin.Token, new { });
        (await BodyJsonAsync(twice)).GetProperty("aiSummary").GetProperty("status").GetString().ShouldBe("pending");
        (await CountAsync(org, dbContext => dbContext.BackgroundJobs.CountAsync(job => job.Kind == SummarizeDatabaseReportJob.Kind && job.Status == BackgroundJobStatus.Queued, CancellationToken)))
            .ShouldBe(1, "pressing retry twice queues one summary");
        await RunJobsAsync(factory);

        var (_, after) = await SavedReportAsync(org);
        after.SummaryStatus.ShouldBe(ReportSummaryStatus.Ready);
        after.SummaryText!.ShouldContain("3 筆");
        after.StatisticsJson.ShouldBe(before.StatisticsJson, "the summary never touches the statistics");
        model.Calls.ShouldBe(2);
        model.LastFacts.ShouldNotContain("王", Case.Sensitive);
    }

    [Fact]
    public async Task A_failing_model_leaves_the_statistics_viewable_and_the_summary_can_be_retried()
    {
        var model = new ScriptedSummaryModel(_ => throw new InvalidOperationException("model down"));
        await using var factory = ScriptedHost(model);
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        await ScheduleAsync(org, admin, databaseId, "weekly");
        await SeedTwoWeeksAsync(org, databaseId);

        await MakeJobsDueAsync(org);
        await RunJobsAsync(factory);

        var (reportId, failed) = await SavedReportAsync(org);
        failed.SummaryStatus.ShouldBe(ReportSummaryStatus.Failed);
        failed.SummaryNote.ShouldBe(ReportDataRules.SummaryFailedNote);
        failed.Status.ShouldBe(ReportStatus.Generated);
        var view = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}", admin.Token));
        view.GetProperty("statistics").GetProperty("recordCount").GetInt32().ShouldBe(3);
        view.GetProperty("statistics").GetProperty("sums").GetArrayLength().ShouldBeGreaterThan(0);
        (view.GetProperty("aiSummary").GetProperty("status").GetString(), view.GetProperty("aiSummary").GetProperty("note").GetString())
            .ShouldBe(("failed", ReportDataRules.SummaryFailedNote));
        (await ModelCallsAsync(org)).ShouldBe([false], "the failed call is recorded as failed");
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(job => job.Kind == SummarizeDatabaseReportJob.Kind, CancellationToken))
                .Status.ShouldBe(BackgroundJobStatus.Succeeded, "a model failure is recorded on the report, not retried by the queue");
        }

        // Recovered: retry, and the summary appears next to the same statistics.
        model.Reply = facts => "整體來看，" + FirstLine(facts) + "。";
        (await admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}/summary", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(factory);
        var (_, recovered) = await SavedReportAsync(org);
        recovered.SummaryStatus.ShouldBe(ReportSummaryStatus.Ready);
        recovered.StatisticsJson.ShouldBe(failed.StatisticsJson);

        // Only a failed or discarded summary can be retried: a ready one is returned as it is.
        var ready = await admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}/summary", admin.Token, new { });
        (await BodyJsonAsync(ready)).GetProperty("aiSummary").GetProperty("status").GetString().ShouldBe("ready");
        model.Calls.ShouldBe(2);
    }

    // --- Withdrawal --------------------------------------------------------------------------

    [Fact]
    public async Task A_withdrawal_after_a_report_leaves_it_unchanged_and_the_next_report_excludes_the_record()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        await ScheduleAsync(org, admin, databaseId, "weekly");
        var week = await SeedTwoWeeksAsync(org, databaseId);
        var second = ReportPeriods.After(ReportFrequency.Weekly, week);
        await SeedAsync(org, databaseId, org.Customer.Id, DayStart(second.From).AddHours(11), 8);

        await MakeJobsDueAsync(org);
        await RunJobsAsync();
        var first = await SavedReportsAsync(org);
        var firstStatistics = first.Single().StatisticsJson!;
        JsonSerializer.Deserialize<DatabasePeriodSummaryResult>(firstStatistics, JsonSerializerOptions.Web)!.RecordCount.ShouldBe(3);

        // The customer withdraws one of this week's records, after the report was made.
        var withdrawn = await FirstSubmissionOfAsync(org, databaseId, week);
        (await customer.Spa.PostAsync($"{SubmissionsPath}/{withdrawn}/withdrawal", customer.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The existing report: byte for byte the same.
        (await SavedReportsAsync(org)).Single().StatisticsJson.ShouldBe(firstStatistics);
        var view = await BodyJsonAsync(await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports/{first.Single().Id}", admin.Token));
        view.GetProperty("statistics").GetProperty("recordCount").GetInt32().ShouldBe(3);

        // The next period's report compares with this week as it is now: without the withdrawn record.
        await MakeJobsDueAsync(org);
        await RunJobsAsync();
        var reports = await SavedReportsAsync(org);
        reports.Count.ShouldBe(2);
        var latest = JsonSerializer.Deserialize<DatabasePeriodSummaryResult>(reports.Single(report => report.PeriodFrom == second.From).StatisticsJson!, JsonSerializerOptions.Web)!;
        (latest.RecordCount, latest.PreviousRecordCount).ShouldBe((1, 2));
        latest.Sums.Single(candidate => candidate.FieldId == CountField).PreviousSum.ShouldBe(10, "the withdrawn 5 is gone from the comparison");
        reports.Single(report => report.PeriodFrom == week.From).StatisticsJson.ShouldBe(firstStatistics, "still unchanged");
    }

    // --- Owner loses access, connection removed ---------------------------------------------

    [Fact]
    public async Task An_owner_who_can_no_longer_read_or_a_removed_connection_skips_the_period_and_says_why()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var assistantId = await ScheduleAsync(org, admin, databaseId, "weekly");
        await SeedTwoWeeksAsync(org, databaseId);

        // The owner is no longer a data manager of the database (the reader still is).
        (await PutAccessAsync(admin, databaseId, [org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await MakeJobsDueAsync(org);
        await RunJobsAsync();

        var skipped = (await SavedReportsAsync(org)).ShouldHaveSingleItem();
        (skipped.Status, skipped.SkipReason, skipped.StatisticsJson, skipped.SummaryStatus)
            .ShouldBe((ReportStatus.Skipped, ReportSkipReason.OwnerCannotRead, null, ReportSummaryStatus.NotRequested));
        var item = (await BodyJsonAsync(await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports", reader.Token)))
            .GetProperty("reports").EnumerateArray().Single();
        (item.GetProperty("status").GetString(), item.GetProperty("skipReason").GetString(), item.GetProperty("skipMessage").GetString())
            .ShouldBe(("skipped", "owner-cannot-read", ReportDataRules.SkipMessage(ReportSkipReason.OwnerCannotRead)));
        var detail = await BodyJsonAsync(await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports/{item.GetProperty("id").GetGuid()}", reader.Token));
        detail.GetProperty("statistics").ValueKind.ShouldBe(JsonValueKind.Null, "a skipped period holds no statistics");
        (await CountAsync(org, dbContext => dbContext.ModelInvocations.CountAsync(CancellationToken))).ShouldBe(0);

        // The chain goes on: the next period is queued, and with the connection removed it is skipped
        // for that reason (the owner is a manager again).
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.DeleteAsync($"{AssistantsPath}/{assistantId}/sources/database/{databaseId}", admin.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await MakeJobsDueAsync(org);
        await RunJobsAsync();
        var reports = await SavedReportsAsync(org);
        reports.Count.ShouldBe(2);
        reports.Select(report => report.SkipReason).ShouldBe([ReportSkipReason.OwnerCannotRead, ReportSkipReason.NotConnected], ignoreOrder: true);
        (await SavedSchedulesAsync(org)).ShouldHaveSingleItem("the schedule survives so the reason is recorded each period");

        // The owner's settings show what is configured.
        var settings = await BodyJsonAsync(await admin.Spa.GetAsync($"{AssistantsPath}/{assistantId}/settings", admin.Token));
        settings.GetProperty("rules").GetProperty("periodicReport").GetString().ShouldBe("weekly");
    }

    // --- Auto-disable after consecutive skipped periods (#179) --------------------------------

    [Fact]
    public async Task The_third_skipped_period_in_a_row_disables_the_schedule_says_why_and_nothing_more_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var assistantId = await ScheduleAsync(org, admin, databaseId, "weekly");

        // The owner is no longer a data manager: every period is skipped.
        (await PutAccessAsync(admin, databaseId, [org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunDuePeriodAsync(org);
        await RunDuePeriodAsync(org);
        var running = await ScheduleAsync(org);
        (running.ConsecutiveSkips, running.AutoDisabledAt).ShouldBe((2, (DateTimeOffset?)null));
        (await QueuedReportJobsAsync(org)).ShouldBe(1, "two skips in a row still queue the next period");

        await RunDuePeriodAsync(org);
        var disabled = await ScheduleAsync(org);
        disabled.ConsecutiveSkips.ShouldBe(ReportSchedule.AutoDisableAfterSkips);
        disabled.AutoDisabledAt.ShouldNotBeNull();
        disabled.AutoDisabledReason.ShouldBe(ReportSkipReason.OwnerCannotRead);
        (await QueuedReportJobsAsync(org)).ShouldBe(0, "the disabling period queues no next job");
        var reports = await SavedReportsAsync(org);
        reports.Count.ShouldBe(3);
        reports.ShouldAllBe(report => report.Status == ReportStatus.Skipped && report.SkipReason == ReportSkipReason.OwnerCannotRead);

        // The owner's settings say so, with the reason; the frequency is still shown.
        var settingsResponse = await admin.Spa.GetAsync($"{AssistantsPath}/{assistantId}/settings", admin.Token);
        var settings = await BodyJsonAsync(settingsResponse);
        OpenApiContract.AssertKeysMatchSchema(settings, "AssistantSettingsView");
        settings.GetProperty("rules").GetProperty("periodicReport").GetString().ShouldBe("weekly");
        var autoDisabled = settings.GetProperty("periodicReportAutoDisabled");
        (autoDisabled.GetProperty("reason").GetString(), autoDisabled.GetProperty("skippedPeriods").GetInt32(), autoDisabled.GetProperty("message").GetString())
            .ShouldBe(("owner-cannot-read", 3, ReportScheduleRules.AutoDisabledMessage(ReportSkipReason.OwnerCannotRead)));
        autoDisabled.GetProperty("disabledAt").GetDateTimeOffset().ShouldBe(disabled.AutoDisabledAt!.Value);

        // Readers of the reports no longer see it as a schedule (no next report will be made).
        var list = await BodyJsonAsync(await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports", reader.Token));
        list.GetProperty("schedules").GetArrayLength().ShouldBe(0);
        list.GetProperty("reports").GetArrayLength().ShouldBe(3, "the skipped rows already written stay as they are");

        // A late or duplicated job for the disabled schedule writes nothing (also with the owner able again).
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            dbContext.BackgroundJobs.Add(BackgroundJob.Create(
                org.Organization.Id,
                GenerateDatabaseReportJob.Kind,
                new GenerateDatabaseReportJob(disabled.Id, disabled.NextPeriodFrom),
                DateTimeOffset.UtcNow.AddMinutes(-1)));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await RunJobsAsync();
        (await SavedReportsAsync(org)).Count.ShouldBe(3);
        (await QueuedReportJobsAsync(org)).ShouldBe(0);
        var still = await ScheduleAsync(org);
        (still.Id, still.NextPeriodFrom, still.ConsecutiveSkips, still.AutoDisabledAt).ShouldBe(
            (disabled.Id, disabled.NextPeriodFrom, disabled.ConsecutiveSkips, disabled.AutoDisabledAt));

        // A PATCH that does not name periodicReport (here: only the collection purpose) leaves it disabled.
        (await PatchRulesAsync(admin, assistantId, new { dataWritePurpose = "改過的收集目的。" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScheduleAsync(org)).Id.ShouldBe(disabled.Id);
        (await ScheduleAsync(org)).IsAutoDisabled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_generated_period_between_skips_resets_the_count()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ScheduleAsync(org, admin, databaseId, "weekly");

        (await PutAccessAsync(admin, databaseId, [org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunDuePeriodAsync(org);
        await RunDuePeriodAsync(org);
        (await ScheduleAsync(org)).ConsecutiveSkips.ShouldBe(2);

        // The owner may read again: the period is generated (too few records still counts as generated).
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunDuePeriodAsync(org);
        (await ScheduleAsync(org)).ConsecutiveSkips.ShouldBe(0);

        // Two more skips: not three in a row, so it keeps running.
        (await PutAccessAsync(admin, databaseId, [org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunDuePeriodAsync(org);
        await RunDuePeriodAsync(org);
        var schedule = await ScheduleAsync(org);
        (schedule.ConsecutiveSkips, schedule.IsAutoDisabled).ShouldBe((2, false));
        (await QueuedReportJobsAsync(org)).ShouldBe(1);
        (await SavedReportsAsync(org)).Select(report => report.Status).ShouldBe(
            [ReportStatus.Skipped, ReportStatus.Skipped, ReportStatus.Generated, ReportStatus.Skipped, ReportStatus.Skipped]);
    }

    // --- Archived database (#180) ----------------------------------------------------------------

    [Fact]
    public async Task An_archived_database_pauses_its_schedule_without_writing_anything_and_unarchiving_resumes_it()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var assistantId = await ScheduleAsync(org, admin, databaseId, "weekly");
        var before = await ScheduleAsync(org);

        (await admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // More periods than it takes to auto-disable come due: no report, no skip row, no count, never
        // disabled; the chain moves on with exactly one queued job.
        for (var period = 0; period < ReportSchedule.AutoDisableAfterSkips + 1; period++)
        {
            await RunDuePeriodAsync(org);
        }

        (await SavedReportsAsync(org)).ShouldBeEmpty();
        var paused = await ScheduleAsync(org);
        (paused.Id, paused.ConsecutiveSkips, paused.AutoDisabledAt).ShouldBe((before.Id, 0, (DateTimeOffset?)null));
        paused.NextPeriodFrom.ShouldBe(before.NextPeriodFrom.AddDays(7 * (ReportSchedule.AutoDisableAfterSkips + 1)));
        (await QueuedReportJobsAsync(org)).ShouldBe(1);

        // Readers see no schedule (no next report while archived); the owner's settings are unchanged and
        // not auto-disabled; asking for another frequency is refused like any target the owner cannot use.
        var list = await BodyJsonAsync(await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports", reader.Token));
        (list.GetProperty("schedules").GetArrayLength(), list.GetProperty("reports").GetArrayLength()).ShouldBe((0, 0));
        var settings = await BodyJsonAsync(await admin.Spa.GetAsync($"{AssistantsPath}/{assistantId}/settings", admin.Token));
        settings.GetProperty("rules").GetProperty("periodicReport").GetString().ShouldBe("weekly");
        settings.GetProperty("periodicReportAutoDisabled").ValueKind.ShouldBe(JsonValueKind.Null);
        var changed = await PatchRulesAsync(admin, assistantId, new { periodicReport = "monthly" });
        changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(changed)).GetProperty("errors").TryGetProperty("periodicReport", out _).ShouldBeTrue();
        (await ScheduleAsync(org)).Id.ShouldBe(before.Id);

        // Unarchived: the next period that comes due is reported normally, and the schedule is listed again.
        (await admin.Spa.PostAsync($"{DatabasesPath}/{databaseId}/unarchive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports", reader.Token)))
            .GetProperty("schedules").GetArrayLength().ShouldBe(1);
        await RunDuePeriodAsync(org);
        var report = (await SavedReportsAsync(org)).Single();
        (report.Status, report.PeriodFrom).ShouldBe((ReportStatus.Generated, paused.NextPeriodFrom));
        (await ScheduleAsync(org)).ConsecutiveSkips.ShouldBe(0);
        (await QueuedReportJobsAsync(org)).ShouldBe(1);
    }

    [Fact]
    public async Task Re_enabling_re_checks_permissions_and_resumes_from_the_current_period_without_back_filling()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var owner2 = await SignInAsync(org, "owner2");
        var outsider = await SignInAsync(org, "internal");
        // A database the assistant's owner may use only while owner2 designates them.
        var databaseId = await CreateDatabaseAsync(owner2, "別人的資料庫");
        (await PutAccessAsync(owner2, databaseId, [org.Owner2.Id, org.Admin.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var assistantId = await ScheduleAsync(org, admin, databaseId, "weekly");

        (await PutAccessAsync(owner2, databaseId, [org.Owner2.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        for (var period = 0; period < ReportSchedule.AutoDisableAfterSkips; period++)
        {
            await RunDuePeriodAsync(org);
        }

        var disabled = await ScheduleAsync(org);
        disabled.IsAutoDisabled.ShouldBeTrue();

        // Someone who may not manage the assistant gets exactly the settings endpoint's usual 403.
        var foreignResume = await PatchRulesAsync(outsider, assistantId, new { periodicReport = "weekly" });
        foreignResume.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(foreignResume, await PatchRulesAsync(outsider, assistantId, new { showCitations = false }));

        // The owner still may not use the database: re-enabling is refused like a new setting.
        var refused = await PatchRulesAsync(admin, assistantId, new { periodicReport = "weekly" });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(refused)).GetProperty("errors").GetProperty("periodicReport")[0].GetString()
            .ShouldBe(ReportScheduleRules.TargetNotUsableMessage);
        (await ScheduleAsync(org)).Id.ShouldBe(disabled.Id, "a refused re-enable changes nothing");
        (await QueuedReportJobsAsync(org)).ShouldBe(0);

        // Designated again: re-enabling replaces the schedule with a fresh one from the period containing
        // today. The periods missed while disabled are not back-filled.
        (await PutAccessAsync(owner2, databaseId, [org.Owner2.Id, org.Admin.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var resumed = await PatchRulesAsync(admin, assistantId, new { periodicReport = "weekly" });
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK, await resumed.Content.ReadAsStringAsync(CancellationToken));
        var settings = await BodyJsonAsync(resumed);
        settings.GetProperty("rules").GetProperty("periodicReport").GetString().ShouldBe("weekly");
        settings.GetProperty("periodicReportAutoDisabled").ValueKind.ShouldBe(JsonValueKind.Null);
        var fresh = await ScheduleAsync(org);
        fresh.Id.ShouldNotBe(disabled.Id, "a job still queued for the old schedule finds nothing");
        (fresh.ConsecutiveSkips, fresh.AutoDisabledAt, fresh.AutoDisabledReason).ShouldBe((0, (DateTimeOffset?)null, (ReportSkipReason?)null));
        var thisWeek = ReportPeriods.Containing(ReportFrequency.Weekly, TodayInTaipei());
        fresh.NextPeriodFrom.ShouldBe(thisWeek.From);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var job = await dbContext.BackgroundJobs.AsNoTracking()
                .SingleAsync(candidate => candidate.Kind == GenerateDatabaseReportJob.Kind && candidate.Status == BackgroundJobStatus.Queued, CancellationToken);
            job.RunAfter.ShouldBe(ReportPeriods.DueAt(thisWeek, Taipei));
        }

        // A second identical request is an ordinary unchanged setting: no second schedule or job.
        (await PatchRulesAsync(admin, assistantId, new { periodicReport = "weekly" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScheduleAsync(org)).Id.ShouldBe(fresh.Id);
        (await QueuedReportJobsAsync(org)).ShouldBe(1);

        // The chain runs again. (In this test the old schedule already reported this week — time is
        // compressed — so the one-per-period guarantee keeps that row and makes no second one.)
        await RunDuePeriodAsync(org);
        var reports = await SavedReportsAsync(org);
        reports.Count.ShouldBe(ReportSchedule.AutoDisableAfterSkips);
        reports.Select(report => report.PeriodFrom).Distinct().Count().ShouldBe(reports.Count);
        (await ScheduleAsync(org)).NextPeriodFrom.ShouldBe(ReportPeriods.After(ReportFrequency.Weekly, thisWeek).From);
        (await QueuedReportJobsAsync(org)).ShouldBe(1);
    }

    // --- Who sees reports --------------------------------------------------------------------

    [Fact]
    public async Task Only_readers_of_the_databases_records_see_reports_and_every_request_re_checks()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ScheduleAsync(org, admin, databaseId, "weekly");
        await SeedTwoWeeksAsync(org, databaseId);
        await MakeJobsDueAsync(org);
        await RunJobsAsync();
        var reportId = (await SavedReportsAsync(org)).Single().Id;
        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");
        var missingDatabase = Guid.NewGuid();
        var missingReport = Guid.NewGuid();

        string[] Paths(Guid database, Guid report) =>
        [
            $"{DatabasesPath}/{database}/reports",
            $"{DatabasesPath}/{database}/reports/{report}",
        ];

        // Signed out.
        foreach (var path in Paths(databaseId, reportId))
        {
            (await _host.CreateSpaClient().Http.GetAsync(path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await _host.CreateSpaClient().PostAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}/summary", null, new { })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        // Neither designated nor allowed to see the database: the same refusal for a real report, a made-up
        // report and a made-up database, and no word of the report in it.
        var refusals = new List<HttpResponseMessage>();
        foreach (var path in Paths(databaseId, reportId).Concat(Paths(databaseId, missingReport)).Concat(Paths(missingDatabase, missingReport)))
        {
            var refused = await customer.Spa.GetAsync(path, customer.Token);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
            refusals.Add(refused);
        }

        (await BodyJsonAsync(refusals[0])).GetProperty("reason").GetString().ShouldBe("database");
        for (var index = 0; index < refusals.Count; index += 2)
        {
            await AssertIdenticalAsync(refusals[0], refusals[index]);
            await AssertIdenticalAsync(refusals[1], refusals[index + 1]);
        }

        foreach (var refused in refusals)
        {
            (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("客服小幫手");
        }

        var retryRefused = await customer.Spa.PostAsync($"{DatabasesPath}/{databaseId}/reports/{reportId}/summary", customer.Token, new { });
        await AssertIdenticalAsync(refusals[0], retryRefused);

        // Another organization: the same as a database that does not exist.
        foreach (var path in Paths(databaseId, reportId))
        {
            var foreign = await adminB.Spa.GetAsync(path, adminB.Token);
            foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            await AssertIdenticalAsync(foreign, await adminB.Spa.GetAsync(Paths(missingDatabase, missingReport)[Array.IndexOf(Paths(databaseId, reportId), path)], adminB.Token));
        }

        // A reader sees them; an id that is not a report of this database is its own refusal.
        foreach (var path in Paths(databaseId, reportId))
        {
            (await reader.Spa.GetAsync(path, reader.Token)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        var unknown = await reader.Spa.GetAsync($"{DatabasesPath}/{databaseId}/reports/{missingReport}", reader.Token);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(unknown)).GetProperty("reason").GetString().ShouldBe("database-report");

        // The assistant's owner has no special right: designated off, they see the database but not its reports.
        (await PutAccessAsync(admin, databaseId, [org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var path in Paths(databaseId, reportId).Concat(Paths(databaseId, missingReport)))
        {
            var owner = await admin.Spa.GetAsync(path, admin.Token);
            owner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await BodyJsonAsync(owner)).GetProperty("reason").GetString().ShouldBe("database-records");
        }

        // Permission revoked: refused on the very next request with the same token, real id or made-up alike.
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        var afterRevoke = new List<HttpResponseMessage>();
        foreach (var path in Paths(databaseId, reportId).Concat(Paths(databaseId, missingReport)))
        {
            var revoked = await reader.Spa.GetAsync(path, reader.Token);
            revoked.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
            afterRevoke.Add(revoked);
        }

        await AssertIdenticalAsync(afterRevoke[0], afterRevoke[2]);
        await AssertIdenticalAsync(afterRevoke[1], afterRevoke[3]);
    }

    // --- Helpers -----------------------------------------------------------------------------

    private static DateOnly TodayInTaipei() => DatabaseFixedQueries.DayOf(DateTimeOffset.UtcNow, Taipei);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset DayStart(DateOnly day) => DatabaseFixedQueries.StartOfDay(day, Taipei);

    private static string FirstLine(string facts) =>
        facts.Split('\n').Select(line => line.Trim()).First(line => line.StartsWith("- ", StringComparison.Ordinal))[2..];

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer, Account Owner2);

    private sealed record SignedIn(SpaClient Spa, string Token, Guid AccountId);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        var owner2 = await _host.CreateAccountAsync(organization, "owner2", Password, AccountRole.SmbAdmin, $"{name}第二管理者", AllAdminPermissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions);
        var customer = await _host.CreateAccountAsync(
            organization, "customer", Password, AccountRole.ExternalCustomer, $"{name}外部客戶",
            AccountPermission.SubmitAuthorizedForms, AccountPermission.ReadOwnTracking);
        return new TestOrganization(organization, admin, internalEmployee, customer, owner2);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        var accountId = loginName switch
        {
            "admin" => org.Admin.Id,
            "owner2" => org.Owner2.Id,
            "internal" => org.Internal.Id,
            _ => org.Customer.Id,
        };
        return new SignedIn(spa, token.AccessToken, accountId);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name = "回報資料庫")
    {
        var response = await owner.Spa.PostAsync(DatabasesPath, owner.Token, new { templateId = "template-periodic-report", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>The admin's assistant. With a knowledge base so that its database is not its only source.</summary>
    private async Task<Guid> CreateAssistantAsync(TestOrganization org, bool withKnowledgeBase, string name = "客服小幫手")
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Admin.Id, name, "協助回報完成數量", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, now);
        dbContext.Assistants.Add(assistant);
        if (withKnowledgeBase)
        {
            var knowledgeBase = SmartAgri.Domain.Knowledge.KnowledgeBase.Create(org.Organization.Id, org.Admin.Id, "知識庫 " + name, string.Empty, now);
            dbContext.KnowledgeBases.Add(knowledgeBase);
            dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        }

        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private static async Task ConnectAsTargetAsync(SignedIn admin, Guid assistantId, Guid databaseId)
    {
        var connected = await admin.Spa.PutAsync($"{AssistantsPath}/{assistantId}/sources/database/{databaseId}", admin.Token, new { });
        connected.StatusCode.ShouldBe(HttpStatusCode.OK, await connected.Content.ReadAsStringAsync(CancellationToken));
        var target = await PatchRulesAsync(admin, assistantId, new { dataWriteDatabaseId = databaseId.ToString(), dataWritePurpose = Purpose });
        target.StatusCode.ShouldBe(HttpStatusCode.OK, await target.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>Creates an assistant on <paramref name="databaseId"/> with the given frequency; returns its id.</summary>
    private async Task<Guid> ScheduleAsync(TestOrganization org, SignedIn admin, Guid databaseId, string frequency)
    {
        var assistantId = await CreateAssistantAsync(org, withKnowledgeBase: true);
        await ConnectAsTargetAsync(admin, assistantId, databaseId);
        var response = await PatchRulesAsync(admin, assistantId, new { periodicReport = frequency });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return assistantId;
    }

    private static Task<HttpResponseMessage> PatchRulesAsync(SignedIn admin, Guid assistantId, object rules) =>
        admin.Spa.PatchAsync($"{AssistantsPath}/{assistantId}/settings", admin.Token, new { rules });

    private static Task<HttpResponseMessage> PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds) =>
        caller.Spa.PutAsync($"{DatabasesPath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });

    private static async Task SetPermissionsAsync(SignedIn admin, Guid accountId, AccountPermission[] permissions)
    {
        var wire = permissions.Select(SmartAgri.Domain.WireNames<AccountPermission>.ToWire).ToArray();
        var response = await admin.Spa.PutAsync($"/api/v1/team/members/{accountId}/permissions", admin.Token, new { permissions = wire });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>Records for the week of the first report and the week before it. This week: 5 (Monday 00:30
    /// +08, which is still Sunday in UTC), 7 and 3; the week before: 6 and 4 (Sunday 23:30 +08, the very
    /// same UTC day as the Monday one). Returns this week.</summary>
    private async Task<DatabaseQueryPeriod> SeedTwoWeeksAsync(TestOrganization org, Guid databaseId)
    {
        var week = ReportPeriods.Containing(ReportFrequency.Weekly, TodayInTaipei());
        await SeedAsync(org, databaseId, org.Customer.Id, DayStart(week.From).AddMinutes(30), 5);
        await SeedAsync(org, databaseId, org.Customer.Id, DayStart(week.From.AddDays(2)).AddHours(12), 7);
        await SeedAsync(org, databaseId, org.Customer.Id, DayStart(week.From.AddDays(4)).AddHours(9), 3);
        await SeedAsync(org, databaseId, org.Customer.Id, DayStart(week.From).AddMinutes(-30), 4);
        await SeedAsync(org, databaseId, org.Customer.Id, DayStart(week.From.AddDays(-3)).AddHours(10), 6);
        return week;
    }

    private async Task<Guid> SeedAsync(TestOrganization org, Guid databaseId, Guid accountId, DateTimeOffset at, double? count)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var database = await dbContext.Databases.SingleAsync(candidate => candidate.Id == databaseId, CancellationToken);
        var version = await dbContext.DatabaseFormVersions
            .Where(candidate => candidate.DatabaseId == databaseId)
            .OrderByDescending(candidate => candidate.VersionNumber)
            .FirstAsync(CancellationToken);
        var terms = new DatabaseConsentTerms("回報資料庫", "測試", "安心商行（回報資料庫）", [], "請勿填寫敏感資料。");
        var submission = DatabaseSubmission.Create(
            database, version, accountId, Guid.NewGuid(), DatabaseSubmissionSource.FormLink, terms, at);
        dbContext.DatabaseSubmissions.Add(submission);
        for (var position = 0; position < version.Fields.Count; position++)
        {
            var field = version.Fields[position];
            dbContext.DatabaseSubmissionEntries.Add(field.Id switch
            {
                "field-report-date" => DatabaseSubmissionEntry.Create(submission, position, field, at.ToString("yyyy-MM-dd"), at.ToString("yyyy-MM-dd"), null, []),
                CountField when count is { } value =>
                    DatabaseSubmissionEntry.Create(submission, position, field, DatabaseAnswerRules.FormatNumber(value, field.Unit), null, value, []),
                _ => DatabaseSubmissionEntry.Create(submission, position, field, "未填寫", null, null, []),
            });
        }

        await dbContext.SaveChangesAsync(CancellationToken);
        return submission.Id;
    }

    /// <summary>The first record (by time) of <paramref name="period"/> in the database.</summary>
    private async Task<Guid> FirstSubmissionOfAsync(TestOrganization org, Guid databaseId, DatabaseQueryPeriod period)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var start = period.Start(Taipei);
        var end = period.EndExclusive(Taipei);
        return await dbContext.DatabaseSubmissions.AsNoTracking()
            .Where(submission => submission.DatabaseId == databaseId && submission.SubmittedAt >= start && submission.SubmittedAt < end)
            .OrderBy(submission => submission.SubmittedAt)
            .Select(submission => submission.Id)
            .FirstAsync(CancellationToken);
    }

    /// <summary>Makes every queued report job of the organization due now (its period is "over").</summary>
    private async Task MakeJobsDueAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await dbContext.BackgroundJobs
            .Where(job => job.Status == BackgroundJobStatus.Queued && job.Kind == GenerateDatabaseReportJob.Kind)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.RunAfter, due), CancellationToken);
    }

    /// <summary>Makes the queued report job due and runs it: one more period is reported.</summary>
    private async Task RunDuePeriodAsync(TestOrganization org)
    {
        await MakeJobsDueAsync(org);
        await RunJobsAsync();
    }

    private Task<int> QueuedReportJobsAsync(TestOrganization org) =>
        CountAsync(org, dbContext => dbContext.BackgroundJobs.CountAsync(
            job => job.Kind == GenerateDatabaseReportJob.Kind && job.Status == BackgroundJobStatus.Queued, CancellationToken));

    private Task RunJobsAsync(WebApplicationFactory<Program>? factory = null) =>
        (factory ?? _host.Factory).Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private async Task<T> CountAsync<T>(TestOrganization org, Func<SmartAgri.Infrastructure.AppDbContext, Task<T>> query)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await query(dbContext);
    }

    private async Task<ReportSchedule> ScheduleAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.ReportSchedules.AsNoTracking().SingleAsync(CancellationToken);
    }

    private async Task<IReadOnlyList<ReportSchedule>> SavedSchedulesAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.ReportSchedules.AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task AssertNoScheduleAsync(TestOrganization org) =>
        (await SavedSchedulesAsync(org)).ShouldBeEmpty();

    private async Task<IReadOnlyList<DatabaseReport>> SavedReportsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.DatabaseReports.AsNoTracking().OrderBy(report => report.PeriodFrom).ToListAsync(CancellationToken);
    }

    private async Task<(Guid Id, DatabaseReport Report)> SavedReportAsync(TestOrganization org)
    {
        var report = (await SavedReportsAsync(org)).Single();
        return (report.Id, report);
    }

    /// <summary>Whether each recorded call of the summary purpose succeeded, oldest first.</summary>
    private async Task<List<bool>> ModelCallsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.ModelInvocations.AsNoTracking()
            .Where(call => call.Purpose == ModelInvocationPurpose.GenerateReportSummary)
            .OrderBy(call => call.At)
            .Select(call => call.Succeeded)
            .ToListAsync(CancellationToken);
    }

    /// <summary>A host whose chat model is <paramref name="model"/> (the recording middleware still wraps it).</summary>
    private WebApplicationFactory<Program> ScriptedHost(ScriptedSummaryModel model) =>
        _host.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(new ChatClientProvider(model, "fake", "smartagri.fake", "scripted-summary", endpoint: null))));

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertIdenticalAsync(HttpResponseMessage first, HttpResponseMessage second)
    {
        var a = await ResponseFingerprint.FromAsync(first);
        var b = await ResponseFingerprint.FromAsync(second);

        b.Status.ShouldBe(a.Status);
        b.ContentType.ShouldBe(a.ContentType);
        b.Body.ShouldBe(a.Body);
        b.SetsCookie.ShouldBe(a.SetsCookie);
    }

    /// <summary>A chat model that answers the report-summary prompt as the test says, and counts calls.</summary>
    private sealed class ScriptedSummaryModel : IChatClient
    {
        public ScriptedSummaryModel(Func<string, string> reply)
        {
            Reply = reply;
        }

        public Func<string, string> Reply { get; set; }

        public int Calls { get; private set; }

        public string LastFacts { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var all = messages.ToList();
            all[0].Text.ShouldStartWith(ReportSummaryPrompt.Marker);
            LastFacts = all[^1].Text;
            var answer = Reply(LastFacts);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
            {
                ModelId = "scripted-summary",
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
