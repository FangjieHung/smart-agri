using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.Authorization;

/// <summary>
/// The "must change password" gate (and, since #356, the deactivated-account check) in
/// <see cref="ApiAuthorizationResultHandler"/>, with a fake flag source, plus how the real endpoints are marked. No database needed; the flow
/// with real tokens is <c>PasswordChangeFlowTests</c> (Docker).
/// </summary>
public class PasswordChangeGateTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private readonly WebApplicationFactory<Program> _factory;

    public PasswordChangeGateTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_flagged_account_gets_password_change_required_from_a_protected_endpoint()
    {
        var source = new FakeFlagSource(flagged: true);
        var (response, nextCalled) = await HandleAsync(source, SignedIn(), PolicyAuthorizationResult.Success());

        nextCalled.ShouldBeFalse();
        response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        response.Body.ShouldBe((await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.PasswordChangeRequired))).Body);
        source.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task The_gate_wins_over_a_permission_specific_403()
    {
        var (response, nextCalled) = await HandleAsync(
            new FakeFlagSource(flagged: true),
            SignedIn(),
            PolicyAuthorizationResult.Forbid(),
            new ForbiddenReasonMetadata(ForbiddenReason.Team));

        nextCalled.ShouldBeFalse();
        Reason(response).ShouldBe("password-change-required");
    }

    [Fact]
    public async Task A_flagged_account_may_call_an_exempt_endpoint()
    {
        var source = new FakeFlagSource(flagged: true);
        var (_, nextCalled) = await HandleAsync(
            source, SignedIn(), PolicyAuthorizationResult.Success(), AllowedWhilePasswordChangeRequiredMetadata.Instance);

        nextCalled.ShouldBeTrue();

        // Read once all the same: a deactivated account is refused by exempt endpoints too (#356).
        source.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_deactivated_account_gets_a_bodiless_401_everywhere_even_on_an_exempt_endpoint()
    {
        foreach (var metadata in new object[][] { [], [AllowedWhilePasswordChangeRequiredMetadata.Instance] })
        {
            var source = new FakeFlagSource(flagged: false, deactivated: true);
            var (response, nextCalled) = await HandleAsync(source, SignedIn(), PolicyAuthorizationResult.Success(), metadata);

            nextCalled.ShouldBeFalse();
            response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized, "the bodiless 401 of a missing or expired token");
            response.Body.ShouldBeEmpty();
            source.Calls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_token_whose_sign_in_was_ended_gets_a_bodiless_401_everywhere_even_on_an_exempt_endpoint()
    {
        foreach (var metadata in new object[][] { [], [AllowedWhilePasswordChangeRequiredMetadata.Instance] })
        {
            var source = new FakeFlagSource(flagged: true, signInEnded: true);
            var (response, nextCalled) = await HandleAsync(source, SignedIn("authorization-1"), PolicyAuthorizationResult.Success(), metadata);

            nextCalled.ShouldBeFalse();
            response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized, "a password change on another device ended this sign-in (#360)");
            response.Body.ShouldBeEmpty();
            source.AuthorizationId.ShouldBe("authorization-1", "the access token's oi_au_id is what the standing read checks");
        }
    }

    [Fact]
    public async Task Deactivation_wins_over_the_password_gate_and_a_permission_specific_403()
    {
        var (response, nextCalled) = await HandleAsync(
            new FakeFlagSource(flagged: true, deactivated: true),
            SignedIn(),
            PolicyAuthorizationResult.Forbid(),
            new ForbiddenReasonMetadata(ForbiddenReason.Team));

        nextCalled.ShouldBeFalse();
        response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task An_account_without_the_flag_passes_and_keeps_its_permission_403()
    {
        (await HandleAsync(new FakeFlagSource(flagged: false), SignedIn(), PolicyAuthorizationResult.Success())).NextCalled.ShouldBeTrue();

        var (response, nextCalled) = await HandleAsync(
            new FakeFlagSource(flagged: false),
            SignedIn(),
            PolicyAuthorizationResult.Forbid(),
            new ForbiddenReasonMetadata(ForbiddenReason.Team));
        nextCalled.ShouldBeFalse();
        Reason(response).ShouldBe("team");
    }

    [Fact]
    public async Task Not_signed_in_is_still_a_challenge_and_never_reads_the_flag()
    {
        var source = new FakeFlagSource(flagged: true);

        var (response, nextCalled) = await HandleAsync(source, new ClaimsPrincipal(new ClaimsIdentity()), PolicyAuthorizationResult.Challenge());

        nextCalled.ShouldBeFalse();
        response.StatusCode.ShouldBe(ChallengeRecorder.StatusCode);
        source.Calls.ShouldBe(0);
    }

    [Fact]
    public void Password_change_required_is_a_403_problem_with_its_own_reason()
    {
        ForbiddenReason.PasswordChangeRequired.WireName.ShouldBe("password-change-required");
        ForbiddenReason.PasswordChangeRequired.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Only_me_and_change_password_are_exempt()
    {
        var exempt = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<AllowedWhilePasswordChangeRequiredMetadata>() is not null)
            .Select(endpoint => $"{string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} {endpoint.RoutePattern.RawText}")
            .ToList();

        exempt.ShouldBe(["GET /api/v1/me", "POST /api/v1/auth/change-password"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_new_endpoint_that_declares_nothing_is_protected_by_default()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddProtectedProbeEndpoint()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ProtectedProbeEndpoint.Path, TestContext.Current.CancellationToken);

        // 401, not 404: the probe exists and went through authorization (and so through the gate).
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<(ApiErrorsTests.CapturedResponse Response, bool NextCalled)> HandleAsync(
        IAccountStandingSource source,
        ClaimsPrincipal user,
        PolicyAuthorizationResult result,
        params object[] endpointMetadata)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .AddLogging()
                .AddSingleton(source)
                .AddSingleton<IAuthenticationService, ChallengeRecorder>()
                .BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
            User = user,
        };
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(endpointMetadata), "test"));
        var nextCalled = false;

        await new ApiAuthorizationResultHandler().HandleAsync(
            _ => { nextCalled = true; return Task.CompletedTask; },
            httpContext,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            result);

        return (ApiErrorsTests.CapturedResponse.From(httpContext), nextCalled);
    }

    private static string? Reason(ApiErrorsTests.CapturedResponse response)
    {
        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.GetProperty("reason").GetString();
    }

    private static ClaimsPrincipal SignedIn(string? authorizationId = null) =>
        new(new ClaimsIdentity(
            authorizationId is null
                ? [new Claim("sub", AccountId.ToString("D"))]
                : [new Claim("sub", AccountId.ToString("D")), new Claim("oi_au_id", authorizationId)],
            authenticationType: "test"));

    /// <summary>Stands in for the authentication handlers: a challenge sets a marker status.</summary>
    private sealed class ChallengeRecorder : IAuthenticationService
    {
        public const int StatusCode = 499;

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCode;
            return Task.CompletedTask;
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new InvalidOperationException("Not expected.");

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new InvalidOperationException("Not expected.");

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new InvalidOperationException("Not expected.");
    }

    private sealed class FakeFlagSource : IAccountStandingSource
    {
        private readonly bool _flagged;
        private readonly bool _deactivated;
        private readonly bool _signInEnded;

        public FakeFlagSource(bool flagged, bool deactivated = false, bool signInEnded = false)
        {
            _flagged = flagged;
            _deactivated = deactivated;
            _signInEnded = signInEnded;
        }

        public int Calls { get; private set; }

        public string? AuthorizationId { get; private set; }

        public Task<AccountStanding> GetAsync(Guid accountId, string? authorizationId, CancellationToken cancellationToken)
        {
            Calls++;
            AuthorizationId = authorizationId;
            return Task.FromResult(accountId == AccountId ? new AccountStanding(_flagged, _deactivated, _signInEnded) : AccountStanding.Unknown);
        }
    }
}
