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

    // --- Form editing (#143) -----------------------------------------------------------------

    private static object Field(
        string? id,
        string label,
        string type = "text",
        bool required = false,
        string[]? options = null,
        object? scale = null,
        string? unit = null) =>
        new { id, label, type, required, options = options ?? [], scale, unit = unit ?? string.Empty };

    private static object Form(int? baseVersionNumber, params object?[] fields) => new { baseVersionNumber, fields };

    private static string FormPath(Guid id) => $"{BasePath}/{id}/form";

    private async Task<List<DatabaseFormVersion>> VersionsAsync(TestOrganization org, Guid databaseId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.DatabaseFormVersions.Where(version => version.DatabaseId == databaseId)
            .OrderBy(version => version.VersionNumber).ToListAsync(CancellationToken);
    }

    [Fact]
    public async Task The_owner_saves_a_new_version_with_added_edited_reordered_and_removed_fields_and_the_old_version_stays_as_it_was()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-satisfaction", "滿意度");
        var original = JsonSerializer.Serialize(DatabaseTemplates.Get(DatabaseTemplateId.Satisfaction).Fields);

        // Template: field-overall-satisfaction (scale), field-liked-services (multiple-choice), field-suggestion (text).
        var response = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(
            1,
            Field("field-suggestion", "其他建議（改名）", required: true),
            Field("field-overall-satisfaction", "整體滿意度", "scale", true, scale: new { min = 0, max = 10, minLabel = "差", maxLabel = "好" }),
            Field(null, "購買金額", "number", unit: " 元 "),
            Field("field-visit-date", "到店日期", "date"),
            Field("field-channel", "來源", "single-choice", options: [" 網路 ", "", "門市"])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var form = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(form, "DatabaseFormView");
        form.GetProperty("versionNumber").GetInt32().ShouldBe(2);
        var fields = form.GetProperty("fields").EnumerateArray().ToList();
        fields.Select(field => field.GetProperty("label").GetString()).ShouldBe(
            ["其他建議（改名）", "整體滿意度", "購買金額", "到店日期", "來源"]);
        // Kept fields keep their ids across versions (even renamed and reordered); a new one is given an id.
        fields[0].GetProperty("id").GetString().ShouldBe("field-suggestion");
        fields[1].GetProperty("id").GetString().ShouldBe("field-overall-satisfaction");
        fields[2].GetProperty("id").GetString().ShouldStartWith("field-");
        fields[2].GetProperty("unit").GetString().ShouldBe("元");
        fields[4].GetProperty("options").EnumerateArray().Select(option => option.GetString()).ShouldBe(["網路", "門市"]);
        fields[4].GetProperty("scale").ValueKind.ShouldBe(JsonValueKind.Null);
        fields[1].GetProperty("scale").GetProperty("max").GetInt32().ShouldBe(10);

        var detail = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}", admin.Token));
        detail.GetProperty("summary").GetProperty("formVersion").GetInt32().ShouldBe(2);
        detail.GetProperty("summary").GetProperty("fieldCount").GetInt32().ShouldBe(5);
        detail.GetProperty("form").GetProperty("versionNumber").GetInt32().ShouldBe(2);
        (await ListAsync(admin))[0].GetProperty("fieldCount").GetInt32().ShouldBe(5);

        var versions = await VersionsAsync(org, databaseId);
        versions.Select(version => version.VersionNumber).ShouldBe([1, 2]);
        versions[0].CreatedByAccountId.ShouldBe(org.Admin.Id);
        versions[1].CreatedByAccountId.ShouldBe(org.Admin.Id);
        // Version 1 is exactly what the template wrote: a receipt that points at it keeps its field names.
        JsonSerializer.Serialize(versions[0].Fields).ShouldBe(original);
        versions[1].Fields.Select(field => field.Id).ShouldBe(fields.Select(field => field.GetProperty("id").GetString()!));

        // A third version changes the second again; versions 1 and 2 both stay as stored.
        var secondJson = JsonSerializer.Serialize(versions[1].Fields);
        var third = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(2, Field("field-suggestion", "建議")));
        third.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await VersionsAsync(org, databaseId);
        after.Select(version => version.VersionNumber).ShouldBe([1, 2, 3]);
        JsonSerializer.Serialize(after[0].Fields).ShouldBe(original);
        JsonSerializer.Serialize(after[1].Fields).ShouldBe(secondJson);
        after[2].Fields.Select(field => field.Id).ShouldBe(["field-suggestion"]);
    }

    [Fact]
    public async Task Saving_the_form_unchanged_returns_the_current_version_and_adds_none()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-blank", "空白");
        var current = (await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}", admin.Token))).GetProperty("form");

        var response = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, new
        {
            baseVersionNumber = 1,
            fields = current.GetProperty("fields").EnumerateArray().Select(field => (object)JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(field.GetRawText())!).ToList(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(response)).GetProperty("versionNumber").GetInt32().ShouldBe(1);
        (await VersionsAsync(org, databaseId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Bad_field_settings_are_422_with_a_key_that_locates_the_field_and_nothing_is_written()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-customer-profile", "客戶");

        var response = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(
            1,
            Field("field-customer-name", "姓名"),
            Field("field-b", "  "),
            Field("field-c", "姓名"),
            Field("field-d", "選擇", "single-choice", options: ["只有一個"]),
            Field("field-e", "量尺", "scale", scale: new { min = 3, max = 3, minLabel = "", maxLabel = "" }),
            Field("field-f", "太多刻度", "scale", scale: new { min = 0, max = 11, minLabel = "", maxLabel = "" }),
            Field("field-g", "類型不明", "radio"),
            Field("field-h", "重複選項", "multiple-choice", options: ["甲", "甲"]),
            null));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("message").GetString().ShouldBe("請填寫欄位名稱。");
        var errors = body.GetProperty("errors");
        string Error(string key) => errors.GetProperty(key).EnumerateArray().Single().GetString()!;
        Error("fields[1].label").ShouldBe("請填寫欄位名稱。");
        Error("fields[2].label").ShouldBe("欄位名稱不可重複。");
        Error("fields[3].options").ShouldBe("單選或多選至少需要 2 個選項。");
        Error("fields[4].scale").ShouldBe("量尺的最小值必須小於最大值。");
        Error("fields[5].scale").ShouldBe("量尺最多 11 個刻度。");
        Error("fields[6].type").ShouldBe("不支援的欄位類型。");
        Error("fields[7].options").ShouldBe("選項不可重複。");
        Error("fields[8]").ShouldBe("欄位內容不可為空。");
        errors.EnumerateObject().Select(property => property.Name).ShouldNotContain("fields[0].label");

        var noFields = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(1));
        noFields.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(noFields)).GetProperty("errors").GetProperty("fields").EnumerateArray().Single().GetString()
            .ShouldBe("表單至少需要一個欄位。");

        var noBase = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(null, Field("field-a", "名")));
        noBase.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(noBase)).GetProperty("errors").TryGetProperty("baseVersionNumber", out _).ShouldBeTrue();

        (await VersionsAsync(org, databaseId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_removed_fields_id_cannot_come_back_for_another_question_and_a_repeated_id_is_refused()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-customer-profile", "客戶");

        // Version 2 drops field-phone, which was in version 1.
        (await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(1, Field("field-customer-name", "姓名")))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var reuse = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(
            2, Field("field-customer-name", "姓名"), Field("field-phone", "完全不同的問題")));
        reuse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(reuse)).GetProperty("errors").GetProperty("fields[1].id").EnumerateArray().Single().GetString()
            .ShouldBe("這個欄位編號屬於先前已移除的欄位，請改用新的欄位。");

        var repeated = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(
            2, Field("field-customer-name", "姓名"), Field("field-customer-name", "姓名二")));
        repeated.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(repeated)).GetProperty("errors").TryGetProperty("fields[1].id", out _).ShouldBeTrue();

        (await VersionsAsync(org, databaseId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Saving_from_an_old_version_is_409_form_version_changed_and_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-blank", "空白");
        (await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(1, Field("field-a", "第二版")))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        // A second editor who still has version 1 open.
        var stale = await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(1, Field("field-a", "第二版（舊的人改的）")));

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await BodyJsonAsync(stale);
        body.GetProperty("reason").GetString().ShouldBe("form-version-changed");
        body.GetProperty("message").GetString()!.ShouldContain("重新載入");
        var versions = await VersionsAsync(org, databaseId);
        versions.Count.ShouldBe(2);
        versions[1].Fields.Single().Label.ShouldBe("第二版");

        // A version from the future is the same refusal.
        (await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(9, Field("field-a", "x")))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Two_saves_from_the_same_version_at_once_leave_exactly_one_new_version_and_one_409()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-blank", "空白");

        var responses = await Task.WhenAll(
            admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(1, Field("field-a", "甲的版本"))),
            admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(1, Field("field-a", "乙的版本"))));

        responses.Select(response => response.StatusCode).OrderBy(status => (int)status)
            .ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var versions = await VersionsAsync(org, databaseId);
        versions.Select(version => version.VersionNumber).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Only_the_owner_can_edit_or_try_the_form_everyone_else_gets_the_same_403_and_nothing_changes()
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
            var save = await caller.Spa.PutAsync(FormPath(databaseId), caller.Token, Form(1, Field("field-x", "被改掉的欄位")));
            var saveNonexistent = await caller.Spa.PutAsync(FormPath(Guid.NewGuid()), caller.Token, Form(1, Field("field-x", "被改掉的欄位")));
            var preview = await caller.Spa.PostAsync($"{FormPath(databaseId)}/preview", caller.Token, new { answers = new { } });
            var previewNonexistent = await caller.Spa.PostAsync($"{FormPath(Guid.NewGuid())}/preview", caller.Token, new { answers = new { } });

            save.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            await AssertIdenticalAsync(save, saveNonexistent);
            preview.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            await AssertIdenticalAsync(preview, previewNonexistent);
            var raw = await save.Content.ReadAsStringAsync(CancellationToken);
            raw.ShouldNotContain("A 的客戶名單");
            raw.ShouldNotContain("客戶姓名");
        }

        var versions = await VersionsAsync(orgA, databaseId);
        versions.Count.ShouldBe(1);
        versions[0].Fields.Select(field => field.Label).ShouldContain("客戶姓名");
    }

    [Fact]
    public async Task The_form_endpoints_need_a_signed_in_account()
    {
        var spa = _host.CreateSpaClient();

        (await spa.PutAsync(FormPath(Guid.NewGuid()), null, Form(1, Field("field-a", "名")))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await spa.PostAsync($"{FormPath(Guid.NewGuid())}/preview", null, new { answers = new { } })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_trial_fill_checks_the_current_form_returns_the_preview_and_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-satisfaction", "滿意度");
        var countsBefore = await RowCountsAsync(org);

        var ok = await admin.Spa.PostAsync($"{FormPath(databaseId)}/preview", admin.Token, new
        {
            answers = new Dictionary<string, object>
            {
                ["field-overall-satisfaction"] = "4",
                ["field-liked-services"] = new[] { "配送速度", "商品品質" },
                ["field-removed"] = "不在表單裡的欄位會被忽略",
            },
        });

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = await BodyJsonAsync(ok);
        OpenApiContract.AssertKeysMatchSchema(preview, "DatabaseTrialPreviewView");
        preview.GetProperty("saved").GetBoolean().ShouldBeFalse();
        preview.GetProperty("formVersion").GetInt32().ShouldBe(1);
        preview.GetProperty("entries").EnumerateArray()
            .Select(entry => (entry.GetProperty("fieldId").GetString(), entry.GetProperty("label").GetString(), entry.GetProperty("display").GetString()))
            .ShouldBe(
            [
                ("field-overall-satisfaction", "整體滿意度", "4 / 5"),
                ("field-liked-services", "喜歡的服務", "商品品質、配送速度"),
                ("field-suggestion", "其他建議", "未填寫"),
            ]);

        var bad = await admin.Spa.PostAsync($"{FormPath(databaseId)}/preview", admin.Token, new
        {
            answers = new Dictionary<string, object> { ["field-liked-services"] = new[] { "不存在的選項" } },
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(bad);
        body.GetProperty("message").GetString().ShouldBe("「整體滿意度」為必填。");
        var errors = body.GetProperty("errors");
        errors.GetProperty("answers.field-overall-satisfaction").EnumerateArray().Single().GetString().ShouldBe("「整體滿意度」為必填。");
        errors.GetProperty("answers.field-liked-services").EnumerateArray().Single().GetString().ShouldBe("「喜歡的服務」請從選項中選擇。");

        // Nothing sent at all is the same rule: every required field is missing.
        (await admin.Spa.PostAsync($"{FormPath(databaseId)}/preview", admin.Token, new { })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await RowCountsAsync(org)).ShouldBe(countsBefore);
    }

    [Fact]
    public async Task A_trial_fill_uses_the_form_as_last_saved()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "template-blank", "空白");
        var blankFieldId = (await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}", admin.Token)))
            .GetProperty("form").GetProperty("fields")[0].GetProperty("id").GetString()!;
        (await admin.Spa.PutAsync(FormPath(databaseId), admin.Token, Form(
            1, Field(blankFieldId, "備註"), Field("field-amount", "金額", "number", true, unit: "元")))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await admin.Spa.PostAsync($"{FormPath(databaseId)}/preview", admin.Token, new
        {
            answers = new Dictionary<string, object> { ["field-amount"] = "12345.5" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = await BodyJsonAsync(response);
        preview.GetProperty("formVersion").GetInt32().ShouldBe(2);
        preview.GetProperty("entries")[1].GetProperty("display").GetString().ShouldBe("12,345.5 元");
    }

    private async Task<(int Databases, int Versions)> RowCountsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return (await dbContext.Databases.CountAsync(CancellationToken), await dbContext.DatabaseFormVersions.CountAsync(CancellationToken));
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
