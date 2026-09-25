using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Domain.Accounts;

namespace SmartAgri.Api.Tests.Authorization;

/// <summary>Permission policies and their handler, with a fake permission source. No
/// database needed.</summary>
public class PermissionAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private readonly WebApplicationFactory<Program> _factory;

    public PermissionAuthorizationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Every_permission_has_a_policy_requiring_it()
    {
        var policies = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var permission in Enum.GetValues<AccountPermission>())
        {
            var policy = await policies.GetPolicyAsync(PermissionPolicies.NameFor(permission));

            policy.ShouldNotBeNull(permission.ToString());
            policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
            policy.Requirements.OfType<PermissionRequirement>().ShouldHaveSingleItem().Permission.ShouldBe(permission);
        }

        PermissionPolicies.NameFor(AccountPermission.ManageAssistants).ShouldBe("permission:manage-assistants");
    }

    [Fact]
    public async Task Endpoints_without_an_explicit_policy_require_a_signed_in_caller()
    {
        var fallback = await _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        fallback.ShouldNotBeNull();
        fallback.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Handler_succeeds_only_when_the_account_currently_holds_the_permission()
    {
        var source = new FakePermissionSource(AccountPermission.ManageAssistants);

        (await AuthorizeAsync(source, AccountPermission.ManageAssistants, SignedIn(AccountId))).ShouldBeTrue();
        (await AuthorizeAsync(source, AccountPermission.ManagePublishing, SignedIn(AccountId))).ShouldBeFalse();
    }

    [Fact]
    public async Task Handler_fails_without_a_usable_subject()
    {
        var source = new FakePermissionSource(AccountPermission.ManageAssistants);

        (await AuthorizeAsync(source, AccountPermission.ManageAssistants, new ClaimsPrincipal(new ClaimsIdentity()))).ShouldBeFalse();
        (await AuthorizeAsync(source, AccountPermission.ManageAssistants, SignedInWithSubject("not-a-guid"))).ShouldBeFalse();
        source.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Permissions_are_read_once_per_request()
    {
        var source = new FakePermissionSource(AccountPermission.ManageAssistants, AccountPermission.ReadOwnTracking);
        var perRequest = new RequestAccountPermissions(source);

        await perRequest.GetAsync(AccountId, CancellationToken.None);
        await perRequest.GetAsync(AccountId, CancellationToken.None);

        source.Calls.ShouldBe(1);

        // A new request (new scope) reads again, so permission changes apply immediately.
        await new RequestAccountPermissions(source).GetAsync(AccountId, CancellationToken.None);
        source.Calls.ShouldBe(2);
    }

    [Fact]
    public void Permissions_are_listed_in_declaration_order()
    {
        RequestAccountPermissions.Ordered(new HashSet<AccountPermission>
        {
            AccountPermission.ReadOwnTracking,
            AccountPermission.ManageAssistants,
            AccountPermission.UseSharedAssistants,
        }).ShouldBe([AccountPermission.ManageAssistants, AccountPermission.UseSharedAssistants, AccountPermission.ReadOwnTracking]);
    }

    [Fact]
    public async Task A_failed_policy_check_returns_the_same_bytes_as_the_forbidden_helper()
    {
        var httpContext = ApiErrorsTests.CreateHttpContext();
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ForbiddenReasonMetadata(ForbiddenReason.Team)), "test"));
        var nextCalled = false;

        await new ApiAuthorizationResultHandler().HandleAsync(
            _ => { nextCalled = true; return Task.CompletedTask; },
            httpContext,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            PolicyAuthorizationResult.Forbid());

        nextCalled.ShouldBeFalse();
        var viaPolicy = ApiErrorsTests.CapturedResponse.From(httpContext);
        var viaHelper = await ApiErrorsTests.ExecuteAsync(ApiErrors.NotFound(ForbiddenReason.Team));
        viaPolicy.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        viaPolicy.ContentType.ShouldBe(viaHelper.ContentType);
        viaPolicy.Body.ShouldBe(viaHelper.Body);
    }

    [Fact]
    public async Task A_successful_policy_check_continues_the_pipeline()
    {
        var httpContext = ApiErrorsTests.CreateHttpContext();
        var nextCalled = false;

        await new ApiAuthorizationResultHandler().HandleAsync(
            _ => { nextCalled = true; return Task.CompletedTask; },
            httpContext,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            PolicyAuthorizationResult.Success());

        nextCalled.ShouldBeTrue();
    }

    private static async Task<bool> AuthorizeAsync(IAccountPermissionSource source, AccountPermission permission, ClaimsPrincipal user)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(source)
            .AddScoped<RequestAccountPermissions>()
            .AddSingleton<IHttpContextAccessor, HttpContextAccessor>()
            .AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorizationBuilder().AddPermissionPolicies();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(user, PermissionPolicies.NameFor(permission));
        return result.Succeeded;
    }

    private static ClaimsPrincipal SignedIn(Guid accountId) => SignedInWithSubject(accountId.ToString("D"));

    private static ClaimsPrincipal SignedInWithSubject(string subject) =>
        new(new ClaimsIdentity([new Claim("sub", subject)], authenticationType: "test"));

    private sealed class FakePermissionSource : IAccountPermissionSource
    {
        private readonly HashSet<AccountPermission> _granted;

        public FakePermissionSource(params AccountPermission[] granted)
        {
            _granted = [.. granted];
        }

        public int Calls { get; private set; }

        public Task<IReadOnlySet<AccountPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlySet<AccountPermission>>(accountId == AccountId ? _granted : new HashSet<AccountPermission>());
        }
    }
}
