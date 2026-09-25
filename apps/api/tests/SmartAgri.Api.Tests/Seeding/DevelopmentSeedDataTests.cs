using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure.Seeding;

namespace SmartAgri.Api.Tests.Seeding;

/// <summary>
/// <see cref="DevelopmentSeedData"/>'s "安心商行" accounts are a contract with the
/// frontend Demo (M1 skeleton plan, Slice 6): display name, role and permissions must
/// equal <c>DEMO_SEED.accounts</c> in
/// <c>apps/admin/src/app/core/repositories/demo-seed.ts</c>. This test parses that file
/// (the same technique <c>AccountWireNameTests</c> uses for <c>account.model.ts</c>) so a
/// change on either side that breaks the match fails here instead of silently drifting.
/// No database needed.
/// </summary>
public partial class DevelopmentSeedDataTests
{
    /// <summary>Maps a seeded login name to the frontend account id it must match.</summary>
    private static readonly (string LoginName, string FrontendAccountId)[] AnxinAccounts =
    [
        ("admin", "account-smb-admin"),
        ("internal", "account-internal-employee"),
        ("customer", "account-external-customer"),
    ];

    [Fact]
    public void Anxin_accounts_match_the_frontend_demo_seed()
    {
        var source = File.ReadAllText(FindDemoSeedFile());
        var frontendAccounts = ParseAccounts(source);

        var anxin = DevelopmentSeedData.Organizations.Single(organization => organization.Code == DevelopmentSeedData.AnxinOrganizationCode);
        anxin.Accounts.Count.ShouldBe(AnxinAccounts.Length, "the seed and demo-seed.ts must have exactly the same accounts");

        foreach (var (loginName, frontendAccountId) in AnxinAccounts)
        {
            frontendAccounts.ShouldContainKey(frontendAccountId);
            var expected = frontendAccounts[frontendAccountId];
            var actual = anxin.Accounts.Single(account => account.LoginName == loginName);

            actual.DisplayName.ShouldBe(expected.DisplayName, $"displayName for '{loginName}'");
            WireNames<AccountRole>.ToWire(actual.Role).ShouldBe(expected.Role, $"role for '{loginName}'");
            actual.Permissions.Select(WireNames<AccountPermission>.ToWire).ShouldBe(expected.Permissions, $"permissions for '{loginName}'");
        }
    }

    [Fact]
    public void Anxin_organization_name_matches_the_ticket()
    {
        var anxin = DevelopmentSeedData.Organizations.Single(organization => organization.Code == DevelopmentSeedData.AnxinOrganizationCode);
        anxin.Name.ShouldBe("安心商行");
    }

    [Fact]
    public void Control_organization_has_exactly_one_smb_admin_account()
    {
        var control = DevelopmentSeedData.Organizations.Single(organization => organization.Code == DevelopmentSeedData.ControlOrganizationCode);

        control.Name.ShouldBe("對照組織");
        var admin = control.Accounts.ShouldHaveSingleItem();
        admin.LoginName.ShouldBe("admin");
        admin.Role.ShouldBe(AccountRole.SmbAdmin);
    }

    [Fact]
    public void Organization_codes_and_login_names_are_ascii_letters_digits_or_slash_only()
    {
        // The authentication ADR and this ticket both call this out explicitly: login
        // names become part of Account's internal Identity user name
        // ("{organizationCode}/{loginName}"), and anything outside this set risks failing
        // a later write (lockout, password change) that touches that column.
        foreach (var organization in DevelopmentSeedData.Organizations)
        {
            AsciiLetterDigitOrSlash().IsMatch(organization.Code).ShouldBeTrue($"organization code '{organization.Code}'");
            foreach (var account in organization.Accounts)
            {
                AsciiLetterDigitOrSlash().IsMatch(account.LoginName).ShouldBeTrue($"login name '{account.LoginName}'");
            }
        }
    }

    /// <summary>One parsed entry from <c>DEMO_SEED.accounts</c>.</summary>
    private sealed record FrontendAccount(string DisplayName, string Role, string[] Permissions);

    private static Dictionary<string, FrontendAccount> ParseAccounts(string source)
    {
        // Isolate the `accounts: [ ... ]` array (it is followed by `assistants:`, the next
        // top-level DEMO_SEED property) so unrelated `{ ... }` objects elsewhere in the
        // file are never considered.
        var arrayMatch = Regex.Match(source, @"accounts:\s*\[(?<body>[\s\S]*?)\n\s*\],\n\s*assistants:");
        arrayMatch.Success.ShouldBeTrue("`accounts: [...]` not found before `assistants:` in demo-seed.ts");

        var accounts = new Dictionary<string, FrontendAccount>(StringComparer.Ordinal);
        foreach (Match objectMatch in AccountObject().Matches(arrayMatch.Groups["body"].Value))
        {
            var body = objectMatch.Groups["body"].Value;
            var id = Field("id", body);
            accounts.Add(
                id,
                new FrontendAccount(
                    Field("displayName", body),
                    Field("role", body),
                    [.. StringLiteral().Matches(Bracketed("permissions", body)).Select(match => match.Groups[1].Value)]));
        }

        return accounts;
    }

    private static string Field(string name, string objectBody)
    {
        var match = Regex.Match(objectBody, $@"{name}:\s*'([^']*)'");
        match.Success.ShouldBeTrue($"field '{name}' not found in account object:\n{objectBody}");
        return match.Groups[1].Value;
    }

    private static string Bracketed(string name, string objectBody)
    {
        var match = Regex.Match(objectBody, $@"{name}:\s*\[([^\]]*)\]");
        match.Success.ShouldBeTrue($"field '{name}' not found in account object:\n{objectBody}");
        return match.Groups[1].Value;
    }

    private static string FindDemoSeedFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "admin", "src", "app", "core", "repositories", "demo-seed.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("apps/admin/src/app/core/repositories/demo-seed.ts not found above the test output directory.");
    }

    [GeneratedRegex(@"\{(?<body>[\s\S]*?)\}")]
    private static partial Regex AccountObject();

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex("^[A-Za-z0-9/]+$")]
    private static partial Regex AsciiLetterDigitOrSlash();
}
