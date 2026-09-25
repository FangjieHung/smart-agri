using System.Net;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// An expired access token gets <c>401</c>. Uses its own host because it moves that
/// host's clock (OpenIddict reads the <see cref="TimeProvider"/> from DI) past the
/// 30-minute lifetime.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class TokenExpiryTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Correct-Horse-9-battery";

    private readonly AuthHostFixture _host;

    public TokenExpiryTests(AuthHostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task An_expired_access_token_is_401_with_no_body()
    {
        var organization = await _host.CreateOrganizationAsync();
        await _host.CreateAccountAsync(organization, "admin", Password);
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", Password);
        (await spa.GetMeAsync(token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Past the 30-minute lifetime plus any clock-skew allowance.
        _host.Clock.Advance(TimeSpan.FromMinutes(40));
        var response = await spa.GetMeAsync(token.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}
