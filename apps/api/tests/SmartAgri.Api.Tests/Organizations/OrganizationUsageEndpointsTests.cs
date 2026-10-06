using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary><c>GET /api/v1/organization/usage</c> (M5a plan §3 F, issue #195) against real PostgreSQL.</summary>
[Trait("Category", TestCategories.Docker)]
public class OrganizationUsageEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Org-Usage-Pass-1!";
    private const string Path = "/api/v1/organization/usage";

    private readonly AuthHostFixture _host;

    public OrganizationUsageEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_publishing_manager_gets_month_used_limit_and_state_of_the_chat_calls_only()
    {
        var (organization, spa, token) = await SignedInAsync(AccountPermission.ManagePublishing);
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 1_000, 500);
        await AddCallAsync(organization, ModelInvocationPurpose.DatabaseQuery, 100, null);
        await AddCallAsync(organization, ModelInvocationPurpose.EmbedQuery, 999_999, null);

        var response = await spa.GetAsync(Path, token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "OrganizationUsageView");
        var taipei = TimeZoneInfo.ConvertTime(_host.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));
        body.GetProperty("month").GetString().ShouldBe($"{taipei.Year:0000}-{taipei.Month:00}");
        body.GetProperty("usedTokens").GetInt64().ShouldBe(1_600);
        body.GetProperty("limitTokens").GetInt64().ShouldBe(2_000_000);
        body.GetProperty("state").GetString().ShouldBe("normal");
    }

    [Fact]
    public async Task The_organizations_own_limit_and_state_are_reported_near_from_80_percent_exceeded_from_100()
    {
        var (organization, spa, token) = await SignedInAsync(AccountPermission.ManagePublishing);
        await SetLimitAsync(organization, 1_000);
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 700, 100);

        var near = await BodyJsonAsync(await spa.GetAsync(Path, token));
        near.GetProperty("limitTokens").GetInt64().ShouldBe(1_000);
        near.GetProperty("usedTokens").GetInt64().ShouldBe(800);
        near.GetProperty("state").GetString().ShouldBe("near");

        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 200, 0);
        _host.Clock.Advance(TimeSpan.FromSeconds(31));
        var exceeded = await BodyJsonAsync(await spa.GetAsync(Path, token));
        exceeded.GetProperty("usedTokens").GetInt64().ShouldBe(1_000);
        exceeded.GetProperty("state").GetString().ShouldBe("exceeded");
    }

    [Fact]
    public async Task Without_manage_publishing_it_is_403_publishing_and_without_a_token_401()
    {
        var (_, spa, token) = await SignedInAsync(AccountPermission.ManageAssistants, AccountPermission.ReadConsentedSubmissions);

        var forbidden = await spa.GetAsync(Path, token);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(forbidden)).GetProperty("reason").GetString().ShouldBe("publishing");

        (await _host.CreateSpaClient().Http.GetAsync(Path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Each_organization_sees_only_its_own_usage()
    {
        var (first, firstSpa, firstToken) = await SignedInAsync(AccountPermission.ManagePublishing);
        var (_, secondSpa, secondToken) = await SignedInAsync(AccountPermission.ManagePublishing);
        await AddCallAsync(first, ModelInvocationPurpose.GenerateAnswer, 123, 0);

        (await BodyJsonAsync(await firstSpa.GetAsync(Path, firstToken))).GetProperty("usedTokens").GetInt64().ShouldBe(123);
        (await BodyJsonAsync(await secondSpa.GetAsync(Path, secondToken))).GetProperty("usedTokens").GetInt64().ShouldBe(0);
    }

    private async Task<(Organization Organization, SpaClient Spa, string Token)> SignedInAsync(params AccountPermission[] permissions)
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        await _host.CreateAccountAsync(organization, "member", Password, AccountRole.SmbAdmin, "成員", permissions);
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "member", Password);
        return (organization, spa, token.AccessToken);
    }

    private async Task AddCallAsync(Organization organization, ModelInvocationPurpose purpose, long? input, long? output)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        dbContext.ModelInvocations.Add(ModelInvocation.Record(
            organization.Id, null, null, purpose, "fake", "fake-model", input, output, 5, succeeded: true, _host.Clock.GetUtcNow()));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task SetLimitAsync(Organization organization, long limit)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        (await dbContext.Organizations.SingleAsync(candidate => candidate.Id == organization.Id, CancellationToken)).SetMonthlyTokenLimit(limit);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
