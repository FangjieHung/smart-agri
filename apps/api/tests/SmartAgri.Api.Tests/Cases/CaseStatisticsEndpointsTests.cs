using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Cases;

/// <summary>
/// 瓶頸統計 over HTTP against real PostgreSQL (M7 plan §3 F, §5 Slice M7-6, decision K; issue #251):
/// <c>GET /api/v1/cases/statistics</c> — the average handling time (creation → completion, only the cases
/// completed within the range, never a cancelled one, <c>null</c> without completions), a transferred case
/// counting for the group that completed it, the overdue count being M7-5's, the manager-only
/// <c>403 organization-settings</c>, a body without any case text, and every number equalling the list it
/// opens. The pure aggregation is <c>CaseStatisticsTests</c> (Application).
/// </summary>
/// <remarks>
/// Accounts as in <c>CaseAttentionEndpointsTests</c>: <c>admin</c> the manager, <c>internal</c> who creates
/// the cases, <c>member</c> 阿明 and <c>colleague</c> in 設備組, <c>other</c> in 採購組, <c>external</c> a
/// customer. Cases are created, accepted, completed and cancelled through the API; only the instants the
/// range depends on (<c>CreatedAt</c>, <c>CompletedAt</c>, <c>CancelledAt</c>, <c>DueAt</c>) are then moved
/// straight in the database, since the API stamps "now" and refuses a due time in the past (decision H).
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public class CaseStatisticsEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Case-Statistics-Pass-1!";
    private const string Path = "/api/v1/cases";
    private const string StatisticsPath = "/api/v1/cases/statistics";
    private const string GroupsPath = "/api/v1/case-groups";
    private const string TypesPath = "/api/v1/case-types";

    private const string SecretTitle = "冷藏庫溫度降不下來-機密標題";
    private const string SecretDescription = "二號冷藏庫的機密說明文字";
    private const string SecretResolution = "已更換壓縮機-機密處理結果";
    private const string SecretReason = "重複的案件-機密取消原因";
    private const string SecretNote = "請補上溫度紀錄照片-機密補件說明";

    private readonly AuthHostFixture _host;

    public CaseStatisticsEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_average_counts_only_cases_completed_within_the_range_from_creation_to_completion_and_never_a_cancelled_one()
    {
        var (org, setup, who) = await ArrangeAsync();
        var now = _host.Clock.GetUtcNow();
        var tenHours = await CompletedCaseAsync(org, who, setup);
        var twentyHours = await CompletedCaseAsync(org, who, setup);
        var completedLongAgo = await CompletedCaseAsync(org, who, setup);
        var cancelled = await CreateCaseAsync(who["internal"], setup);
        await ActAsync(org, who["member"], cancelled, "accept");
        await ActAsync(org, who["member"], cancelled, "cancel", new { reason = SecretReason });

        await MoveAsync(org, tenHours, createdAt: now.AddHours(-11), completedAt: now.AddHours(-1));
        await MoveAsync(org, twentyHours, createdAt: now.AddHours(-22), completedAt: now.AddHours(-2));
        await MoveAsync(org, completedLongAgo, createdAt: now.AddDays(-41), completedAt: now.AddDays(-40));
        // A cancelled case created long ago: if it counted, the average would jump.
        await MoveAsync(org, cancelled, createdAt: now.AddDays(-20), cancelledAt: now.AddHours(-3));

        var statistics = await StatisticsAsync(who["admin"]);
        var row = statistics.Rows.ShouldHaveSingleItem();
        (row.TypeName, row.GroupName).ShouldBe(("設備故障報修", "設備組"));
        row.CompletedCount.ShouldBe(2, "the case completed 40 days ago is outside the default 30 days");
        row.CancelledCount.ShouldBe(1);
        row.AverageHandlingHours.ShouldNotBeNull().ShouldBe(15, tolerance: 1e-6);
        row.OpenCount.ShouldBe(0);

        // A range holding only the old completion averages that one alone; a range with none is null.
        var old = DateOnly.FromDateTime(now.AddDays(-40).UtcDateTime);
        var oldRow = (await StatisticsAsync(who["admin"], $"?from={old:yyyy-MM-dd}&to={old:yyyy-MM-dd}")).Rows.ShouldHaveSingleItem();
        (oldRow.CompletedCount, oldRow.CancelledCount).ShouldBe((1, 0));
        oldRow.AverageHandlingHours.ShouldNotBeNull().ShouldBe(24, tolerance: 1e-6);

        await CreateCaseAsync(who["internal"], setup, title: "未結案");
        var empty = DateOnly.FromDateTime(now.AddDays(-100).UtcDateTime);
        var openOnly = (await StatisticsAsync(who["admin"], $"?from={empty:yyyy-MM-dd}&to={empty:yyyy-MM-dd}")).Rows.ShouldHaveSingleItem();
        (openOnly.OpenCount, openOnly.CompletedCount, openOnly.CancelledCount).ShouldBe((1, 0, 0));
        openOnly.AverageHandlingHours.ShouldBeNull("no completion within the range: the screen shows 「—」");
    }

    [Fact]
    public async Task The_range_is_whole_UTC_days_and_an_invalid_one_is_422()
    {
        var (org, setup, who) = await ArrangeAsync();
        var day = DateOnly.FromDateTime(_host.Clock.GetUtcNow().AddDays(-10).UtcDateTime);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var firstInstant = await CompletedCaseAsync(org, who, setup);
        var nextDay = await CompletedCaseAsync(org, who, setup);
        await MoveAsync(org, firstInstant, createdAt: start.AddHours(-6), completedAt: start);
        await MoveAsync(org, nextDay, createdAt: start.AddHours(-6), completedAt: start.AddDays(1));

        var statistics = await StatisticsAsync(who["admin"], $"?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        (statistics.From, statistics.To).ShouldBe((day, day));
        var row = statistics.Rows.ShouldHaveSingleItem();
        row.CompletedCount.ShouldBe(1, "00:00 UTC belongs to the day, 00:00 of the next day does not");
        row.AverageHandlingHours.ShouldNotBeNull().ShouldBe(6, tolerance: 1e-6);

        foreach (var query in new[] { $"?from={day.AddDays(1):yyyy-MM-dd}&to={day:yyyy-MM-dd}", $"?from={day.AddDays(-180):yyyy-MM-dd}&to={day:yyyy-MM-dd}" })
        {
            var response = await who["admin"].Spa.GetAsync(StatisticsPath + query, who["admin"].Token);
            var raw = await response.Content.ReadAsStringAsync(CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, raw);
            JsonDocument.Parse(raw).RootElement.GetProperty("reason").GetString().ShouldBe("invalid-date-range");
        }

        // The default is the newest 30 days, inclusive of today (UTC).
        var today = DateOnly.FromDateTime(_host.Clock.GetUtcNow().UtcDateTime);
        var defaults = await StatisticsAsync(who["admin"]);
        (defaults.From, defaults.To).ShouldBe((today.AddDays(-29), today));
    }

    [Fact]
    public async Task A_transferred_case_counts_for_the_group_that_completed_it()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await ActAsync(org, who["member"], caseId, "accept");
        await ActAsync(org, who["member"], caseId, "transfer", new { groupId = setup.Purchasing, note = "請採購組詢價" });
        await ActAsync(org, who["other"], caseId, "accept");
        await ActAsync(org, who["other"], caseId, "complete", new { resolution = SecretResolution });

        var row = (await StatisticsAsync(who["admin"])).Rows.ShouldHaveSingleItem("設備組 no longer has it: no row of its own");
        (row.GroupId, row.GroupName, row.CompletedCount).ShouldBe((setup.Purchasing, "採購組", 1));
    }

    [Fact]
    public async Task The_overdue_count_is_M7_5s_and_every_number_equals_the_list_it_opens()
    {
        var (org, setup, who) = await ArrangeAsync();
        var now = _host.Clock.GetUtcNow();
        var pendingOverdue = await CreateCaseAsync(who["internal"], setup, title: "待受理、逾期");
        await CreateCaseAsync(who["internal"], setup, title: "待受理、未逾期");
        var awaitingOverdue = await CreateCaseAsync(who["internal"], setup, title: "待補件、逾期");
        await ActAsync(org, who["member"], awaitingOverdue, "accept");
        await ActAsync(org, who["member"], awaitingOverdue, "request-info", new { note = SecretNote });
        var completedOverdue = await CompletedCaseAsync(org, who, setup);
        var cancelledOverdue = await CreateCaseAsync(who["internal"], setup, title: "已取消、逾期");
        await ActAsync(org, who["internal"], cancelledOverdue, "cancel");
        var purchasingOpen = await CreateCaseAsync(who["internal"], setup, group: setup.Purchasing, title: "採購組未結案");
        foreach (var id in new[] { pendingOverdue, awaitingOverdue, completedOverdue, cancelledOverdue })
        {
            await MoveAsync(org, id, dueAt: now.AddHours(-1));
        }

        await MoveAsync(org, completedOverdue, createdAt: now.AddHours(-8), completedAt: now.AddHours(-4));

        var statistics = await StatisticsAsync(who["admin"]);
        statistics.Rows.Select(row => row.GroupName).ShouldBe(["採購組", "設備組"], "by type name, then group name");
        var equipment = statistics.Rows.Single(row => row.GroupId == setup.Equipment);
        (equipment.OpenCount, equipment.OverdueCount, equipment.CompletedCount, equipment.CancelledCount).ShouldBe((3, 2, 1, 1));
        equipment.AverageHandlingHours.ShouldNotBeNull().ShouldBe(4, tolerance: 1e-6);
        var purchasing = statistics.Rows.Single(row => row.GroupId == setup.Purchasing);
        (purchasing.OpenCount, purchasing.OverdueCount, purchasing.CompletedCount, purchasing.CancelledCount).ShouldBe((1, 0, 0, 0));
        purchasing.AverageHandlingHours.ShouldBeNull();

        // M7-5's own list filter gives the same overdue cases (a closed case is never overdue).
        (await ListedIdsAsync(who["admin"], $"{Path}?status=all&overdue=true")).ShouldBe([awaitingOverdue, pendingOverdue], ignoreOrder: true);

        var range = $"closedFrom={statistics.From:yyyy-MM-dd}&closedTo={statistics.To:yyyy-MM-dd}";
        foreach (var row in statistics.Rows)
        {
            var filter = $"typeId={row.TypeId}&groupId={row.GroupId}";
            (await ListedIdsAsync(who["admin"], $"{Path}?status=open&{filter}")).Count.ShouldBe(row.OpenCount, row.GroupName);
            (await ListedIdsAsync(who["admin"], $"{Path}?status=open&overdue=true&{filter}")).Count.ShouldBe(row.OverdueCount, row.GroupName);
            (await ListedIdsAsync(who["admin"], $"{Path}?status=completed&{range}&{filter}")).Count.ShouldBe(row.CompletedCount, row.GroupName);
            (await ListedIdsAsync(who["admin"], $"{Path}?status=cancelled&{range}&{filter}")).Count.ShouldBe(row.CancelledCount, row.GroupName);
        }

        (await ListedIdsAsync(who["admin"], $"{Path}?status=completed&{range}")).ShouldBe([completedOverdue]);
        (await ListedIdsAsync(who["admin"], $"{Path}?status=closed&{range}")).ShouldBe([cancelledOverdue, completedOverdue], ignoreOrder: true);
        var before = statistics.From.AddDays(-1);
        (await ListedIdsAsync(who["admin"], $"{Path}?status=closed&closedFrom={before:yyyy-MM-dd}&closedTo={before:yyyy-MM-dd}")).ShouldBeEmpty();
        (await ListedIdsAsync(who["admin"], $"{Path}?status=all&{range}")).ShouldNotContain(purchasingOpen, "an open case was not closed in any range");

        var invalid = await who["admin"].Spa.GetAsync($"{Path}?closedFrom={statistics.To:yyyy-MM-dd}&closedTo={statistics.From:yyyy-MM-dd}", who["admin"].Token);
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("reason").GetString()
            .ShouldBe("invalid-date-range");
    }

    [Fact]
    public async Task Only_the_manager_gets_the_statistics_without_any_case_text_and_only_of_their_own_organization()
    {
        var (org, setup, who) = await ArrangeAsync();
        var completed = await CreateCaseAsync(who["internal"], setup, title: SecretTitle, description: SecretDescription);
        await ActAsync(org, who["member"], completed, "accept");
        await ActAsync(org, who["member"], completed, "comment", new { note = SecretNote });
        await ActAsync(org, who["member"], completed, "complete", new { resolution = SecretResolution });
        var cancelled = await CreateCaseAsync(who["internal"], setup, title: SecretTitle, description: SecretDescription);
        await ActAsync(org, who["member"], cancelled, "accept");
        await ActAsync(org, who["member"], cancelled, "cancel", new { reason = SecretReason });
        await CreateCaseAsync(who["internal"], setup, title: SecretTitle, description: SecretDescription);

        var (_, otherSetup, otherWho) = await ArrangeAsync("別家商行");
        await CreateCaseAsync(otherWho["internal"], otherSetup);
        await CreateCaseAsync(otherWho["internal"], otherSetup);

        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.OrganizationSettings));
        foreach (var login in new[] { "internal", "member", "other", "external" })
        {
            var response = await who[login].Spa.GetAsync(StatisticsPath, who[login].Token);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, login);
            (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, login);
            JsonDocument.Parse(expected.Body).RootElement.GetProperty("reason").GetString().ShouldBe("organization-settings");

            // Even an invalid range: the manager check comes first.
            (await who[login].Spa.GetAsync($"{StatisticsPath}?from=2026-10-07&to=2026-01-01", who[login].Token)).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden, login);
        }

        (await _host.CreateSpaClient().Http.GetAsync(StatisticsPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var ok = await who["admin"].Spa.GetAsync(StatisticsPath, who["admin"].Token);
        var raw = await ok.Content.ReadAsStringAsync(CancellationToken);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        foreach (var secret in new[] { SecretTitle, SecretDescription, SecretResolution, SecretReason, SecretNote, "冷藏庫", "壓縮機" })
        {
            raw.ShouldNotContain(secret);
        }

        var body = JsonDocument.Parse(raw).RootElement;
        OpenApiContract.AssertKeysMatchSchema(body, "CaseStatisticsView");
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["from", "to", "rows"]);
        var row = body.GetProperty("rows").EnumerateArray().ShouldHaveSingleItem("only this organization's cases");
        row.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["type", "group", "openCount", "overdueCount", "completedCount", "cancelledCount", "averageHandlingHours"]);
        row.GetProperty("type").EnumerateObject().Select(property => property.Name).ShouldBe(["id", "name"]);
        row.GetProperty("group").EnumerateObject().Select(property => property.Name).ShouldBe(["id", "name", "archived"]);
        (row.GetProperty("openCount").GetInt32(), row.GetProperty("completedCount").GetInt32(), row.GetProperty("cancelledCount").GetInt32())
            .ShouldBe((1, 1, 1));

        var otherRow = (await StatisticsAsync(otherWho["admin"])).Rows.ShouldHaveSingleItem();
        (otherRow.TypeId, otherRow.OpenCount).ShouldBe((otherSetup.Type, 2));
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private sealed record Statistics(DateOnly From, DateOnly To, IReadOnlyList<Row> Rows);

    private sealed record Row(
        Guid TypeId, string TypeName, Guid GroupId, string GroupName,
        int OpenCount, int OverdueCount, int CompletedCount, int CancelledCount, double? AverageHandlingHours);

    private sealed record TestOrganization(Organization Organization, Account Member, Account Colleague, Account Other);

    private sealed record Setup(Guid Equipment, Guid Purchasing, Guid Type);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<(TestOrganization Org, Setup Setup, Dictionary<string, SignedIn> Who)> ArrangeAsync(string name = "統計商行")
    {
        var org = await CreateOrganizationAsync(name);
        var setup = await SetUpAsync(org);
        var who = new Dictionary<string, SignedIn>();
        foreach (var login in new[] { "admin", "internal", "member", "colleague", "other", "external" })
        {
            who[login] = await SignInAsync(org, login);
        }

        return (org, setup, who);
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name)
    {
        var organization = await _host.CreateOrganizationAsync(name);
        await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AccountPermission.ManageAssistants);
        await _host.CreateAccountAsync(organization, "internal", Password, AccountRole.InternalEmployee, $"{name}同仁", AccountPermission.UseSharedAssistants);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}阿明", AccountPermission.UseSharedAssistants);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, $"{name}設備組同事", AccountPermission.UseSharedAssistants);
        var other = await _host.CreateAccountAsync(
            organization, "other", Password, AccountRole.InternalEmployee, $"{name}採購組成員", AccountPermission.UseSharedAssistants);
        await _host.CreateAccountAsync(organization, "external", Password, AccountRole.ExternalCustomer, $"{name}客戶", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, member, colleague, other);
    }

    /// <summary>設備組 (阿明, colleague), 採購組 (other), and the type 設備故障報修 defaulting to 設備組, 72 hours.</summary>
    private async Task<Setup> SetUpAsync(TestOrganization org)
    {
        var admin = await SignInAsync(org, "admin");
        var equipment = await CreateGroupAsync(admin, "設備組");
        var purchasing = await CreateGroupAsync(admin, "採購組");
        (await admin.Spa.PutAsync($"{GroupsPath}/{equipment}/members", admin.Token, new { accountIds = new[] { org.Member.Id, org.Colleague.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.PutAsync($"{GroupsPath}/{purchasing}/members", admin.Token, new { accountIds = new[] { org.Other.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var type = await admin.Spa.PostAsync(TypesPath, admin.Token, new
        {
            name = "設備故障報修", description = "", defaultGroupId = equipment, defaultDueHours = 72, isActive = true,
        });
        type.StatusCode.ShouldBe(HttpStatusCode.Created, await type.Content.ReadAsStringAsync(CancellationToken));
        return new Setup(equipment, purchasing, (await BodyJsonAsync(type)).GetProperty("id").GetGuid());
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateGroupAsync(SignedIn admin, string name)
    {
        var response = await admin.Spa.PostAsync(GroupsPath, admin.Token, new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>A case in 設備組 (or <paramref name="group"/>), due in 72 hours.</summary>
    private async Task<Guid> CreateCaseAsync(
        SignedIn caller, Setup setup, Guid? group = null, string title = "冷藏庫溫度降不下來", string description = "說明")
    {
        var response = await caller.Spa.PostAsync(Path, caller.Token, new
        {
            typeId = setup.Type,
            groupId = group ?? setup.Equipment,
            dueAt = _host.Clock.GetUtcNow().AddHours(72),
            title,
            description,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("case").GetProperty("id").GetGuid();
    }

    /// <summary>A case in 設備組 that 阿明 accepted and completed.</summary>
    private async Task<Guid> CompletedCaseAsync(TestOrganization org, Dictionary<string, SignedIn> who, Setup setup)
    {
        var caseId = await CreateCaseAsync(who["internal"], setup, title: "已完成");
        await ActAsync(org, who["member"], caseId, "accept");
        await ActAsync(org, who["member"], caseId, "complete", new { resolution = SecretResolution });
        return caseId;
    }

    /// <summary>Moves the instants the statistics read, straight in the database (the API stamps "now").</summary>
    private async Task MoveAsync(
        TestOrganization org,
        Guid caseId,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? cancelledAt = null,
        DateTimeOffset? dueAt = null)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var item = await dbContext.Cases.AsNoTracking().SingleAsync(candidate => candidate.Id == caseId, CancellationToken);
        var created = createdAt ?? item.CreatedAt;
        var completed = completedAt ?? item.CompletedAt;
        var cancelled = cancelledAt ?? item.CancelledAt;
        var due = dueAt ?? item.DueAt;
        (await dbContext.Cases.Where(candidate => candidate.Id == caseId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.CreatedAt, created)
                        .SetProperty(candidate => candidate.CompletedAt, completed)
                        .SetProperty(candidate => candidate.CancelledAt, cancelled)
                        .SetProperty(candidate => candidate.DueAt, due),
                    CancellationToken))
            .ShouldBe(1);
    }

    /// <summary>Posts <paramref name="action"/> with the case's current <c>eventCount</c>; it must succeed.</summary>
    private async Task ActAsync(TestOrganization org, SignedIn caller, Guid caseId, string action, object? fields = null)
    {
        int eventCount;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            eventCount = (await dbContext.Cases.AsNoTracking().SingleAsync(item => item.Id == caseId, CancellationToken)).EventCount;
        }

        var body = new Dictionary<string, object?> { ["eventCount"] = eventCount };
        if (fields is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(fields).EnumerateObject())
            {
                body[property.Name] = property.Value.Clone();
            }
        }

        var path = action == "comment" ? $"{Path}/{caseId}/comments" : $"{Path}/{caseId}:{action}";
        var response = await caller.Spa.PostAsync(path, caller.Token, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<Statistics> StatisticsAsync(SignedIn caller, string query = "")
    {
        var response = await caller.Spa.GetAsync(StatisticsPath + query, caller.Token);
        var raw = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        var body = JsonDocument.Parse(raw).RootElement;
        OpenApiContract.AssertKeysMatchSchema(body, "CaseStatisticsView");
        return new Statistics(
            DateOnly.Parse(body.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            DateOnly.Parse(body.GetProperty("to").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            [
                .. body.GetProperty("rows").EnumerateArray().Select(row => new Row(
                    row.GetProperty("type").GetProperty("id").GetGuid(),
                    row.GetProperty("type").GetProperty("name").GetString()!,
                    row.GetProperty("group").GetProperty("id").GetGuid(),
                    row.GetProperty("group").GetProperty("name").GetString()!,
                    row.GetProperty("openCount").GetInt32(),
                    row.GetProperty("overdueCount").GetInt32(),
                    row.GetProperty("completedCount").GetInt32(),
                    row.GetProperty("cancelledCount").GetInt32(),
                    row.GetProperty("averageHandlingHours").ValueKind == JsonValueKind.Null
                        ? null
                        : row.GetProperty("averageHandlingHours").GetDouble())),
            ]);
    }

    private static async Task<List<Guid>> ListedIdsAsync(SignedIn caller, string path)
    {
        var response = await caller.Spa.GetAsync(path, caller.Token);
        var raw = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        return [.. JsonDocument.Parse(raw).RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetGuid())];
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
