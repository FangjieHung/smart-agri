using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>
/// Archiving a database (封存, #180) against real PostgreSQL: only the owner archives and unarchives, both
/// idempotent, everyone else (a data manager included), another organization and a missing id get the same
/// <c>403 database</c>; an archived database leaves the default list (the <c>archived=true</c> filter lists
/// it), its form link takes no new submission with the same refusal as a missing database, while records,
/// the timeline, statistics, receipts, withdrawals and members' own submissions keep working; unarchiving
/// restores everything. The assistant entry points are in <c>ChatFormRequestUxTests</c>,
/// <c>ChatDatabaseQueryEndpointsTests</c> and <c>PeriodicReportEndpointsTests</c>.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseArchiveEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Database-Archive-Pass-1!";
    private const string BasePath = "/api/v1/databases";
    private const string SubmissionsPath = "/api/v1/submissions";
    private const string DatabaseName = "客戶資料庫";

    private readonly AuthHostFixture _host;

    public DatabaseArchiveEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static object ValidAnswers(string name = "王小明") => new Dictionary<string, object>
    {
        ["field-customer-name"] = name,
        ["field-phone"] = "0912-345-678",
        ["field-first-visit"] = "2026-09-21",
        ["field-customer-type"] = "企業",
    };

    [Fact]
    public async Task Only_the_owner_archives_and_unarchives_idempotently_and_everyone_else_gets_the_same_403()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var orgB = await CreateOrganizationAsync("組織 B");
        var foreignAdmin = await SignInAsync(orgB, "admin");

        // A data manager (who sees the database read-only), a member, another organization's owner-like
        // admin, a missing id: one byte-identical 403, for both actions, and nothing changes.
        var missing = await admin.Spa.PostAsync($"{BasePath}/{Guid.NewGuid()}/archive", admin.Token, new { });
        missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(missing)).GetProperty("reason").GetString().ShouldBe("database");
        foreach (var action in new[] { "archive", "unarchive" })
        {
            foreach (var (who, caller) in new[]
            {
                ("a data manager", reader),
                ("a member", await SignInAsync(org, "customer")),
                ("another organization", foreignAdmin),
            })
            {
                var refused = await caller.Spa.PostAsync($"{BasePath}/{databaseId}/{action}", caller.Token, new { });
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{action} by {who}");
                await AssertIdenticalAsync(refused, missing);
                (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain(DatabaseName);
            }

            (await admin.Spa.PostAsync($"{BasePath}/{databaseId}/{action}", null, new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await ArchivedAtAsync(org, databaseId)).ShouldBeNull();

        // The owner archives: 200 with the summary, archivedAt set; again keeps the first time.
        var archived = await admin.Spa.PostAsync($"{BasePath}/{databaseId}/archive", admin.Token, new { });
        archived.StatusCode.ShouldBe(HttpStatusCode.OK, await archived.Content.ReadAsStringAsync(CancellationToken));
        var summary = await BodyJsonAsync(archived);
        OpenApiContract.AssertKeysMatchSchema(summary, "DatabaseSummaryView");
        summary.GetProperty("id").GetGuid().ShouldBe(databaseId);
        var archivedAt = summary.GetProperty("archivedAt").GetDateTimeOffset();
        (await ArchivedAtAsync(org, databaseId)).ShouldBe(archivedAt);

        var again = await BodyJsonAsync(await admin.Spa.PostAsync($"{BasePath}/{databaseId}/archive", admin.Token, new { }));
        again.GetProperty("archivedAt").GetDateTimeOffset().ShouldBe(archivedAt);
        (await ArchivedAtAsync(org, databaseId)).ShouldBe(archivedAt);

        // Refusals are the same for an archived database (it says nothing about the state).
        var refusedArchived = await reader.Spa.PostAsync($"{BasePath}/{databaseId}/unarchive", reader.Token, new { });
        await AssertIdenticalAsync(refusedArchived, missing);
        (await ArchivedAtAsync(org, databaseId)).ShouldBe(archivedAt);

        // Unarchive: archivedAt back to null (sent as null, not omitted); again is a no-op.
        var restored = await BodyJsonAsync(await admin.Spa.PostAsync($"{BasePath}/{databaseId}/unarchive", admin.Token, new { }));
        restored.GetProperty("archivedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        var restoredAgain = await admin.Spa.PostAsync($"{BasePath}/{databaseId}/unarchive", admin.Token, new { });
        restoredAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(restoredAgain)).GetProperty("archivedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ArchivedAtAsync(org, databaseId)).ShouldBeNull();
    }

    [Fact]
    public async Task An_archived_database_leaves_the_default_list_and_the_archived_filter_lists_it()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var kept = await CreateDatabaseAsync(admin, "使用中的資料庫");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.PostAsync($"{BasePath}/{databaseId}/archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The owner and the data manager alike: in use by default, archived ones only with the filter.
        foreach (var caller in new[] { admin, reader })
        {
            var inUse = await BodyJsonAsync(await caller.Spa.GetAsync(BasePath, caller.Token));
            Ids(inUse).ShouldNotContain(databaseId);
            (await BodyJsonAsync(await caller.Spa.GetAsync($"{BasePath}?archived=false", caller.Token))).GetRawText()
                .ShouldBe(inUse.GetRawText());
            var archived = await BodyJsonAsync(await caller.Spa.GetAsync($"{BasePath}?archived=true", caller.Token));
            Ids(archived).ShouldBe([databaseId]);
            OpenApiContract.AssertKeysMatchSchema(archived[0], "DatabaseSummaryView");
            archived[0].GetProperty("archivedAt").ValueKind.ShouldBe(JsonValueKind.String);

            // The detail still opens by id (read-only for the data manager), saying it is archived.
            var detail = await BodyJsonAsync(await caller.Spa.GetAsync($"{BasePath}/{databaseId}", caller.Token));
            detail.GetProperty("summary").GetProperty("archivedAt").ValueKind.ShouldBe(JsonValueKind.String);
        }

        Ids(await BodyJsonAsync(await admin.Spa.GetAsync(BasePath, admin.Token))).ShouldBe([kept]);
        (await BodyJsonAsync(await admin.Spa.GetAsync(BasePath, admin.Token)))[0].GetProperty("archivedAt").ValueKind
            .ShouldBe(JsonValueKind.Null);

        // Another organization's archived databases never show up.
        var orgB = await CreateOrganizationAsync("組織 B");
        var foreignAdmin = await SignInAsync(orgB, "admin");
        (await BodyAsync(await foreignAdmin.Spa.GetAsync($"{BasePath}?archived=true", foreignAdmin.Token))).ShouldBe("[]");

        // Unarchived: back in the default list.
        (await admin.Spa.PostAsync($"{BasePath}/{databaseId}/unarchive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        Ids(await BodyJsonAsync(await admin.Spa.GetAsync(BasePath, admin.Token))).ShouldBe([kept, databaseId]);
        (await BodyAsync(await admin.Spa.GetAsync($"{BasePath}?archived=true", admin.Token))).ShouldBe("[]");
    }

    [Fact]
    public async Task The_form_link_refuses_new_submissions_like_a_missing_database_until_unarchived()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        var earlierKey = Guid.NewGuid();
        (await SubmitAsync(customer, databaseId, earlierKey, ValidAnswers())).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await admin.Spa.PostAsync($"{BasePath}/{databaseId}/archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Form, review and submit: the same refusal as a database that does not exist, naming nothing.
        var missingId = Guid.NewGuid();
        var formRefused = await customer.Spa.GetAsync($"{BasePath}/{databaseId}/submission-form", customer.Token);
        formRefused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(formRefused, await customer.Spa.GetAsync($"{BasePath}/{missingId}/submission-form", customer.Token));
        (await formRefused.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain(DatabaseName);

        var review = new { formVersionNumber = 1, answers = ValidAnswers() };
        var reviewRefused = await customer.Spa.PostAsync($"{BasePath}/{databaseId}/submission-form/review", customer.Token, review);
        reviewRefused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(
            reviewRefused, await customer.Spa.PostAsync($"{BasePath}/{missingId}/submission-form/review", customer.Token, review));

        var submitRefused = await SubmitAsync(customer, databaseId, Guid.NewGuid(), ValidAnswers("李大華"));
        submitRefused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(submitRefused, await SubmitAsync(customer, missingId, Guid.NewGuid(), ValidAnswers("李大華")));
        (await SubmissionCountAsync(org)).ShouldBe(1);

        // A retry of the fill made before archiving still gets its original receipt, and nothing is added.
        var retried = await SubmitAsync(customer, databaseId, earlierKey, ValidAnswers());
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SubmissionCountAsync(org)).ShouldBe(1);

        // Unarchived: the form and a new submission work again.
        (await admin.Spa.PostAsync($"{BasePath}/{databaseId}/unarchive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await customer.Spa.GetAsync($"{BasePath}/{databaseId}/submission-form", customer.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SubmitAsync(customer, databaseId, Guid.NewGuid(), ValidAnswers("李大華"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SubmissionCountAsync(org)).ShouldBe(2);
    }

    [Fact]
    public async Task Records_statistics_receipts_withdrawals_and_own_submissions_keep_working_while_archived()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var reader = await SignInAsync(org, "internal");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = await BodyJsonAsync(await SubmitAsync(customer, databaseId, Guid.NewGuid(), ValidAnswers()));
        var second = await BodyJsonAsync(await SubmitAsync(customer, databaseId, Guid.NewGuid(), ValidAnswers("李大華")));
        var firstId = first.GetProperty("id").GetGuid();

        (await admin.Spa.PostAsync($"{BasePath}/{databaseId}/archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The data manager reads records, the timeline and the statistics as before.
        var records = await BodyJsonAsync(await reader.Spa.GetAsync($"{BasePath}/{databaseId}/records", reader.Token));
        records.GetProperty("records").GetArrayLength().ShouldBe(2);
        (await reader.Spa.GetAsync($"{BasePath}/{databaseId}/tracking", reader.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Wide enough around now for any statistics time zone.
        var from = DateTimeOffset.UtcNow.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var to = DateTimeOffset.UtcNow.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var count = await reader.Spa.GetAsync($"{BasePath}/{databaseId}/queries/record-count?from={from}&to={to}", reader.Token);
        count.StatusCode.ShouldBe(HttpStatusCode.OK, await count.Content.ReadAsStringAsync(CancellationToken));
        (await BodyJsonAsync(count)).GetProperty("count").GetInt32().ShouldBe(2);
        var listed = await BodyJsonAsync(await reader.Spa.GetAsync($"{BasePath}?archived=true", reader.Token));
        (listed[0].GetProperty("recordCount").GetInt32(), listed[0].GetProperty("subjectCount").GetInt32()).ShouldBe((2, 1));

        // The submitter: own submissions, the receipt, and withdrawing one.
        var own = await BodyJsonAsync(await customer.Spa.GetAsync(SubmissionsPath, customer.Token));
        own.GetProperty("submissions").GetArrayLength().ShouldBe(2);
        var receipt = await customer.Spa.GetAsync($"{SubmissionsPath}/{firstId}", customer.Token);
        receipt.StatusCode.ShouldBe(HttpStatusCode.OK);
        var withdrawn = await customer.Spa.PostAsync($"{SubmissionsPath}/{firstId}/withdrawal", customer.Token, new { });
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, await withdrawn.Content.ReadAsStringAsync(CancellationToken));
        (await BodyJsonAsync(withdrawn)).GetProperty("withdrawnAt").ValueKind.ShouldBe(JsonValueKind.String);

        // The withdrawal shows up for the data manager on the next request; the other record is untouched.
        var after = await BodyJsonAsync(await reader.Spa.GetAsync($"{BasePath}/{databaseId}/records", reader.Token));
        after.GetProperty("records").EnumerateArray().Select(record => record.GetProperty("id").GetGuid())
            .ShouldBe([second.GetProperty("id").GetGuid()]);

        // Archiving deleted nothing.
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.DatabaseFormVersions.CountAsync(version => version.DatabaseId == databaseId, CancellationToken)).ShouldBe(1);
        (await dbContext.DatabaseDataManagers.CountAsync(designation => designation.DatabaseId == databaseId, CancellationToken)).ShouldBe(2);
    }

    // --- Helpers ----------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "封存商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions);
        var customer = await _host.CreateAccountAsync(
            organization, "customer", Password, AccountRole.ExternalCustomer, $"{name}外部客戶",
            AccountPermission.SubmitAuthorizedForms, AccountPermission.ReadOwnTracking);
        return new TestOrganization(organization, admin, internalEmployee, customer);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name = DatabaseName)
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SubmitAsync(SignedIn caller, Guid databaseId, Guid submissionId, object answers) =>
        caller.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", caller.Token, new
        {
            submissionId,
            formVersionNumber = 1,
            consent = true,
            answers,
        });

    private static Task<HttpResponseMessage> PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds) =>
        caller.Spa.PutAsync($"{BasePath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });

    private async Task<DateTimeOffset?> ArchivedAtAsync(TestOrganization org, Guid databaseId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.Databases.AsNoTracking()
            .Where(database => database.Id == databaseId)
            .Select(database => database.ArchivedAt)
            .SingleAsync(CancellationToken);
    }

    private async Task<int> SubmissionCountAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.DatabaseSubmissions.CountAsync(CancellationToken);
    }

    private static List<Guid> Ids(JsonElement list) => [.. list.EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(CancellationToken);

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
