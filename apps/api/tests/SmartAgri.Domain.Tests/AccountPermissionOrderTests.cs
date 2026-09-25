using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Domain.Accounts;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// <see cref="AccountPermissionOrder"/> must list permissions exactly like the frontend's
/// <c>ACCOUNT_PERMISSIONS</c> (<c>apps/admin/src/app/core/domain/team.model.ts</c>), which
/// orders both the team panel and the stored permission lists.
/// </summary>
public partial class AccountPermissionOrderTests
{
    [Fact]
    public void Order_matches_the_frontend_ACCOUNT_PERMISSIONS()
    {
        var source = File.ReadAllText(FindTeamModel());
        var declaration = Regex.Match(source, @"export\s+const\s+ACCOUNT_PERMISSIONS\b.*?\n\];", RegexOptions.Singleline);
        declaration.Success.ShouldBeTrue("`export const ACCOUNT_PERMISSIONS` not found in team.model.ts");

        var frontendOrder = PermissionId().Matches(declaration.Value).Select(match => match.Groups[1].Value);

        AccountPermissionOrder.All.Select(WireNames<AccountPermission>.ToWire).ShouldBe(frontendOrder);
    }

    [Fact]
    public void Order_lists_every_permission_once()
    {
        AccountPermissionOrder.All.ShouldBe(Enum.GetValues<AccountPermission>(), ignoreOrder: true);
        AccountPermissionOrder.All.ShouldBeUnique();
    }

    [Fact]
    public void Sort_drops_duplicates_and_follows_the_frontend_order()
    {
        AccountPermissionOrder.Sort(
        [
            AccountPermission.UseSharedAssistants,
            AccountPermission.SubmitAuthorizedForms,
            AccountPermission.UseSharedAssistants,
            AccountPermission.ReadConsentedSubmissions,
        ]).ShouldBe(
        [
            AccountPermission.ReadConsentedSubmissions,
            AccountPermission.SubmitAuthorizedForms,
            AccountPermission.UseSharedAssistants,
        ]);
    }

    private static string FindTeamModel()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "admin", "src", "app", "core", "domain", "team.model.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("apps/admin/src/app/core/domain/team.model.ts not found above the test output directory.");
    }

    [GeneratedRegex(@"id:\s*'([^']*)'")]
    private static partial Regex PermissionId();
}
