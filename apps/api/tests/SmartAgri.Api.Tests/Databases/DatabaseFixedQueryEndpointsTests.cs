using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>
/// The fixed statistics queries and the comparison in the timeline (M4 #147) against real
/// PostgreSQL: access and isolation are re-applied on every call and refusals reveal nothing;
/// only defined parameters pass (<c>422</c>); counts and sums are right across period boundaries
/// and subjects; a withdrawal drops out of the next answer; a form revision does not break the
/// comparison of a field (stable field id); too little data is "not enough records", not a trend.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseFixedQueryEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Database-Queries-Pass-1!";
    private const string BasePath = "/api/v1/databases";
    private const string SubmissionsPath = "/api/v1/submissions";
    private const string CountField = "field-completed-count";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private readonly AuthHostFixture _host;

    public DatabaseFixedQueryEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();

    // --- Access and isolation ----------------------------------------------------------------

    [Fact]
    public async Task Every_query_re_applies_access_and_a_refusal_reveals_nothing_not_even_that_the_parameters_were_wrong()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var internalEmployee = await SignInAsync(org, "internal");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-10T10:00:00Z"), 4);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-11T10:00:00Z"), 6);
        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");
        var missing = Guid.NewGuid();

        foreach (var (name, valid) in ValidQueries(customer.AccountId))
        {
            var path = QueryPath(databaseId, name, valid);
            var missingPath = QueryPath(missing, name, valid);
            var invalidPath = QueryPath(databaseId, name, "bogus=1");

            // Signed out.
            (await _host.CreateSpaClient().Http.GetAsync(path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);

            // Permitted but not designated, and a member without the permission: the database is invisible.
            foreach (var (who, caller) in new[] { ("not designated", internalEmployee), ("the submitter", customer) })
            {
                var refused = await caller.Spa.GetAsync(path, caller.Token);
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{name} / {who}");
                (await BodyJsonAsync(refused)).GetProperty("reason").GetString().ShouldBe("database");
                await AssertIdenticalAsync(refused, await caller.Spa.GetAsync(missingPath, caller.Token));
                await AssertIdenticalAsync(refused, await caller.Spa.GetAsync(invalidPath, caller.Token));
                (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("field-completed-count");
            }

            // Another organization: identical to a database that does not exist.
            var foreign = await adminB.Spa.GetAsync(path, adminB.Token);
            foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            await AssertIdenticalAsync(foreign, await adminB.Spa.GetAsync(missingPath, adminB.Token));
            await AssertIdenticalAsync(foreign, await adminB.Spa.GetAsync(invalidPath, adminB.Token));
        }

        // The designated owner reads; once the designation is removed, the owner sees the database
        // but not its statistics, and bad parameters get the same refusal as good ones.
        foreach (var (name, valid) in ValidQueries(customer.AccountId))
        {
            (await admin.Spa.GetAsync(QueryPath(databaseId, name, valid), admin.Token)).StatusCode.ShouldBe(HttpStatusCode.OK, name);
        }

        (await PutAccessAsync(admin, databaseId, [org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var (name, valid) in ValidQueries(customer.AccountId))
        {
            var owner = await admin.Spa.GetAsync(QueryPath(databaseId, name, valid), admin.Token);
            owner.StatusCode.ShouldBe(HttpStatusCode.Forbidden, name);
            (await BodyJsonAsync(owner)).GetProperty("reason").GetString().ShouldBe("database-records");
            await AssertIdenticalAsync(owner, await admin.Spa.GetAsync(QueryPath(databaseId, name, "bogus=1"), admin.Token));
            (await internalEmployee.Spa.GetAsync(QueryPath(databaseId, name, valid), internalEmployee.Token)).StatusCode
                .ShouldBe(HttpStatusCode.OK, "the newly designated manager");
        }

        // Permission revoked: refused on the very next request with the same token.
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        foreach (var (name, valid) in ValidQueries(customer.AccountId))
        {
            (await internalEmployee.Spa.GetAsync(QueryPath(databaseId, name, valid), internalEmployee.Token)).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden, name);
        }

        // The timeline's comparison follows the same refusal.
        (await internalEmployee.Spa.GetAsync($"{BasePath}/{databaseId}/tracking", internalEmployee.Token)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_subject_must_have_submitted_to_this_database_and_every_other_subject_is_the_same_answer()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var other = await CreateSecondCustomerAsync(org);
        var databaseA = await CreateDatabaseAsync(admin, name: "A");
        var databaseB = await CreateDatabaseAsync(admin, name: "B");
        await SeedAsync(org, databaseA, customer.AccountId, At("2026-02-10T10:00:00Z"), 4);
        await SeedAsync(org, databaseB, other.AccountId, At("2026-02-10T10:00:00Z"), 99);
        var orgB = await CreateOrganizationAsync("組織 B");
        var foreignAccount = (await SignInAsync(orgB, "customer")).AccountId;

        foreach (var (name, _) in ValidQueries(customer.AccountId))
        {
            string Path(string subject) => QueryPath(databaseA, name, ValidFor(name, subject));

            var reference = await admin.Spa.GetAsync(Path(Guid.NewGuid().ToString()), admin.Token);
            reference.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, name);
            var body = await BodyJsonAsync(reference);
            body.GetProperty("errors").GetProperty("subjectId")[0].GetString().ShouldBe("找不到這位追蹤對象。");

            // Another database's subject, another organization's account, a manager who never
            // submitted and text that is no id at all: byte-identical to a subject that does not exist.
            foreach (var subject in new[] { other.AccountId.ToString(), foreignAccount.ToString(), org.Admin.Id.ToString(), "not-a-guid", Guid.Empty.ToString() })
            {
                var response = await admin.Spa.GetAsync(Path(subject), admin.Token);
                response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{name} / {subject}");
                (await BodyJsonAsync(response)).GetRawText().ShouldBe(body.GetRawText(), $"{name} / {subject}");
            }

            (await admin.Spa.GetAsync(Path(customer.AccountId.ToString()), admin.Token)).StatusCode
                .ShouldBe(HttpStatusCode.OK, "the one who submitted");
        }

        // Database B's numbers never show up in A's answers.
        var sum = await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseA, "field-sum", $"from=2026-02-01&to=2026-02-28&fieldId={CountField}"), admin.Token));
        sum.GetProperty("field").GetProperty("sum").GetDouble().ShouldBe(4);
    }

    // --- Parameters --------------------------------------------------------------------------

    [Fact]
    public async Task Only_defined_parameters_and_values_are_accepted()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-10T10:00:00Z"), 4);

        var cases = new (string Name, string Query, string Key)[]
        {
            ("record-count", "", "period"),
            ("record-count", "period=this-year", "period"),
            ("record-count", "period=last-week&from=2026-02-01&to=2026-02-02", "period"),
            ("record-count", "from=2026-02-01", "to"),
            ("record-count", "from=2026-02-30&to=2026-03-01", "from"),
            ("record-count", "from=2026-03-01&to=2026-02-01", "from"),
            ("record-count", "from=2020-01-01&to=2026-01-01", "to"),
            ("record-count", "period=last-week&select=*", "select"),
            ("record-count", "period=last-week&sql=select%201", "sql"),
            ("record-count", "period=last-week&fieldId=" + CountField, "fieldId"),
            ("record-count", "period=last-week&period=this-week", "period"),
            ("field-sum", "period=last-week", "fieldId"),
            ("field-sum", "period=last-week&fieldId=field-nope", "fieldId"),
            ("field-sum", "period=last-week&fieldId=field-report-date", "fieldId"),
            ("field-sum", "period=last-week&fieldId=" + CountField + "&expr=a%2Bb", "expr"),
            ("period-summary", "period=last-week&fieldId=" + CountField, "fieldId"),
            ("period-summary", "period=", "period"),
            ("subject-comparison", "", "subjectId"),
            ("subject-comparison", $"subjectId={customer.AccountId}&period=last-week", "period"),
            ("subject-comparison", $"subjectId={customer.AccountId}&fieldId=field-issue", "fieldId"),
            ("subject-comparison", $"subjectId={customer.AccountId}&fieldId=field-nope", "fieldId"),
        };

        foreach (var (name, query, key) in cases)
        {
            var response = await admin.Spa.GetAsync(QueryPath(databaseId, name, query), admin.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{name}?{query}");
            var body = await BodyJsonAsync(response);
            body.GetProperty("errors").TryGetProperty(key, out _).ShouldBeTrue($"{name}?{query}: errors.{key} in {body.GetRawText()}");
        }

        // The same names are not routes: there is no endpoint that runs an arbitrary query.
        (await admin.Spa.GetAsync($"{BasePath}/{databaseId}/queries/sql?q=select%201", admin.Token)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await admin.Spa.GetAsync($"{BasePath}/{databaseId}/queries", admin.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // --- Not enough data ---------------------------------------------------------------------

    [Fact]
    public async Task No_records_and_one_record_are_zero_counts_and_not_enough_records_for_a_trend()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        const string Range = "from=2026-02-01&to=2026-02-28";

        var count = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", Range), admin.Token));
        OpenApiContract.AssertKeysMatchSchema(count, "DatabaseRecordCountResult");
        count.GetProperty("count").GetInt32().ShouldBe(0);
        count.GetProperty("previousCount").GetInt32().ShouldBe(0);
        count.GetProperty("changeLabel").GetString().ShouldBe("持平");
        count.GetProperty("subjectId").ValueKind.ShouldBe(JsonValueKind.Null, "sent as null, not omitted");

        var sum = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "field-sum", $"{Range}&fieldId={CountField}"), admin.Token));
        OpenApiContract.AssertKeysMatchSchema(sum, "DatabaseFieldSumResult");
        sum.GetProperty("field").GetProperty("sum").GetDouble().ShouldBe(0);
        sum.GetProperty("field").GetProperty("recordCount").GetInt32().ShouldBe(0);
        sum.GetProperty("field").GetProperty("display").GetString().ShouldBe("0 件");

        var summary = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "period-summary", Range), admin.Token));
        OpenApiContract.AssertKeysMatchSchema(summary, "DatabasePeriodSummaryResult");
        summary.GetProperty("recordCount").GetInt32().ShouldBe(0);
        summary.GetProperty("sums").EnumerateArray().Select(line => line.GetProperty("fieldId").GetString()).ShouldBe([CountField]);

        var tracking = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}/tracking", admin.Token));
        tracking.GetProperty("subjects").GetArrayLength().ShouldBe(0);

        // One record: counted, but a single record is not a trend.
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-10T10:00:00Z"), 4);
        var comparisonPath = QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}");
        var comparison = await BodyJsonAsync(await admin.Spa.GetAsync(comparisonPath, admin.Token));
        OpenApiContract.AssertKeysMatchSchema(comparison, "DatabaseSubjectComparisonResult");
        var result = comparison.GetProperty("comparison");
        result.GetProperty("status").GetString().ShouldBe("insufficient-records");
        result.GetProperty("recordCount").GetInt32().ShouldBe(1);
        result.GetProperty("message").GetString().ShouldBe("目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。");
        result.GetProperty("summary").ValueKind.ShouldBe(JsonValueKind.Null);
        result.GetProperty("metrics").GetArrayLength().ShouldBe(0, "no fake trend");

        var one = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", Range), admin.Token));
        one.GetProperty("count").GetInt32().ShouldBe(1);
        one.GetProperty("change").GetInt32().ShouldBe(1);
        one.GetProperty("changeLabel").GetString().ShouldBe("+1 筆");
    }

    // --- Periods and subjects ----------------------------------------------------------------

    [Fact]
    public async Task Counts_and_sums_are_right_across_period_boundaries_and_subjects()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var other = await CreateSecondCustomerAsync(org);
        var databaseId = await CreateDatabaseAsync(admin);

        // Customer: 5 records around February 2026 (Taipei calendar days); the other member: one inside it.
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-01-31T23:59:59+08:00"), 1);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-01T00:00:00+08:00"), 2);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-14T12:00:00+08:00"), 3.5);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-28T23:59:59.999+08:00"), 4);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-03-01T00:00:00+08:00"), 5);
        await SeedAsync(org, databaseId, other.AccountId, At("2026-02-10T10:00:00Z"), 10);
        await SeedAsync(org, databaseId, other.AccountId, At("2026-02-11T10:00:00Z"), null);
        const string February = "from=2026-02-01&to=2026-02-28";

        var count = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", February), admin.Token));
        count.GetProperty("period").GetProperty("from").GetString().ShouldBe("2026-02-01");
        count.GetProperty("period").GetProperty("to").GetString().ShouldBe("2026-02-28");
        count.GetProperty("period").GetProperty("period").ValueKind.ShouldBe(JsonValueKind.Null);
        count.GetProperty("period").GetProperty("label").GetString().ShouldBe("2026-02-01 至 2026-02-28");
        count.GetProperty("previousPeriod").GetProperty("from").GetString().ShouldBe("2026-01-04", "the 28 days right before");
        count.GetProperty("previousPeriod").GetProperty("to").GetString().ShouldBe("2026-01-31");
        count.GetProperty("count").GetInt32().ShouldBe(5, "the 1st 00:00:00+08 and the 28th 23:59:59.999+08 are inside; Jan 31 and Mar 1 are not");
        count.GetProperty("previousCount").GetInt32().ShouldBe(1, "Jan 31 23:59:59+08");
        count.GetProperty("change").GetInt32().ShouldBe(4);
        count.GetProperty("changeLabel").GetString().ShouldBe("+4 筆");

        var forCustomer = await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "record-count", $"{February}&subjectId={customer.AccountId}"), admin.Token));
        forCustomer.GetProperty("count").GetInt32().ShouldBe(3);
        forCustomer.GetProperty("subjectId").GetGuid().ShouldBe(customer.AccountId);

        var sum = await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "field-sum", $"{February}&fieldId={CountField}"), admin.Token));
        var field = sum.GetProperty("field");
        field.GetProperty("sum").GetDouble().ShouldBe(19.5);
        field.GetProperty("display").GetString().ShouldBe("19.5 件");
        field.GetProperty("recordCount").GetInt32().ShouldBe(4, "the other member's blank value has nothing to add");
        field.GetProperty("previousSum").GetDouble().ShouldBe(1);
        field.GetProperty("change").GetDouble().ShouldBe(18.5);
        field.GetProperty("changeLabel").GetString().ShouldBe("+18.5 件");
        field.GetProperty("label").GetString().ShouldBe("本期完成數量");
        field.GetProperty("unit").GetString().ShouldBe("件");

        var customerSum = await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "field-sum", $"{February}&fieldId={CountField}&subjectId={customer.AccountId}"), admin.Token));
        customerSum.GetProperty("field").GetProperty("sum").GetDouble().ShouldBe(9.5);

        var summary = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "period-summary", February), admin.Token));
        summary.GetProperty("recordCount").GetInt32().ShouldBe(5);
        summary.GetProperty("recordCountChangeLabel").GetString().ShouldBe("+4 筆");
        var line = summary.GetProperty("sums").EnumerateArray().Single();
        line.GetProperty("sum").GetDouble().ShouldBe(19.5);
        OpenApiContract.AssertKeysMatchSchema(summary, "DatabasePeriodSummaryResult");

        // A single day, and the day with nothing.
        var march1 = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", "from=2026-03-01&to=2026-03-01"), admin.Token));
        march1.GetProperty("count").GetInt32().ShouldBe(1);
        march1.GetProperty("previousCount").GetInt32().ShouldBe(1, "the day before: Feb 28 23:59:59.999+08");
        var empty = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", "from=2025-05-01&to=2025-05-31"), admin.Token));
        empty.GetProperty("count").GetInt32().ShouldBe(0);

        // First / previous / current across all five of the customer's records.
        var comparison = (await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}"), admin.Token))).GetProperty("comparison");
        comparison.GetProperty("status").GetString().ShouldBe("available");
        comparison.GetProperty("recordCount").GetInt32().ShouldBe(5);
        comparison.GetProperty("summary").GetString().ShouldBe("比較 5 筆已同意提交的紀錄（2026-01-31 至 2026-03-01）。");
        var metric = comparison.GetProperty("metrics").EnumerateArray().Single();
        metric.GetProperty("first").GetProperty("display").GetString().ShouldBe("1 件");
        metric.GetProperty("previous").GetProperty("display").GetString().ShouldBe("4 件");
        metric.GetProperty("current").GetProperty("display").GetString().ShouldBe("5 件");
        metric.GetProperty("current").GetProperty("date").GetString().ShouldBe("2026-03-01");
        metric.GetProperty("changeFromPreviousLabel").GetString().ShouldBe("+1 件");
        metric.GetProperty("changeFromFirstLabel").GetString().ShouldBe("+4 件");
        metric.GetProperty("direction").GetString().ShouldBe("up");
        metric.GetProperty("points").EnumerateArray().Select(point => point.GetProperty("value").GetDouble()).ShouldBe([1, 2, 3.5, 4, 5]);
        metric.GetProperty("summary").GetString().ShouldBe("本期完成數量：本次 5 件，較上次 +1 件，較首次 +4 件。");
        OpenApiContract.AssertKeysMatchSchema(metric, "DatabaseMetricComparison");
    }

    [Fact]
    public async Task Named_periods_count_what_is_inside_them_and_compare_with_the_period_before()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(org, databaseId, customer.AccountId, now.AddHours(-1), 3);
        await SeedAsync(org, databaseId, customer.AccountId, now.AddDays(-10), 8);
        await SeedAsync(org, databaseId, customer.AccountId, now.AddDays(-40), 100);

        var week = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", "period=last-7-days"), admin.Token));
        OpenApiContract.AssertKeysMatchSchema(week, "DatabaseRecordCountResult");
        week.GetProperty("period").GetProperty("period").GetString().ShouldBe("last-7-days");
        week.GetProperty("period").GetProperty("to").GetString().ShouldBe(
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Taipei).DateTime).ToString("yyyy-MM-dd"), "today in Taipei");
        week.GetProperty("period").GetProperty("label").GetString()!.ShouldStartWith("近 7 天（");
        week.GetProperty("count").GetInt32().ShouldBe(1);
        week.GetProperty("previousCount").GetInt32().ShouldBe(1, "ten days ago is in the seven days before");

        var thirty = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", "period=last-30-days"), admin.Token));
        thirty.GetProperty("count").GetInt32().ShouldBe(2);

        foreach (var name in new[] { "this-week", "last-week", "this-month", "last-month" })
        {
            var response = await admin.Spa.GetAsync(QueryPath(databaseId, "period-summary", $"period={name}"), admin.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, name);
            var period = (await BodyJsonAsync(response)).GetProperty("period");
            period.GetProperty("period").GetString().ShouldBe(name);
            DateOnly.Parse(period.GetProperty("from").GetString()!).ShouldBeLessThanOrEqualTo(DateOnly.Parse(period.GetProperty("to").GetString()!));
        }
    }

    // --- Time zone boundaries (Statistics:TimeZone, default Asia/Taipei) --------------------------

    [Fact]
    public async Task Days_weeks_and_months_are_Taipei_calendar_days_not_UTC_days()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        // 07:30 in Taipei on Oct 3 is still Oct 2 in UTC; 00:30 on Oct 4 in Taipei is Oct 3 in UTC.
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-10-02T23:30:00Z"), 1);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-10-03T16:30:00Z"), 2);
        // Week boundary: Sunday Oct 4 23:59:59 +08 is the last second of the week of Sep 28; Monday 00:00 +08 starts the next.
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-10-04T15:59:59Z"), 4);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-10-04T16:00:00Z"), 8);
        // Month boundary: Oct 31 23:30 +08 is October; Nov 1 00:30 +08 is already November (still Oct 31 in UTC).
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-10-31T15:30:00Z"), 16);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-10-31T16:30:00Z"), 32);

        async Task<(int Count, double Sum)> Day(string query)
        {
            var count = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", query), admin.Token));
            var sum = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "field-sum", $"{query}&fieldId={CountField}"), admin.Token));
            return (count.GetProperty("count").GetInt32(), sum.GetProperty("field").GetProperty("sum").GetDouble());
        }

        (await Day("from=2026-10-03&to=2026-10-03")).ShouldBe((1, 1), "UTC 2026-10-02T23:30Z is Taipei Oct 3 07:30");
        (await Day("from=2026-10-04&to=2026-10-04")).ShouldBe((2, 6), "UTC 2026-10-03T16:30Z is Taipei Oct 4 00:30, with the Sunday 15:59:59Z one");
        (await Day("from=2026-10-02&to=2026-10-02")).ShouldBe((0, 0), "nothing belongs to Taipei Oct 2");
        (await Day("from=2026-10-05&to=2026-10-05")).ShouldBe((1, 8), "Monday 00:00 +08 is the start of the next week");

        // Whole week Mon Sep 28 .. Sun Oct 4 vs the next week.
        (await Day("from=2026-09-28&to=2026-10-04")).ShouldBe((3, 7), "the Sunday 23:59:59 +08 record is in the week, the Monday 00:00 +08 one is not");
        (await Day("from=2026-10-05&to=2026-10-11")).ShouldBe((1, 8));

        // Whole October vs November.
        (await Day("from=2026-10-01&to=2026-10-31")).ShouldBe((5, 31), "Oct 31 23:30 +08 is in October; Nov 1 00:30 +08 is not, though it is Oct 31 in UTC");
        (await Day("from=2026-11-01&to=2026-11-30")).ShouldBe((1, 32));

        // The previous period of one day is the day before, in Taipei too.
        var previous = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", "from=2026-10-04&to=2026-10-04"), admin.Token));
        previous.GetProperty("previousPeriod").GetProperty("from").GetString().ShouldBe("2026-10-03");
        previous.GetProperty("previousCount").GetInt32().ShouldBe(1);

        // The dates a comparison shows are Taipei days, as the receipts show them.
        var points = (await BodyJsonAsync(await admin.Spa.GetAsync(
                QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}"), admin.Token)))
            .GetProperty("comparison").GetProperty("metrics")[0].GetProperty("points").EnumerateArray()
            .Select(point => point.GetProperty("date").GetString()).ToList();
        points.ShouldBe(["2026-10-03", "2026-10-04", "2026-10-04", "2026-10-05", "2026-10-31", "2026-11-01"]);
    }

    // --- Withdrawal --------------------------------------------------------------------------

    [Fact]
    public async Task A_withdrawn_record_drops_out_of_the_next_answer_of_every_query_and_of_the_timeline()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        await SeedAsync(org, databaseId, customer.AccountId, At("2026-02-10T10:00:00Z"), 1000);
        var first = await SubmitAsync(customer, databaseId, 10);
        var second = await SubmitAsync(customer, databaseId, 20);
        var third = await SubmitAsync(customer, databaseId, 40);
        var around = $"from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}";

        async Task<(int Count, double Sum, JsonElement Comparison)> ReadAsync()
        {
            var count = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "record-count", around), admin.Token));
            var sum = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "field-sum", $"{around}&fieldId={CountField}"), admin.Token));
            var summary = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "period-summary", around), admin.Token));
            var comparison = (await BodyJsonAsync(await admin.Spa.GetAsync(
                QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}"), admin.Token))).GetProperty("comparison");
            var tracking = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}/tracking", admin.Token));

            summary.GetProperty("recordCount").GetInt32().ShouldBe(count.GetProperty("count").GetInt32());
            summary.GetProperty("sums")[0].GetProperty("sum").GetDouble().ShouldBe(sum.GetProperty("field").GetProperty("sum").GetDouble());
            tracking.GetProperty("subjects").EnumerateArray().Single().GetProperty("comparison").GetRawText()
                .ShouldBe(comparison.GetRawText(), "the timeline carries the same comparison");
            return (count.GetProperty("count").GetInt32(), sum.GetProperty("field").GetProperty("sum").GetDouble(), comparison);
        }

        var all = await ReadAsync();
        all.Count.ShouldBe(3);
        all.Sum.ShouldBe(70);
        all.Comparison.GetProperty("metrics")[0].GetProperty("current").GetProperty("value").GetDouble().ShouldBe(40);
        all.Comparison.GetProperty("recordCount").GetInt32().ShouldBe(4);

        (await WithdrawAsync(customer, third)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterOne = await ReadAsync();
        afterOne.Count.ShouldBe(2);
        afterOne.Sum.ShouldBe(30);
        afterOne.Comparison.GetProperty("recordCount").GetInt32().ShouldBe(3);
        var metric = afterOne.Comparison.GetProperty("metrics")[0];
        metric.GetProperty("current").GetProperty("value").GetDouble().ShouldBe(20, "the withdrawn 40 is gone");
        metric.GetProperty("points").GetArrayLength().ShouldBe(3);
        metric.GetProperty("points").EnumerateArray().Select(point => point.GetProperty("recordId").GetGuid()).ShouldNotContain(third);
        afterOne.Comparison.GetRawText().ShouldNotContain("40 件");

        (await WithdrawAsync(customer, second)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WithdrawAsync(customer, first)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterAll = await ReadAsync();
        afterAll.Count.ShouldBe(0);
        afterAll.Sum.ShouldBe(0);
        afterAll.Comparison.GetProperty("status").GetString().ShouldBe("insufficient-records");
        afterAll.Comparison.GetProperty("recordCount").GetInt32().ShouldBe(1, "only the February record is left");
    }

    [Fact]
    public async Task A_subject_whose_every_record_was_withdrawn_is_still_a_subject_with_nothing_to_compare()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        var only = await SubmitAsync(customer, databaseId, 10);
        (await WithdrawAsync(customer, only)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var comparison = (await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}"), admin.Token))).GetProperty("comparison");
        comparison.GetProperty("status").GetString().ShouldBe("insufficient-records");
        comparison.GetProperty("recordCount").GetInt32().ShouldBe(0);

        var tracking = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}/tracking", admin.Token));
        OpenApiContract.AssertKeysMatchSchema(tracking, "DatabaseTrackingView");
        var subject = tracking.GetProperty("subjects").EnumerateArray().Single();
        subject.GetProperty("withdrawals").GetArrayLength().ShouldBe(1);
        subject.GetProperty("comparison").GetProperty("recordCount").GetInt32().ShouldBe(0);
        subject.GetProperty("comparison").GetProperty("status").GetString().ShouldBe("insufficient-records");
    }

    // --- Form revisions ----------------------------------------------------------------------

    [Fact]
    public async Task A_form_revision_does_not_break_the_comparison_because_fields_are_matched_by_id()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        await SubmitAsync(customer, databaseId, 5);

        // Version 2: the field is renamed (same id), another is added, the unit stays.
        var v2 = await admin.Spa.PutAsync($"{BasePath}/{databaseId}/form", admin.Token, new
        {
            baseVersionNumber = 1,
            fields = new object[]
            {
                new { id = "field-report-date", label = "回報日期", type = "date", required = true, options = Array.Empty<string>(), scale = (object?)null, unit = "" },
                new { id = CountField, label = "本期完成件數", type = "number", required = true, options = Array.Empty<string>(), scale = (object?)null, unit = "件" },
                new { id = (string?)null, label = "備註", type = "text", required = false, options = Array.Empty<string>(), scale = (object?)null, unit = "" },
            },
        });
        v2.StatusCode.ShouldBe(HttpStatusCode.OK, await v2.Content.ReadAsStringAsync(CancellationToken));
        await SubmitAsync(customer, databaseId, 8, formVersion: 2);

        var metric = (await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}"), admin.Token)))
            .GetProperty("comparison").GetProperty("metrics").EnumerateArray().Single();
        metric.GetProperty("fieldId").GetString().ShouldBe(CountField);
        metric.GetProperty("label").GetString().ShouldBe("本期完成件數", "the latest record's label");
        metric.GetProperty("points").GetArrayLength().ShouldBe(2, "one record per form version, same field id");
        metric.GetProperty("changeFromPreviousLabel").GetString().ShouldBe("+3 件");

        var around = $"from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}";
        var sum = (await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "field-sum", $"{around}&fieldId={CountField}"), admin.Token))).GetProperty("field");
        sum.GetProperty("sum").GetDouble().ShouldBe(13);
        sum.GetProperty("label").GetString().ShouldBe("本期完成件數", "the field's latest definition");

        // Version 3 drops the field: its history is still queryable, and a record without it compares nothing.
        var v3 = await admin.Spa.PutAsync($"{BasePath}/{databaseId}/form", admin.Token, new
        {
            baseVersionNumber = 2,
            fields = new object[]
            {
                new { id = "field-report-date", label = "回報日期", type = "date", required = true, options = Array.Empty<string>(), scale = (object?)null, unit = "" },
            },
        });
        v3.StatusCode.ShouldBe(HttpStatusCode.OK, await v3.Content.ReadAsStringAsync(CancellationToken));

        (await admin.Spa.GetAsync(QueryPath(databaseId, "field-sum", $"{around}&fieldId={CountField}"), admin.Token)).StatusCode
            .ShouldBe(HttpStatusCode.OK, "a removed field's id is still a defined field");
        var summary = await BodyJsonAsync(await admin.Spa.GetAsync(QueryPath(databaseId, "period-summary", around), admin.Token));
        summary.GetProperty("sums").EnumerateArray().Single().GetProperty("sum").GetDouble().ShouldBe(13);

        var submit = await customer.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", customer.Token, new
        {
            submissionId = Guid.NewGuid(),
            formVersionNumber = 3,
            consent = true,
            answers = new Dictionary<string, object> { ["field-report-date"] = "2026-10-03" },
        });
        submit.StatusCode.ShouldBe(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync(CancellationToken));
        var latest = (await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}"), admin.Token))).GetProperty("comparison");
        latest.GetProperty("status").GetString().ShouldBe("insufficient-records");
        latest.GetProperty("recordCount").GetInt32().ShouldBe(3);
        latest.GetProperty("message").GetString().ShouldBe("目前有 3 筆紀錄，但沒有任何數字或量尺欄位累積 2 筆以上的數值，無法比較。");
    }

    [Fact]
    public async Task A_scale_field_is_compared_with_the_scale_as_the_axis_and_the_unit_of_a_score()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin, "template-progress", "進度");
        foreach (var score in new[] { "3", "6", "9" })
        {
            var response = await customer.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", customer.Token, new
            {
                submissionId = Guid.NewGuid(),
                formVersionNumber = 1,
                consent = true,
                answers = new Dictionary<string, object> { ["field-check-date"] = "2026-10-03", ["field-condition-score"] = score },
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        }

        var metric = (await BodyJsonAsync(await admin.Spa.GetAsync(
            QueryPath(databaseId, "subject-comparison", $"subjectId={customer.AccountId}&fieldId=field-condition-score"), admin.Token)))
            .GetProperty("comparison").GetProperty("metrics").EnumerateArray().Single();
        metric.GetProperty("unit").GetString().ShouldBe("分");
        metric.GetProperty("current").GetProperty("display").GetString().ShouldBe("9 / 10");
        metric.GetProperty("changeFromFirstLabel").GetString().ShouldBe("+6 分");
        metric.GetProperty("axis").GetProperty("min").GetDouble().ShouldBe(0);
        metric.GetProperty("axis").GetProperty("max").GetDouble().ShouldBe(10);

        // A scale cannot be summed.
        var sum = await admin.Spa.GetAsync(QueryPath(databaseId, "field-sum", "period=last-7-days&fieldId=field-condition-score"), admin.Token);
        sum.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(sum)).GetProperty("errors").GetProperty("fieldId")[0].GetString().ShouldBe("只有數字欄位可以加總。");
    }

    // --- Helpers -----------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token, Guid AccountId);

    private static IEnumerable<(string Name, string Query)> ValidQueries(Guid subjectId)
    {
        yield return ("record-count", "from=2026-02-01&to=2026-02-28");
        yield return ("field-sum", $"from=2026-02-01&to=2026-02-28&fieldId={CountField}");
        yield return ("period-summary", "from=2026-02-01&to=2026-02-28");
        yield return ("subject-comparison", $"subjectId={subjectId}");
    }

    private static string ValidFor(string name, string subject) => name switch
    {
        "subject-comparison" => $"subjectId={subject}",
        "field-sum" => $"from=2026-02-01&to=2026-02-28&fieldId={CountField}&subjectId={subject}",
        _ => $"from=2026-02-01&to=2026-02-28&subjectId={subject}",
    };

    private static string QueryPath(Guid databaseId, string name, string query) =>
        $"{BasePath}/{databaseId}/queries/{name}" + (query.Length > 0 ? "?" + query : string.Empty);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants,
            AccountPermission.ReadConsentedSubmissions);
        var customer = await _host.CreateAccountAsync(
            organization, "customer", Password, AccountRole.ExternalCustomer, $"{name}外部客戶",
            AccountPermission.SubmitAuthorizedForms,
            AccountPermission.ReadOwnTracking);

        return new TestOrganization(organization, admin, internalEmployee, customer);
    }

    private async Task<SignedIn> CreateSecondCustomerAsync(TestOrganization org)
    {
        var account = await _host.CreateAccountAsync(
            org.Organization, "customer2", Password, AccountRole.ExternalCustomer, "第二位外部客戶",
            AccountPermission.SubmitAuthorizedForms);
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, "customer2", Password);
        return new SignedIn(spa, token.AccessToken, account.Id);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        var accountId = loginName switch
        {
            "admin" => org.Admin.Id,
            "internal" => org.Internal.Id,
            _ => org.Customer.Id,
        };
        return new SignedIn(spa, token.AccessToken, accountId);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string template = "template-periodic-report", string name = "回報資料庫")
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { templateId = template, name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Submits through the API (the time is "now") with a number for the count field.</summary>
    private static async Task<Guid> SubmitAsync(SignedIn caller, Guid databaseId, double count, int formVersion = 1)
    {
        var response = await caller.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", caller.Token, new
        {
            submissionId = Guid.NewGuid(),
            formVersionNumber = formVersion,
            consent = true,
            answers = new Dictionary<string, object> { ["field-report-date"] = "2026-10-03", [CountField] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> WithdrawAsync(SignedIn caller, Guid submissionId) =>
        caller.Spa.PostAsync($"{SubmissionsPath}/{submissionId}/withdrawal", caller.Token, new { });

    /// <summary>Writes a record of the periodic-report form as it would have been submitted at
    /// <paramref name="at"/> (a number, or a blank optional field when <see langword="null"/>).</summary>
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

    private static Task<HttpResponseMessage> PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds) =>
        caller.Spa.PutAsync($"{BasePath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });

    private static async Task SetPermissionsAsync(SignedIn admin, Guid accountId, AccountPermission[] permissions)
    {
        var wire = permissions.Select(SmartAgri.Domain.WireNames<AccountPermission>.ToWire).ToArray();
        var response = await admin.Spa.PutAsync($"/api/v1/team/members/{accountId}/permissions", admin.Token, new { permissions = wire });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

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
}
