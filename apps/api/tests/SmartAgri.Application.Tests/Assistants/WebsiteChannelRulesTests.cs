using System.Text.Json;
using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>
/// <see cref="WebsiteChannelRules"/> (issue #194): the settings validation ported from the
/// frontend, and the publishing gate (M5a plan §3 C, decision B).
/// </summary>
public sealed class WebsiteChannelRulesTests
{
    // --- Allowed domains: the same cases as the frontend's validateAllowedDomain() spec --------

    [Fact]
    public void Every_shared_allowed_domain_case_gives_the_same_result_as_the_frontend()
    {
        // apps/admin/src/app/core/domain/allowed-domain-cases.json is also run by
        // apps/admin/src/app/core/domain/publishing.model.spec.ts (next to it).
        var cases = SharedDomainCases();
        cases.Count.ShouldBeGreaterThan(20);

        foreach (var testCase in cases)
        {
            var name = testCase.GetProperty("name").GetString()!;
            var raw = testCase.GetProperty("raw").GetString()!;
            var existing = testCase.GetProperty("existing").EnumerateArray().Select(item => item.GetString()!).ToList();
            var expected = testCase.GetProperty("error");

            var error = WebsiteChannelRules.ValidateDomain(raw, existing);

            if (expected.ValueKind == JsonValueKind.Null)
            {
                error.ShouldBeNull(name);
                WebsiteChannelRules.NormalizeDomain(raw).ShouldBe(testCase.GetProperty("normalized").GetString(), name);
            }
            else
            {
                error.ShouldBe(expected.GetString(), name);
            }
        }
    }

    // --- Settings (the frontend's validateWebsiteSettings()) -----------------------------------

    [Fact]
    public void Valid_settings_are_trimmed_and_the_domains_normalized_in_order()
    {
        var result = WebsiteChannelRules.ForUpdate(
            "  安心客服  ", " 您好！ ", "ocean", "bottom-left", ["Shop.Example.com", "www.example.com "]);

        result.IsValid.ShouldBeTrue();
        result.Value.ShouldBe(new WebsiteChannelSettings(
            "安心客服", "您好！", WebsiteBrandColor.Ocean, WebsiteLauncherPosition.BottomLeft, result.Value.AllowedDomains));
        result.Value.AllowedDomains.ShouldBe(["shop.example.com", "www.example.com"]);
    }

    [Fact]
    public void No_domain_at_all_is_valid_settings()
    {
        WebsiteChannelRules.ForUpdate("名稱", "歡迎", "forest", "bottom-right", null).Value.AllowedDomains.ShouldBeEmpty();
        WebsiteChannelRules.ForUpdate("名稱", "歡迎", "forest", "bottom-right", []).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Every_broken_field_is_reported_at_once_and_only_the_first_broken_domain()
    {
        var result = WebsiteChannelRules.ForUpdate(
            new string('名', AssistantWebsiteChannel.DisplayNameMaxLength + 1),
            "  ",
            "red",
            null,
            ["ok.example.com", "https://bad.example.com", "*.example.com"]);

        result.IsValid.ShouldBeFalse();
        result.Failures.Select(failure => (failure.Field, failure.Message)).ShouldBe(
        [
            ("displayName", WebsiteChannelRules.DisplayNameTooLongMessage),
            ("welcomeMessage", WebsiteChannelRules.WelcomeMessageRequiredMessage),
            ("brandColor", WebsiteChannelRules.BrandColorInvalidMessage),
            ("position", WebsiteChannelRules.PositionInvalidMessage),
            ("allowedDomains", WebsiteChannelRules.DomainHasSchemeOrPathMessage),
        ]);
    }

    [Fact]
    public void Duplicates_and_a_sixth_domain_are_refused_like_the_frontend()
    {
        WebsiteChannelRules.ForUpdate("名稱", "歡迎", "forest", "bottom-right", ["a.example.com", "A.example.com"])
            .Failures.Single().Message.ShouldBe("「a.example.com」已在允許清單中。");
        WebsiteChannelRules.ForUpdate(
                "名稱", "歡迎", "forest", "bottom-right",
                ["a.example.com", "b.example.com", "c.example.com", "d.example.com", "e.example.com", "f.example.com"])
            .Failures.Single().Message.ShouldBe(WebsiteChannelRules.TooManyDomainsMessage);
    }

    [Fact]
    public void A_welcome_message_over_the_limit_and_a_blank_name_are_refused()
    {
        var result = WebsiteChannelRules.ForUpdate(
            null, new string('歡', AssistantWebsiteChannel.WelcomeMessageMaxLength + 1), "plum", "bottom-right", []);

        result.Failures.Select(failure => failure.Field).ShouldBe(["displayName", "welcomeMessage"]);
        result.Failures[1].Message.ShouldBe(WebsiteChannelRules.WelcomeMessageTooLongMessage);
    }

    [Fact]
    public void Defaults_follow_the_frontend_mock_and_fit_the_limits()
    {
        var defaults = WebsiteChannelRules.Defaults(new string('助', 40));

        defaults.DisplayName.Length.ShouldBe(AssistantWebsiteChannel.DisplayNameMaxLength);
        defaults.WelcomeMessage.ShouldStartWith("您好，我是助助");
        defaults.BrandColor.ShouldBe(WebsiteBrandColor.Forest);
        defaults.Position.ShouldBe(WebsiteLauncherPosition.BottomRight);
        defaults.AllowedDomains.ShouldBeEmpty();
        WebsiteChannelRules.Defaults("客服").WelcomeMessage.ShouldBe("您好，我是客服，有什麼可以協助？");
    }

    // --- The publishing gate ------------------------------------------------------------------

    [Fact]
    public void Publishing_is_allowed_when_every_condition_holds()
    {
        WebsiteChannelRules.PublishFailures(AssistantAcceptanceStatus.Passed, 1, AssistantStatus.Ready, [], true)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AssistantAcceptanceStatus.NotAccepted)]
    [InlineData(AssistantAcceptanceStatus.Failed)]
    [InlineData(AssistantAcceptanceStatus.Outdated)]
    public void Publishing_needs_acceptance_passed_now(AssistantAcceptanceStatus acceptance)
    {
        WebsiteChannelRules.PublishFailures(acceptance, 1, AssistantStatus.Ready, [], true)
            .Select(failure => failure.Field).ShouldBe(["acceptance"]);
    }

    [Fact]
    public void Every_failed_condition_is_listed_under_its_own_key_with_one_entry_per_non_owned_knowledge_base()
    {
        var failures = WebsiteChannelRules.PublishFailures(
            AssistantAcceptanceStatus.Failed,
            0,
            AssistantStatus.Paused,
            [new WebsiteKnowledgeBaseRef(Guid.NewGuid(), "同仁的知識庫"), new WebsiteKnowledgeBaseRef(Guid.NewGuid(), "公開知識庫")],
            publicBaseUrlConfigured: false);

        failures.Select(failure => failure.Field).ShouldBe(
            ["acceptance", "allowed-domains", "assistant-paused", "knowledge-ownership", "knowledge-ownership", "public-base-url"]);
        failures.Where(failure => failure.Field == "knowledge-ownership").Select(failure => failure.Message).ShouldBe(
        [
            WebsiteChannelRules.KnowledgeNotOwnedMessage("同仁的知識庫"),
            WebsiteChannelRules.KnowledgeNotOwnedMessage("公開知識庫"),
        ]);
    }

    [Fact]
    public void Publishing_needs_the_public_base_url()
    {
        WebsiteChannelRules.PublishFailures(AssistantAcceptanceStatus.Passed, 1, AssistantStatus.Ready, [], false)
            .Single().Field.ShouldBe("public-base-url");
    }

    private static List<JsonElement> SharedDomainCases()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "admin", "src", "app", "core", "domain", "allowed-domain-cases.json");
            if (File.Exists(candidate))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(item => item.Clone())];
            }
        }

        throw new FileNotFoundException("apps/admin/src/app/core/domain/allowed-domain-cases.json not found above the test output directory.");
    }
}
