using System.Net;
using System.Text.Json;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Tests.Team;

/// <summary>
/// Issue #361: every list of accounts to pick from leaves deactivated accounts out — assistant and
/// knowledge-base share targets, data-manager candidates, case-group member candidates — while what was
/// already chosen for them stays (a save that sends it back keeps it; it cannot be newly chosen), the
/// database auto-case choices count only members in use, and reactivating puts the account back on offer.
/// Issue #363: what is still chosen for a deactivated account is listed to the owner, flagged
/// <c>deactivated</c>, until the owner removes it.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DeactivatedAccountPickerTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Picker-Pass-1!";

    private readonly AuthHostFixture _host;

    public DeactivatedAccountPickerTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pickers_leave_deactivated_accounts_out_keep_what_was_chosen_and_offer_them_again_once_reactivated()
    {
        var organization = await _host.CreateOrganizationAsync("選單商行");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, "使用中的同仁", AccountPermission.UseSharedAssistants);
        var retired = await _host.CreateAccountAsync(
            organization, "retired", Password, AccountRole.InternalEmployee, "停用的同仁", AccountPermission.UseSharedAssistants);
        var gone = await _host.CreateAccountAsync(
            organization, "gone", Password, AccountRole.InternalEmployee, "另一位停用的同仁", AccountPermission.UseSharedAssistants);

        // The retired colleague was chosen everywhere before leaving; the other one nowhere.
        var now = DateTimeOffset.UtcNow;
        var assistant = Assistant.Create(
            organization.Id, admin.Id, "管理者的助理", "回答問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, now);
        var knowledgeBase = KnowledgeBase.Create(organization.Id, admin.Id, "設備手冊", "設備說明", now);
        knowledgeBase.ChangeSharing(KnowledgeSharingScope.SpecificAccounts, allowOriginalDownload: false, now);
        var database = Database.Create(organization.Id, admin.Id, "巡檢紀錄", "巡檢", DatabaseTemplateId.Blank, now);
        var form = DatabaseFormVersion.Create(
            database, 1, [new DatabaseFormField("field-note", "備註", DatabaseFieldType.Text, false, [], null, string.Empty)], admin.Id, now);
        var group = CaseGroup.Create(organization.Id, "設備組", now);
        var type = CaseType.Create(organization.Id, "設備故障報修", "", group, 72, isActive: true, now);
        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            dbContext.AddRange(assistant, knowledgeBase, database, form, group, type);
            await dbContext.SaveChangesAsync(CancellationToken);
            dbContext.AddRange(
                new AssistantShare(assistant, retired.Id),
                new KnowledgeBaseShare(knowledgeBase, retired.Id),
                DatabaseDataManager.Create(database, admin.Id, admin.Id, now),
                DatabaseDataManager.Create(database, retired.Id, admin.Id, now),
                CaseGroupMember.Create(group, colleague.Id, admin.Id, now),
                CaseGroupMember.Create(group, retired.Id, admin.Id, now));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        using var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;

        // Neither holds anything to hand over, so no successor is needed (also issue #361).
        foreach (var account in new[] { retired, gone })
        {
            var deactivated = await spa.PostAsync($"/api/v1/team/members/{account.Id}:deactivate", token, new { successorAccountId = (Guid?)null });
            deactivated.StatusCode.ShouldBe(HttpStatusCode.OK, await deactivated.Content.ReadAsStringAsync(CancellationToken));
        }

        var picks = await ReadPicksAsync(spa, token, assistant.Id, knowledgeBase.Id, database.Id);
        picks.AssistantCandidates.ShouldBe([colleague.Id], "assistant share targets");
        picks.Flagged.ShouldBe([retired.Id, retired.Id, retired.Id], "listed, flagged, wherever still chosen (issue #363)");
        picks.KnowledgeBaseCandidates.ShouldBe([colleague.Id], "knowledge-base share targets");
        picks.DataManagerCandidates.ShouldBe([admin.Id, colleague.Id], "data-manager candidates, in role order");
        picks.CaseGroupCandidates.ShouldBe([admin.Id, colleague.Id], "case-group member candidates");
        picks.AutoCaseMemberCount.ShouldBe(1, "only the colleague in use handles the group's cases");

        // What was chosen for the retired colleague stays chosen, and is listed where it was.
        picks.AssistantShared.ShouldBe([retired.Id]);
        picks.KnowledgeBaseShared.ShouldBe([retired.Id]);
        picks.DataManagers.ShouldBe([admin.Id, retired.Id], ignoreOrder: true);
        picks.CaseGroupMembers.ShouldBe([colleague.Id, retired.Id], ignoreOrder: true);

        // Saving each picker with the existing choice sent back keeps it; a deactivated account that
        // was never chosen cannot be added: dropped like an unknown id for sharing, refused otherwise.
        var platform = await spa.PutAsync(
            $"/api/v1/assistants/{assistant.Id}/publishing/platform", token,
            new { accountIds = new[] { retired.Id.ToString(), gone.Id.ToString(), colleague.Id.ToString() } });
        platform.StatusCode.ShouldBe(HttpStatusCode.OK);
        Ids((await BodyJsonAsync(platform)).GetProperty("allowedAccountIds")).ShouldBe([retired.Id, colleague.Id], ignoreOrder: true);

        var sharing = await spa.PutAsync($"/api/v1/knowledge-bases/{knowledgeBase.Id}/sharing", token, new
        {
            scope = "specific-accounts",
            sharedWithAccountIds = new[] { retired.Id.ToString(), gone.Id.ToString() },
            allowOriginalDownload = false,
        });
        sharing.StatusCode.ShouldBe(HttpStatusCode.OK, await sharing.Content.ReadAsStringAsync(CancellationToken));
        var onlyGone = await spa.PutAsync($"/api/v1/knowledge-bases/{knowledgeBase.Id}/sharing", token, new
        {
            scope = "specific-accounts",
            sharedWithAccountIds = new[] { gone.Id.ToString() },
            allowOriginalDownload = false,
        });
        onlyGone.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "nobody valid is left to share with");

        (await spa.PutAsync($"/api/v1/databases/{database.Id}/access", token, new { dataManagerAccountIds = new[] { admin.Id, retired.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var newManager = await spa.PutAsync(
            $"/api/v1/databases/{database.Id}/access", token, new { dataManagerAccountIds = new[] { admin.Id, retired.Id, gone.Id } });
        newManager.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await spa.PutAsync($"/api/v1/case-groups/{group.Id}/members", token, new { accountIds = new[] { colleague.Id, retired.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var newMember = await spa.PutAsync(
            $"/api/v1/case-groups/{group.Id}/members", token, new { accountIds = new[] { colleague.Id, retired.Id, gone.Id } });
        newMember.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(newMember)).GetProperty("reason").GetString().ShouldBe("member-not-eligible");

        picks = await ReadPicksAsync(spa, token, assistant.Id, knowledgeBase.Id, database.Id);
        picks.KnowledgeBaseShared.ShouldBe([retired.Id]);
        picks.DataManagers.ShouldBe([admin.Id, retired.Id], ignoreOrder: true);
        picks.CaseGroupMembers.ShouldBe([colleague.Id, retired.Id], ignoreOrder: true);

        // Reactivated: offered (and counted) again everywhere.
        (await spa.PostAsync($"/api/v1/team/members/{retired.Id}:reactivate", token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        picks = await ReadPicksAsync(spa, token, assistant.Id, knowledgeBase.Id, database.Id);
        picks.AssistantCandidates.ShouldBe([colleague.Id, retired.Id], ignoreOrder: true);
        picks.KnowledgeBaseCandidates.ShouldBe([colleague.Id, retired.Id], ignoreOrder: true);
        // Role, then display name by ordinal comparison: 「使用中的同仁」 before 「停用的同仁」.
        picks.DataManagerCandidates.ShouldBe([admin.Id, colleague.Id, retired.Id]);
        picks.CaseGroupCandidates.ShouldBe([admin.Id, colleague.Id, retired.Id]);
        picks.AutoCaseMemberCount.ShouldBe(2);
        picks.Flagged.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_owner_sees_deactivated_share_targets_flagged_until_removed_and_nobody_else_does()
    {
        var organization = await _host.CreateOrganizationAsync("停用標示商行");
        var owner = await _host.CreateAccountAsync(
            organization, "owner", Password, AccountRole.SmbAdmin, "擁有者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, "使用中的同仁",
            AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions);
        var retired = await _host.CreateAccountAsync(
            organization, "retired", Password, AccountRole.InternalEmployee, "停用的同仁",
            AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions);

        var now = DateTimeOffset.UtcNow;
        var assistant = Assistant.Create(
            organization.Id, owner.Id, "擁有者的助理", "回答問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, now);
        var knowledgeBase = KnowledgeBase.Create(organization.Id, owner.Id, "產品手冊", "產品說明", now);
        knowledgeBase.ChangeSharing(KnowledgeSharingScope.SpecificAccounts, allowOriginalDownload: false, now);
        var database = Database.Create(organization.Id, owner.Id, "回報紀錄", "回報", DatabaseTemplateId.Blank, now);
        var form = DatabaseFormVersion.Create(
            database, 1, [new DatabaseFormField("field-note", "備註", DatabaseFieldType.Text, false, [], null, string.Empty)], owner.Id, now);
        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            dbContext.AddRange(assistant, knowledgeBase, database, form);
            await dbContext.SaveChangesAsync(CancellationToken);
            dbContext.AddRange(
                new AssistantShare(assistant, colleague.Id),
                new AssistantShare(assistant, retired.Id),
                new KnowledgeBaseShare(knowledgeBase, colleague.Id),
                new KnowledgeBaseShare(knowledgeBase, retired.Id),
                DatabaseDataManager.Create(database, colleague.Id, owner.Id, now),
                DatabaseDataManager.Create(database, retired.Id, owner.Id, now));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        using var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "owner", Password)).AccessToken;

        // In use: nothing is flagged.
        (await ReadFlagsAsync(spa, token, assistant.Id, knowledgeBase.Id, database.Id)).ShouldBe(new Flags([], [], []));

        var deactivated = await spa.PostAsync($"/api/v1/team/members/{retired.Id}:deactivate", token, new { successorAccountId = (Guid?)null });
        deactivated.StatusCode.ShouldBe(HttpStatusCode.OK, await deactivated.Content.ReadAsStringAsync(CancellationToken));

        // The owner sees the deactivated account where it is still chosen, flagged; the colleague in use is not.
        var flags = await ReadFlagsAsync(spa, token, assistant.Id, knowledgeBase.Id, database.Id);
        flags.ShouldBe(new Flags([retired.Id], [retired.Id], [retired.Id]));

        // Anybody else: the publishing and knowledge-base pages are the owner's only, and a data manager
        // reading the database gets no candidates, so no flag either.
        var colleagueToken = (await spa.SignInAsync(organization.Code, "colleague", Password)).AccessToken;
        (await spa.GetAsync($"/api/v1/assistants/{assistant.Id}/publishing", colleagueToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await spa.GetAsync($"/api/v1/knowledge-bases/{knowledgeBase.Id}", colleagueToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var colleagueAccess = (await GetOkAsync(spa, colleagueToken, $"/api/v1/databases/{database.Id}")).GetProperty("access");
        colleagueAccess.GetProperty("viewerIsDataManager").GetBoolean().ShouldBeTrue();
        colleagueAccess.GetProperty("candidates").GetArrayLength().ShouldBe(0);
        colleagueAccess.GetProperty("dataManagers").EnumerateArray()
            .All(manager => !manager.TryGetProperty("deactivated", out _)).ShouldBeTrue("designations carry no flag");

        // The owner removes it in each picker; every save's response (and the next read) lists it no more.
        var platform = await spa.PutAsync(
            $"/api/v1/assistants/{assistant.Id}/publishing/platform", token, new { accountIds = new[] { colleague.Id.ToString() } });
        platform.StatusCode.ShouldBe(HttpStatusCode.OK);
        var platformBody = await BodyJsonAsync(platform);
        Ids(platformBody.GetProperty("allowedAccountIds")).ShouldBe([colleague.Id]);
        FlaggedIds(platformBody.GetProperty("candidates")).ShouldBeEmpty();
        CandidateIds(platformBody.GetProperty("candidates")).ShouldNotContain(retired.Id);

        var sharing = await spa.PutAsync($"/api/v1/knowledge-bases/{knowledgeBase.Id}/sharing", token, new
        {
            scope = "specific-accounts",
            sharedWithAccountIds = new[] { colleague.Id.ToString() },
            allowOriginalDownload = false,
        });
        sharing.StatusCode.ShouldBe(HttpStatusCode.OK, await sharing.Content.ReadAsStringAsync(CancellationToken));

        var access = await spa.PutAsync($"/api/v1/databases/{database.Id}/access", token, new { dataManagerAccountIds = new[] { colleague.Id } });
        access.StatusCode.ShouldBe(HttpStatusCode.OK, await access.Content.ReadAsStringAsync(CancellationToken));
        var accessBody = await BodyJsonAsync(access);
        FlaggedIds(accessBody.GetProperty("candidates")).ShouldBeEmpty();
        CandidateIds(accessBody.GetProperty("candidates")).ShouldNotContain(retired.Id);

        (await ReadFlagsAsync(spa, token, assistant.Id, knowledgeBase.Id, database.Id)).ShouldBe(new Flags([], [], []));
        var knowledge = await GetOkAsync(spa, token, $"/api/v1/knowledge-bases/{knowledgeBase.Id}");
        CandidateIds(knowledge.GetProperty("shareTargets")).ShouldBe([colleague.Id], "removed, so no longer listed");
    }

    private sealed record Flags(List<Guid> Assistant, List<Guid> KnowledgeBase, List<Guid> Database)
    {
        public bool Equals(Flags? other) =>
            other is not null
            && Assistant.SequenceEqual(other.Assistant)
            && KnowledgeBase.SequenceEqual(other.KnowledgeBase)
            && Database.SequenceEqual(other.Database);

        public override int GetHashCode() => HashCode.Combine(Assistant.Count, KnowledgeBase.Count, Database.Count);

        public override string ToString() =>
            $"assistant [{string.Join(", ", Assistant)}], knowledge base [{string.Join(", ", KnowledgeBase)}], database [{string.Join(", ", Database)}]";
    }

    private static async Task<Flags> ReadFlagsAsync(SpaClient spa, string token, Guid assistantId, Guid knowledgeBaseId, Guid databaseId)
    {
        var platform = (await GetOkAsync(spa, token, $"/api/v1/assistants/{assistantId}/publishing")).GetProperty("platform");
        var knowledge = await GetOkAsync(spa, token, $"/api/v1/knowledge-bases/{knowledgeBaseId}");
        var access = (await GetOkAsync(spa, token, $"/api/v1/databases/{databaseId}")).GetProperty("access");
        return new Flags(
            FlaggedIds(platform.GetProperty("candidates")),
            FlaggedIds(knowledge.GetProperty("shareTargets")),
            FlaggedIds(access.GetProperty("candidates")));
    }

    private sealed record Picks(
        List<Guid> AssistantCandidates,
        List<Guid> AssistantShared,
        List<Guid> KnowledgeBaseCandidates,
        List<Guid> KnowledgeBaseShared,
        List<Guid> DataManagerCandidates,
        List<Guid> DataManagers,
        List<Guid> CaseGroupCandidates,
        List<Guid> CaseGroupMembers,
        int AutoCaseMemberCount,
        List<Guid> Flagged);

    private static async Task<Picks> ReadPicksAsync(
        SpaClient spa, string token, Guid assistantId, Guid knowledgeBaseId, Guid databaseId)
    {
        var platform = (await GetOkAsync(spa, token, $"/api/v1/assistants/{assistantId}/publishing")).GetProperty("platform");
        var knowledge = await GetOkAsync(spa, token, $"/api/v1/knowledge-bases/{knowledgeBaseId}");
        var access = (await GetOkAsync(spa, token, $"/api/v1/databases/{databaseId}")).GetProperty("access");
        var groups = await GetOkAsync(spa, token, "/api/v1/case-groups");
        var autoCase = await GetOkAsync(spa, token, $"/api/v1/databases/{databaseId}/auto-case");
        return new Picks(
            CandidateIds(platform.GetProperty("candidates")),
            Ids(platform.GetProperty("allowedAccountIds")),
            CandidateIds(knowledge.GetProperty("shareTargets")),
            Ids(knowledge.GetProperty("sharing").GetProperty("sharedWithAccountIds")),
            CandidateIds(access.GetProperty("candidates")),
            [.. access.GetProperty("dataManagers").EnumerateArray().Select(item => item.GetProperty("account").GetProperty("id").GetGuid())],
            CandidateIds(groups.GetProperty("candidates")),
            CandidateIds(groups.GetProperty("groups")[0].GetProperty("members")),
            autoCase.GetProperty("options")[0].GetProperty("memberCount").GetInt32(),
            [.. FlaggedIds(platform.GetProperty("candidates")),
                .. FlaggedIds(knowledge.GetProperty("shareTargets")),
                .. FlaggedIds(access.GetProperty("candidates"))]);
    }

    private static async Task<JsonElement> GetOkAsync(SpaClient spa, string token, string path)
    {
        var response = await spa.GetAsync(path, token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{path}: {await response.Content.ReadAsStringAsync(CancellationToken)}");
        return await BodyJsonAsync(response);
    }

    /// <summary>The accounts on offer: a listed deactivated account (issue #363) is not.</summary>
    private static List<Guid> CandidateIds(JsonElement array) =>
        [.. array.EnumerateArray().Where(item => !IsFlagged(item)).Select(item => item.GetProperty("id").GetGuid())];

    /// <summary>The listed accounts flagged <c>deactivated</c> (issue #363).</summary>
    private static List<Guid> FlaggedIds(JsonElement array) =>
        [.. array.EnumerateArray().Where(IsFlagged).Select(item => item.GetProperty("id").GetGuid())];

    private static bool IsFlagged(JsonElement item) =>
        item.TryGetProperty("deactivated", out var flag) && flag.GetBoolean();

    private static List<Guid> Ids(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetGuid())];

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
