using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Tenancy;
using CaseEntity = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Api.Tests.Cases;

/// <summary>
/// 送出後自動開案 (M7 plan §3 I, §5 Slice M7-10; issue #255) against real PostgreSQL: every entry point
/// (the form link for an internal account and for an external customer, an assistant's form request)
/// opens exactly one case in the record's own save; a retry, concurrent requests with one key and the
/// loser of a same-key race open nothing more; a refused submission opens nothing; the case has no
/// creator, holds nothing that was submitted and stays hidden from the submitter; a withdrawn record
/// reads 「紀錄已撤回」 and the case still flows; only the manager sets it, only to an active type; and a
/// type in use cannot be deactivated.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseAutoCaseTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Auto-Case-Pass-1!";
    private const string CasesPath = "/api/v1/cases";
    private const string GroupsPath = "/api/v1/case-groups";
    private const string TypesPath = "/api/v1/case-types";
    private const string DatabasesPath = "/api/v1/databases";
    private const string DatabaseName = "客戶資料庫";
    private const string ExpectedTitle = "客戶資料庫：新紀錄";
    private const string ExpectedDescription = "由數據庫送出自動建立，內容請開啟紀錄查看。";

    /// <summary>What a member submits; none of it may ever appear in a case.</summary>
    private static readonly string[] SubmittedValues = ["王小明", "0912-345-678", "2026-09-21", "企業"];

    private readonly AuthHostFixture _host;

    public DatabaseAutoCaseTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static Dictionary<string, object> ValidAnswers() => new()
    {
        ["field-customer-name"] = SubmittedValues[0],
        ["field-phone"] = SubmittedValues[1],
        ["field-first-visit"] = SubmittedValues[2],
        ["field-customer-type"] = SubmittedValues[3],
    };

    // --- Opening cases ---------------------------------------------------------------------------

    [Fact]
    public async Task Every_entry_point_opens_one_case_without_a_creator_and_without_anything_submitted()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var clerk = await SignInAsync(org, "clerk");
        var external = await SignInAsync(org, "external");
        var member = await SignInAsync(org, "member");
        var setup = await SetUpAsync(org, admin);
        var assistantId = await CreateFormAssistantAsync(org, admin, setup.Database);

        // Off: a submission opens no case.
        (await SubmitFormAsync(clerk, setup.Database, Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CasesAsync(org)).ShouldBeEmpty();

        await SetAutoCaseAsync(admin, setup.Database, setup.Type);
        var internalKey = Guid.NewGuid();
        var byClerk = await ReceiptIdAsync(await SubmitFormAsync(clerk, setup.Database, internalKey), HttpStatusCode.Created);
        var byExternal = await ReceiptIdAsync(await SubmitFormAsync(external, setup.Database, Guid.NewGuid()), HttpStatusCode.Created);
        var chatKey = Guid.NewGuid();
        var chat = await SubmitChatFormAsync(clerk, assistantId, setup.Database, chatKey);
        chat.StatusCode.ShouldBe(HttpStatusCode.Created, await chat.Content.ReadAsStringAsync(CancellationToken));
        var byChat = (await BodyJsonAsync(chat)).GetProperty("receipt").GetProperty("id").GetGuid();

        // Retries of the form link and of the chat form: the same receipt, no second case.
        (await ReceiptIdAsync(await SubmitFormAsync(clerk, setup.Database, internalKey), HttpStatusCode.OK)).ShouldBe(byClerk);
        (await SubmitChatFormAsync(clerk, assistantId, setup.Database, chatKey)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var cases = await CasesAsync(org);
        cases.Select(item => item.SubmissionId).ShouldBe([byClerk, byExternal, byChat], ignoreOrder: true);
        foreach (var item in cases)
        {
            (item.Origin, item.CreatedByAccountId, item.DatabaseId, item.TypeId, item.GroupId, item.Status)
                .ShouldBe((CaseOrigin.DatabaseSubmission, (Guid?)null, (Guid?)setup.Database, setup.Type, setup.Equipment, CaseStatus.Pending));
            (item.Title, item.Description).ShouldBe((ExpectedTitle, ExpectedDescription));
            item.DueAt.ShouldBe(item.CreatedAt.AddHours(72), "the type's default handling time");
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var events = await dbContext.CaseEvents.AsNoTracking().ToListAsync(CancellationToken);
            events.Count.ShouldBe(3);
            events.ShouldAllBe(caseEvent => caseEvent.Action == CaseEventAction.Created && caseEvent.ActorAccountId == null && caseEvent.Ordinal == 1);
            var submittedAt = await dbContext.DatabaseSubmissions.AsNoTracking()
                .ToDictionaryAsync(submission => submission.Id, submission => submission.SubmittedAt, CancellationToken);
            cases.ShouldAllBe(item => item.CreatedAt == submittedAt[item.SubmissionId!.Value], "the case is written in the record's save");
        }

        // The raw JSON a group member and the manager get carries nothing that was submitted.
        foreach (var (who, caller) in new[] { ("group member", member), ("manager", admin) })
        {
            foreach (var item in cases)
            {
                var raw = await DetailRawAsync(caller, item.Id);
                foreach (var value in SubmittedValues)
                {
                    raw.ShouldNotContain(value, Shouldly.Case.Sensitive, who);
                }

                var body = JsonDocument.Parse(raw).RootElement;
                OpenApiContract.AssertKeysMatchSchema(body.GetProperty("links").GetProperty("record"), "CaseRecordLinkView");
                var view = body.GetProperty("case");
                (view.GetProperty("origin").GetString(), view.GetProperty("createdBy").ValueKind, view.GetProperty("title").GetString())
                    .ShouldBe(("database-submission", JsonValueKind.Null, ExpectedTitle), who);
                body.GetProperty("events")[0].GetProperty("actor").ValueKind.ShouldBe(JsonValueKind.Null, who);
                var record = body.GetProperty("links").GetProperty("record");
                (record.GetProperty("submissionId").GetGuid(), record.GetProperty("state").GetString(), record.GetProperty("databaseName").GetString())
                    .ShouldBe((item.SubmissionId!.Value, "available", DatabaseName), who);
            }
        }

        var list = await member.Spa.GetAsync(CasesPath, member.Token);
        var listRaw = await list.Content.ReadAsStringAsync(CancellationToken);
        foreach (var value in SubmittedValues)
        {
            listRaw.ShouldNotContain(value);
        }
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_open_one_case()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var external = await SignInAsync(org, "external");
        var setup = await SetUpAsync(org, admin);
        await SetAutoCaseAsync(admin, setup.Database, setup.Type);
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SubmitFormAsync(external, setup.Database, key)));

        responses.Select(response => response.StatusCode).ShouldAllBe(status => status == HttpStatusCode.Created || status == HttpStatusCode.OK);
        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBeLessThanOrEqualTo(1);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var submission = await dbContext.DatabaseSubmissions.AsNoTracking().SingleAsync(CancellationToken);
        (await dbContext.Cases.AsNoTracking().SingleAsync(CancellationToken)).SubmissionId.ShouldBe(submission.Id);
        (await dbContext.CaseEvents.CountAsync(CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task The_loser_of_a_same_key_race_drops_its_case_with_its_record()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var setup = await SetUpAsync(org, admin);
        await SetAutoCaseAsync(admin, setup.Database, setup.Type);
        var command = new DatabaseSubmissionCommand(
            setup.Database, org.External.Id, Guid.NewGuid(), 1, true,
            DatabaseEndpoints.ToAnswerInputs(ValidAnswers().ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value))),
            DatabaseSubmissionSource.FormLink);

        // The loser has passed every check (no row for the key yet) and is about to save its record and
        // its case when the winner, with the same key, commits first.
        DatabaseSubmissionOutcome? winner = null;
        var race = new RunBeforeFirstSave(async () =>
        {
            await using var winnerContext = _host.Postgres.CreateDbContext(org.Organization.Id);
            winner = await new DatabaseSubmissionService(winnerContext, _host.Clock).SubmitAsync(command, CancellationToken);
        });
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_host.Postgres.ConnectionString).AddInterceptors(race).Options;
        await using var loserContext = new AppDbContext(options, new FixedOrganizationContext(org.Organization.Id));

        var loser = await new DatabaseSubmissionService(loserContext, _host.Clock).SubmitAsync(command, CancellationToken);

        race.Ran.ShouldBeTrue();
        var created = winner.ShouldBeOfType<DatabaseSubmissionOutcome.Created>();
        loser.ShouldBeOfType<DatabaseSubmissionOutcome.Replayed>().Receipt.Id.ShouldBe(created.Receipt.Id);
        var cases = await CasesAsync(org);
        cases.ShouldHaveSingleItem().SubmissionId.ShouldBe(created.Receipt.Id);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(1);
        (await dbContext.CaseEvents.CountAsync(CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_submission_opens_no_case()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var external = await SignInAsync(org, "external");
        var setup = await SetUpAsync(org, admin);
        await SetAutoCaseAsync(admin, setup.Database, setup.Type);

        var missingName = ValidAnswers();
        missingName.Remove("field-customer-name");
        var invalid = await SubmitFormAsync(external, setup.Database, Guid.NewGuid(), answers: missingName);
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "a validation error");
        var noConsent = await SubmitFormAsync(external, setup.Database, Guid.NewGuid(), consent: false);
        noConsent.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(noConsent)).GetProperty("reason").GetString().ShouldBe("consent-required");

        await SaveNewFormVersionAsync(admin, setup.Database);
        var changed = await SubmitFormAsync(external, setup.Database, Guid.NewGuid(), formVersionNumber: 1);
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(changed)).GetProperty("reason").GetString().ShouldBe("form-version-changed");

        (await admin.Spa.PostAsync($"{DatabasesPath}/{setup.Database}/archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var archived = await SubmitFormAsync(external, setup.Database, Guid.NewGuid(), formVersionNumber: 2);
        archived.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an archived database takes no submission");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseSubmissions.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.Cases.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.CaseEvents.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- Who sees the case -----------------------------------------------------------------------

    [Fact]
    public async Task The_submitter_internal_or_external_gets_the_same_403_case_and_the_group_and_manager_see_it()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var clerk = await SignInAsync(org, "clerk");
        var external = await SignInAsync(org, "external");
        var member = await SignInAsync(org, "member");
        var setup = await SetUpAsync(org, admin);
        await SetAutoCaseAsync(admin, setup.Database, setup.Type);
        await SubmitFormAsync(clerk, setup.Database, Guid.NewGuid());
        await SubmitFormAsync(external, setup.Database, Guid.NewGuid());
        var cases = await CasesAsync(org);
        cases.Count.ShouldBe(2);

        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        foreach (var item in cases)
        {
            foreach (var (who, caller) in new[] { ("internal submitter", clerk), ("external submitter", external) })
            {
                var response = await caller.Spa.GetAsync($"{CasesPath}/{item.Id}", caller.Token);
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, who);
            }

            (await member.Spa.GetAsync($"{CasesPath}/{item.Id}", member.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await admin.Spa.GetAsync($"{CasesPath}/{item.Id}", admin.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await ListedIdsAsync(clerk, $"{CasesPath}?status=all")).ShouldBeEmpty();
        (await ListedIdsAsync(member, CasesPath)).Count.ShouldBe(2);
        var externalList = await external.Spa.GetAsync(CasesPath, external.Token);
        externalList.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await externalList.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
    }

    [Fact]
    public async Task After_the_record_is_withdrawn_the_case_reads_withdrawn_and_can_still_be_accepted_and_completed()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var external = await SignInAsync(org, "external");
        var member = await SignInAsync(org, "member");
        var setup = await SetUpAsync(org, admin);
        await SetAutoCaseAsync(admin, setup.Database, setup.Type);
        var submissionId = await ReceiptIdAsync(await SubmitFormAsync(external, setup.Database, Guid.NewGuid()), HttpStatusCode.Created);
        var caseId = (await CasesAsync(org)).ShouldHaveSingleItem().Id;

        var withdrawal = await external.Spa.PostAsync($"/api/v1/submissions/{submissionId}/withdrawal", external.Token, new { });
        withdrawal.StatusCode.ShouldBe(HttpStatusCode.OK, await withdrawal.Content.ReadAsStringAsync(CancellationToken));

        foreach (var caller in new[] { member, admin })
        {
            var record = JsonDocument.Parse(await DetailRawAsync(caller, caseId)).RootElement.GetProperty("links").GetProperty("record");
            (record.GetProperty("state").GetString(), record.GetProperty("canRead").GetBoolean(), record.GetProperty("databaseName").GetString())
                .ShouldBe(("withdrawn", false, DatabaseName));
        }

        await ActAsync(org, member, caseId, "accept");
        await ActAsync(org, member, caseId, "complete", new { resolution = "已回電確認" });
        var done = JsonDocument.Parse(await DetailRawAsync(member, caseId)).RootElement;
        done.GetProperty("case").GetProperty("status").GetString().ShouldBe("completed");
        done.GetProperty("links").GetProperty("record").GetProperty("state").GetString().ShouldBe("withdrawn");
    }

    // --- The setting -----------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_manager_reads_and_sets_it_and_only_to_an_active_type_and_each_change_is_recorded()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync("別家商行");
        var admin = await SignInAsync(org, "admin");
        var otherAdmin = await SignInAsync(other, "admin");
        var setup = await SetUpAsync(org, admin);
        var otherSetup = await SetUpAsync(other, otherAdmin);
        var inactive = await CreateTypeAsync(admin, "停用的類型", setup.Equipment, isActive: false);
        var path = $"{DatabasesPath}/{setup.Database}/auto-case";

        // Not the manager, or a database that is not there: the same bytes.
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.OrganizationSettings));
        foreach (var login in new[] { "clerk", "handler", "external" })
        {
            var caller = await SignInAsync(org, login);
            foreach (var response in new[]
                     {
                         await caller.Spa.GetAsync(path, caller.Token),
                         await caller.Spa.PutAsync(path, caller.Token, new { caseTypeId = setup.Type }),
                     })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, login);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, login);
            }
        }

        foreach (var (who, databaseId) in new[] { ("unknown database", Guid.NewGuid()), ("another organization's database", otherSetup.Database) })
        {
            foreach (var response in new[]
                     {
                         await admin.Spa.GetAsync($"{DatabasesPath}/{databaseId}/auto-case", admin.Token),
                         await admin.Spa.PutAsync($"{DatabasesPath}/{databaseId}/auto-case", admin.Token, new { caseTypeId = setup.Type }),
                     })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, who);
            }
        }

        // Off by default; every active type is an option with its group's unreadable members.
        var initial = await BodyJsonAsync(await admin.Spa.GetAsync(path, admin.Token));
        OpenApiContract.AssertKeysMatchSchema(initial, "DatabaseAutoCaseView");
        initial.GetProperty("caseTypeId").ValueKind.ShouldBe(JsonValueKind.Null);
        var options = initial.GetProperty("options").EnumerateArray().ToList();
        options.Select(option => option.GetProperty("name").GetString()).ShouldBe(["設備故障報修", "採購申請"]);
        OpenApiContract.AssertKeysMatchSchema(options[0], "DatabaseAutoCaseOptionView");
        (options[0].GetProperty("group").GetProperty("name").GetString(), options[0].GetProperty("memberCount").GetInt32(),
                options[0].GetProperty("unreadableMemberCount").GetInt32(), options[0].GetProperty("defaultDueHours").GetInt32())
            .ShouldBe(("設備組", 2, 2, 72), "neither member reads the records");
        (options[1].GetProperty("memberCount").GetInt32(), options[1].GetProperty("unreadableMemberCount").GetInt32())
            .ShouldBe((1, 0), "the handler is a designated data manager holding the permission");

        // Only an active type of this organization.
        foreach (var (who, typeId) in new[] { ("inactive", inactive), ("unknown", Guid.NewGuid()), ("another organization's", otherSetup.Type) })
        {
            var refused = await admin.Spa.PutAsync(path, admin.Token, new { caseTypeId = typeId });
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, who);
            var body = await BodyJsonAsync(refused);
            body.GetProperty("reason").GetString().ShouldBe("case-type-inactive", who);
            body.GetProperty("errors").GetProperty("caseTypeId")[0].GetString().ShouldBe("這個案件類型已停用或不存在，請選擇其他類型。", who);
        }

        (await ActivitiesAsync(org)).ShouldBeEmpty();

        // On, again the same (nothing written), another type, off.
        var on = await admin.Spa.PutAsync(path, admin.Token, new { caseTypeId = setup.Type });
        on.StatusCode.ShouldBe(HttpStatusCode.OK, await on.Content.ReadAsStringAsync(CancellationToken));
        (await BodyJsonAsync(on)).GetProperty("caseTypeId").GetGuid().ShouldBe(setup.Type);
        (await admin.Spa.PutAsync(path, admin.Token, new { caseTypeId = setup.Type })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.PutAsync(path, admin.Token, new { caseTypeId = setup.Purchase })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var off = await BodyJsonAsync(await admin.Spa.PutAsync(path, admin.Token, new { caseTypeId = (Guid?)null }));
        off.GetProperty("caseTypeId").ValueKind.ShouldBe(JsonValueKind.Null);

        var activities = await ActivitiesAsync(org);
        activities.Select(activity => (activity.Action, activity.ActorAccountId)).ShouldAllBe(
            entry => entry.Action == OrganizationActivityAction.DatabaseAutoCaseChanged && entry.ActorAccountId == org.Admin.Id);
        activities.Select(activity => Describe(activity.Detail!)).ShouldBe(
        [
            $"{DatabaseName}: - → 設備故障報修",
            $"{DatabaseName}: 設備故障報修 → 採購申請",
            $"{DatabaseName}: 採購申請 → -",
        ]);
        (await ActivitiesAsync(other)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_type_a_database_uses_cannot_be_deactivated()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var setup = await SetUpAsync(org, admin);
        var second = await CreateDatabaseAsync(admin, "門市回報");
        await SetAutoCaseAsync(admin, setup.Database, setup.Type);
        await SetAutoCaseAsync(admin, second, setup.Type);

        var refused = await admin.Spa.PutAsync($"{TypesPath}/{setup.Type}", admin.Token, TypeBody("設備故障報修", setup.Equipment, isActive: false));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(refused);
        body.GetProperty("reason").GetString().ShouldBe("case-type-in-use");
        const string message = "「設備故障報修」是數據庫「客戶資料庫」、「門市回報」送出後自動開案的類型。請先在這些數據庫改選其他類型或關閉自動開案，再停用。";
        body.GetProperty("message").GetString().ShouldBe(message);
        body.GetProperty("errors").EnumerateObject().ShouldHaveSingleItem().Name.ShouldBe("databases");
        body.GetProperty("errors").GetProperty("databases")[0].GetString().ShouldBe(message);

        // Changing anything else stays possible while it is in use.
        (await admin.Spa.PutAsync($"{TypesPath}/{setup.Type}", admin.Token, TypeBody("設備報修", setup.Equipment, isActive: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetAutoCaseAsync(admin, setup.Database, null);
        (await admin.Spa.PutAsync($"{TypesPath}/{setup.Type}", admin.Token, TypeBody("設備報修", setup.Equipment, isActive: false)))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the other database still uses it");
        await SetAutoCaseAsync(admin, second, null);
        (await admin.Spa.PutAsync($"{TypesPath}/{setup.Type}", admin.Token, TypeBody("設備報修", setup.Equipment, isActive: false)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private sealed record TestOrganization(
        Organization Organization, Account Admin, Account Clerk, Account Handler, Account Member, Account Colleague, Account External);

    /// <summary>設備組 (member, colleague), 採購組 (handler), the types 設備故障報修 (設備組, 72 h) and 採購申請
    /// (採購組, 24 h), and the admin's 客戶資料庫 with the handler as its data manager.</summary>
    private sealed record Setup(Guid Equipment, Guid Purchasing, Guid Type, Guid Purchase, Guid Database);

    private sealed record SignedIn(SpaClient Spa, string Token);

    /// <summary>Runs <paramref name="action"/> once, just before the first save of the context it is on.</summary>
    private sealed class RunBeforeFirstSave(Func<Task> action) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran)
            {
                Ran = true;
                await action();
            }

            return result;
        }
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "自動開案商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var clerk = await _host.CreateAccountAsync(
            organization, "clerk", Password, AccountRole.InternalEmployee, $"{name}櫃檯同仁",
            AccountPermission.SubmitAuthorizedForms, AccountPermission.UseSharedAssistants);
        var handler = await _host.CreateAccountAsync(
            organization, "handler", Password, AccountRole.InternalEmployee, $"{name}資料管理者", AccountPermission.ReadConsentedSubmissions);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}設備組成員", AccountPermission.UseSharedAssistants);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, $"{name}設備組同事", AccountPermission.UseSharedAssistants);
        var external = await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, $"{name}客戶",
            AccountPermission.SubmitAuthorizedForms, AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, clerk, handler, member, colleague, external);
    }

    private async Task<Setup> SetUpAsync(TestOrganization org, SignedIn admin)
    {
        var equipment = await CreateGroupAsync(admin, "設備組", org.Member.Id, org.Colleague.Id);
        var purchasing = await CreateGroupAsync(admin, "採購組", org.Handler.Id);
        var type = await CreateTypeAsync(admin, "設備故障報修", equipment);
        var purchase = await CreateTypeAsync(admin, "採購申請", purchasing, hours: 24);
        var database = await CreateDatabaseAsync(admin, DatabaseName);
        var access = await admin.Spa.PutAsync($"{DatabasesPath}/{database}/access", admin.Token, new { dataManagerAccountIds = new[] { org.Handler.Id } });
        access.StatusCode.ShouldBe(HttpStatusCode.OK, await access.Content.ReadAsStringAsync(CancellationToken));
        return new Setup(equipment, purchasing, type, purchase, database);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateGroupAsync(SignedIn admin, string name, params Guid[] members)
    {
        var response = await admin.Spa.PostAsync(GroupsPath, admin.Token, new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var id = (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
        (await admin.Spa.PutAsync($"{GroupsPath}/{id}/members", admin.Token, new { accountIds = members })).StatusCode.ShouldBe(HttpStatusCode.OK);
        return id;
    }

    private static object TypeBody(string name, Guid groupId, bool isActive, int hours = 72) =>
        new { name, description = "", defaultGroupId = groupId, defaultDueHours = hours, isActive };

    private static async Task<Guid> CreateTypeAsync(SignedIn admin, string name, Guid groupId, int hours = 72, bool isActive = true)
    {
        var response = await admin.Spa.PostAsync(TypesPath, admin.Token, TypeBody(name, groupId, isActive, hours));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name)
    {
        var response = await owner.Spa.PostAsync(DatabasesPath, owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task SetAutoCaseAsync(SignedIn admin, Guid databaseId, Guid? typeId)
    {
        var response = await admin.Spa.PutAsync($"{DatabasesPath}/{databaseId}/auto-case", admin.Token, new { caseTypeId = typeId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static Task<HttpResponseMessage> SubmitFormAsync(
        SignedIn caller, Guid databaseId, Guid submissionId, bool consent = true, int formVersionNumber = 1, Dictionary<string, object>? answers = null) =>
        caller.Spa.PostAsync($"{DatabasesPath}/{databaseId}/submissions", caller.Token, new
        {
            submissionId,
            formVersionNumber,
            consent,
            answers = answers ?? ValidAnswers(),
        });

    private static Task<HttpResponseMessage> SubmitChatFormAsync(SignedIn caller, Guid assistantId, Guid databaseId, Guid submissionId) =>
        caller.Spa.PostAsync($"/api/v1/assistants/{assistantId}/chat/forms/{databaseId}/submissions", caller.Token, new
        {
            submissionId,
            formVersionNumber = 1,
            consent = true,
            answers = ValidAnswers(),
            threadId = (Guid?)null,
        });

    private static async Task<Guid> ReceiptIdAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>The admin's assistant (keeps no conversations), shared with the clerk, whose form target is
    /// <paramref name="databaseId"/>.</summary>
    private async Task<Guid> CreateFormAssistantAsync(TestOrganization org, SignedIn admin, Guid databaseId)
    {
        Guid assistantId;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var assistant = Assistant.Create(
                org.Organization.Id, org.Admin.Id, "客服小幫手", "協助客戶留下聯絡資料", null,
                AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
                "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: false, DateTimeOffset.UtcNow);
            dbContext.Assistants.Add(assistant);
            dbContext.AssistantShares.Add(new AssistantShare(assistant, org.Clerk.Id));
            await dbContext.SaveChangesAsync(CancellationToken);
            assistantId = assistant.Id;
        }

        var connected = await admin.Spa.PutAsync($"/api/v1/assistants/{assistantId}/sources/database/{databaseId}", admin.Token, new { });
        connected.StatusCode.ShouldBe(HttpStatusCode.OK, await connected.Content.ReadAsStringAsync(CancellationToken));
        var target = await admin.Spa.PatchAsync($"/api/v1/assistants/{assistantId}/settings", admin.Token, new
        {
            rules = new { dataWriteDatabaseId = databaseId.ToString(), dataWritePurpose = "記錄客戶聯絡方式，方便客服回電。" },
        });
        target.StatusCode.ShouldBe(HttpStatusCode.OK, await target.Content.ReadAsStringAsync(CancellationToken));
        return assistantId;
    }

    private static async Task SaveNewFormVersionAsync(SignedIn owner, Guid databaseId)
    {
        var detail = await BodyJsonAsync(await owner.Spa.GetAsync($"{DatabasesPath}/{databaseId}", owner.Token));
        var form = detail.GetProperty("form");
        var fields = JsonNode.Parse(form.GetProperty("fields").GetRawText())!.AsArray();
        fields.Add(new JsonObject { ["label"] = "備註", ["type"] = "text", ["required"] = false, ["options"] = new JsonArray(), ["unit"] = "" });
        var response = await owner.Spa.PutAsync($"{DatabasesPath}/{databaseId}/form", owner.Token, new
        {
            baseVersionNumber = form.GetProperty("versionNumber").GetInt32(),
            fields,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>Posts a case action (M7-4) with the case's current <c>eventCount</c>; it must succeed.</summary>
    private async Task ActAsync(TestOrganization org, SignedIn caller, Guid caseId, string action, object? fields = null)
    {
        int eventCount;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            eventCount = (await dbContext.Cases.AsNoTracking().SingleAsync(item => item.Id == caseId, CancellationToken)).EventCount;
        }

        var body = new Dictionary<string, object?> { ["eventCount"] = eventCount };
        foreach (var property in JsonSerializer.SerializeToElement(fields ?? new { }).EnumerateObject())
        {
            body[property.Name] = property.Value.Clone();
        }

        var response = await caller.Spa.PostAsync($"{CasesPath}/{caseId}:{action}", caller.Token, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync(CancellationToken)}");
    }

    private async Task<List<CaseEntity>> CasesAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.Cases.AsNoTracking().OrderBy(item => item.CreatedAt).ToListAsync(CancellationToken);
    }

    private async Task<List<OrganizationActivity>> ActivitiesAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.OrganizationActivities.AsNoTracking()
            .Where(activity => activity.Action == OrganizationActivityAction.DatabaseAutoCaseChanged)
            .OrderBy(activity => activity.At)
            .ToListAsync(CancellationToken);
    }

    private static string Describe(string detail)
    {
        var root = JsonDocument.Parse(detail).RootElement;
        static string Name(JsonElement side) => side.ValueKind == JsonValueKind.Null ? "-" : side.GetProperty("name").GetString()!;
        return $"{root.GetProperty("databaseName").GetString()}: {Name(root.GetProperty("from"))} → {Name(root.GetProperty("to"))}";
    }

    private static async Task<List<Guid>> ListedIdsAsync(SignedIn caller, string path)
    {
        var response = await caller.Spa.GetAsync(path, caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return [.. (await BodyJsonAsync(response)).EnumerateArray().Select(row => row.GetProperty("id").GetGuid())];
    }

    private static async Task<string> DetailRawAsync(SignedIn caller, Guid caseId)
    {
        var response = await caller.Spa.GetAsync($"{CasesPath}/{caseId}", caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(CancellationToken);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
