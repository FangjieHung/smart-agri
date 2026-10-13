using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Api.Tests.Team;

/// <summary>
/// 停用成員並指定接手人 and 重新啟用 (issue #356) against real PostgreSQL and the real OpenIddict
/// pipeline: the member can no longer sign in and every token they hold stops working; their assets
/// go to the successor, who can then manage them; the successor rules, the self and last-manager
/// guards, reactivation, organization isolation, and one transaction for all of it.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class MemberOffboardingTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Offboarding-Pass-1!";
    private const string TeamPath = "/api/v1/team";

    private readonly AuthHostFixture _host;

    public MemberOffboardingTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deactivating_hands_every_asset_to_the_successor_who_can_then_manage_them()
    {
        var org = await CreateOrganizationAsync();
        var assets = await GiveMemberAssetsAsync(org);

        var preview = await GetJsonOkAsync(org.AdminSpa, $"{TeamPath}/members/{org.Member.Id}/offboarding", org.AdminToken);
        preview.GetProperty("block").ValueKind.ShouldBe(JsonValueKind.Null);
        var holdings = preview.GetProperty("holdings");
        (holdings.GetProperty("assistants").GetInt32(), holdings.GetProperty("drafts").GetInt32(),
            holdings.GetProperty("knowledgeBases").GetInt32(), holdings.GetProperty("databases").GetInt32(),
            holdings.GetProperty("openIssues").GetInt32(), holdings.GetProperty("openCases").GetInt32(),
            holdings.GetProperty("borrowedSources").GetInt32())
            .ShouldBe((1, 1, 1, 1, 1, 1, 1));
        Strings(preview.GetProperty("requiredPermissions")).ShouldBe(["manage-assistants", "manage-data-sources"]);
        var candidates = preview.GetProperty("candidates").EnumerateArray().ToDictionary(candidate => candidate.GetProperty("id").GetGuid());
        candidates.Keys.ShouldNotContain(org.Member.Id);
        candidates[org.Successor.Id].GetProperty("eligible").GetBoolean().ShouldBeTrue();
        candidates[org.Successor.Id].GetProperty("canHandleIssues").GetBoolean().ShouldBeTrue();
        candidates[org.Customer.Id].GetProperty("ineligibleReason").GetString().ShouldBe("external-customer");
        candidates[org.Colleague.Id].GetProperty("ineligibleReason").GetString().ShouldBe("missing-permissions");
        Strings(candidates[org.Colleague.Id].GetProperty("missingPermissions")).ShouldBe(["manage-data-sources"]);

        var response = await DeactivateAsync(org, org.Member.Id, org.Successor.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        var transferred = body.GetProperty("transferred");
        (transferred.GetProperty("assistants").GetInt32(), transferred.GetProperty("drafts").GetInt32(),
            transferred.GetProperty("knowledgeBases").GetInt32(), transferred.GetProperty("databases").GetInt32(),
            transferred.GetProperty("issuesReassigned").GetInt32(), transferred.GetProperty("issuesUnassigned").GetInt32(),
            transferred.GetProperty("cases").GetInt32())
            .ShouldBe((1, 1, 1, 1, 1, 0, 1));
        var memberRow = body.GetProperty("team").GetProperty("members").EnumerateArray()
            .Single(member => member.GetProperty("id").GetGuid() == org.Member.Id);
        memberRow.GetProperty("deactivatedAt").ValueKind.ShouldBe(JsonValueKind.String);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.Assistants.SingleAsync(item => item.Id == assets.AssistantId, CancellationToken)).OwnerAccountId.ShouldBe(org.Successor.Id);
            (await dbContext.AssistantDrafts.SingleAsync(item => item.Id == assets.DraftId, CancellationToken)).OwnerAccountId.ShouldBe(org.Successor.Id);
            (await dbContext.KnowledgeBases.SingleAsync(item => item.Id == assets.KnowledgeBaseId, CancellationToken)).OwnerAccountId.ShouldBe(org.Successor.Id);
            (await dbContext.Databases.SingleAsync(item => item.Id == assets.DatabaseId, CancellationToken)).OwnerAccountId.ShouldBe(org.Successor.Id);

            // An owner is never a share target: the successor's own share rows go; the colleague's stay.
            (await dbContext.AssistantShares.Where(share => share.AssistantId == assets.AssistantId).Select(share => share.AccountId).ToListAsync(CancellationToken))
                .ShouldBe([org.Colleague.Id]);
            (await dbContext.KnowledgeBaseShares.Where(share => share.KnowledgeBaseId == assets.KnowledgeBaseId).Select(share => share.AccountId).ToListAsync(CancellationToken))
                .ShouldBe([org.Colleague.Id]);

            // The successor takes the member's place among the data managers, with history.
            (await dbContext.DatabaseDataManagers.Where(designation => designation.DatabaseId == assets.DatabaseId).Select(designation => designation.AccountId).ToListAsync(CancellationToken))
                .ShouldBe([org.Successor.Id]);
            (await dbContext.DatabaseDataManagerChanges.Where(change => change.DatabaseId == assets.DatabaseId)
                    .Select(change => new { change.AccountId, change.Assigned, change.ChangedByAccountId }).ToListAsync(CancellationToken))
                .ShouldBe(
                [
                    new { AccountId = org.Member.Id, Assigned = false, ChangedByAccountId = org.Admin.Id },
                    new { AccountId = org.Successor.Id, Assigned = true, ChangedByAccountId = org.Admin.Id },
                ], ignoreOrder: true);

            // A share someone else gave the member is theirs to change: untouched.
            (await dbContext.KnowledgeBaseShares.AnyAsync(share => share.KnowledgeBaseId == assets.BorrowedKnowledgeBaseId && share.AccountId == org.Member.Id, CancellationToken))
                .ShouldBeTrue();

            var issue = await dbContext.AssistantIssues.SingleAsync(item => item.Id == assets.IssueId, CancellationToken);
            issue.AssigneeAccountId.ShouldBe(org.Successor.Id);
            var assigned = await dbContext.AssistantIssueEvents.Where(item => item.IssueId == assets.IssueId)
                .OrderBy(item => item.Ordinal).LastAsync(CancellationToken);
            (assigned.Action, assigned.ActorAccountId, assigned.AssigneeAccountId)
                .ShouldBe((AssistantIssueEventAction.Assigned, org.Admin.Id, (Guid?)org.Successor.Id));

            var handedOver = await dbContext.Cases.SingleAsync(item => item.Id == assets.CaseId, CancellationToken);
            (handedOver.OwnerAccountId, handedOver.Status).ShouldBe(((Guid?)org.Successor.Id, CaseStatus.InProgress));
            var accepted = await dbContext.CaseEvents.Where(item => item.CaseId == assets.CaseId).OrderBy(item => item.Ordinal).LastAsync(CancellationToken);
            (accepted.Action, accepted.ActorAccountId, accepted.OwnerAccountId).ShouldBe((CaseEventAction.Accepted, (Guid?)org.Successor.Id, (Guid?)org.Successor.Id));
            accepted.Note.ShouldBe("「離職同仁」的帳號已停用，案件移交給「接手同仁」。");

            // Private conversations are not transferred (retention decides when they go).
            (await dbContext.ChatThreads.SingleAsync(item => item.Id == assets.ThreadId, CancellationToken)).AccountId.ShouldBe(org.Member.Id);

            var activity = await dbContext.OrganizationActivities.SingleAsync(item => item.Action == OrganizationActivityAction.MemberDeactivated, CancellationToken);
            activity.ActorAccountId.ShouldBe(org.Admin.Id);
            using var detail = JsonDocument.Parse(activity.Detail!);
            detail.RootElement.GetProperty("member").GetProperty("id").GetGuid().ShouldBe(org.Member.Id);
            detail.RootElement.GetProperty("successor").GetProperty("displayName").GetString().ShouldBe("接手同仁");
            detail.RootElement.GetProperty("transferred").GetProperty("assistants").GetInt32().ShouldBe(1);
        }

        // The successor manages every one of them through the ordinary endpoints.
        var successor = org.SuccessorSpa;
        (await successor.GetAsync($"/api/v1/assistants/{assets.AssistantId}/settings", org.SuccessorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await successor.GetAsync($"/api/v1/assistant-drafts/{assets.DraftId}", org.SuccessorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await successor.GetAsync($"/api/v1/knowledge-bases/{assets.KnowledgeBaseId}", org.SuccessorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var database = await successor.GetAsync($"/api/v1/databases/{assets.DatabaseId}", org.SuccessorToken);
        database.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await database.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("\"viewerCanManage\":true");
        var assignedIssues = await GetJsonOkAsync(successor, "/api/v1/issues?scope=assigned", org.SuccessorToken);
        assignedIssues.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(assets.IssueId);
        var caseDetail = await GetJsonOkAsync(successor, $"/api/v1/cases/{assets.CaseId}", org.SuccessorToken);
        Strings(caseDetail.GetProperty("allowedActions")).ShouldContain("complete");

        // Someone else still cannot.
        (await org.ColleagueSpa.GetAsync($"/api/v1/assistants/{assets.AssistantId}/settings", org.ColleagueToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_deactivated_member_cannot_sign_in_and_every_token_they_hold_stops_working()
    {
        var org = await CreateOrganizationAsync();
        using var memberSpa = _host.CreateSpaClient();
        var token = await memberSpa.SignInAsync(org.Organization.Code, "member", Password, SpaClient.SpaScope);
        (await memberSpa.GetMeAsync(token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await DeactivateAsync(org, org.Member.Id, org.Successor.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The access token in hand: refused on its very next call, /me included.
        (await memberSpa.GetMeAsync(token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await memberSpa.GetAsync("/api/v1/assistants", token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The refresh token: revoked, like every token entry of the account.
        await ShouldBeRejectedAsync(await memberSpa.RefreshAsync(token.RefreshToken!));
        (await TokenStatusesAsync(org.Member.Id)).ShouldAllBe(status => status == Statuses.Revoked);

        // The sign-in cookie: its security stamp was rotated, so it mints no new code.
        var authorize = await memberSpa.AuthorizeAsync(SpaClient.NewCodeVerifier(), scope: SpaClient.SpaScope);
        authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        authorize.Headers.Location!.ToString().ShouldNotContain("code=");

        // The password: the same 401 as a wrong one.
        using var again = _host.CreateSpaClient();
        var login = await again.LoginAsync(org.Organization.Code, "member", Password);
        var wrong = await again.LoginAsync(org.Organization.Code, "member", "not-the-password");
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldBeIdenticalAsync(login, wrong);
    }

    /// <summary>
    /// The checks that hold even when nothing was revoked: an account marked deactivated without its
    /// stamp rotated or its tokens revoked (as if a token entry had been missed) still cannot refresh,
    /// call the API or sign in — the token endpoint (<c>CanContinueAsync</c>), the per-request account
    /// check and the login endpoint each look at <c>DeactivatedAt</c> themselves.
    /// </summary>
    [Fact]
    public async Task Each_entry_point_refuses_a_deactivated_account_on_its_own()
    {
        var org = await CreateOrganizationAsync();
        using var memberSpa = _host.CreateSpaClient();
        var token = await memberSpa.SignInAsync(org.Organization.Code, "member", Password, SpaClient.SpaScope);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var account = await dbContext.Accounts.SingleAsync(candidate => candidate.Id == org.Member.Id, CancellationToken);
            account.Deactivate(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await ShouldBeRejectedAsync(await memberSpa.RefreshAsync(token.RefreshToken!));
        (await memberSpa.GetMeAsync(token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var fresh = _host.CreateSpaClient())
        {
            (await fresh.LoginAsync(org.Organization.Code, "member", Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var authorize = await memberSpa.AuthorizeAsync(SpaClient.NewCodeVerifier(), scope: SpaClient.SpaScope);
        authorize.Headers.Location!.ToString().ShouldNotContain("code=");
    }

    [Fact]
    public async Task The_successor_must_be_another_internal_member_in_use_holding_the_needed_permissions()
    {
        var org = await CreateOrganizationAsync();
        var assets = await GiveMemberAssetsAsync(org);
        var other = await _host.CreateOrganizationAsync("別的組織");
        var otherAdmin = await _host.CreateAccountAsync(
            other, "admin", Password, AccountRole.SmbAdmin, "別的管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);
        var retired = await _host.CreateAccountAsync(
            org.Organization, "retired", Password, AccountRole.InternalEmployee, "已停用的同仁",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.Accounts.SingleAsync(account => account.Id == retired.Id, CancellationToken)).Deactivate(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        foreach (var successorId in new Guid?[] { null, org.Member.Id, org.Customer.Id, org.Colleague.Id, retired.Id, otherAdmin.Id, Guid.NewGuid() })
        {
            var response = await org.AdminSpa.PostAsync(
                $"{TeamPath}/members/{org.Member.Id}:deactivate", org.AdminToken, new { successorAccountId = successorId });
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, $"successor {successorId}");
            (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("successor-not-eligible");
        }

        // Nothing was written by any of them.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.Accounts.SingleAsync(account => account.Id == org.Member.Id, CancellationToken)).DeactivatedAt.ShouldBeNull();
            (await dbContext.Assistants.SingleAsync(item => item.Id == assets.AssistantId, CancellationToken)).OwnerAccountId.ShouldBe(org.Member.Id);
            (await dbContext.OrganizationActivities.AnyAsync(item => item.Action == OrganizationActivityAction.MemberDeactivated, CancellationToken)).ShouldBeFalse();
        }

        // The deactivated one is not even offered.
        var preview = await GetJsonOkAsync(org.AdminSpa, $"{TeamPath}/members/{org.Member.Id}/offboarding", org.AdminToken);
        preview.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("id").GetGuid()).ShouldNotContain(retired.Id);

        // The caller may take it over themselves.
        (await DeactivateAsync(org, org.Member.Id, org.Admin.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_handle_assistant_issues_the_successor_gets_the_assets_but_the_issues_become_unassigned()
    {
        var org = await CreateOrganizationAsync();
        var assets = await GiveMemberAssetsAsync(org);

        // The admin holds manage-assistants and manage-data-sources but not handle-assistant-issues.
        var response = await DeactivateAsync(org, org.Member.Id, org.Admin.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var transferred = (await BodyJsonAsync(response)).GetProperty("transferred");
        (transferred.GetProperty("issuesReassigned").GetInt32(), transferred.GetProperty("issuesUnassigned").GetInt32()).ShouldBe((0, 1));

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantIssues.SingleAsync(item => item.Id == assets.IssueId, CancellationToken)).AssigneeAccountId.ShouldBeNull();
        (await dbContext.Assistants.SingleAsync(item => item.Id == assets.AssistantId, CancellationToken)).OwnerAccountId.ShouldBe(org.Admin.Id);
    }

    [Fact]
    public async Task Nobody_deactivates_themselves_or_the_last_manager_in_use()
    {
        var org = await CreateOrganizationAsync();

        var self = await DeactivateAsync(org, org.Admin.Id, org.Successor.Id);
        self.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(self)).GetProperty("reason").GetString().ShouldBe("cannot-deactivate-self");
        (await GetJsonOkAsync(org.AdminSpa, $"{TeamPath}/members/{org.Admin.Id}/offboarding", org.AdminToken))
            .GetProperty("block").GetString().ShouldBe("self");

        // The successor manages the team (manage-assistants) but is not a manager: the admin is the
        // organization's only smb-admin in use.
        var lastAdmin = await org.SuccessorSpa.PostAsync(
            $"{TeamPath}/members/{org.Admin.Id}:deactivate", org.SuccessorToken, new { successorAccountId = org.Successor.Id });
        lastAdmin.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(lastAdmin)).GetProperty("reason").GetString().ShouldBe("last-admin");
        (await GetJsonOkAsync(org.SuccessorSpa, $"{TeamPath}/members/{org.Admin.Id}/offboarding", org.SuccessorToken))
            .GetProperty("block").GetString().ShouldBe("last-admin");

        // A second manager in use lifts it; a deactivated one would not have.
        await _host.CreateAccountAsync(org.Organization, "admin2", Password, AccountRole.SmbAdmin, "第二位管理者", AccountPermission.ManageAssistants);
        (await org.SuccessorSpa.PostAsync(
                $"{TeamPath}/members/{org.Admin.Id}:deactivate", org.SuccessorToken, new { successorAccountId = org.Successor.Id }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reactivating_lets_the_member_sign_in_again_and_brings_no_asset_back()
    {
        var org = await CreateOrganizationAsync();
        var assets = await GiveMemberAssetsAsync(org);
        (await DeactivateAsync(org, org.Member.Id, org.Successor.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var twice = await DeactivateAsync(org, org.Member.Id, org.Successor.Id);
        twice.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(twice)).GetProperty("reason").GetString().ShouldBe("member-already-deactivated");

        var reactivated = await org.AdminSpa.PostAsync($"{TeamPath}/members/{org.Member.Id}:reactivate", org.AdminToken, new { });
        reactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(reactivated)).GetProperty("members").EnumerateArray()
            .Single(member => member.GetProperty("id").GetGuid() == org.Member.Id)
            .GetProperty("deactivatedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        using var memberSpa = _host.CreateSpaClient();
        var token = await memberSpa.SignInAsync(org.Organization.Code, "member", Password, SpaClient.SpaScope);
        (await memberSpa.GetMeAsync(token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await memberSpa.RefreshOkAsync(token.RefreshToken!);

        var again = await org.AdminSpa.PostAsync($"{TeamPath}/members/{org.Member.Id}:reactivate", org.AdminToken, new { });
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(again)).GetProperty("reason").GetString().ShouldBe("member-not-deactivated");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Assistants.SingleAsync(item => item.Id == assets.AssistantId, CancellationToken)).OwnerAccountId.ShouldBe(org.Successor.Id);
        (await dbContext.OrganizationActivities.Where(item => item.Action == OrganizationActivityAction.MemberReactivated).CountAsync(CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Another_organization_is_not_touched_and_cannot_even_see_the_member()
    {
        var org = await CreateOrganizationAsync("安心商行");
        var control = await CreateOrganizationAsync("對照商行");
        var controlAssets = await GiveMemberAssetsAsync(control);
        using var controlMemberSpa = _host.CreateSpaClient();
        var controlToken = await controlMemberSpa.SignInAsync(control.Organization.Code, "member", Password, SpaClient.SpaScope);

        // The control organization's manager addressing the first organization's member: the same 403
        // as for an id that does not exist, from all three endpoints.
        var preview = await control.AdminSpa.GetAsync($"{TeamPath}/members/{org.Member.Id}/offboarding", control.AdminToken);
        var deactivate = await control.AdminSpa.PostAsync(
            $"{TeamPath}/members/{org.Member.Id}:deactivate", control.AdminToken, new { successorAccountId = control.Successor.Id });
        var reactivate = await control.AdminSpa.PostAsync($"{TeamPath}/members/{org.Member.Id}:reactivate", control.AdminToken, new { });
        var unknown = await control.AdminSpa.GetAsync($"{TeamPath}/members/{Guid.NewGuid()}/offboarding", control.AdminToken);
        foreach (var refused in new[] { preview, deactivate, reactivate })
        {
            await ShouldBeIdenticalAsync(refused, unknown);
        }

        unknown.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await DeactivateAsync(org, org.Member.Id, org.Successor.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await controlMemberSpa.GetMeAsync(controlToken.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await controlMemberSpa.RefreshOkAsync(controlToken.RefreshToken!);
        await using var dbContext = _host.Postgres.CreateDbContext(control.Organization.Id);
        (await dbContext.Accounts.SingleAsync(account => account.Id == control.Member.Id, CancellationToken)).DeactivatedAt.ShouldBeNull();
        (await dbContext.Assistants.SingleAsync(item => item.Id == controlAssets.AssistantId, CancellationToken)).OwnerAccountId.ShouldBe(control.Member.Id);
        (await dbContext.AssistantIssues.SingleAsync(item => item.Id == controlAssets.IssueId, CancellationToken)).AssigneeAccountId.ShouldBe(control.Member.Id);
        (await dbContext.OrganizationActivities.AnyAsync(CancellationToken)).ShouldBeFalse();
    }

    /// <summary>
    /// A failure at the very last write (the activity row, after every asset moved, the account was
    /// marked and its tokens revoked) leaves nothing changed: one transaction.
    /// </summary>
    [Fact]
    public async Task A_failure_at_the_last_step_leaves_nothing_half_done()
    {
        var org = await CreateOrganizationAsync();
        var assets = await GiveMemberAssetsAsync(org);
        using var memberSpa = _host.CreateSpaClient();
        var token = await memberSpa.SignInAsync(org.Organization.Code, "member", Password, SpaClient.SpaScope);

        var function = $"fail_offboarding_{org.Organization.Id:N}";
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
#pragma warning disable EF1002 // The names are this test's own GUID, not input.
            await dbContext.Database.ExecuteSqlRawAsync(
                $$"""
                CREATE FUNCTION {{function}}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."OrganizationId" = '{{org.Organization.Id}}' AND NEW."Action" = 'member-deactivated' THEN
                        RAISE EXCEPTION 'forced failure for the transaction test';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER {{function}} BEFORE INSERT ON "OrganizationActivities" FOR EACH ROW EXECUTE FUNCTION {{function}}();
                """,
                CancellationToken);
#pragma warning restore EF1002
        }

        try
        {
            (await DeactivateAsync(org, org.Member.Id, org.Successor.Id)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
#pragma warning disable EF1002
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""DROP TRIGGER {function} ON "OrganizationActivities"; DROP FUNCTION {function}();""", CancellationToken);
#pragma warning restore EF1002
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.Accounts.SingleAsync(account => account.Id == org.Member.Id, CancellationToken)).DeactivatedAt.ShouldBeNull();
            (await dbContext.Assistants.SingleAsync(item => item.Id == assets.AssistantId, CancellationToken)).OwnerAccountId.ShouldBe(org.Member.Id);
            (await dbContext.AssistantDrafts.SingleAsync(item => item.Id == assets.DraftId, CancellationToken)).OwnerAccountId.ShouldBe(org.Member.Id);
            (await dbContext.KnowledgeBases.SingleAsync(item => item.Id == assets.KnowledgeBaseId, CancellationToken)).OwnerAccountId.ShouldBe(org.Member.Id);
            (await dbContext.Databases.SingleAsync(item => item.Id == assets.DatabaseId, CancellationToken)).OwnerAccountId.ShouldBe(org.Member.Id);
            (await dbContext.AssistantShares.CountAsync(share => share.AssistantId == assets.AssistantId, CancellationToken)).ShouldBe(2);
            (await dbContext.DatabaseDataManagers.Where(designation => designation.DatabaseId == assets.DatabaseId).Select(designation => designation.AccountId).ToListAsync(CancellationToken))
                .ShouldBe([org.Member.Id]);
            (await dbContext.DatabaseDataManagerChanges.AnyAsync(change => change.DatabaseId == assets.DatabaseId, CancellationToken)).ShouldBeFalse();
            (await dbContext.AssistantIssues.SingleAsync(item => item.Id == assets.IssueId, CancellationToken)).AssigneeAccountId.ShouldBe(org.Member.Id);
            (await dbContext.Cases.SingleAsync(item => item.Id == assets.CaseId, CancellationToken)).OwnerAccountId.ShouldBe(org.Member.Id);
        }

        // The tokens were not revoked and the stamp not rotated either.
        (await memberSpa.GetMeAsync(token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await memberSpa.RefreshOkAsync(token.RefreshToken!);

        // And once the cause is gone, the same request goes through.
        (await DeactivateAsync(org, org.Member.Id, org.Successor.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_manage_assistants_the_caller_gets_the_teams_403()
    {
        var org = await CreateOrganizationAsync();
        await _host.CreateAccountAsync(org.Organization, "plain", Password, AccountRole.InternalEmployee, "一般同仁", AccountPermission.UseSharedAssistants);
        using var plainSpa = _host.CreateSpaClient();
        var plainToken = (await plainSpa.SignInAsync(org.Organization.Code, "plain", Password)).AccessToken;

        var preview = await plainSpa.GetAsync($"{TeamPath}/members/{org.Member.Id}/offboarding", plainToken);
        var deactivate = await plainSpa.PostAsync(
            $"{TeamPath}/members/{org.Member.Id}:deactivate", plainToken, new { successorAccountId = org.Successor.Id });
        var team = await plainSpa.GetAsync(TeamPath, plainToken);
        await ShouldBeIdenticalAsync(deactivate, team);
        preview.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ShouldBeIdenticalAsync(preview, team);
    }

    // ---------- fixtures ----------

    private sealed record TestOrganization(
        Organization Organization,
        Account Admin,
        SpaClient AdminSpa,
        string AdminToken,
        Account Member,
        Account Successor,
        SpaClient SuccessorSpa,
        string SuccessorToken,
        Account Colleague,
        SpaClient ColleagueSpa,
        string ColleagueToken,
        Account Customer);

    private sealed record MemberAssets(
        Guid AssistantId,
        Guid DraftId,
        Guid KnowledgeBaseId,
        Guid BorrowedKnowledgeBaseId,
        Guid DatabaseId,
        Guid IssueId,
        Guid CaseId,
        Guid ThreadId);

    /// <summary>
    /// The manager (all of the seed's manager permissions, no <c>handle-assistant-issues</c>), the member
    /// leaving, a successor who may take everything, a colleague missing <c>manage-data-sources</c>, and an
    /// external customer.
    /// </summary>
    private async Task<TestOrganization> CreateOrganizationAsync(string name = "停用商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, "離職同仁",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.HandleAssistantIssues,
            AccountPermission.ReadConsentedSubmissions);
        var successor = await _host.CreateAccountAsync(
            organization, "successor", Password, AccountRole.InternalEmployee, "接手同仁",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.HandleAssistantIssues,
            AccountPermission.ReadConsentedSubmissions);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, "其他同仁", AccountPermission.UseSharedAssistants,
            AccountPermission.ManageAssistants);
        var customer = await _host.CreateAccountAsync(
            organization, "customer", Password, AccountRole.ExternalCustomer, "外部客戶",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);

        var adminSpa = _host.CreateSpaClient();
        var successorSpa = _host.CreateSpaClient();
        var colleagueSpa = _host.CreateSpaClient();
        return new TestOrganization(
            organization,
            admin,
            adminSpa,
            (await adminSpa.SignInAsync(organization.Code, "admin", Password)).AccessToken,
            member,
            successor,
            successorSpa,
            (await successorSpa.SignInAsync(organization.Code, "successor", Password)).AccessToken,
            colleague,
            colleagueSpa,
            (await colleagueSpa.SignInAsync(organization.Code, "colleague", Password)).AccessToken,
            customer);
    }

    /// <summary>
    /// One of each: an assistant shared with the successor and the colleague, using the member's own
    /// knowledge base (shared the same way) and database (the member a data manager) plus a knowledge
    /// base the colleague owns and shared with the member; a draft; an unresolved 處理事項 assigned to
    /// the member; a case the member accepted; and one of the member's own conversations.
    /// </summary>
    private async Task<MemberAssets> GiveMemberAssetsAsync(TestOrganization org)
    {
        var organizationId = org.Organization.Id;
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);

        var assistant = Assistant.Create(
            organizationId, org.Member.Id, "離職同仁的助理", "回答設備問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, now);
        var knowledgeBase = KnowledgeBase.Create(organizationId, org.Member.Id, "設備手冊", "設備說明", now);
        knowledgeBase.ChangeSharing(KnowledgeSharingScope.SpecificAccounts, allowOriginalDownload: false, now);
        var borrowed = KnowledgeBase.Create(organizationId, org.Colleague.Id, "同仁的知識庫", "別人的", now);
        borrowed.ChangeSharing(KnowledgeSharingScope.SpecificAccounts, allowOriginalDownload: false, now);
        var database = Database.Create(organizationId, org.Member.Id, "巡檢紀錄", "巡檢", DatabaseTemplateId.Blank, now);
        var form = DatabaseFormVersion.Create(
            database, 1, [new DatabaseFormField("field-note", "備註", DatabaseFieldType.Text, false, [], null, string.Empty)], org.Member.Id, now);
        var draft = new AssistantDraft(organizationId, org.Member.Id, "{}", 1, now);
        dbContext.AddRange(assistant, knowledgeBase, borrowed, database, form, draft);
        await dbContext.SaveChangesAsync(CancellationToken);

        dbContext.AddRange(
            new AssistantShare(assistant, org.Successor.Id),
            new AssistantShare(assistant, org.Colleague.Id),
            new KnowledgeBaseShare(knowledgeBase, org.Successor.Id),
            new KnowledgeBaseShare(knowledgeBase, org.Colleague.Id),
            new KnowledgeBaseShare(borrowed, org.Member.Id),
            new AssistantKnowledgeBase(assistant, knowledgeBase, now),
            new AssistantKnowledgeBase(assistant, borrowed, now),
            new AssistantDatabase(assistant, database, now),
            DatabaseDataManager.Create(database, org.Member.Id, org.Member.Id, now));

        var (issue, issueCreated) = AssistantIssue.OpenFromHandoff(
            assistant, org.Colleague.Id, "冷藏庫溫度降不下來？", "請檢查門封條。", unverified: false, org.Member.Id, now);
        dbContext.AddRange(issue, issueCreated);

        var group = CaseGroup.Create(organizationId, "設備組", now);
        var type = CaseType.Create(organizationId, "設備故障報修", "", group, 72, isActive: true, now);
        var (item, caseCreated) = Case.Create(CaseOrigin.Manual, type, group, org.Colleague.Id, "冷藏庫溫度降不下來", "", now.AddHours(72), CaseLinks.None, now);
        var caseAccepted = item.Accept(org.Member.Id, now);
        dbContext.AddRange(group, type, item, caseCreated, caseAccepted);

        var thread = new ChatThread(assistant, org.Member.Id, "我的對話", now);
        dbContext.Add(thread);
        await dbContext.SaveChangesAsync(CancellationToken);

        return new MemberAssets(assistant.Id, draft.Id, knowledgeBase.Id, borrowed.Id, database.Id, issue.Id, item.Id, thread.Id);
    }

    // ---------- helpers ----------

    private static Task<HttpResponseMessage> DeactivateAsync(TestOrganization org, Guid memberId, Guid successorId) =>
        org.AdminSpa.PostAsync($"{TeamPath}/members/{memberId}:deactivate", org.AdminToken, new { successorAccountId = successorId });

    private async Task<List<string?>> TokenStatusesAsync(Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var statuses = await dbContext.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(token => token.Subject == accountId.ToString("D"))
            .Select(token => token.Status)
            .ToListAsync(CancellationToken);
        statuses.ShouldNotBeEmpty();
        return statuses;
    }

    private static async Task ShouldBeRejectedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString().ShouldBe(OpenIddict.Abstractions.OpenIddictConstants.Errors.InvalidGrant, body);
    }

    private static async Task<JsonElement> GetJsonOkAsync(SpaClient spa, string path, string accessToken)
    {
        var response = await spa.GetAsync(path, accessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await BodyJsonAsync(response);
    }

    /// <summary>Byte-for-byte identical: status, content type, body and whether a cookie is set.</summary>
    private static async Task ShouldBeIdenticalAsync(HttpResponseMessage actual, HttpResponseMessage expected)
    {
        var a = await ResponseFingerprint.FromAsync(actual);
        var b = await ResponseFingerprint.FromAsync(expected);
        a.Status.ShouldBe(b.Status);
        a.ContentType.ShouldBe(b.ContentType);
        a.Body.ShouldBe(b.Body);
        a.SetsCookie.ShouldBe(b.SetsCookie);
    }

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString())];

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
