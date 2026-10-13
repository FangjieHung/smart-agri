using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Setup;
using SmartAgri.Application.Line;
using SmartAgri.Application.Notifications;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Notifications;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Notifications;

namespace SmartAgri.Api.Tests.Notifications;

/// <summary>
/// 轉真人 notifications end to end (#355): a website or LINE visitor given the assistant's refusal
/// creates an in-app notification for the assistant's owner and — through the job queue — an Email
/// to the in-process <see cref="FakeSmtpServer"/>; never a real mailbox. Each test adds its own
/// organization, so counts are its own.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class VisitorHandoffTests : IClassFixture<HandoffHostFixture>
{
    private const string Password = "Handoff-Notify-Pass-1!";
    private const string LineSecret = "8c9f1d2e3a4b5c6d7e8f90a1b2c3d4e5";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";
    private const string RelatedQuestion = "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？";
    private const string UnrelatedQuestion = "zzzz qqqq xxxx 請找真人";
    private const string RefusalMessage = "目前的資料中找不到這個問題的答案，店長會在這裡回覆您。";
    private const string AssistantName = "退貨小幫手";

    private readonly HandoffHostFixture _host;

    public VisitorHandoffTests(HandoffHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private FakeSmtpServer Smtp => _host.Smtp;

    // --- Website ----------------------------------------------------------------------------------------

    /// <summary>Acceptance: a website refusal gives the owner one pending handoff (and one Email) per
    /// visitor session per half hour; an answered question gives nothing. The Email has the
    /// organization, assistant, channel, time and admin link, and nothing the visitor wrote.</summary>
    [Fact]
    public async Task A_website_refusal_notifies_the_owner_in_the_admin_and_by_Email_without_any_visitor_content()
    {
        var org = await CreateOrganizationAsync("官網商行");
        var owner = OwnerAddress();
        await SetEmailAsync(org.AdminSpa, org.AdminToken, owner);
        var assistantId = await PublishedWebsiteAssistantAsync(org);
        var visitor = VisitorClient();
        var token = await SessionTokenAsync(visitor, assistantId);

        (await ReplyKindAsync(visitor, assistantId, token, RelatedQuestion)).ShouldBe("company-data");
        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(0, "an answered question is no handoff");

        (await ReplyKindAsync(visitor, assistantId, token, UnrelatedQuestion)).ShouldBe("no-result");
        (await ReplyKindAsync(visitor, assistantId, token, UnrelatedQuestion)).ShouldBe("no-result");
        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(1, "the same visitor session asking again is one person waiting");

        var another = await SessionTokenAsync(visitor, assistantId);
        (await ReplyKindAsync(visitor, assistantId, another, UnrelatedQuestion)).ShouldBe("no-result");
        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(2, "another visitor");

        var list = await ListAsync(org.AdminSpa, org.AdminToken);
        list.Count.ShouldBe(2);
        list.ShouldAllBe(item => item.GetProperty("assistantId").GetGuid() == assistantId
            && item.GetProperty("assistantName").GetString() == AssistantName
            && item.GetProperty("channel").GetString() == "website"
            && item.GetProperty("handledAt").ValueKind == JsonValueKind.Null);

        await RunJobsAsync();
        var mails = Smtp.To(owner);
        mails.Count.ShouldBe(2);
        var mail = mails[0];
        mail.From.ShouldBe(HandoffHostFixture.From);
        mail.FromHeader.ShouldContain(HandoffHostFixture.From);
        mail.Username.ShouldBe(HandoffHostFixture.Username, "signed in with the configured account");
        mail.Subject.ShouldBe($"【轉真人】{AssistantName}：官網的訪客需要專人協助");
        mail.Body.ShouldContain($"官網商行的助理「{AssistantName}」在官網回覆了「找不到資料時的回覆」");
        mail.Body.ShouldContain("管道：官網");
        mail.Body.ShouldContain($"後台：{AuthHostFixture.SpaOrigin}/app/handoffs");
        mail.Body.ShouldMatch(@"時間：\d{4}-\d{2}-\d{2} \d{2}:\d{2}（Asia/Taipei）");
        foreach (var message in mails)
        {
            (message.Subject + message.Body).ShouldNotContain("zzzz", Case.Insensitive, "nothing the visitor wrote");
            (message.Subject + message.Body).ShouldNotContain(RefusalMessage, Case.Sensitive, "nor what the assistant answered");
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.VisitorHandoffs.AsNoTracking().Where(row => row.AssistantId == assistantId).ToListAsync(CancellationToken))
                .ShouldAllBe(row => row.EmailSentAt != null);
        }

        await RunJobsAsync();
        Smtp.To(owner).Count.ShouldBe(2, "a job that ran is not sent again");
    }

    // --- LINE -------------------------------------------------------------------------------------------

    /// <summary>Acceptance: a delivered LINE refusal (one-to-one and in a group) gives the owner a handoff
    /// and an Email that points to LINE Official Account Manager; no LINE id is stored or sent.</summary>
    [Fact]
    public async Task A_LINE_refusal_notifies_the_owner_and_Emails_them_without_the_LINE_id()
    {
        var org = await CreateOrganizationAsync("LINE 商行");
        var owner = OwnerAddress();
        await SetEmailAsync(org.AdminSpa, org.AdminToken, owner);
        var line = await LineAssistantAsync(org);
        var userId = "U" + Guid.NewGuid().ToString("N");
        var groupId = "C" + Guid.NewGuid().ToString("N");

        await PostLineAsync(line, LineText(LineUser(userId), RelatedQuestion, "reply-token-related"));
        await EventuallyAsync(() => LineReplies(line).Count == 1, "the answer");
        // Another chat: in the first one the earlier answer is remembered and would steer retrieval.
        var refusedUserId = "U" + Guid.NewGuid().ToString("N");
        await PostLineAsync(line, LineText(LineUser(refusedUserId), UnrelatedQuestion, "reply-token-refused"));
        await EventuallyAsync(() => LineReplies(line).Count == 2, "the refusal");
        await PostLineAsync(
            line,
            LineText(LineGroup(groupId, userId), "@安心客服 " + UnrelatedQuestion, "reply-token-group",
                mentionees: """[{"index":0,"length":5,"type":"user","isSelf":true}]"""));
        await EventuallyAsync(() => LineReplies(line).Count == 3, "the group refusal");
        LineReplies(line)[1].Json.GetProperty("messages")[0].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain(RefusalMessage);
        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBeLessThanOrEqualTo(2, "the answered question is no handoff");

        await EventuallyAsync(async () => await PendingCountAsync(org.AdminSpa, org.AdminToken) == 2, "a handoff per chat (recorded after the reply)");
        var list = await ListAsync(org.AdminSpa, org.AdminToken);
        list.ShouldAllBe(item => item.GetProperty("channel").GetString() == "line");

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var rows = await dbContext.VisitorHandoffs.AsNoTracking().Where(row => row.AssistantId == line.AssistantId).ToListAsync(CancellationToken);
            rows.Count.ShouldBe(2);
            JsonSerializer.Serialize(rows).ShouldNotContain(refusedUserId);
            JsonSerializer.Serialize(rows).ShouldNotContain(groupId);
        }

        await RunJobsAsync();
        var mails = Smtp.To(owner);
        mails.Count.ShouldBe(2);
        mails[0].Subject.ShouldBe($"【轉真人】{AssistantName}：LINE 的訪客需要專人協助");
        mails[0].Body.ShouldContain("LINE Official Account Manager");
        mails.ShouldAllBe(mail => !mail.Data.Contains(refusedUserId) && !mail.Body.Contains(refusedUserId) && !mail.Body.Contains(groupId) && !mail.Body.Contains(userId));
    }

    // --- No SMTP, failures, opt-out -----------------------------------------------------------------------

    /// <summary>Acceptance: without <c>Smtp:Host</c> the refusal still gives the owner the in-app
    /// notification; nothing is enqueued, sent or logged as an error, and the settings say Email is off.</summary>
    [Fact]
    public async Task Without_SMTP_a_refusal_still_notifies_in_the_admin_and_nothing_is_sent_or_fails()
    {
        var telemetry = new TelemetryCapture();
        await using var noSmtp = _host.Factory.WithWebHostBuilder(builder =>
        {
            telemetry.Attach(builder);
            builder.ConfigureTestServices(services => services.Configure<SmtpOptions>(options => options.Host = null));
        });
        var org = await CreateOrganizationAsync("沒有郵件商行", noSmtp);
        var owner = OwnerAddress();
        var settings = await SetEmailAsync(org.AdminSpa, org.AdminToken, owner);
        settings.GetProperty("emailDeliveryConfigured").GetBoolean().ShouldBeFalse();
        var assistantId = await PublishedWebsiteAssistantAsync(org, noSmtp);
        var visitor = VisitorClient(noSmtp);

        (await ReplyKindAsync(visitor, assistantId, await SessionTokenAsync(visitor, assistantId), UnrelatedQuestion)).ShouldBe("no-result");

        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(1);
        (await EmailJobsAsync(org.Organization.Id)).ShouldBeEmpty();
        await noSmtp.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        Smtp.To(owner).ShouldBeEmpty();
        telemetry.Logs.ShouldNotContain(log => log.Contains("could not be recorded", StringComparison.Ordinal));
    }

    /// <summary>Acceptance: the visitor's reply is complete before any Email is tried; a send the SMTP
    /// server refuses is retried with backoff until it goes through, and the job's error and the logs carry
    /// neither the recipient nor the SMTP password.</summary>
    [Fact]
    public async Task A_failed_send_is_retried_with_backoff_and_never_touches_the_conversation()
    {
        var telemetry = new TelemetryCapture();
        await using var observed = _host.Factory.WithWebHostBuilder(telemetry.Attach);
        var org = await CreateOrganizationAsync("重試商行", observed);
        var owner = OwnerAddress();
        await SetEmailAsync(org.AdminSpa, org.AdminToken, owner);
        var assistantId = await PublishedWebsiteAssistantAsync(org, observed);
        var visitor = VisitorClient(observed);
        await observed.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        Smtp.FailNext(2);

        var run = await RunAsync(visitor, assistantId, await SessionTokenAsync(visitor, assistantId), UnrelatedQuestion);
        run.Status.ShouldBe(HttpStatusCode.OK);
        run.Types[^1].ShouldBe("RUN_FINISHED", "the reply is complete; the Email is the job queue's");

        var runner = observed.Services.GetRequiredService<JobRunner>();
        await runner.RunUntilIdleAsync(CancellationToken);
        var job = (await EmailJobsAsync(org.Organization.Id)).ShouldHaveSingleItem();
        job.Status.ShouldBe(BackgroundJobStatus.Queued, "a retry is scheduled");
        job.Attempts.ShouldBe(1);
        job.LastError.ShouldNotBeNull().ShouldContain("SmtpException");
        job.LastError.ShouldNotContain(owner);
        Smtp.To(owner).ShouldBeEmpty();

        _host.Clock.Advance(TimeSpan.FromSeconds(31));
        await runner.RunUntilIdleAsync(CancellationToken);
        (await EmailJobsAsync(org.Organization.Id)).Single().Attempts.ShouldBe(2);
        Smtp.To(owner).ShouldBeEmpty();

        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        await runner.RunUntilIdleAsync(CancellationToken);
        var done = (await EmailJobsAsync(org.Organization.Id)).Single();
        done.Status.ShouldBe(BackgroundJobStatus.Succeeded);
        done.Attempts.ShouldBe(3);
        Smtp.To(owner).Count.ShouldBe(1);
        Smtp.Rejected.ShouldBeGreaterThanOrEqualTo(2);

        var logs = telemetry.Logs.Concat(telemetry.OpenTelemetryLogs).ToList();
        logs.ShouldContain(log => log.Contains("Handoff Email for", StringComparison.Ordinal) && log.Contains("sent (attempt 3)", StringComparison.Ordinal));
        logs.ShouldNotContain(log => log.Contains(owner, StringComparison.OrdinalIgnoreCase));
        logs.ShouldNotContain(log => log.Contains(HandoffHostFixture.SmtpPassword, StringComparison.Ordinal));
        logs.ShouldNotContain(log => log.Contains("zzzz", StringComparison.Ordinal));
    }

    /// <summary>Acceptance: even when recording the handoff fails, the visitor gets the full refusal.</summary>
    [Fact]
    public async Task A_notifier_failure_never_fails_the_visitors_reply()
    {
        await using var broken = _host.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IEmailSender, BrokenEmailSender>())));
        var org = await CreateOrganizationAsync("故障商行", broken);
        var assistantId = await PublishedWebsiteAssistantAsync(org, broken);
        var visitor = VisitorClient(broken);

        var run = await RunAsync(visitor, assistantId, await SessionTokenAsync(visitor, assistantId), UnrelatedQuestion);

        run.Status.ShouldBe(HttpStatusCode.OK);
        run.Types.ShouldBe(["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM", "RUN_FINISHED"]);
        run.Body.ShouldContain("no-result");
        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(0, "nothing could be saved, and nothing half-saved");
    }

    /// <summary>Acceptance: an owner who turned the Email off gets the in-app notification only; turning
    /// it off also stops an Email already in the queue.</summary>
    [Fact]
    public async Task An_owner_who_opts_out_gets_no_Email_but_still_the_in_app_notification()
    {
        var org = await CreateOrganizationAsync("關閉通知商行");
        var owner = OwnerAddress();
        await SetEmailAsync(org.AdminSpa, org.AdminToken, owner, handoffEmails: false);
        var assistantId = await PublishedWebsiteAssistantAsync(org);
        var visitor = VisitorClient();

        (await ReplyKindAsync(visitor, assistantId, await SessionTokenAsync(visitor, assistantId), UnrelatedQuestion)).ShouldBe("no-result");
        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(1);
        (await EmailJobsAsync(org.Organization.Id)).ShouldBeEmpty("an opted-out owner's Email is not even queued");

        await SetEmailAsync(org.AdminSpa, org.AdminToken, owner, handoffEmails: true);
        (await ReplyKindAsync(visitor, assistantId, await SessionTokenAsync(visitor, assistantId), UnrelatedQuestion)).ShouldBe("no-result");
        (await EmailJobsAsync(org.Organization.Id)).Count.ShouldBe(1);
        await SetEmailAsync(org.AdminSpa, org.AdminToken, owner, handoffEmails: false);

        await RunJobsAsync();
        Smtp.To(owner).ShouldBeEmpty("the choice is read when the job runs");
        (await EmailJobsAsync(org.Organization.Id)).Single().Status.ShouldBe(BackgroundJobStatus.Succeeded);
        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(2);
    }

    // --- Who sees and handles them ------------------------------------------------------------------------

    /// <summary>Acceptance: only the assistant's owner lists, counts and marks its handoffs; another
    /// member of the organization and another organization's administrator see nothing and get the same
    /// <c>403 handoff</c> as for an id that does not exist.</summary>
    [Fact]
    public async Task Only_the_assistants_owner_sees_and_handles_its_handoffs_and_other_organizations_see_nothing()
    {
        var org = await CreateOrganizationAsync("擁有者商行");
        var assistantId = await PublishedWebsiteAssistantAsync(org);
        var visitor = VisitorClient();
        (await ReplyKindAsync(visitor, assistantId, await SessionTokenAsync(visitor, assistantId), UnrelatedQuestion)).ShouldBe("no-result");
        var handoffId = (await ListAsync(org.AdminSpa, org.AdminToken)).ShouldHaveSingleItem().GetProperty("id").GetGuid();

        var memberSpa = _host.CreateSpaClient();
        var memberToken = (await memberSpa.SignInAsync(org.Organization.Code, "internal", Password)).AccessToken;
        var other = await CreateOrganizationAsync("別家商行");

        var unknown = await other.AdminSpa.PostAsync(HandlePath(Guid.NewGuid()), other.AdminToken, new { });
        unknown.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var reference = await ResponseFingerprint.FromAsync(unknown);
        JsonDocument.Parse(reference.Body).RootElement.GetProperty("reason").GetString().ShouldBe("handoff");
        foreach (var (spa, token, who) in new[] { (memberSpa, memberToken, "a member who is not the owner"), (other.AdminSpa, other.AdminToken, "another organization") })
        {
            (await ListAsync(spa, token)).ShouldBeEmpty(who);
            (await ListAsync(spa, token, "all")).ShouldBeEmpty(who);
            (await PendingCountAsync(spa, token)).ShouldBe(0, who);
            var refused = await spa.PostAsync(HandlePath(handoffId), token, new { });
            (await ResponseFingerprint.FromAsync(refused)).Body.ShouldBe(reference.Body, who);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
        }

        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(1, "nobody else marked it");
        var handled = await org.AdminSpa.PostAsync(HandlePath(handoffId), org.AdminToken, new { });
        handled.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await BodyJsonAsync(handled);
        view.GetProperty("handledByAccountId").GetGuid().ShouldBe(org.Admin.Id);
        view.GetProperty("handledByName").GetString().ShouldBe(org.Admin.DisplayName);
        var handledAt = view.GetProperty("handledAt").GetDateTimeOffset();

        (await PendingCountAsync(org.AdminSpa, org.AdminToken)).ShouldBe(0);
        (await ListAsync(org.AdminSpa, org.AdminToken)).ShouldBeEmpty("the default list is the pending ones");
        (await ListAsync(org.AdminSpa, org.AdminToken, "handled")).ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(handoffId);
        _host.Clock.Advance(TimeSpan.FromSeconds(5));
        var again = await BodyJsonAsync(await org.AdminSpa.PostAsync(HandlePath(handoffId), org.AdminToken, new { }));
        again.GetProperty("handledAt").GetDateTimeOffset().ShouldBe(handledAt, "marking twice keeps the first");

        (await org.AdminSpa.GetAsync(HandoffsPath + "?status=everything", org.AdminToken)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>Acceptance: the settings are the caller's own; an address must be a valid one, blank
    /// clears it, and the default is "no address, Emails on".</summary>
    [Fact]
    public async Task Notification_settings_are_the_callers_own_and_validate_the_address()
    {
        var org = await CreateOrganizationAsync("設定商行");
        var initial = await BodyJsonAsync(await org.AdminSpa.GetAsync(SettingsPath, org.AdminToken));
        initial.GetProperty("email").ValueKind.ShouldBe(JsonValueKind.Null);
        initial.GetProperty("handoffEmails").GetBoolean().ShouldBeTrue();
        initial.GetProperty("emailDeliveryConfigured").GetBoolean().ShouldBeTrue();

        foreach (var invalid in new[] { "not-an-address", "a@b", "Owner <owner@example.org>", "a@example.org,b@example.org", "a@example.org\r\nBcc: x@example.org" })
        {
            var refused = await org.AdminSpa.PutAsync(SettingsPath, org.AdminToken, new { email = invalid, handoffEmails = true });
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, invalid);
            (await BodyJsonAsync(refused)).GetProperty("errors").GetProperty("email")[0].GetString()
                .ShouldBe(AccountNotificationSettings.EmailInvalidMessage);
        }

        var saved = await SetEmailAsync(org.AdminSpa, org.AdminToken, "  owner@shop.example.org ", handoffEmails: false);
        saved.GetProperty("email").GetString().ShouldBe("owner@shop.example.org");
        saved.GetProperty("handoffEmails").GetBoolean().ShouldBeFalse();
        (await BodyJsonAsync(await org.AdminSpa.GetAsync(SettingsPath, org.AdminToken))).GetProperty("email").GetString().ShouldBe("owner@shop.example.org");

        var memberSpa = _host.CreateSpaClient();
        var memberToken = (await memberSpa.SignInAsync(org.Organization.Code, "internal", Password)).AccessToken;
        (await BodyJsonAsync(await memberSpa.GetAsync(SettingsPath, memberToken))).GetProperty("email").ValueKind.ShouldBe(JsonValueKind.Null);

        var cleared = await SetEmailAsync(org.AdminSpa, org.AdminToken, "  ");
        cleared.GetProperty("email").ValueKind.ShouldBe(JsonValueKind.Null);
        (await org.AdminSpa.Http.GetAsync(SettingsPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private const string HandoffsPath = "/api/v1/handoffs";
    private const string SettingsPath = "/api/v1/me/notification-settings";

    private static string HandlePath(Guid id) => $"{HandoffsPath}/{id}:handle";

    private static string OwnerAddress() => $"owner-{Guid.NewGuid():N}@shop.example.org";

    private sealed record TestOrganization(Organization Organization, Account Admin, SpaClient AdminSpa, string AdminToken, WebApplicationFactory<Program> Factory);

    private sealed record LineSetup(Guid OrganizationId, Guid AssistantId, LineBot Bot);

    private sealed record RecordedRun(HttpStatusCode Status, string Body, IReadOnlyList<string> Types);

    private Task RunJobsAsync() => _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private async Task<TestOrganization> CreateOrganizationAsync(string name, WebApplicationFactory<Program>? via = null)
    {
        var factory = via ?? _host.Factory;
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManagePublishing, AccountPermission.ManageDataSources);
        await _host.CreateAccountAsync(organization, "internal", Password, AccountRole.InternalEmployee, $"{name}同仁", AccountPermission.UseSharedAssistants);
        var spa = new SpaClient(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;
        return new TestOrganization(organization, admin, spa, token, factory);
    }

    private static async Task<JsonElement> SetEmailAsync(SpaClient spa, string token, string email, bool handoffEmails = true)
    {
        var response = await spa.PutAsync(SettingsPath, token, new { email, handoffEmails });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await BodyJsonAsync(response);
    }

    private static async Task<int> PendingCountAsync(SpaClient spa, string token)
    {
        var response = await spa.GetAsync(HandoffsPath + "/attention", token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await BodyJsonAsync(response)).GetProperty("pendingCount").GetInt32();
    }

    private static async Task<IReadOnlyList<JsonElement>> ListAsync(SpaClient spa, string token, string? status = null)
    {
        var response = await spa.GetAsync(status is null ? HandoffsPath : $"{HandoffsPath}?status={status}", token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return [.. (await BodyJsonAsync(response)).EnumerateArray()];
    }

    private async Task<List<BackgroundJob>> EmailJobsAsync(Guid organizationId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        return await dbContext.BackgroundJobs.AsNoTracking().Where(job => job.Kind == SendHandoffEmailJob.Kind).ToListAsync(CancellationToken);
    }

    /// <summary>The admin's assistant, connected to a knowledge base with one approved document, its
    /// acceptance passed and its website channel published: serving.</summary>
    private async Task<Guid> PublishedWebsiteAssistantAsync(TestOrganization org, WebApplicationFactory<Program>? via = null)
    {
        var assistantId = await AssistantWithKnowledgeAsync(org, via);
        var path = $"/api/v1/assistants/{assistantId}/publishing/website";
        var current = await BodyJsonAsync(await org.AdminSpa.GetAsync(path, org.AdminToken));
        var saved = await org.AdminSpa.PutAsync(path, org.AdminToken, new
        {
            displayName = "安心客服",
            welcomeMessage = "您好，有什麼可以協助？",
            brandColor = "ocean",
            position = "bottom-right",
            allowedDomains = new[] { "shop.example.com" },
            revision = current.GetProperty("revision").GetInt32(),
        });
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(CancellationToken));
        var published = await org.AdminSpa.PostAsync($"{path}:publish", org.AdminToken, new { });
        published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(CancellationToken));
        return assistantId;
    }

    private async Task<Guid> AssistantWithKnowledgeAsync(TestOrganization org, WebApplicationFactory<Program>? via = null)
    {
        var created = await org.AdminSpa.PostAsync("/api/v1/knowledge-bases", org.AdminToken, new { name = "對話知識庫", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = (await BodyJsonAsync(created)).GetProperty("id").GetGuid();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"# 退貨政策\n\n{ReturnClause}\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "退貨政策.md");
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-bases/{knowledgeBaseId}/documents") { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", org.AdminToken);
        var uploaded = await org.AdminSpa.Http.SendAsync(upload, CancellationToken);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var versionId = (await BodyJsonAsync(uploaded)).GetProperty("latestVersionId").GetGuid();
        await (via ?? _host.Factory).Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        var approved = await org.AdminSpa.PostAsync(
            $"/api/v1/knowledge-bases/{knowledgeBaseId}/versions/approve", org.AdminToken, new { versionIds = new[] { versionId.ToString() } });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));

        var now = _host.Clock.GetUtcNow().AddHours(-2);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Admin.Id, AssistantName, "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            RefusalMessage, showCitations: true, keepConversations: false, now);
        dbContext.Assistants.Add(assistant);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(row => row.Id == knowledgeBaseId, CancellationToken);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        dbContext.AssistantTestCases.Add(new AssistantTestCase(
            org.Organization.Id, assistant.Id, "退貨期限是幾天？", AssistantTestCaseCategory.Common,
            AssistantTestExpectedKind.CompanyData, [Guid.NewGuid()], null, ordinal: 1, now));
        var run = AssistantTestRun.Queue(org.Organization.Id, assistant.Id, AssistantTestRunTrigger.Manual, now);
        run.Start("test", AuthHostFixture.ChatModel, 0.3, now);
        run.Complete(1, 0, now);
        dbContext.AssistantTestRuns.Add(run);
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private async Task<LineSetup> LineAssistantAsync(TestOrganization org)
    {
        var assistantId = await AssistantWithKnowledgeAsync(org);
        var bot = _host.Line.AddBot("@handoff-line");
        var protector = _host.Factory.Services.GetRequiredService<ISecretProtector>();
        var now = _host.Clock.GetUtcNow().AddHours(-2);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(row => row.Id == assistantId, CancellationToken);
        var channel = new AssistantLineChannel(
            assistant, "@handoff-line", "1650000000",
            protector.Protect(AssistantLineChannel.ChannelSecretPurpose, LineSecret, now),
            protector.Protect(AssistantLineChannel.AccessTokenPurpose, bot.AccessToken, now),
            "歡迎！", AssistantLineChannel.DefaultNonTextReply, now);
        channel.RecordConnectionChecks(
            [.. LineConnectionCheck.All.Select(kind => new LineConnectionCheck(kind, LineConnectionCheckState.Passed, "通過。"))], bot.UserId, now);
        channel.Publish(org.Admin.Id, now);
        dbContext.AssistantLineChannels.Add(channel);
        await dbContext.SaveChangesAsync(CancellationToken);
        return new LineSetup(org.Organization.Id, assistantId, bot);
    }

    private IReadOnlyList<LineRequest> LineReplies(LineSetup setup) =>
        [.. _host.Line.RequestsWith(setup.Bot.AccessToken).Where(request => request.Endpoint == FakeLineServer.Reply)];

    private async Task PostLineAsync(LineSetup setup, string lineEvent)
    {
        var body = $$"""{"destination":"{{setup.Bot.UserId}}","events":[{{lineEvent}}]}""";
        var bytes = Encoding.UTF8.GetBytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/line/webhook/{setup.AssistantId}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("x-line-signature", LineWebhookSignature.Compute(LineSecret, bytes));
        var response = await _host.Factory.CreateClient().SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string LineUser(string userId) => $$"""{"type":"user","userId":"{{userId}}"}""";

    private static string LineGroup(string groupId, string userId) => $$"""{"type":"group","groupId":"{{groupId}}","userId":"{{userId}}"}""";

    private static string LineText(string source, string text, string replyToken, string? mentionees = null)
    {
        var id = "m-" + Guid.NewGuid().ToString("N");
        var message = mentionees is null
            ? JsonSerializer.Serialize(new { id, type = "text", quoteToken = "q", text })
            : $$"""{"id":"{{id}}","type":"text","quoteToken":"q","text":{{JsonSerializer.Serialize(text)}},"mention":{"mentionees":""" + mentionees + "}}";
        return $$"""
            {"type":"message","mode":"active","timestamp":1759737600000,"source":{{source}},
             "webhookEventId":"{{"01J" + Guid.NewGuid().ToString("N")[..23].ToUpperInvariant()}}",
             "deliveryContext":{"isRedelivery":false},"replyToken":"{{replyToken}}","message":{{message}}}
            """;
    }

    private HttpClient VisitorClient(WebApplicationFactory<Program>? via = null) =>
        (via ?? _host.Factory).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static async Task<string> SessionTokenAsync(HttpClient visitor, Guid assistantId)
    {
        var response = await visitor.PostAsJsonAsync($"/api/v1/public/assistants/{assistantId}/visitor-sessions", new { host = (string?)null }, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("token").GetString()!;
    }

    private static async Task<string?> ReplyKindAsync(HttpClient visitor, Guid assistantId, string token, string question)
    {
        var run = await RunAsync(visitor, assistantId, token, question);
        run.Status.ShouldBe(HttpStatusCode.OK, run.Body);
        var reply = ParseEvents(run.Body)
            .Single(e => e.GetProperty("type").GetString() == "CUSTOM" && e.GetProperty("name").GetString() == "smartagri.reply")
            .GetProperty("value");
        return reply.GetProperty("reply").GetProperty("kind").GetString();
    }

    private static async Task<RecordedRun> RunAsync(HttpClient visitor, Guid assistantId, string token, string question)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/public/assistants/{assistantId}/chat/runs")
        {
            Content = JsonContent.Create(new
            {
                threadId = string.Empty,
                runId = "run-handoff",
                state = new { },
                messages = new[] { new { id = "question", role = "user", content = question } },
                tools = Array.Empty<object>(),
                context = Array.Empty<object>(),
                forwardedProps = new { },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Visitor", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await visitor.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return new RecordedRun(response.StatusCode, body, [.. ParseEvents(body).Select(e => e.GetProperty("type").GetString()!)]);
    }

    private static IReadOnlyList<JsonElement> ParseEvents(string body) =>
    [
        .. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(record => string.Concat(record.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line["data: ".Length..])))
            .Where(data => data.Length > 0)
            .Select(data => JsonDocument.Parse(data).RootElement.Clone()),
    ];

    private static Task EventuallyAsync(Func<bool> condition, string what) =>
        EventuallyAsync(() => Task.FromResult(condition()), what);

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new ShouldAssertException($"Timed out waiting for {what}.");
            }

            await Task.Delay(50, CancellationToken);
        }
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();

    /// <summary>An <see cref="IEmailSender"/> whose every member throws: the notifier's own failure path.</summary>
    private sealed class BrokenEmailSender : IEmailSender
    {
        public bool IsConfigured => throw new InvalidOperationException("broken on purpose");

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) => throw new InvalidOperationException("broken on purpose");
    }
}

/// <summary>The Api host of <see cref="VisitorHandoffTests"/>: the usual one, with <c>Smtp</c> pointed at
/// its own <see cref="FakeSmtpServer"/> (signing in, no TLS — allowed in Development only).</summary>
public sealed class HandoffHostFixture : AuthHostFixture
{
    public const string From = "notify@example.org";
    public const string Username = "notify@example.org";
    public const string SmtpPassword = "smtp-app-password-9c1e";

    public FakeSmtpServer Smtp { get; } = new();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.Configure<SmtpOptions>(options =>
        {
            options.Host = "127.0.0.1";
            options.Port = Smtp.Port;
            options.From = From;
            options.Username = Username;
            options.Password = SmtpPassword;
            options.UseStartTls = false;
        });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Smtp.DisposeAsync();
    }
}
