using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// <c>GET/PUT /api/v1/organization/retention</c>, the preview, and the daily cleanup (M6 plan §5 Slice 4,
/// issue #241) against real PostgreSQL. Time is controlled by passing the cleanup an explicit instant
/// (and the handler a fixed clock); the HTTP calls use the host's clock. Cleanup tests:
/// <c>OrganizationRetentionTests.Cleanup.cs</c>.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed partial class OrganizationRetentionTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Org-Retention-Pass-1!";
    private const string Path = "/api/v1/organization/retention";
    private const string PreviewPath = "/api/v1/organization/retention/preview";

    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly AuthHostFixture _host;

    public OrganizationRetentionTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Everyone_in_the_organization_reads_the_retention_and_whether_they_may_change_it()
    {
        var org = await CreateOrganizationAsync();

        foreach (var (login, canChange) in new[] { ("admin", true), ("internal", false), ("external", false) })
        {
            var caller = await SignInAsync(org, login);
            var response = await caller.Spa.GetAsync(Path, caller.Token);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, login);
            var body = await BodyJsonAsync(response);
            OpenApiContract.AssertKeysMatchSchema(body, "OrganizationRetentionView");
            body.GetProperty("days").ValueKind.ShouldBe(JsonValueKind.Null);
            body.GetProperty("pending").ValueKind.ShouldBe(JsonValueKind.Null);
            body.GetProperty("options").EnumerateArray().Select(option => option.GetInt32()).ShouldBe([30, 90, 180, 365]);
            body.GetProperty("canChange").GetBoolean().ShouldBe(canChange, login);
            body.GetProperty("lastChange").ValueKind.ShouldBe(JsonValueKind.Null);
            body.GetProperty("revision").GetInt32().ShouldBe(0);
        }

        (await _host.CreateSpaClient().Http.GetAsync(Path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Shortening_waits_out_seven_days_in_which_nothing_is_deleted_and_the_first_cleanup_after_deletes()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var now = _host.Clock.GetUtcNow();
        var expired = await SeedThreadAsync(org, now.AddDays(-40));

        var response = await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision = 0 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "OrganizationRetentionView");
        body.GetProperty("days").ValueKind.ShouldBe(JsonValueKind.Null);
        var pending = body.GetProperty("pending");
        OpenApiContract.AssertKeysMatchSchema(pending, "OrganizationRetentionPendingView");
        pending.GetProperty("days").GetInt32().ShouldBe(30);
        var effectiveAt = pending.GetProperty("effectiveAt").GetDateTimeOffset();
        effectiveAt.ShouldBe(now.AddDays(7), TimeSpan.FromMinutes(1));
        body.GetProperty("revision").GetInt32().ShouldBe(1);
        body.GetProperty("lastChange").GetProperty("actorName").GetString().ShouldBe("保存商行管理者");

        var changed = (await ActivitiesAsync(org)).ShouldHaveSingleItem();
        (changed.Action, changed.ActorAccountId).ShouldBe((OrganizationActivityAction.RetentionChanged, (Guid?)org.Admin.Id));
        using (var detail = JsonDocument.Parse(changed.Detail!))
        {
            detail.RootElement.GetProperty("from").ValueKind.ShouldBe(JsonValueKind.Null);
            detail.RootElement.GetProperty("to").GetInt32().ShouldBe(30);
            detail.RootElement.GetProperty("effectiveAt").GetDateTimeOffset().ShouldBe(effectiveAt);
        }

        // Cleanups inside the buffer delete nothing: the current retention is still forever.
        foreach (var asOf in new[] { now.AddDays(1), effectiveAt.AddMinutes(-1) })
        {
            var inBuffer = await RunCleanupAsync(org, asOf);
            (inBuffer.Days, inBuffer.TookEffect, inBuffer.ThreadCount).ShouldBe(((int?)null, (RetentionSwitch?)null, 0));
            (await ThreadExistsAsync(org, expired.ThreadId)).ShouldBeTrue();
        }

        // The first cleanup after it makes 30 days current and deletes.
        var after = await RunCleanupAsync(org, effectiveAt.AddHours(1));
        after.TookEffect.ShouldBe(new RetentionSwitch(null, 30));
        (after.Days, after.ThreadCount).ShouldBe(((int?)30, 1));
        (await ThreadExistsAsync(org, expired.ThreadId)).ShouldBeFalse();
        (await ActivitiesAsync(org)).Select(activity => activity.Action).ShouldBe(
        [
            OrganizationActivityAction.RetentionChanged,
            OrganizationActivityAction.RetentionTookEffect,
            OrganizationActivityAction.RetentionCleanup,
        ]);

        var member = await SignInAsync(org, "internal");
        var read = await BodyJsonAsync(await member.Spa.GetAsync(Path, member.Token));
        read.GetProperty("days").GetInt32().ShouldBe(30);
        read.GetProperty("pending").ValueKind.ShouldBe(JsonValueKind.Null);
        read.GetProperty("revision").GetInt32().ShouldBe(2);
        read.GetProperty("lastChange").GetProperty("actorName").GetString().ShouldBe("系統");
    }

    [Fact]
    public async Task Changing_back_inside_the_buffer_deletes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var now = _host.Clock.GetUtcNow();
        var expired = await SeedThreadAsync(org, now.AddDays(-40));
        (await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var back = await BodyJsonAsync(await admin.Spa.PutAsync(Path, admin.Token, new { days = (int?)null, revision = 1 }));

        back.GetProperty("days").ValueKind.ShouldBe(JsonValueKind.Null);
        back.GetProperty("pending").ValueKind.ShouldBe(JsonValueKind.Null);
        back.GetProperty("revision").GetInt32().ShouldBe(2);
        var cancelled = (await ActivitiesAsync(org))[1];
        cancelled.Action.ShouldBe(OrganizationActivityAction.RetentionChangeCancelled);
        using (var detail = JsonDocument.Parse(cancelled.Detail!))
        {
            detail.RootElement.GetProperty("days").ValueKind.ShouldBe(JsonValueKind.Null);
            detail.RootElement.GetProperty("cancelledDays").GetInt32().ShouldBe(30);
        }

        var later = await RunCleanupAsync(org, now.AddDays(30));
        (later.Days, later.TookEffect, later.ThreadCount).ShouldBe(((int?)null, (RetentionSwitch?)null, 0));
        (await ThreadExistsAsync(org, expired.ThreadId)).ShouldBeTrue();

        // Nothing pending and the same value again: 200, nothing written.
        var same = await BodyJsonAsync(await admin.Spa.PutAsync(Path, admin.Token, new { days = (int?)null, revision = 2 }));
        same.GetProperty("revision").GetInt32().ShouldBe(2);
        (await ActivitiesAsync(org)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Lengthening_applies_at_once_and_drops_the_pending_value()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var now = _host.Clock.GetUtcNow();
        await MakeCurrentAsync(org, 30);
        var revision = (await LoadAsync(org)).SettingsRevision;
        var fortyDaysOld = await SeedThreadAsync(org, now.AddDays(-40));
        (await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var shorter = await BodyJsonAsync(await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision }));
        shorter.GetProperty("revision").GetInt32().ShouldBe(revision, "30 is already current");

        var longer = await BodyJsonAsync(await admin.Spa.PutAsync(Path, admin.Token, new { days = 90, revision }));
        longer.GetProperty("days").GetInt32().ShouldBe(90);
        longer.GetProperty("pending").ValueKind.ShouldBe(JsonValueKind.Null);
        var activity = (await ActivitiesAsync(org)).Last();
        using (var detail = JsonDocument.Parse(activity.Detail!))
        {
            (detail.RootElement.GetProperty("from").GetInt32(), detail.RootElement.GetProperty("to").GetInt32()).ShouldBe((30, 90));
            detail.RootElement.GetProperty("effectiveAt").GetDateTimeOffset().ShouldBe(now, TimeSpan.FromMinutes(1));
        }

        // A cleanup right now uses 90 days: the 40-day-old thread stays.
        (await RunCleanupAsync(org, _host.Clock.GetUtcNow())).ThreadCount.ShouldBe(0);
        (await ThreadExistsAsync(org, fortyDaysOld.ThreadId)).ShouldBeTrue();

        // And with a shorter one pending, a longer one wins at once too.
        (await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision = revision + 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var forever = await BodyJsonAsync(await admin.Spa.PutAsync(Path, admin.Token, new { days = (int?)null, revision = revision + 2 }));
        forever.GetProperty("days").ValueKind.ShouldBe(JsonValueKind.Null);
        forever.GetProperty("pending").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ActivitiesAsync(org)).Last().Action.ShouldBe(OrganizationActivityAction.RetentionChanged);
    }

    [Fact]
    public async Task Non_managers_get_the_same_403_for_the_change_and_the_preview()
    {
        var org = await CreateOrganizationAsync();
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.OrganizationSettings));

        foreach (var login in new[] { "internal", "external" })
        {
            var caller = await SignInAsync(org, login);
            var responses = new[]
            {
                await caller.Spa.PutAsync(Path, caller.Token, new { days = 30, revision = 0 }),
                await caller.Spa.GetAsync($"{PreviewPath}?days=30", caller.Token),
                await caller.Spa.GetAsync($"{PreviewPath}?days=45", caller.Token),
            };

            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, login);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, login);
                response.Content.Headers.ContentType?.ToString().ShouldBe(expected.ContentType);
            }
        }

        var organization = await LoadAsync(org);
        (organization.SettingsRevision, organization.PendingRetentionDays, organization.RetentionCleanupNextRunAt)
            .ShouldBe((0, (int?)null, (DateTimeOffset?)null));
        (await ActivitiesAsync(org)).ShouldBeEmpty();
        (await RetentionJobsAsync(org)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_period_outside_the_options_is_422_and_a_stale_revision_409_shared_with_the_chat_model()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        foreach (var days in new[] { 0, 45, 400 })
        {
            var put = await admin.Spa.PutAsync(Path, admin.Token, new { days, revision = 0 });
            put.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, days.ToString());
            (await BodyJsonAsync(put)).GetProperty("errors").GetProperty("days").GetArrayLength().ShouldBe(1);

            var preview = await admin.Spa.GetAsync($"{PreviewPath}?days={days}", admin.Token);
            preview.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, days.ToString());
            (await BodyJsonAsync(preview)).GetProperty("errors").GetProperty("days").GetArrayLength().ShouldBe(1);
        }

        (await admin.Spa.GetAsync(PreviewPath, admin.Token)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The chat model and the retention share one revision.
        (await admin.Spa.PutAsync("/api/v1/organization/chat-model", admin.Token, new { modelId = (string?)AuthHostFixture.ChatModel, revision = 0 }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var stale = await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision = 0 });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(stale)).GetProperty("reason").GetString().ShouldBe("organization-settings-conflict");

        var organization = await LoadAsync(org);
        (organization.SettingsRevision, organization.PendingRetentionDays).ShouldBe((1, (int?)null));
        (await ActivitiesAsync(org)).Select(activity => activity.Action).ShouldBe([OrganizationActivityAction.ChatModelChanged]);
        (await RetentionJobsAsync(org)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_preview_counts_what_a_cleanup_at_the_same_instant_deletes()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var now = _host.Clock.GetUtcNow();
        await SeedThreadAsync(org, now.AddDays(-200));
        await SeedThreadAsync(org, now.AddDays(-95));
        await SeedThreadAsync(org, now.AddDays(-40));
        await SeedThreadAsync(org, now.AddDays(-31.5));
        await SeedThreadAsync(org, now.AddDays(-20));
        await SeedThreadAsync(org, now.AddHours(-1));

        var counts = new Dictionary<int, (int Count, DateTimeOffset Cutoff)>();
        foreach (var days in OrganizationRetention.Options)
        {
            var response = await admin.Spa.GetAsync($"{PreviewPath}?days={days}", admin.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
            var body = await BodyJsonAsync(response);
            OpenApiContract.AssertKeysMatchSchema(body, "OrganizationRetentionPreviewView");
            body.GetProperty("days").GetInt32().ShouldBe(days);
            counts[days] = (body.GetProperty("threadCount").GetInt32(), body.GetProperty("cutoff").GetDateTimeOffset());
        }

        counts.ToDictionary(pair => pair.Key, pair => pair.Value.Count).ShouldBe(new Dictionary<int, int> { [30] = 4, [90] = 2, [180] = 1, [365] = 0 });
        counts[30].Cutoff.ShouldBe(RetentionCleanupRules.Cutoff(now, 30, Taipei));

        // The preview changed nothing.
        (await LoadAsync(org)).SettingsRevision.ShouldBe(0);
        (await ActivitiesAsync(org)).ShouldBeEmpty();

        await MakeCurrentAsync(org, 30);
        var cleanup = await RunCleanupAsync(org, now);
        (cleanup.ThreadCount, cleanup.Cutoff).ShouldBe((counts[30].Count, (DateTimeOffset?)counts[30].Cutoff));
    }

    [Fact]
    public async Task The_first_retention_in_days_starts_one_cleanup_chain_at_the_next_local_three_oclock()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var before = _host.Clock.GetUtcNow();

        (await admin.Spa.PutAsync(Path, admin.Token, new { days = 90, revision = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var nextRunAt = (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldNotBeNull();
        nextRunAt.ShouldBe(RetentionCleanupRules.NextRunAfter(before, Taipei), TimeSpan.FromMinutes(1));
        TimeZoneInfo.ConvertTime(nextRunAt, Taipei).TimeOfDay.ShouldBe(TimeSpan.FromHours(3));
        var job = (await RetentionJobsAsync(org)).ShouldHaveSingleItem();
        (job.Status, job.RunAfter, RunAtOf(job)).ShouldBe((BackgroundJobStatus.Queued, nextRunAt, nextRunAt));

        // Further changes — back to forever, then days again — keep the one chain.
        (await admin.Spa.PutAsync(Path, admin.Token, new { days = (int?)null, revision = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RetentionJobsAsync(org)).Count.ShouldBe(1);
        (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldBe(nextRunAt);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account External, Guid AssistantId);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private sealed record SeededThread(Guid ThreadId, Guid[] MessageIds);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<TestOrganization> CreateOrganizationAsync()
    {
        var organization = await _host.CreateOrganizationAsync("保存商行");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "保存商行管理者", AccountPermission.ManageAssistants);
        // Builds assistants and publishes them, and is still no manager (decision A).
        var member = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, "保存商行同仁",
            AccountPermission.ManageAssistants, AccountPermission.ManagePublishing);
        var external = await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, "保存商行客戶", AccountPermission.UseSharedAssistants);

        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var assistant = Assistant.Create(
            organization.Id, admin.Id, "保存助理", "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);
        dbContext.Assistants.Add(assistant);
        await dbContext.SaveChangesAsync(CancellationToken);
        return new TestOrganization(organization, admin, member, external, assistant.Id);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = new SpaClient(_host.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    /// <summary>A thread of the internal member whose question, reply (with one citation) and last
    /// activity are all at <paramref name="lastActivityAt"/>, or whose first message is at
    /// <paramref name="firstMessageAt"/> when given.</summary>
    private async Task<SeededThread> SeedThreadAsync(
        TestOrganization org, DateTimeOffset lastActivityAt, DateTimeOffset? firstMessageAt = null)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(candidate => candidate.Id == org.AssistantId, CancellationToken);
        var first = firstMessageAt ?? lastActivityAt;
        var thread = new ChatThread(assistant, org.Internal.Id, "保存期限測試", first);
        dbContext.ChatThreads.Add(thread);
        var question = ChatMessage.Account(thread, "退貨要多久？", first);
        var answer = ChatMessage.Assistant(thread, "七天內可以退貨。[1]", ChatReplyKind.CompanyData, null, [], lastActivityAt);
        dbContext.ChatMessages.AddRange(question, answer);
        dbContext.ChatMessageCitations.Add(new ChatMessageCitation(
            answer, ordinal: 1, chunkId: null, knowledgeBaseId: null, knowledgeBaseName: "退貨知識庫",
            documentId: null, documentName: "退貨政策", versionId: null, versionNumber: 1,
            versionEffectiveFrom: first.AddDays(-1), locationLabel: "第 1 頁", excerpt: "七天內可以退貨。", text: "七天內可以退貨。"));
        await dbContext.SaveChangesAsync(CancellationToken);
        return new SeededThread(thread.Id, [question.Id, answer.Id]);
    }

    private async Task<Guid> SeedOutcomeAsync(TestOrganization org, AnswerOutcomeChannel channel, DateTimeOffset at)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var outcome = AnswerOutcome.Record(org.Organization.Id, org.AssistantId, channel, AnswerReplyKind.CompanyData, null, [], at);
        dbContext.AnswerOutcomes.Add(outcome);
        await dbContext.SaveChangesAsync(CancellationToken);
        return outcome.Id;
    }

    /// <summary>Makes <paramref name="days"/> the current retention directly (pending, then past its
    /// buffer), without starting a chain.</summary>
    private async Task MakeCurrentAsync(TestOrganization org, int days)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var organization = await dbContext.Organizations.SingleAsync(candidate => candidate.Id == org.Organization.Id, CancellationToken);
        var longAgo = DateTimeOffset.UtcNow.AddYears(-5);
        organization.ChangeRetention(days, organization.SettingsRevision, longAgo).ShouldBe(OrganizationSettingsChange.Changed);
        if (organization.PendingRetentionDays is not null)
        {
            organization.ApplyDueRetention(longAgo.AddDays(8)).ShouldNotBeNull();
        }

        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private RetentionCleanupService NewService(AppDbContext dbContext, DateTimeOffset now, string timeZone = "Asia/Taipei", int? batchSize = null) =>
        new(
            dbContext,
            new FixedClock(now),
            Options.Create(new StatisticsOptions { TimeZone = timeZone }),
            _host.Factory.Services.GetRequiredService<RetentionCleanupMetrics>(),
            NullLogger<RetentionCleanupService>.Instance)
        {
            BatchSize = batchSize ?? RetentionCleanupRules.BatchSize,
        };

    private async Task<RetentionCleanupResult> RunCleanupAsync(
        TestOrganization org, DateTimeOffset asOf, string timeZone = "Asia/Taipei", int? batchSize = null)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await NewService(dbContext, asOf, timeZone, batchSize).RunAsync(asOf, CancellationToken);
    }

    private async Task<bool> ThreadExistsAsync(TestOrganization org, Guid threadId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.ChatThreads.AnyAsync(thread => thread.Id == threadId, CancellationToken);
    }

    private async Task<Organization> LoadAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        return await dbContext.Organizations.AsNoTracking().SingleAsync(candidate => candidate.Id == org.Organization.Id, CancellationToken);
    }

    private async Task<List<OrganizationActivity>> ActivitiesAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.OrganizationActivities.AsNoTracking()
            .OrderBy(activity => activity.At).ThenBy(activity => activity.Id)
            .ToListAsync(CancellationToken);
    }

    private async Task<List<BackgroundJob>> RetentionJobsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.BackgroundJobs.AsNoTracking()
            .Where(job => job.Kind == RetentionCleanupJob.Kind)
            .OrderBy(job => job.CreatedAt).ThenBy(job => job.Id)
            .ToListAsync(CancellationToken);
    }

    private static DateTimeOffset RunAtOf(BackgroundJob job) =>
        JsonSerializer.Deserialize<RetentionCleanupJob>(job.Payload, JsonSerializerOptions.Web)!.RunAt;

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
