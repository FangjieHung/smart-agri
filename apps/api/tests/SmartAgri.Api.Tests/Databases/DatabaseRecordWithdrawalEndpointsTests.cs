using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>
/// Viewing records and withdrawing them (M4, issue #146) against real PostgreSQL: a submitter sees
/// and withdraws only their own submissions; withdrawal really deletes the content in one
/// transaction and keeps a content-free trail; withdrawing again (also concurrently) has one
/// consistent result; the timeline and every list or count exclude withdrawn submissions; only a
/// designated data manager holding the permission reads the timeline, and nobody crosses an
/// organization, database or subject boundary through any endpoint.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseRecordWithdrawalEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Database-Withdrawal-Pass-1!";
    private const string BasePath = "/api/v1/databases";
    private const string SubmissionsPath = "/api/v1/submissions";
    private const string WithdrawalForbiddenMessage = "找不到這筆紀錄，或你沒有撤回它的權限。";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private readonly AuthHostFixture _host;

    public DatabaseRecordWithdrawalEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static Dictionary<string, object> Answers(string name, string phone = "0912-345-678") => new()
    {
        ["field-customer-name"] = name,
        ["field-phone"] = phone,
        ["field-customer-type"] = "企業",
    };

    // --- The submitter's own records ---------------------------------------------------------

    [Fact]
    public async Task A_submitter_lists_only_their_own_submissions_including_withdrawn_trails()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var other = await CreateSecondCustomerAsync(org);

        var first = await SubmitAsync(customer, databaseId, Answers("王小明"));
        var second = await SubmitAsync(customer, databaseId, Answers("王小明", "0987-654-321"));
        await SubmitAsync(other, databaseId, Answers("李大華"));
        (await WithdrawAsync(customer, first)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await customer.Spa.GetAsync(SubmissionsPath, customer.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "DatabaseOwnSubmissionListView");
        var rows = body.GetProperty("submissions").EnumerateArray().ToList();
        rows.Select(row => row.GetProperty("id").GetGuid()).ShouldBe([second, first], "newest first, only the caller's");
        OpenApiContract.AssertKeysMatchSchema(rows[0], "DatabaseOwnSubmissionView");
        rows[0].GetProperty("withdrawnAt").ValueKind.ShouldBe(JsonValueKind.Null, "sent as null, not omitted");
        rows[1].GetProperty("withdrawnAt").GetDateTimeOffset().ShouldBeGreaterThan(rows[1].GetProperty("submittedAt").GetDateTimeOffset());
        rows[1].GetProperty("databaseName").GetString().ShouldBe("客戶資料庫");
        rows[1].GetProperty("source").GetString().ShouldBe("form-link");
        rows[1].GetProperty("receiptNumber").GetString()!.ShouldMatch(@"^R-\d{8}-[0-9A-F]{10}$");
        body.GetRawText().ShouldNotContain("王小明");
        body.GetRawText().ShouldNotContain("李大華");

        // The data manager has no submissions of their own; another organization sees none of these.
        (await BodyJsonAsync(await admin.Spa.GetAsync(SubmissionsPath, admin.Token))).GetProperty("submissions").GetArrayLength().ShouldBe(0);
        var orgB = await CreateOrganizationAsync("組織 B");
        var customerB = await SignInAsync(orgB, "customer");
        (await BodyJsonAsync(await customerB.Spa.GetAsync(SubmissionsPath, customerB.Token))).GetProperty("submissions").GetArrayLength().ShouldBe(0);

        (await _host.CreateSpaClient().Http.GetAsync(SubmissionsPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Nobody_but_the_submitter_can_read_or_withdraw_and_refusals_reveal_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var other = await CreateSecondCustomerAsync(org);
        var submissionId = await SubmitAsync(customer, databaseId, Answers("王小明"));
        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        var missing = await WithdrawAsync(customer, Guid.NewGuid());
        missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var missingBody = await BodyJsonAsync(missing);
        missingBody.GetProperty("reason").GetString().ShouldBe("submission-withdrawal");
        missingBody.GetProperty("message").GetString().ShouldBe(WithdrawalForbiddenMessage);

        foreach (var (who, caller) in new[]
                 {
                     ("another member of the organization", other),
                     ("the designated data manager", admin),
                     ("another organization's admin", adminB),
                 })
        {
            var refused = await WithdrawAsync(caller, submissionId);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            await AssertIdenticalAsync(refused, await WithdrawAsync(caller, Guid.NewGuid()));
            await AssertIdenticalAsync(refused, missing);
            (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("王小明");

            var receipt = await caller.Spa.GetAsync($"{SubmissionsPath}/{submissionId}", caller.Token);
            receipt.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            await AssertIdenticalAsync(receipt, await caller.Spa.GetAsync($"{SubmissionsPath}/{Guid.NewGuid()}", caller.Token));
        }

        (await customer.Spa.PostAsync($"{SubmissionsPath}/{submissionId}/withdrawal", null, new { })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        // Nothing was withdrawn by any of them.
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.SingleAsync(CancellationToken)).WithdrawnAt.ShouldBeNull();
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(4);
    }

    // --- Withdrawal ---------------------------------------------------------------------------

    [Fact]
    public async Task Withdrawal_deletes_every_value_and_snapshot_and_keeps_a_content_free_trail()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var kept = await SubmitAsync(customer, databaseId, Answers("保留的紀錄"));
        var submissionId = await SubmitAsync(customer, databaseId, Answers("要撤回的人", "0911-000-111"));
        var before = await BodyJsonAsync(await customer.Spa.GetAsync($"{SubmissionsPath}/{submissionId}", customer.Token));
        before.GetProperty("withdrawnAt").ValueKind.ShouldBe(JsonValueKind.Null);

        var response = await WithdrawAsync(customer, submissionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var receipt = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(receipt, "DatabaseSubmissionReceiptView");
        receipt.GetProperty("id").GetGuid().ShouldBe(submissionId);
        receipt.GetProperty("receiptNumber").GetString().ShouldBe(before.GetProperty("receiptNumber").GetString());
        receipt.GetProperty("submittedAt").GetDateTimeOffset().ShouldBe(before.GetProperty("submittedAt").GetDateTimeOffset());
        receipt.GetProperty("entries").GetArrayLength().ShouldBe(0);
        var withdrawnAt = receipt.GetProperty("withdrawnAt").GetDateTimeOffset();
        receipt.GetRawText().ShouldNotContain("要撤回的人");
        receipt.GetRawText().ShouldNotContain("0911-000-111");

        // Directly in the tables: the trail is there, no content row of it is.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var trail = await dbContext.DatabaseSubmissions.SingleAsync(s => s.Id == submissionId, CancellationToken);
            trail.WithdrawnAt.ShouldBe(withdrawnAt);
            trail.SubmittedByAccountId.ShouldBe(org.Customer.Id);
            trail.FormVersionNumber.ShouldBe(1);
            (await dbContext.DatabaseSubmissionEntries.CountAsync(e => e.SubmissionId == submissionId, CancellationToken)).ShouldBe(0);
            (await dbContext.DatabaseSubmissionEntries.CountAsync(e => e.SubmissionId == kept, CancellationToken)).ShouldBe(4);

            // No column of any remaining row holds what was filled in (raw SQL, no query filter).
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync(CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM "DatabaseSubmissionEntries" e
                WHERE e."SubmissionId" = @id OR e."Display" LIKE '%要撤回的人%' OR e."TextValue" LIKE '%0911-000-111%';
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = submissionId;
            command.Parameters.Add(parameter);
            Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
            await using var submissionRow = connection.CreateCommand();
            submissionRow.CommandText = """SELECT row_to_json(s)::text FROM "DatabaseSubmissions" s WHERE s."Id" = @id;""";
            var rowParameter = submissionRow.CreateParameter();
            rowParameter.ParameterName = "id";
            rowParameter.Value = submissionId;
            submissionRow.Parameters.Add(rowParameter);
            var rowJson = (string)(await submissionRow.ExecuteScalarAsync(CancellationToken))!;
            rowJson.ShouldNotContain("要撤回的人");
            rowJson.ShouldNotContain("0911");
        }

        // The receipt reads the same from now on; the submitter's list shows the trail.
        (await BodyJsonAsync(await customer.Spa.GetAsync($"{SubmissionsPath}/{submissionId}", customer.Token))).GetRawText()
            .ShouldBe(receipt.GetRawText());
    }

    [Fact]
    public async Task Withdrawing_again_and_concurrently_gives_one_consistent_result()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var once = await SubmitAsync(customer, databaseId, Answers("王小明"));
        var racing = await SubmitAsync(customer, databaseId, Answers("陳小美"));

        // Again: the same 200 and body, with the original time.
        var first = await BodyJsonAsync(await WithdrawAsync(customer, once));
        var again = await WithdrawAsync(customer, once);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(again)).GetRawText().ShouldBe(first.GetRawText());

        // Concurrently: all succeed with the same trail; exactly one withdrawal happened.
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => WithdrawAsync(customer, racing)));
        responses.Select(r => r.StatusCode).ShouldAllBe(status => status == HttpStatusCode.OK);
        var bodies = new HashSet<string>();
        foreach (var response in responses)
        {
            bodies.Add((await BodyJsonAsync(response)).GetRawText());
        }

        bodies.Count.ShouldBe(1);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.DatabaseSubmissions.CountAsync(s => s.WithdrawnAt != null, CancellationToken)).ShouldBe(2);
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Resending_a_withdrawn_submissions_key_returns_the_withdrawn_receipt_and_records_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var key = Guid.NewGuid();
        (await PostSubmissionAsync(customer, databaseId, key, 1, Answers("王小明"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        var withdrawn = await BodyJsonAsync(await WithdrawAsync(customer, await OnlySubmissionIdAsync(org)));

        // Same key, same content: the final state of that fill — withdrawn, no content.
        var replay = await PostSubmissionAsync(customer, databaseId, key, 1, Answers("王小明"));
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(replay)).GetRawText().ShouldBe(withdrawn.GetRawText());

        // Even with other content (it cannot be compared: it was deleted) nothing is recorded again.
        var other = await PostSubmissionAsync(customer, databaseId, key, 1, Answers("李大華"));
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(other)).GetProperty("entries").GetArrayLength().ShouldBe(0);

        // Another database with the same key is a conflict, as before.
        var otherDatabase = await CreateDatabaseAsync(admin, "另一個資料庫");
        var conflict = await PostSubmissionAsync(customer, otherDatabase, key, 1, Answers("王小明"));
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(conflict)).GetProperty("reason").GetString().ShouldBe("submission-key-reused");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(1);
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- The data manager's timeline ------------------------------------------------------------

    [Fact]
    public async Task The_timeline_separates_active_records_from_withdrawn_trails_and_lists_and_counts_exclude_them()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var other = await CreateSecondCustomerAsync(org);
        var older = await SubmitAsync(customer, databaseId, Answers("王小明"));
        var withdrawn = await SubmitAsync(customer, databaseId, Answers("撤回內容甲", "0911-000-111"));
        var otherOnly = await SubmitAsync(other, databaseId, Answers("撤回內容乙"));
        await WithdrawAsync(customer, withdrawn);
        await WithdrawAsync(other, otherOnly);

        var response = await admin.Spa.GetAsync($"{BasePath}/{databaseId}/tracking", admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tracking = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(tracking, "DatabaseTrackingView");
        tracking.GetProperty("databaseId").GetGuid().ShouldBe(databaseId);
        var subjects = tracking.GetProperty("subjects").EnumerateArray().ToList();

        // The member whose only submission was withdrawn is still a subject (latest activity first).
        subjects.Select(s => s.GetProperty("subject").GetProperty("id").GetGuid()).ShouldBe([other.AccountId, org.Customer.Id]);
        OpenApiContract.AssertKeysMatchSchema(subjects[0], "DatabaseTrackedSubjectView");
        subjects[0].GetProperty("records").GetArrayLength().ShouldBe(0);
        subjects[0].GetProperty("withdrawals").GetArrayLength().ShouldBe(1);

        var customerSubject = subjects[1];
        customerSubject.GetProperty("subject").GetProperty("displayName").GetString().ShouldBe("安心商行外部客戶");
        customerSubject.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([older]);
        customerSubject.GetProperty("records")[0].GetProperty("entries")[0].GetProperty("display").GetString().ShouldBe("王小明");
        var trail = customerSubject.GetProperty("withdrawals")[0];
        OpenApiContract.AssertKeysMatchSchema(trail, "DatabaseWithdrawnRecordView");
        trail.GetProperty("id").GetGuid().ShouldBe(withdrawn);
        trail.GetProperty("source").GetString().ShouldBe("form-link");
        trail.GetProperty("withdrawnAt").GetDateTimeOffset().ShouldBeGreaterThan(trail.GetProperty("submittedAt").GetDateTimeOffset());
        var raw = tracking.GetRawText();
        raw.ShouldNotContain("撤回內容甲");
        raw.ShouldNotContain("撤回內容乙");
        raw.ShouldNotContain("0911-000-111");

        // The records list and the shared active-record query count only the active one.
        var records = (await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}/records", admin.Token)))
            .GetProperty("records").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        records.ShouldBe([older]);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await DatabaseActiveRecords.Of(dbContext, databaseId).CountAsync(CancellationToken)).ShouldBe(1);
        (await DatabaseActiveRecords.EntriesOf(dbContext, DatabaseActiveRecords.Of(dbContext, databaseId)).CountAsync(CancellationToken)).ShouldBe(4);
    }

    [Fact]
    public async Task Only_a_designated_data_manager_with_the_permission_reads_a_timeline_and_never_across_databases_or_organizations()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var manager = await SignInAsync(org, "internal");
        var customer = await SignInAsync(org, "customer");
        var other = await CreateSecondCustomerAsync(org);
        var databaseA = await CreateDatabaseAsync(admin, "A 資料庫");
        var databaseB = await CreateDatabaseAsync(admin, "B 資料庫");
        await SubmitAsync(customer, databaseA, Answers("只在 A 的內容"));
        await SubmitAsync(other, databaseB, Answers("只在 B 的內容"));
        var trackingA = $"{BasePath}/{databaseA}/tracking";
        var missing = $"{BasePath}/{Guid.NewGuid()}/tracking";

        // Database boundary: B's timeline has B's subject only.
        var b = await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseB}/tracking", admin.Token));
        b.GetProperty("subjects").EnumerateArray().Select(s => s.GetProperty("subject").GetProperty("id").GetGuid()).ShouldBe([other.AccountId]);
        b.GetRawText().ShouldNotContain("只在 A 的內容");

        // Not designated (but permitted), and the submitter: the database is invisible.
        foreach (var (who, caller) in new[] { ("not designated", manager), ("the submitter", customer) })
        {
            var refused = await caller.Spa.GetAsync(trackingA, caller.Token);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            (await BodyJsonAsync(refused)).GetProperty("reason").GetString().ShouldBe("database", who);
            await AssertIdenticalAsync(refused, await caller.Spa.GetAsync(missing, caller.Token));
        }

        // Designated and permitted: reads it; permission revoked: refused on the next request.
        await PutAccessAsync(admin, databaseA, [org.Admin.Id, org.Internal.Id]);
        (await manager.Spa.GetAsync(trackingA, manager.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await manager.Spa.GetAsync($"{BasePath}/{databaseB}/tracking", manager.Token)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden, "designated for A only");
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        (await manager.Spa.GetAsync(trackingA, manager.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Designation removed: the owner still sees the database but no longer its timeline.
        await PutAccessAsync(admin, databaseA, [org.Internal.Id]);
        var owner = await admin.Spa.GetAsync(trackingA, admin.Token);
        owner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var ownerBody = await BodyJsonAsync(owner);
        ownerBody.GetProperty("reason").GetString().ShouldBe("database-records");
        ownerBody.GetRawText().ShouldNotContain("只在 A 的內容");

        // Another organization: the same 403 as a missing id.
        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");
        var foreign = await adminB.Spa.GetAsync(trackingA, adminB.Token);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(foreign, await adminB.Spa.GetAsync(missing, adminB.Token));

        (await _host.CreateSpaClient().Http.GetAsync(trackingA, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Helpers ---------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token, Guid AccountId);

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

    /// <summary>A second external customer of <paramref name="org"/>, signed in.</summary>
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

    private static Task<HttpResponseMessage> PostSubmissionAsync(
        SignedIn caller, Guid databaseId, Guid key, int formVersionNumber, object answers) =>
        caller.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", caller.Token, new
        {
            submissionId = key,
            formVersionNumber,
            consent = true,
            answers,
        });

    /// <summary>Submits with a new key and returns the submission's id.</summary>
    private static async Task<Guid> SubmitAsync(SignedIn caller, Guid databaseId, object answers)
    {
        var response = await PostSubmissionAsync(caller, databaseId, Guid.NewGuid(), 1, answers);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> WithdrawAsync(SignedIn caller, Guid submissionId) =>
        caller.Spa.PostAsync($"{SubmissionsPath}/{submissionId}/withdrawal", caller.Token, new { });

    private async Task<Guid> OnlySubmissionIdAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return (await dbContext.DatabaseSubmissions.SingleAsync(CancellationToken)).Id;
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
