using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>
/// Consented submission and receipts (M4, issue #145) against real PostgreSQL: nothing is
/// recorded without consent, with invalid answers, for a stale form version or for anyone outside
/// the organization; a success records the content with a receipt whose snapshot never changes; a
/// retried or doubly sent submission is one record; only a designated data manager holding the
/// permission reads the content, and revoking either takes effect on the next request.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseSubmissionEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Database-Submission-Pass-1!";
    private const string BasePath = "/api/v1/databases";
    private const string ReceiptPath = "/api/v1/submissions";
    private const string FormForbiddenMessage = "你沒有填寫這份表單的權限，或它已不存在。";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private readonly AuthHostFixture _host;

    public DatabaseSubmissionEndpointsTests(AuthHostFixture host)
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

    // --- Before submitting ------------------------------------------------------------------

    [Fact]
    public async Task The_submission_form_shows_purpose_recipient_the_actual_readers_and_the_current_version()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");

        // The creator is the one data manager and holds the permission; the customer (designated
        // too) does not hold it, so is not an actual reader.
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Customer.Id]);

        var response = await customer.Spa.GetAsync($"{BasePath}/{databaseId}/submission-form", customer.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var form = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(form, "DatabaseSubmissionFormView");
        form.GetProperty("databaseId").GetGuid().ShouldBe(databaseId);
        form.GetProperty("databaseName").GetString().ShouldBe("客戶資料庫");
        form.GetProperty("purpose").GetString().ShouldBe("整理客戶聯絡方式與類型，方便後續服務。");
        form.GetProperty("recipient").GetString().ShouldBe("安心商行（客戶資料庫）");
        Strings(form.GetProperty("viewers")).ShouldBe(["安心商行管理者"]);
        form.GetProperty("sensitiveNotice").GetString()!.ShouldContain("敏感");
        form.GetProperty("withdrawalNotice").GetString()!.ShouldContain("撤回");
        form.GetProperty("form").GetProperty("versionNumber").GetInt32().ShouldBe(1);
        form.GetProperty("form").GetProperty("fields").GetArrayLength().ShouldBe(4);

        // Designating the internal employee (who holds the permission) adds a reader.
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);
        var again = await BodyJsonAsync(await customer.Spa.GetAsync($"{BasePath}/{databaseId}/submission-form", customer.Token));
        Strings(again.GetProperty("viewers")).ShouldBe(["安心商行管理者", "安心商行客服同仁"]);
    }

    [Fact]
    public async Task Reviewing_before_consent_validates_with_the_submission_rule_and_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var reviewPath = $"{BasePath}/{databaseId}/submission-form/review";

        var ok = await customer.Spa.PostAsync(reviewPath, customer.Token, new { formVersionNumber = 1, answers = ValidAnswers() });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = await BodyJsonAsync(ok);
        OpenApiContract.AssertKeysMatchSchema(preview, "DatabaseTrialPreviewView");
        preview.GetProperty("saved").GetBoolean().ShouldBeFalse();
        preview.GetProperty("entries")[3].GetProperty("display").GetString().ShouldBe("企業");

        var invalid = await customer.Spa.PostAsync(reviewPath, customer.Token, new { formVersionNumber = 1, answers = new { } });
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(invalid)).GetProperty("errors").TryGetProperty("answers.field-customer-name", out _).ShouldBeTrue();

        await SaveNewFormVersionAsync(admin, databaseId, "姓名");
        var stale = await customer.Spa.PostAsync(reviewPath, customer.Token, new { formVersionNumber = 1, answers = ValidAnswers() });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(stale)).GetProperty("reason").GetString().ShouldBe("form-version-changed");

        var refused = await admin.Spa.PostAsync(reviewPath, admin.Token, new { formVersionNumber = 2, answers = ValidAnswers() });
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(refused, await customer.Spa.PostAsync(
            $"{BasePath}/{Guid.NewGuid()}/submission-form/review", customer.Token, new { formVersionNumber = 1, answers = ValidAnswers() }));

        await AssertNothingRecordedAsync(org);
    }

    // --- Refusals record nothing ------------------------------------------------------------

    [Fact]
    public async Task Without_explicit_consent_nothing_is_recorded()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");

        foreach (var (why, consent) in new (string, object?)[] { ("consent false", false), ("consent missing", null) })
        {
            var response = await SubmitAsync(customer, databaseId, Guid.NewGuid(), 1, consent, ValidAnswers());

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, why);
            var body = await BodyJsonAsync(response);
            body.GetProperty("reason").GetString().ShouldBe("consent-required", why);
            body.GetProperty("message").GetString().ShouldBe("尚未同意，資料沒有送出。", why);
            body.GetProperty("errors").GetProperty("consent")[0].GetString().ShouldBe("請先勾選同意，才能送出資料。", why);
        }

        await AssertNothingRecordedAsync(org);
    }

    [Fact]
    public async Task Invalid_answers_are_reported_before_consent_and_record_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");

        var invalid = new Dictionary<string, object>
        {
            ["field-first-visit"] = "2026-02-30",
            ["field-customer-type"] = "政府",
        };

        // Not consented either: the field errors still come first, like the mock.
        foreach (var consent in new[] { true, false })
        {
            var response = await SubmitAsync(customer, databaseId, Guid.NewGuid(), 1, consent, invalid);

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var errors = (await BodyJsonAsync(response)).GetProperty("errors");
            errors.GetProperty("answers.field-customer-name")[0].GetString().ShouldBe("「客戶姓名」為必填。");
            errors.GetProperty("answers.field-first-visit")[0].GetString().ShouldBe("「首次來店日期」請輸入日期。");
            errors.GetProperty("answers.field-customer-type")[0].GetString().ShouldBe("「客戶類型」請從選項中選擇。");
            errors.TryGetProperty("consent", out _).ShouldBeFalse();
        }

        // The request's own members.
        var bare = await customer.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", customer.Token, new { consent = true, answers = ValidAnswers() });
        bare.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var bareErrors = (await BodyJsonAsync(bare)).GetProperty("errors");
        bareErrors.TryGetProperty("submissionId", out _).ShouldBeTrue();
        bareErrors.TryGetProperty("formVersionNumber", out _).ShouldBeTrue();

        await AssertNothingRecordedAsync(org);
    }

    [Fact]
    public async Task A_form_changed_since_it_was_loaded_is_409_and_records_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        await SaveNewFormVersionAsync(admin, databaseId, "姓名");

        var response = await SubmitAsync(customer, databaseId, Guid.NewGuid(), 1, true, ValidAnswers());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("form-version-changed");
        body.GetProperty("message").GetString()!.ShouldContain("重新載入");
        await AssertNothingRecordedAsync(org);

        // Reloading gives version 2, which submits.
        var form = await BodyJsonAsync(await customer.Spa.GetAsync($"{BasePath}/{databaseId}/submission-form", customer.Token));
        form.GetProperty("form").GetProperty("versionNumber").GetInt32().ShouldBe(2);
        (await SubmitAsync(customer, databaseId, Guid.NewGuid(), 2, true, ValidAnswers())).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Another_organizations_database_a_missing_one_and_a_caller_without_the_permission_get_the_same_403()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var adminA = await SignInAsync(orgA, "admin");
        var databaseId = await CreateDatabaseAsync(adminA, "A 的客戶名單");
        var orgB = await CreateOrganizationAsync("組織 B");
        var customerB = await SignInAsync(orgB, "customer");
        var internalA = await SignInAsync(orgA, "internal");

        var missingForm = await customerB.Spa.GetAsync($"{BasePath}/{Guid.NewGuid()}/submission-form", customerB.Token);
        var missingSubmit = await SubmitAsync(customerB, Guid.NewGuid(), Guid.NewGuid(), 1, true, ValidAnswers());
        missingForm.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(missingForm)).GetProperty("message").GetString().ShouldBe(FormForbiddenMessage);

        foreach (var (who, caller) in new[] { ("another organization's member", customerB), ("no submit-authorized-forms", internalA), ("the owner, without the permission", adminA) })
        {
            var form = await caller.Spa.GetAsync($"{BasePath}/{databaseId}/submission-form", caller.Token);
            form.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            await AssertIdenticalAsync(form, missingForm);
            (await form.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("A 的客戶名單");

            var submit = await SubmitAsync(caller, databaseId, Guid.NewGuid(), 1, true, ValidAnswers());
            submit.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            await AssertIdenticalAsync(submit, missingSubmit);
        }

        (await adminA.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", null, new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertNothingRecordedAsync(orgA);
        await AssertNothingRecordedAsync(orgB);
    }

    // --- Success, receipts and snapshots ----------------------------------------------------

    [Fact]
    public async Task A_consented_submission_records_typed_values_and_returns_a_receipt_with_the_snapshot()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var key = Guid.NewGuid();

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var response = await SubmitAsync(customer, databaseId, key, 1, true, new Dictionary<string, object>
        {
            ["field-customer-name"] = "  王小明 ",
            ["field-customer-type"] = "企業",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var receipt = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(receipt, "DatabaseSubmissionReceiptView");
        var submissionId = receipt.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"{ReceiptPath}/{submissionId}");
        receipt.GetProperty("receiptNumber").GetString()!.ShouldMatch(@"^R-\d{8}-[0-9A-F]{10}$");
        receipt.GetProperty("submittedAt").GetDateTimeOffset().ShouldBeInRange(before, DateTimeOffset.UtcNow.AddSeconds(1));
        receipt.GetProperty("databaseId").GetGuid().ShouldBe(databaseId);
        receipt.GetProperty("databaseName").GetString().ShouldBe("客戶資料庫");
        receipt.GetProperty("recipient").GetString().ShouldBe("安心商行（客戶資料庫）");
        Strings(receipt.GetProperty("viewers")).ShouldBe(["安心商行管理者"]);
        receipt.GetProperty("formVersionNumber").GetInt32().ShouldBe(1);
        receipt.GetProperty("source").GetString().ShouldBe("form-link");
        receipt.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("fieldId").GetString(), e.GetProperty("label").GetString(), e.GetProperty("type").GetString(), e.GetProperty("display").GetString()))
            .ShouldBe(
            [
                ("field-customer-name", "客戶姓名", "text", "王小明"),
                ("field-phone", "聯絡電話", "text", "未填寫"),
                ("field-first-visit", "首次來店日期", "date", "未填寫"),
                ("field-customer-type", "客戶類型", "single-choice", "企業"),
            ]);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var submission = await dbContext.DatabaseSubmissions.SingleAsync(CancellationToken);
            submission.Id.ShouldBe(submissionId);
            submission.SubmittedByAccountId.ShouldBe(org.Customer.Id);
            submission.IdempotencyKey.ShouldBe(key);
            submission.Source.ShouldBe(DatabaseSubmissionSource.FormLink);
            submission.WithdrawnAt.ShouldBeNull();
            submission.FormVersionId.ShouldBe(receipt.GetProperty("formVersionId").GetGuid());
            var entries = await dbContext.DatabaseSubmissionEntries.OrderBy(e => e.Position).ToListAsync(CancellationToken);
            entries.Select(e => (e.FieldId, e.TextValue)).ShouldBe(
            [
                ("field-customer-name", "王小明"),
                ("field-phone", null),
                ("field-first-visit", null),
                ("field-customer-type", "企業"),
            ]);
        }

        // The submitter reads the same receipt back; nobody else can, not even a data manager.
        var read = await customer.Spa.GetAsync($"{ReceiptPath}/{submissionId}", customer.Token);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(read)).GetRawText().ShouldBe(receipt.GetRawText());
        var byAdmin = await admin.Spa.GetAsync($"{ReceiptPath}/{submissionId}", admin.Token);
        var missing = await admin.Spa.GetAsync($"{ReceiptPath}/{Guid.NewGuid()}", admin.Token);
        byAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(byAdmin, missing);
    }

    [Fact]
    public async Task Numbers_scales_and_multiple_choices_are_stored_typed()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "滿意度", "template-satisfaction");
        var customer = await SignInAsync(org, "customer");

        var response = await SubmitAsync(customer, databaseId, Guid.NewGuid(), 1, true, new Dictionary<string, object>
        {
            ["field-overall-satisfaction"] = "4",
            ["field-liked-services"] = new[] { "配送速度", "商品品質" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var displays = (await BodyJsonAsync(response)).GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("display").GetString()).ToList();
        displays.ShouldBe(["4 / 5", "商品品質、配送速度", "未填寫"]);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var entries = await dbContext.DatabaseSubmissionEntries.OrderBy(e => e.Position).ToListAsync(CancellationToken);
        entries[0].NumberValue.ShouldBe(4);
        entries[0].FieldType.ShouldBe(DatabaseFieldType.Scale);
        entries[1].ChoiceValues.ShouldBe(["商品品質", "配送速度"], "in the form's option order");
        entries[2].ChoiceValues.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_receipt_and_the_record_read_the_same_after_the_form_changes()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");

        var receipt = await BodyJsonAsync(await SubmitAsync(customer, databaseId, Guid.NewGuid(), 1, true, ValidAnswers()));
        var submissionId = receipt.GetProperty("id").GetGuid();
        var recordBefore = (await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}/records", admin.Token)))
            .GetProperty("records")[0].GetRawText();

        // Rename the field, add one, and change who can read: none of it touches the snapshot.
        await SaveNewFormVersionAsync(admin, databaseId, "聯絡人姓名");
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);

        (await BodyJsonAsync(await customer.Spa.GetAsync($"{ReceiptPath}/{submissionId}", customer.Token))).GetRawText()
            .ShouldBe(receipt.GetRawText());
        (await BodyJsonAsync(await admin.Spa.GetAsync($"{BasePath}/{databaseId}/records", admin.Token)))
            .GetProperty("records")[0].GetRawText().ShouldBe(recordBefore);
        receipt.GetProperty("entries")[0].GetProperty("label").GetString().ShouldBe("客戶姓名");
    }

    // --- Idempotency ------------------------------------------------------------------------

    [Fact]
    public async Task Resending_the_same_key_returns_the_same_receipt_and_records_once()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var key = Guid.NewGuid();

        var first = await SubmitAsync(customer, databaseId, key, 1, true, ValidAnswers());
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var receipt = (await BodyJsonAsync(first)).GetRawText();

        var second = await SubmitAsync(customer, databaseId, key, 1, true, ValidAnswers());
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(second)).GetRawText().ShouldBe(receipt);

        // Still the original receipt after the form changed (the retry is not a new submission).
        await SaveNewFormVersionAsync(admin, databaseId, "姓名");
        var third = await SubmitAsync(customer, databaseId, key, 1, true, ValidAnswers());
        third.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(third)).GetRawText().ShouldBe(receipt);

        // The same key with other content is refused, and changes nothing.
        var other = await SubmitAsync(customer, databaseId, key, 1, true, ValidAnswers("李大華"));
        other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(other)).GetProperty("reason").GetString().ShouldBe("submission-key-reused");

        // Another member's key space is their own: the same key value is a new submission.
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.SubmitAuthorizedForms]);
        var internalEmployee = await SignInAsync(org, "internal");
        (await SubmitAsync(internalEmployee, databaseId, key, 2, true, new Dictionary<string, object>
        {
            ["field-customer-name"] = "王小明",
            ["field-customer-type"] = "企業",
        })).StatusCode.ShouldBe(HttpStatusCode.Created);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(s => s.SubmittedByAccountId == org.Customer.Id, CancellationToken)).ShouldBe(1);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(2);
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(4 + 5);
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_record_once_and_all_get_the_same_receipt()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => SubmitAsync(customer, databaseId, key, 1, true, ValidAnswers())));

        responses.Select(r => r.StatusCode).ShouldAllBe(status => status == HttpStatusCode.Created || status == HttpStatusCode.OK);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBeLessThanOrEqualTo(1);
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await BodyJsonAsync(response)).GetProperty("id").GetGuid());
        }

        ids.Count.ShouldBe(1);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(1);
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(4);
    }

    // --- Reading records --------------------------------------------------------------------

    [Fact]
    public async Task Only_a_designated_data_manager_holding_the_permission_reads_the_content_and_revoking_either_takes_effect()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var customer = await SignInAsync(org, "customer");
        var manager = await SignInAsync(org, "internal");
        (await SubmitAsync(customer, databaseId, Guid.NewGuid(), 1, true, ValidAnswers())).StatusCode.ShouldBe(HttpStatusCode.Created);
        var recordsPath = $"{BasePath}/{databaseId}/records";
        var missingPath = $"{BasePath}/{Guid.NewGuid()}/records";

        // The creator is designated and permitted.
        var list = await BodyJsonAsync(await admin.Spa.GetAsync(recordsPath, admin.Token));
        OpenApiContract.AssertKeysMatchSchema(list, "DatabaseRecordListView");
        var record = list.GetProperty("records")[0];
        record.GetProperty("submitter").GetProperty("displayName").GetString().ShouldBe("安心商行外部客戶");
        record.GetProperty("entries")[0].GetProperty("display").GetString().ShouldBe("王小明");

        // Permitted but not designated; the submitter themself: invisible database.
        foreach (var (who, caller) in new[] { ("not designated", manager), ("the submitter", customer) })
        {
            var refused = await caller.Spa.GetAsync(recordsPath, caller.Token);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            (await BodyJsonAsync(refused)).GetProperty("reason").GetString().ShouldBe("database", who);
            await AssertIdenticalAsync(refused, await caller.Spa.GetAsync(missingPath, caller.Token));
            (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("王小明");
        }

        // Designated: reads it.
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);
        (await manager.Spa.GetAsync(recordsPath, manager.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Permission revoked: the next request with the same token is refused.
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        (await manager.Spa.GetAsync(recordsPath, manager.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The owner who removes themselves still sees the database but no longer its records.
        await PutAccessAsync(admin, databaseId, [org.Internal.Id]);
        var owner = await admin.Spa.GetAsync(recordsPath, admin.Token);
        owner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await BodyJsonAsync(owner);
        body.GetProperty("reason").GetString().ShouldBe("database-records");
        body.GetRawText().ShouldNotContain("王小明");

        // Another organization: the same 403 as a missing id.
        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");
        var foreign = await adminB.Spa.GetAsync(recordsPath, adminB.Token);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(foreign, await adminB.Spa.GetAsync(missingPath, adminB.Token));
    }

    // --- Helpers ---------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token);

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

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name = "客戶資料庫", string templateId = "template-customer-profile")
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { templateId, name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SubmitAsync(
        SignedIn caller, Guid databaseId, Guid submissionId, int formVersionNumber, object? consent, object answers) =>
        caller.Spa.PostAsync($"{BasePath}/{databaseId}/submissions", caller.Token, new
        {
            submissionId,
            formVersionNumber,
            consent,
            answers,
        });

    private static Task<HttpResponseMessage> PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds) =>
        caller.Spa.PutAsync($"{BasePath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });

    /// <summary>Saves the next form version: the first field renamed to <paramref name="firstLabel"/>
    /// plus a new optional text field.</summary>
    private static async Task SaveNewFormVersionAsync(SignedIn owner, Guid databaseId, string firstLabel)
    {
        var detail = await BodyJsonAsync(await owner.Spa.GetAsync($"{BasePath}/{databaseId}", owner.Token));
        var form = detail.GetProperty("form");
        var fields = JsonNode.Parse(form.GetProperty("fields").GetRawText())!.AsArray();
        fields[0]!["label"] = firstLabel;
        fields.Add(new JsonObject { ["label"] = "備註", ["type"] = "text", ["required"] = false, ["options"] = new JsonArray(), ["unit"] = "" });
        var response = await owner.Spa.PutAsync($"{BasePath}/{databaseId}/form", owner.Token, new
        {
            baseVersionNumber = form.GetProperty("versionNumber").GetInt32(),
            fields,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task SetPermissionsAsync(SignedIn admin, Guid accountId, AccountPermission[] permissions)
    {
        var wire = permissions.Select(SmartAgri.Domain.WireNames<AccountPermission>.ToWire).ToArray();
        var response = await admin.Spa.PutAsync($"/api/v1/team/members/{accountId}/permissions", admin.Token, new { permissions = wire });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private async Task AssertNothingRecordedAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.DatabaseSubmissionEntries.CountAsync(CancellationToken)).ShouldBe(0);
    }

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString())];

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
