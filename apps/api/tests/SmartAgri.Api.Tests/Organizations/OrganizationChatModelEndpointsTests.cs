using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Ai;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// <see cref="AuthHostFixture"/> plus a second host on the same database whose deployment also offers
/// <see cref="SecondChatModel"/> (with the real resolver): a choice made there is <c>selected</c>, and
/// the same choice read through the original host is <c>removed</c>.
/// </summary>
public sealed class OrganizationChatModelHostFixture : AuthHostFixture
{
    private WebApplicationFactory<Program>? _offered;

    /// <summary>The host whose deployment offers the default and <see cref="SecondChatModel"/>.</summary>
    public WebApplicationFactory<Program> Offered => _offered ?? throw new InvalidOperationException("Not initialized.");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _offered = SecondChatModel.Offered(Factory);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_offered is not null)
        {
            await _offered.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}

/// <summary>
/// <c>GET/PUT /api/v1/organization/chat-model</c>, the manager check and the organization activity
/// log (M6 plan §5 Slice 2, issue #239) against real PostgreSQL. That the choice reaches answers and
/// test runs: <c>ChatRunEndpointsTests.ChatModel.cs</c>, <c>AssistantTestRunEndpointsTests.ChatModel.cs</c>.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class OrganizationChatModelEndpointsTests : IClassFixture<OrganizationChatModelHostFixture>
{
    private const string Password = "Org-Chat-Model-Pass-1!";
    private const string Path = "/api/v1/organization/chat-model";
    private const string DefaultModel = AuthHostFixture.ChatModel;

    private readonly OrganizationChatModelHostFixture _host;

    public OrganizationChatModelEndpointsTests(OrganizationChatModelHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Everyone_in_the_organization_reads_the_list_the_choice_and_whether_they_may_change_it()
    {
        var org = await CreateOrganizationAsync();

        foreach (var (login, canChange) in new[] { ("admin", true), ("internal", false), ("external", false) })
        {
            var caller = await SignInAsync(org, login);
            var response = await caller.Spa.GetAsync(Path, caller.Token);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, login);
            var body = await BodyJsonAsync(response);
            OpenApiContract.AssertKeysMatchSchema(body, "OrganizationChatModelView");
            Options(body).ShouldBe([(DefaultModel, DefaultModel, DefaultModel), (SecondChatModel.Id, SecondChatModel.DisplayName, SecondChatModel.Model)]);
            body.GetProperty("selectedId").ValueKind.ShouldBe(JsonValueKind.Null);
            Effective(body).ShouldBe((DefaultModel, DefaultModel, DefaultModel));
            body.GetProperty("source").GetString().ShouldBe("deployment-default");
            body.GetProperty("canChange").GetBoolean().ShouldBe(canChange, login);
            body.GetProperty("lastChange").ValueKind.ShouldBe(JsonValueKind.Null);
            body.GetProperty("revision").GetInt32().ShouldBe(0);
        }

        (await Client(_host.Offered).Http.GetAsync(Path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_managers_change_writes_one_activity_and_the_same_value_again_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var changed = await admin.Spa.PutAsync(Path, admin.Token, new { modelId = SecondChatModel.Id, revision = 0 });

        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(changed);
        OpenApiContract.AssertKeysMatchSchema(body, "OrganizationChatModelView");
        body.GetProperty("selectedId").GetString().ShouldBe(SecondChatModel.Id);
        Effective(body).ShouldBe((SecondChatModel.Id, SecondChatModel.DisplayName, SecondChatModel.Model));
        body.GetProperty("source").GetString().ShouldBe("selected");
        body.GetProperty("revision").GetInt32().ShouldBe(1);
        var lastChange = body.GetProperty("lastChange");
        lastChange.GetProperty("actorName").GetString().ShouldBe("模型商行管理者");
        lastChange.GetProperty("at").GetDateTimeOffset().ShouldBe(_host.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));

        var activity = (await ActivitiesAsync(org)).ShouldHaveSingleItem();
        (activity.Action, activity.ActorAccountId).ShouldBe((OrganizationActivityAction.ChatModelChanged, (Guid?)org.Admin.Id));
        using (var detail = JsonDocument.Parse(activity.Detail!))
        {
            Side(detail.RootElement.GetProperty("from")).ShouldBe(((string?)null, (string?)DefaultModel));
            Side(detail.RootElement.GetProperty("to")).ShouldBe(((string?)SecondChatModel.Id, (string?)SecondChatModel.DisplayName));
            detail.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["from", "to"], ignoreOrder: true);
        }

        // The same value again: 200, nothing written, the revision unchanged.
        var same = await admin.Spa.PutAsync(Path, admin.Token, new { modelId = SecondChatModel.Id, revision = 1 });
        same.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyJsonAsync(same)).GetProperty("revision").GetInt32().ShouldBe(1);
        (await ActivitiesAsync(org)).Count.ShouldBe(1);

        // null: back to the deployment default, a second row.
        var reset = await BodyJsonAsync(await admin.Spa.PutAsync(Path, admin.Token, new { modelId = (string?)null, revision = 1 }));
        reset.GetProperty("selectedId").ValueKind.ShouldBe(JsonValueKind.Null);
        reset.GetProperty("source").GetString().ShouldBe("deployment-default");
        reset.GetProperty("revision").GetInt32().ShouldBe(2);
        var activities = await ActivitiesAsync(org);
        activities.Count.ShouldBe(2);
        JsonDocument.Parse(activities[1].Detail!).RootElement.GetProperty("to").GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);

        // Another member reads who changed it last.
        var member = await SignInAsync(org, "internal");
        (await BodyJsonAsync(await member.Spa.GetAsync(Path, member.Token)))
            .GetProperty("lastChange").GetProperty("actorName").GetString().ShouldBe("模型商行管理者");
    }

    [Fact]
    public async Task Internal_employees_and_external_customers_get_the_same_403_as_every_other_non_manager()
    {
        var org = await CreateOrganizationAsync();
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.OrganizationSettings));

        foreach (var login in new[] { "internal", "external" })
        {
            var caller = await SignInAsync(org, login);
            var response = await caller.Spa.PutAsync(Path, caller.Token, new { modelId = SecondChatModel.Id, revision = 0 });

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, login);
            (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, login);
            response.Content.Headers.ContentType?.ToString().ShouldBe(expected.ContentType);
            var body = JsonDocument.Parse(expected.Body).RootElement;
            (body.GetProperty("reason").GetString(), body.GetProperty("message").GetString())
                .ShouldBe(("organization-settings", "只有管理者可以變更組織設定。"));

            // Even a malformed body: the manager check comes first.
            using var malformed = new HttpRequestMessage(HttpMethod.Put, Path)
            {
                Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
            };
            malformed.Headers.Authorization = new("Bearer", caller.Token);
            (await caller.Spa.Http.SendAsync(malformed, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, login);
        }

        (await LoadAsync(org)).SettingsRevision.ShouldBe(0);
        (await ActivitiesAsync(org)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_organizations_manager_is_no_manager_here()
    {
        var first = await CreateOrganizationAsync();
        var second = await CreateOrganizationAsync();

        // The check reads the account under the organization filter of the token's organization: a
        // manager of another organization is simply not found there, so the policy fails exactly as
        // for a non-manager (same reason metadata, same bytes as asserted above).
        await using var dbContext = _host.Postgres.CreateDbContext(second.Organization.Id);
        var roles = new RequestAccountRole(dbContext);
        (await roles.GetAsync(first.Admin.Id, CancellationToken)).ShouldBeNull();
        (await roles.IsOrganizationAdminAsync(first.Admin.Id, CancellationToken)).ShouldBeFalse();
        (await roles.IsOrganizationAdminAsync(second.Admin.Id, CancellationToken)).ShouldBeTrue();
        (await roles.IsOrganizationAdminAsync(second.Internal.Id, CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_role_is_read_from_the_database_so_the_same_token_loses_and_gains_the_right_at_once()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "internal");
        (await admin.Spa.PutAsync(Path, admin.Token, new { modelId = SecondChatModel.Id, revision = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetRoleAsync(org, org.Admin.Id, AccountRole.InternalEmployee);
        await SetRoleAsync(org, org.Internal.Id, AccountRole.SmbAdmin);

        var demoted = await admin.Spa.PutAsync(Path, admin.Token, new { modelId = (string?)null, revision = 1 });
        demoted.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(demoted)).GetProperty("reason").GetString().ShouldBe("organization-settings");
        (await BodyJsonAsync(await admin.Spa.GetAsync(Path, admin.Token))).GetProperty("canChange").GetBoolean().ShouldBeFalse();

        (await BodyJsonAsync(await member.Spa.GetAsync(Path, member.Token))).GetProperty("canChange").GetBoolean().ShouldBeTrue();
        (await member.Spa.PutAsync(Path, member.Token, new { modelId = (string?)null, revision = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_id_not_in_the_list_is_422_and_a_stale_revision_409_and_neither_writes()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var unknown = await admin.Spa.PutAsync(Path, admin.Token, new { modelId = "not-offered", revision = 0 });
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(unknown)).GetProperty("errors").GetProperty("modelId").GetArrayLength().ShouldBe(1);

        var stale = await admin.Spa.PutAsync(Path, admin.Token, new { modelId = SecondChatModel.Id, revision = 7 });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyJsonAsync(stale)).GetProperty("reason").GetString().ShouldBe("organization-settings-conflict");

        var organization = await LoadAsync(org);
        (organization.ChatModelId, organization.SettingsRevision).ShouldBe(((string?)null, 0));
        (await ActivitiesAsync(org)).ShouldBeEmpty();

        // Ids compare ignoring case; the canonical spelling is stored.
        var differentCase = await BodyJsonAsync(await admin.Spa.PutAsync(Path, admin.Token, new { modelId = "SECOND", revision = 0 }));
        differentCase.GetProperty("selectedId").GetString().ShouldBe(SecondChatModel.Id);
    }

    [Fact]
    public async Task A_choice_the_deployment_no_longer_offers_is_removed_and_the_default_is_used()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        (await admin.Spa.PutAsync(Path, admin.Token, new { modelId = SecondChatModel.Id, revision = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The original host's deployment offers only the default.
        var elsewhere = await SignInAsync(org, "internal", _host.Factory);
        var body = await BodyJsonAsync(await elsewhere.Spa.GetAsync(Path, elsewhere.Token));

        Options(body).ShouldBe([(DefaultModel, DefaultModel, DefaultModel)]);
        body.GetProperty("selectedId").GetString().ShouldBe(SecondChatModel.Id);
        Effective(body).ShouldBe((DefaultModel, DefaultModel, DefaultModel));
        body.GetProperty("source").GetString().ShouldBe("removed");

        await using var scope = _host.Factory.Services.CreateAsyncScope();
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var resolved = await new OrganizationChatModelResolver(scope.ServiceProvider.GetRequiredService<ChatModelCatalog>(), dbContext)
            .ResolveAsync(CancellationToken);
        (resolved.Entry.Model, resolved.Source).ShouldBe((DefaultModel, ChatModelSource.Removed));
    }

    [Fact]
    public async Task No_response_contains_a_models_key_or_endpoint()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var texts = new List<string>
        {
            await (await admin.Spa.GetAsync(Path, admin.Token)).Content.ReadAsStringAsync(CancellationToken),
            await (await admin.Spa.PutAsync(Path, admin.Token, new { modelId = SecondChatModel.Id, revision = 0 })).Content.ReadAsStringAsync(CancellationToken),
        };

        foreach (var text in texts)
        {
            text.ShouldContain(SecondChatModel.Model);
            text.ShouldNotContain(SecondChatModel.ApiKey);
            text.ShouldNotContain("second-model.internal");
            using var document = JsonDocument.Parse(text);
            PropertyNames(document.RootElement).ShouldNotContain(name =>
                name.Contains("key", StringComparison.OrdinalIgnoreCase) || name.Contains("endpoint", StringComparison.OrdinalIgnoreCase)
                || name.Contains("provider", StringComparison.OrdinalIgnoreCase));
        }

        var activity = (await ActivitiesAsync(org)).ShouldHaveSingleItem();
        activity.Detail!.ShouldNotContain(SecondChatModel.ApiKey);
        activity.Detail!.ShouldNotContain("second-model.internal");
    }

    [Fact]
    public async Task The_resolver_reads_the_choice_once_per_scope_and_afresh_in_the_next()
    {
        var org = await CreateOrganizationAsync();
        await using var scope = _host.Offered.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ChatModelCatalog>();
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        await ChooseDirectlyAsync(org, SecondChatModel.Id);

        var resolver = new OrganizationChatModelResolver(catalog, dbContext);
        var first = await resolver.ResolveAsync(CancellationToken);
        (first.Entry.Id, first.Source).ShouldBe((SecondChatModel.Id, ChatModelSource.Selected));

        await ChooseDirectlyAsync(org, null);
        (await resolver.ResolveAsync(CancellationToken)).ShouldBeSameAs(first, "one answer for the whole scope");

        var next = await new OrganizationChatModelResolver(catalog, dbContext).ResolveAsync(CancellationToken);
        (next.Entry.Model, next.Source).ShouldBe((DefaultModel, ChatModelSource.DeploymentDefault));
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account External);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<TestOrganization> CreateOrganizationAsync()
    {
        var organization = await _host.CreateOrganizationAsync("模型商行");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "模型商行管理者", AccountPermission.ManageAssistants);
        // A colleague who builds assistants (manage-assistants) is still not a manager (decision A).
        var member = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, "模型商行同仁",
            AccountPermission.ManageAssistants, AccountPermission.ManagePublishing);
        var external = await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, "模型商行客戶", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, member, external);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName, WebApplicationFactory<Program>? via = null)
    {
        var spa = Client(via ?? _host.Offered);
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static SpaClient Client(WebApplicationFactory<Program> factory) =>
        new(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    private async Task SetRoleAsync(TestOrganization org, Guid accountId, AccountRole role)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Accounts
                .Where(account => account.Id == accountId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Role, role), CancellationToken))
            .ShouldBe(1);
    }

    private async Task ChooseDirectlyAsync(TestOrganization org, string? modelId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var organization = await dbContext.Organizations.SingleAsync(candidate => candidate.Id == org.Organization.Id, CancellationToken);
        organization.ChangeChatModel(modelId, organization.SettingsRevision).ShouldBe(OrganizationSettingsChange.Changed);
        await dbContext.SaveChangesAsync(CancellationToken);
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

    private static List<(string?, string?, string?)> Options(JsonElement body) =>
        [.. body.GetProperty("options").EnumerateArray().Select(Triple)];

    private static (string?, string?, string?) Effective(JsonElement body) => Triple(body.GetProperty("effective"));

    private static (string?, string?) Side(JsonElement side) =>
        (side.GetProperty("id").GetString(), side.GetProperty("displayName").GetString());

    private static (string?, string?, string?) Triple(JsonElement option) =>
        (option.GetProperty("id").GetString(), option.GetProperty("displayName").GetString(), option.GetProperty("model").GetString());

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => PropertyNames(property.Value).Prepend(property.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
