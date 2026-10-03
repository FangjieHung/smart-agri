using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Databases;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>
/// <c>/api/v1/database-templates</c> and <c>/api/v1/databases</c> against real PostgreSQL (M4,
/// issue #142). Each test builds its own organizations with the seed's three roles, so tests
/// never see each other's data even though they share one database.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Database-Endpoint-Pass-1!";
    private const string TemplatesPath = "/api/v1/database-templates";
    private const string BasePath = "/api/v1/databases";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private readonly AuthHostFixture _host;

    public DatabaseEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Permission: only manage-data-sources may see templates and create ----------------

    [Fact]
    public async Task Members_without_manage_data_sources_get_403_database_on_templates_and_create_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();

        foreach (var loginName in new[] { "customer", "internal" })
        {
            var member = await SignInAsync(org, loginName);

            var templates = await member.Spa.GetAsync(TemplatesPath, member.Token);
            var create = await member.Spa.PostAsync(BasePath, member.Token, new { templateId = "template-blank", name = "客戶的數據庫" });

            foreach (var response in new[] { templates, create })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, loginName);
                var body = await BodyJsonAsync(response);
                body.GetProperty("reason").GetString().ShouldBe("database");
                body.GetProperty("message").GetString().ShouldBe("只有可管理資料來源的帳號可以建立資料庫。");
            }

            // Listing is open to every signed-in account: theirs is simply empty.
            (await ListAsync(member)).GetArrayLength().ShouldBe(0);
        }

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Databases.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.DatabaseFormVersions.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Every_endpoint_needs_a_signed_in_account()
    {
        var spa = _host.CreateSpaClient();

        (await spa.Http.GetAsync(TemplatesPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await spa.Http.GetAsync(BasePath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await spa.PostAsync(BasePath, null, new { templateId = "template-blank", name = "名" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await spa.Http.GetAsync($"{BasePath}/{Guid.NewGuid()}", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Templates -------------------------------------------------------------------------

    [Fact]
    public async Task Templates_list_the_five_templates_with_their_fields()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.GetAsync(TemplatesPath, admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var templates = await BodyJsonAsync(response);
        templates.EnumerateArray().Select(template => template.GetProperty("id").GetString()).ShouldBe(
        [
            "template-customer-profile", "template-periodic-report", "template-satisfaction", "template-progress", "template-blank",
        ]);
        templates.EnumerateArray().Select(template => template.GetProperty("name").GetString())
            .ShouldBe(["客戶基本資料", "定期回報", "滿意度調查", "症狀或進度追蹤", "空白模板"]);
        foreach (var template in templates.EnumerateArray())
        {
            OpenApiContract.AssertKeysMatchSchema(template, "DatabaseTemplateView");
        }

        var satisfaction = templates[2].GetProperty("fields");
        satisfaction.GetArrayLength().ShouldBe(3);
        var scale = satisfaction[0];
        scale.GetProperty("id").GetString().ShouldBe("field-overall-satisfaction");
        scale.GetProperty("type").GetString().ShouldBe("scale");
        scale.GetProperty("required").GetBoolean().ShouldBeTrue();
        scale.GetProperty("scale").GetProperty("min").GetInt32().ShouldBe(1);
        scale.GetProperty("scale").GetProperty("max").GetInt32().ShouldBe(5);
        scale.GetProperty("scale").GetProperty("maxLabel").GetString().ShouldBe("非常滿意");
        satisfaction[1].GetProperty("options").EnumerateArray().Select(option => option.GetString())
            .ShouldBe(["商品品質", "客服回應", "配送速度"]);
        // Inapplicable members are sent, as null / empty, never omitted.
        satisfaction[1].GetProperty("scale").ValueKind.ShouldBe(JsonValueKind.Null);
        satisfaction[2].GetProperty("unit").GetString().ShouldBe(string.Empty);
    }

    // --- Create ----------------------------------------------------------------------------

    [Fact]
    public async Task Creating_from_each_template_copies_its_fields_into_form_version_1()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        foreach (var template in DatabaseTemplates.All)
        {
            var templateId = WireNames<DatabaseTemplateId>.ToWire(template.Id);
            var response = await admin.Spa.PostAsync(BasePath, admin.Token, new { templateId, name = $"  {template.Name}數據庫 " });

            response.StatusCode.ShouldBe(HttpStatusCode.Created, templateId);
            var summary = await BodyJsonAsync(response);
            OpenApiContract.AssertKeysMatchSchema(summary, "DatabaseSummaryView");
            var id = summary.GetProperty("id").GetGuid();
            response.Headers.Location!.ToString().ShouldBe($"{BasePath}/{id}");
            summary.GetProperty("name").GetString().ShouldBe($"{template.Name}數據庫");
            summary.GetProperty("purpose").GetString().ShouldBe(template.Description);
            summary.GetProperty("templateId").GetString().ShouldBe(templateId);
            summary.GetProperty("templateName").GetString().ShouldBe(template.Name);
            summary.GetProperty("fieldCount").GetInt32().ShouldBe(template.Fields.Count);
            summary.GetProperty("formVersion").GetInt32().ShouldBe(1);
            summary.GetProperty("owner").GetProperty("id").GetGuid().ShouldBe(org.Admin.Id);
            summary.GetProperty("owner").GetProperty("displayName").GetString().ShouldBe("安心商行管理者");
            summary.GetProperty("viewerCanManage").GetBoolean().ShouldBeTrue();
            summary.GetProperty("updatedAt").GetDateTimeOffset().ShouldBe(summary.GetProperty("createdAt").GetDateTimeOffset());
            summary.TryGetProperty("recordCount", out _).ShouldBeFalse();
            summary.TryGetProperty("subjectCount", out _).ShouldBeFalse();

            var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{id}", admin.Token));
            OpenApiContract.AssertKeysMatchSchema(detail, "DatabaseDetailView");
            detail.GetProperty("form").GetProperty("versionNumber").GetInt32().ShouldBe(1);
            detail.GetProperty("form").GetProperty("fields").EnumerateArray()
                .Select(field => (field.GetProperty("id").GetString(), field.GetProperty("label").GetString(),
                    field.GetProperty("type").GetString(), field.GetProperty("required").GetBoolean()))
                .ShouldBe(template.Fields.Select(field =>
                    ((string?)field.Id, (string?)field.Label, (string?)WireNames<DatabaseFieldType>.ToWire(field.Type), field.Required)));
        }

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var databases = await dbContext.Databases.OrderBy(database => database.CreatedAt).ToListAsync(CancellationToken);
        databases.Select(database => database.TemplateId).ShouldBe(Enum.GetValues<DatabaseTemplateId>());
        databases.ShouldAllBe(database => database.OwnerAccountId == org.Admin.Id);
        var versions = await dbContext.DatabaseFormVersions.ToListAsync(CancellationToken);
        versions.Count.ShouldBe(DatabaseTemplates.All.Count);
        versions.ShouldAllBe(version => version.VersionNumber == 1 && version.CreatedByAccountId == org.Admin.Id);
        var progress = versions.Single(version => version.DatabaseId == databases[3].Id);
        // Read back from jsonb exactly as written (records with lists compare by reference, so via JSON).
        JsonSerializer.Serialize(progress.Fields).ShouldBe(JsonSerializer.Serialize(DatabaseTemplates.Get(DatabaseTemplateId.Progress).Fields));
        progress.Fields[1].Scale.ShouldBe(new DatabaseScaleRange(0, 10, "最差", "最好"));
    }

    [Fact]
    public async Task A_given_purpose_replaces_the_templates_description()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.PostAsync(
            BasePath, admin.Token, new { templateId = "template-periodic-report", name = "週報", purpose = " 每週一回報出貨量 " });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await BodyJsonAsync(response)).GetProperty("purpose").GetString().ShouldBe("每週一回報出貨量");
    }

    [Fact]
    public async Task Invalid_create_is_422_naming_every_bad_field_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var response = await admin.Spa.PostAsync(BasePath, admin.Token, new { templateId = "template-unknown", name = "   " });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("message").GetString().ShouldBe("請選擇一個模板。");
        body.GetProperty("errors").GetProperty("templateId").EnumerateArray().Select(error => error.GetString())
            .ShouldBe(["請選擇一個模板。"]);
        body.GetProperty("errors").GetProperty("name").EnumerateArray().Select(error => error.GetString())
            .ShouldBe(["請輸入資料庫名稱。"]);

        var tooLong = await admin.Spa.PostAsync(
            BasePath, admin.Token, new { templateId = "template-blank", name = new string('名', Database.NameMaxLength + 1) });
        tooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(tooLong)).GetProperty("message").GetString().ShouldBe("資料庫名稱請在 40 個字以內。");

        var missingTemplate = await admin.Spa.PostAsync(BasePath, admin.Token, new { name = "沒有模板" });
        missingTemplate.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(missingTemplate)).GetProperty("errors").TryGetProperty("templateId", out _).ShouldBeTrue();

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Databases.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.DatabaseFormVersions.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- Visibility: list and detail are the owner's own ------------------------------------

    [Fact]
    public async Task The_list_shows_only_the_callers_own_databases_oldest_first()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var first = await CreateDatabaseAsync(admin, "template-satisfaction", "第一個");
        var second = await CreateDatabaseAsync(admin, "template-blank", "第二個");

        await _host.CreateAccountAsync(org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var admin2 = await SignInAsync(org, "admin2");
        var admin2s = await CreateDatabaseAsync(admin2, "template-progress", "第二位管理者的");

        var otherOrg = await CreateOrganizationAsync("其他組織");
        await CreateDatabaseAsync(await SignInAsync(otherOrg, "admin"), "template-blank", "其他組織的");

        var list = await ListAsync(admin);
        list.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([first, second]);
        list[0].GetProperty("templateName").GetString().ShouldBe("滿意度調查");
        list[0].GetProperty("fieldCount").GetInt32().ShouldBe(3);
        list[0].GetProperty("owner").GetProperty("displayName").GetString().ShouldBe("安心商行管理者");
        foreach (var item in list.EnumerateArray())
        {
            OpenApiContract.AssertKeysMatchSchema(item, "DatabaseSummaryView");
        }

        (await ListAsync(admin2)).EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([admin2s]);
        (await ListAsync(await SignInAsync(org, "internal"))).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Another_organizations_or_another_owners_database_is_the_same_403_as_a_missing_one_and_reveals_nothing()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var adminA = await SignInAsync(orgA, "admin");
        var databaseId = await CreateDatabaseAsync(adminA, "template-customer-profile", "A 的客戶名單");

        await _host.CreateAccountAsync(orgA.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AllAdminPermissions);
        var orgB = await CreateOrganizationAsync("組織 B");

        var callers = new[]
        {
            ("another organization's admin", await SignInAsync(orgB, "admin")),
            ("same organization, every permission, not the owner", await SignInAsync(orgA, "admin2")),
            ("same organization, read-consented-submissions", await SignInAsync(orgA, "internal")),
        };

        foreach (var (who, caller) in callers)
        {
            var toSomeoneElses = await caller.Spa.GetAsync($"{BasePath}/{databaseId}", caller.Token);
            var toNonexistent = await caller.Spa.GetAsync($"{BasePath}/{Guid.NewGuid()}", caller.Token);

            toSomeoneElses.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            await AssertIdenticalAsync(toSomeoneElses, toNonexistent);
            var raw = await toSomeoneElses.Content.ReadAsStringAsync(CancellationToken);
            var body = JsonDocument.Parse(raw).RootElement;
            body.GetProperty("reason").GetString().ShouldBe("database", who);
            body.GetProperty("message").GetString().ShouldBe("你沒有這個資料庫的存取權限，或它已不存在。");
            raw.ShouldNotContain("A 的客戶名單");
            raw.ShouldNotContain("客戶姓名");

            (await ListAsync(caller)).EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldNotContain(databaseId);
        }

        // The owner still sees it unchanged.
        var detail = await BodyJsonAsync(await adminA.Spa.GetAsync($"{BasePath}/{databaseId}", adminA.Token));
        detail.GetProperty("summary").GetProperty("name").GetString().ShouldBe("A 的客戶名單");
    }

    [Fact]
    public async Task A_route_id_that_is_not_a_guid_never_reaches_the_endpoint()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        // The mock's ids (database-orders) are not GUIDs: the {id:guid} route does not match.
        (await admin.Spa.GetAsync($"{BasePath}/database-orders", admin.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // --- Helpers ---------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token);

    /// <summary>An organization with the seed's three accounts and initial permissions.</summary>
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

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler,
    /// which the fixture disposes.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string templateId, string name)
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { templateId, name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ListAsync(SignedIn viewer)
    {
        var response = await viewer.Spa.GetAsync(BasePath, viewer.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Status, content type, body bytes and cookies all equal.</summary>
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
