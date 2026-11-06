using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// The pieces of FB 粉專貼文 (#379) without a host or a database: the <c>appsecret_proof</c>, the consent URL with and
/// without a Facebook Login for Business configuration, <c>Coordinator:Facebook:ConfigId</c>'s validation, how Graph's
/// errors are read, the provider's failure codes, and no token in any <c>ToString</c>.
/// </summary>
public sealed class CoordinatorFacebookTests
{
    private const string AppId = "1029384756001";
    private const string AppSecret = "unit-app-secret-0123456789";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void The_appsecret_proof_is_the_lowercase_hex_HMAC_SHA256_of_the_token_keyed_with_the_app_secret()
    {
        // RFC 4231-style known answer: HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog").
        FacebookGraphClient.AppSecretProof("key", "The quick brown fox jumps over the lazy dog")
            .ShouldBe("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");
        var graph = new FakeGraphServer(AppId, AppSecret);
        FacebookGraphClient.AppSecretProof(AppSecret, "page-token").ShouldBe(graph.ProofOf("page-token"));
    }

    [Fact]
    public void The_consent_URL_asks_for_the_page_permissions_or_names_the_Login_for_Business_configuration()
    {
        var consent = new CoordinatorOAuthConsent("protected-state", "https://assistant.example.org/api/v1/coordinator/oauth/facebook/callback", null);

        var byScope = Query(Provider(new CoordinatorFacebookOptions { AppId = AppId, AppSecret = AppSecret }, out _).ConsentUrl(consent), out var path);
        path.ShouldBe("https://www.facebook.com/v26.0/dialog/oauth");
        byScope.ShouldBe(
            new Dictionary<string, string>
            {
                ["client_id"] = AppId,
                ["redirect_uri"] = consent.RedirectUri,
                ["state"] = "protected-state",
                ["response_type"] = "code",
                ["scope"] = "pages_show_list,pages_manage_posts,pages_read_engagement",
            },
            ignoreOrder: true);

        var byConfiguration = Query(
            Provider(new CoordinatorFacebookOptions { AppId = AppId, AppSecret = AppSecret, ConfigId = " 778899 " }, out _).ConsentUrl(consent), out _);
        byConfiguration["config_id"].ShouldBe("778899");
        byConfiguration.ShouldNotContainKey("scope", "a configuration replaces scope");
        Provider(new CoordinatorFacebookOptions(), out _).ShouldSatisfyAllConditions(
            provider => provider.IsConfigured.ShouldBeFalse(),
            provider => provider.UsesPkce.ShouldBeFalse(),
            provider => provider.Service.ShouldBe(CoordinatorService.Facebook));
    }

    [Fact]
    public void A_Login_for_Business_configuration_id_needs_the_app_and_is_digits()
    {
        new CoordinatorFacebookOptions { AppId = AppId, AppSecret = AppSecret, ConfigId = "778899" }.Validate().ShouldBeNull();
        new CoordinatorFacebookOptions { AppId = AppId, AppSecret = AppSecret, ConfigId = " " }.ShouldSatisfyAllConditions(
            options => options.Validate().ShouldBeNull(), options => options.ResolvedConfigId.ShouldBeNull());
        new CoordinatorFacebookOptions { ConfigId = "778899" }.Validate().ShouldNotBeNull()
            .ShouldContain("Coordinator:Facebook:ConfigId is set but Coordinator:Facebook:AppId and Coordinator:Facebook:AppSecret are not");
        new CoordinatorFacebookOptions { AppId = AppId, AppSecret = AppSecret, ConfigId = "abc" }.Validate().ShouldNotBeNull().ShouldContain("digits only");
        var error = new CoordinatorFacebookOptions { AppSecret = AppSecret, ConfigId = "778899" }.Validate().ShouldNotBeNull();
        error.ShouldContain("Coordinator:Facebook:AppId is not");
        error.ShouldNotContain(AppSecret);
    }

    [Theory]
    [InlineData(190, null, "token")]
    [InlineData(190, 463, "token")]
    [InlineData(102, null, "token")]
    [InlineData(100, 460, "token")]
    [InlineData(200, 492, "token")]
    [InlineData(10, null, "permission")]
    [InlineData(200, null, "permission")]
    [InlineData(299, null, "permission")]
    [InlineData(4, null, "limit")]
    [InlineData(17, null, "limit")]
    [InlineData(32, null, "limit")]
    [InlineData(341, null, "limit")]
    [InlineData(613, 1996, "limit")]
    [InlineData(80001, null, "limit")]
    [InlineData(1, null, "unavailable")]
    [InlineData(2, null, "unavailable")]
    [InlineData(100, 33, "missing")]
    [InlineData(368, null, "other")]
    [InlineData(506, null, "other")]
    public void Graph_errors_read_as_the_token_a_permission_a_limit_an_outage_or_a_missing_object(int code, int? subcode, string expected)
    {
        var error = new FacebookGraphError(code, subcode, "OAuthException", "trace");
        var result = new FacebookGraphResult(FacebookGraphOutcome.GraphError, 400, error);
        var read = FacebookGraphErrors.IsTokenRefused(error) ? "token"
            : FacebookGraphErrors.IsPermissionMissing(error) ? "permission"
            : FacebookGraphErrors.IsRateLimited(result) ? "limit"
            : FacebookGraphErrors.IsUnavailable(result) ? "unavailable"
            : FacebookGraphErrors.IsMissingObject(error) ? "missing"
            : "other";
        read.ShouldBe(expected);
    }

    [Fact]
    public void Without_Graphs_error_object_only_the_HTTP_status_and_the_transport_count()
    {
        FacebookGraphErrors.IsUnavailable(new FacebookGraphResult(FacebookGraphOutcome.HttpError, 502, null)).ShouldBeTrue();
        FacebookGraphErrors.IsUnavailable(new FacebookGraphResult(FacebookGraphOutcome.TimedOut, null, null)).ShouldBeTrue();
        FacebookGraphErrors.IsUnavailable(new FacebookGraphResult(FacebookGraphOutcome.NetworkError, null, null)).ShouldBeTrue();
        FacebookGraphErrors.IsRateLimited(new FacebookGraphResult(FacebookGraphOutcome.HttpError, 429, null)).ShouldBeTrue();
        FacebookGraphErrors.IsUnavailable(new FacebookGraphResult(FacebookGraphOutcome.HttpError, 400, null)).ShouldBeFalse();
        FacebookGraphErrors.IsTokenRefused(null).ShouldBeFalse();
    }

    [Fact]
    public async Task The_provider_fails_with_a_short_code_of_ours_when_Graph_refuses_or_is_unreachable()
    {
        var options = new CoordinatorFacebookOptions { AppId = AppId, AppSecret = AppSecret };
        var provider = Provider(options, out var graph);
        const string Redirect = "https://assistant.example.org/api/v1/coordinator/oauth/facebook/callback";

        // An unknown code: Graph refuses it.
        (await provider.ExchangeAsync(new CoordinatorOAuthCodeExchange("no-such-code", Redirect, null), CancellationToken))
            .FailureCode.ShouldBe(CoordinatorFacebookOAuthProvider.ExchangeCode);

        // Graph unreachable while exchanging.
        var unreachable = graph.AddUser(Redirect, graph.NewPage("粉專", FakeGraphServer.AdminTasks));
        graph.Script(unreachable.Code, FakeGraphServer.AccessTokenEndpoint, GraphBehavior.NetworkError);
        (await provider.ExchangeAsync(new CoordinatorOAuthCodeExchange(unreachable.Code, Redirect, null), CancellationToken))
            .FailureCode.ShouldBe(CoordinatorFailureCodes.FacebookUnavailable);

        // The owner did not grant pages_show_list.
        var stingy = graph.AddUser(Redirect, graph.NewPage("粉專", FakeGraphServer.AdminTasks));
        graph.Script(stingy.LongToken, FakeGraphServer.AccountsEndpoint, GraphBehavior.Error(HttpStatusCode.Forbidden, 200, null, "(#200) pages_show_list"));
        (await provider.ExchangeAsync(new CoordinatorOAuthCodeExchange(stingy.Code, Redirect, null), CancellationToken))
            .FailureCode.ShouldBe(CoordinatorFailureCodes.FacebookPermission);

        // Another redirect URI than the one of the dialog: Graph refuses the code.
        var moved = graph.AddUser(Redirect, graph.NewPage("粉專", FakeGraphServer.AdminTasks));
        (await provider.ExchangeAsync(new CoordinatorOAuthCodeExchange(moved.Code, Redirect + "/", null), CancellationToken))
            .FailureCode.ShouldBe(CoordinatorFacebookOAuthProvider.ExchangeCode);

        // A selection whose pending data is not ours.
        (await provider.SelectAsync(new CoordinatorOAuthPending("not json"), "1", CancellationToken))
            .FailureCode.ShouldBe(CoordinatorFacebookOAuthProvider.ExchangeCode);
        graph.Requests.ShouldAllBe(request => request.Parameter("access_token") == null || request.Parameter("appsecret_proof") == graph.ProofOf(request.Parameter("access_token")!));
    }

    [Fact]
    public async Task Several_pages_wait_in_the_pending_data_and_the_choice_is_connected_with_its_own_token()
    {
        var provider = Provider(new CoordinatorFacebookOptions { AppId = AppId, AppSecret = AppSecret }, out var graph);
        const string Redirect = "https://assistant.example.org/api/v1/coordinator/oauth/facebook/callback";
        var first = graph.NewPage("一店", "CREATE_CONTENT");
        var second = graph.NewPage(" ", FakeGraphServer.AdminTasks);
        var user = graph.AddUser(Redirect, first, second, first);

        var outcome = await provider.ExchangeAsync(new CoordinatorOAuthCodeExchange(user.Code, Redirect, null), CancellationToken);

        outcome.Kind.ShouldBe(CoordinatorOAuthOutcomeKind.NeedsSelection);
        outcome.Choices.ShouldBe([new CoordinatorOAuthChoice(first.Id, "一店"), new CoordinatorOAuthChoice(second.Id, second.Id)], "distinct; a blank name shows the id");
        outcome.ToString().ShouldNotContain(first.AccessToken);
        outcome.Pending!.ToString().ShouldNotContain(first.AccessToken);
        var chosen = await provider.SelectAsync(outcome.Pending, second.Id, CancellationToken);
        chosen.Credential.ShouldNotBeNull().ShouldSatisfyAllConditions(
            credential => credential.Secret.ShouldBe(second.AccessToken),
            credential => credential.ExternalId.ShouldBe(second.Id),
            credential => credential.DisplayName.ShouldBe(second.Id),
            credential => credential.ToString().ShouldNotContain(second.AccessToken));
        (await provider.SelectAsync(outcome.Pending, "999", CancellationToken)).FailureCode.ShouldBe(CoordinatorFacebookOAuthProvider.ExchangeCode);
    }

    [Fact]
    public void No_token_in_any_ToString()
    {
        const string Token = "page-token-never-shown-0123456789";
        new FacebookPage("123", "粉專", Token, ["CREATE_CONTENT"]).ToString().ShouldNotContain(Token);
        new FacebookGraphResult<string>(FacebookGraphOutcome.Succeeded, 200, null, Token).ToString().ShouldNotContain(Token);
        var connection = CoordinatorConnection.Create(
            Guid.CreateVersion7(), CoordinatorService.Facebook, new ProtectedSecret("ciphertext", "6789", DateTimeOffset.UnixEpoch), "123", "粉專", DateTimeOffset.UnixEpoch);
        new CoordinatorFacebookAccess(connection, Token).ShouldSatisfyAllConditions(
            access => access.ToString().ShouldNotContain(Token),
            access => access.PageId.ShouldBe("123"));
    }

    private static CoordinatorFacebookOAuthProvider Provider(CoordinatorFacebookOptions options, out FakeGraphServer graph)
    {
        graph = new FakeGraphServer(AppId, AppSecret);
        var http = new HttpClient(graph, disposeHandler: false) { BaseAddress = FacebookGraphClient.BaseAddress };
        var wrapped = Options.Create(options);
        return new CoordinatorFacebookOAuthProvider(
            new FacebookGraphClient(http, wrapped, NullLogger<FacebookGraphClient>.Instance), wrapped, NullLogger<CoordinatorFacebookOAuthProvider>.Instance);
    }

    private static Dictionary<string, string> Query(Uri uri, out string path)
    {
        path = uri.GetLeftPart(UriPartial.Path);
        return QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
    }
}
