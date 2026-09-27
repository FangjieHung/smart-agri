using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Knowledge.Extraction;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>A signed-in knowledge base owner of a fresh organization, and uploads through the
/// real endpoint (for the embedding and vector tests, Slice 7).</summary>
internal sealed record KnowledgeTestOwner(AuthHostFixture Host, Organization Organization, SpaClient Spa, string Token, Guid AccountId, Guid KnowledgeBaseId)
{
    private const string Password = "Knowledge-Embedding-Pass-1!";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler,
    /// which the fixture disposes.</remarks>
    public static async Task<KnowledgeTestOwner> CreateAsync(AuthHostFixture host, string name = "安心商行")
    {
        var organization = await host.CreateOrganizationAsync(name);
        await host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AccountPermission.ManageDataSources);
        var spa = host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;
        await using var dbContext = host.Postgres.CreateDbContext(organization.Id);
        var accountId = await dbContext.Accounts.Select(account => account.Id).SingleAsync(CancellationToken);

        var created = await spa.PostAsync("/api/v1/knowledge-bases", token, new { name = "退換貨政策", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("id").GetGuid();
        return new KnowledgeTestOwner(host, organization, spa, token, accountId, knowledgeBaseId);
    }

    /// <summary>Uploads a committed fixture; returns its version id (processing is queued).</summary>
    public async Task<Guid> UploadAsync(string fixture, string? fileName = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(KnowledgeFixtures.Read(fixture));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName ?? fixture);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-bases/{KnowledgeBaseId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await Spa.Http.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var documentId = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("id").GetGuid();
        await using var dbContext = Host.Postgres.CreateDbContext(Organization.Id);
        return await dbContext.KnowledgeDocumentVersions
            .Where(version => version.DocumentId == documentId)
            .Select(version => version.Id)
            .SingleAsync(CancellationToken);
    }
}
