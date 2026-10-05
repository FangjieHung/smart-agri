using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Tenancy;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>
/// The database list's and detail's <c>recordCount</c>/<c>subjectCount</c> (issue #177) against
/// real PostgreSQL: a caller who may read the records on this request (designated data manager
/// holding <c>read-consented-submissions</c>, #144) gets the active-record counts, and a
/// withdrawal lowers them; anyone else gets no count keys at all, so their response is the same
/// bytes as before #177 and does not change when records are added or withdrawn.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseSummaryCountsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Database-Counts-Pass-1!";
    private const string BasePath = "/api/v1/databases";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private readonly AuthHostFixture _host;

    public DatabaseSummaryCountsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static Dictionary<string, object> Answers(string name) => new()
    {
        ["field-customer-name"] = name,
        ["field-phone"] = "0912-345-678",
        ["field-customer-type"] = "企業",
    };

    [Fact]
    public async Task Readers_get_active_record_and_subject_counts_that_withdrawals_lower()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var customer = await SignInAsync(org, "customer");
        var other = await CreateSecondCustomerAsync(org);
        var counted = await CreateDatabaseAsync(admin, "有紀錄的");
        var empty = await CreateDatabaseAsync(admin, "沒有紀錄的");

        await SubmitAsync(customer, counted, Answers("甲"));
        var second = await SubmitAsync(customer, counted, Answers("乙"));
        var others = await SubmitAsync(other, counted, Answers("丙"));

        (await CountsInListAsync(admin)).ShouldBe(new Dictionary<Guid, (int?, int?)>
        {
            [counted] = (3, 2),
            [empty] = (0, 0),
        });
        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{counted}", admin.Token));
        OpenApiContract.AssertKeysMatchSchema(detail, "DatabaseDetailView");
        CountsOf(detail.GetProperty("summary")).ShouldBe((3, 2));

        // Withdrawing one of two records keeps the subject; withdrawing the only one drops it.
        await WithdrawAsync(customer, second);
        (await CountsInListAsync(admin))[counted].ShouldBe((2, 2));
        await WithdrawAsync(other, others);
        (await CountsInListAsync(admin))[counted].ShouldBe((1, 1));
        CountsOf((await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{counted}", admin.Token))).GetProperty("summary"))
            .ShouldBe((1, 1));
    }

    [Fact]
    public async Task Non_readers_get_no_count_keys_and_the_same_bytes_whatever_the_records()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var manager = await SignInAsync(org, "internal");
        var customer = await SignInAsync(org, "customer");
        var databaseId = await CreateDatabaseAsync(admin);
        var detailPath = $"{BasePath}/{databaseId}";

        // The internal employee is designated but lacks the permission; the owner holds the
        // permission but is no longer designated. Both still see the database.
        await PutAccessAsync(admin, databaseId, [org.Internal.Id]);
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        var before = new Dictionary<string, string>
        {
            ["owner list"] = await BodyAsync(admin, BasePath),
            ["owner detail"] = await BodyAsync(admin, detailPath),
            ["unpermitted manager list"] = await BodyAsync(manager, BasePath),
        };
        foreach (var (who, body) in before)
        {
            body.ShouldNotContain("recordCount", Case.Sensitive, who);
            body.ShouldNotContain("subjectCount", Case.Sensitive, who);
        }

        var withdrawn = await SubmitAsync(customer, databaseId, Answers("甲"));
        await SubmitAsync(customer, databaseId, Answers("乙"));
        await WithdrawAsync(customer, withdrawn);

        (await BodyAsync(admin, BasePath)).ShouldBe(before["owner list"]);
        (await BodyAsync(admin, detailPath)).ShouldBe(before["owner detail"]);
        (await BodyAsync(manager, BasePath)).ShouldBe(before["unpermitted manager list"]);

        // Granted the permission: counts on the next request; revoked again: gone on the next one.
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.ReadConsentedSubmissions]);
        (await CountsInListAsync(manager))[databaseId].ShouldBe((1, 1));
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        (await BodyAsync(manager, BasePath)).ShouldBe(before["unpermitted manager list"]);

        // The submitter does not see the database at all.
        (await ListAsync(customer)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_new_database_shows_zero_counts_only_to_a_creator_holding_the_permission()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var withPermission = await BodyJsonAsync(
            await admin.Spa.PostAsync(BasePath, admin.Token, new { templateId = "template-blank", name = "可讀" }));
        CountsOf(withPermission).ShouldBe((0, 0));

        await SetPermissionsAsync(admin, org.Admin.Id, [AccountPermission.ManageAssistants, AccountPermission.ManageDataSources]);
        var created = await admin.Spa.PostAsync(BasePath, admin.Token, new { templateId = "template-blank", name = "不可讀" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var withoutPermission = await BodyJsonAsync(created);
        OpenApiContract.AssertKeysMatchSchema(withoutPermission, "DatabaseSummaryView");
        withoutPermission.TryGetProperty("recordCount", out _).ShouldBeFalse();
        withoutPermission.TryGetProperty("subjectCount", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_counts_of_every_listed_database_are_one_grouped_query()
    {
        using var dbContext = TenancyTestContexts.Create(Guid.NewGuid());

        var sql = DatabaseActiveRecords.CountsOf(dbContext, [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]).ToQueryString();

        sql.Split("SELECT").Length.ShouldBe(2, sql);
        sql.ShouldContain("GROUP BY");
        sql.ShouldContain("COUNT(DISTINCT");
        sql.ShouldContain("\"WithdrawnAt\" IS NULL");
        sql.ShouldContain("= ANY (");
    }

    // --- Helpers ---------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token, Guid AccountId);

    private static (int?, int?) CountsOf(JsonElement summary)
    {
        OpenApiContract.AssertKeysMatchSchema(summary, "DatabaseSummaryView");
        return (
            summary.TryGetProperty("recordCount", out var records) ? records.GetInt32() : null,
            summary.TryGetProperty("subjectCount", out var subjects) ? subjects.GetInt32() : null);
    }

    private static async Task<Dictionary<Guid, (int?, int?)>> CountsInListAsync(SignedIn caller) =>
        (await ListAsync(caller)).EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid(), CountsOf);

    private static async Task<JsonElement> ListAsync(SignedIn caller)
    {
        var response = await caller.Spa.GetAsync(BasePath, caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    private static async Task<string> BodyAsync(SignedIn caller, string path)
    {
        var response = await caller.Spa.GetAsync(path, caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync(CancellationToken);
    }

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

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name = "客戶資料庫")
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SubmitAsync(SignedIn caller, Guid databaseId, object answers)
    {
        var response = await caller.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", caller.Token, new
        {
            submissionId = Guid.NewGuid(),
            formVersionNumber = 1,
            consent = true,
            answers,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task WithdrawAsync(SignedIn caller, Guid submissionId)
    {
        var response = await caller.Spa.PostAsync($"/api/v1/submissions/{submissionId}/withdrawal", caller.Token, new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds)
    {
        var response = await caller.Spa.PutAsync($"{BasePath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

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
}
