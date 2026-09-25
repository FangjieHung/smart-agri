using System.Net;
using System.Text.Json;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// A deployment with exactly one organization (its own database): the organization code
/// may be omitted. Tests only ever add accounts, never organizations.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class SingleOrganizationSignInTests : IClassFixture<SingleOrganizationSignInTests.Fixture>
{
    private const string Password = "Correct-Horse-9-battery";

    private readonly Fixture _host;

    public SingleOrganizationSignInTests(Fixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_options_do_not_require_an_organization_code()
    {
        using var spa = _host.CreateSpaClient();

        var response = await spa.Http.GetAsync("/api/v1/auth/login-options", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        body.GetProperty("organizationCodeRequired").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Signing_in_without_an_organization_code_uses_the_only_organization()
    {
        var account = await _host.CreateAccountAsync(_host.Organization, "solo", Password, AccountRole.SmbAdmin, "唯一管理者", AccountPermission.ManageAssistants);
        using var spa = _host.CreateSpaClient();

        var token = await spa.SignInAsync(organizationCode: null, "solo", Password);
        var me = await spa.GetMeJsonAsync(token.AccessToken);

        me.GetProperty("id").GetGuid().ShouldBe(account.Id);
        me.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(_host.Organization.Id);
    }

    [Fact]
    public async Task The_right_code_still_works_and_a_wrong_code_fails_like_any_other_error()
    {
        await _host.CreateAccountAsync(_host.Organization, "coded", Password);
        using var spa = _host.CreateSpaClient();

        (await spa.LoginAsync(_host.Organization.Code, "coded", Password)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var wrongCode = await ResponseFingerprint.FromAsync(await spa.LoginAsync("other-org", "coded", Password));
        var wrongPassword = await ResponseFingerprint.FromAsync(await spa.LoginAsync(null, "coded", "Wrong-Password-1"));
        wrongCode.Status.ShouldBe(HttpStatusCode.Unauthorized);
        wrongCode.Body.ShouldBe(wrongPassword.Body);
        wrongCode.ContentType.ShouldBe(wrongPassword.ContentType);
    }

    public sealed class Fixture : AuthHostFixture
    {
        private Organization? _organization;

        public Organization Organization => _organization ?? throw new InvalidOperationException("Not initialized.");

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            _organization = await CreateOrganizationAsync("唯一組織");
        }
    }
}
