using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Domain.Accounts;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// The role and permission names are a contract with the frontend
/// (<c>apps/admin/src/app/core/domain/account.model.ts</c>): the API serializes them and
/// the database stores them, so they must match the frontend's kebab-case strings
/// character for character.
/// </summary>
public partial class AccountWireNameTests
{
    private static readonly string[] ExpectedRoles =
        ["smb-admin", "internal-employee", "external-customer"];

    private static readonly string[] ExpectedPermissions =
    [
        "manage-assistants",
        "manage-data-sources",
        "manage-publishing",
        "read-consented-submissions",
        "use-shared-assistants",
        "submit-authorized-forms",
        "read-own-tracking",
    ];

    [Fact]
    public void Roles_serialize_to_the_frontend_names()
    {
        Enum.GetValues<AccountRole>()
            .Select(role => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(role)))
            .ShouldBe(ExpectedRoles);
    }

    [Fact]
    public void Permissions_serialize_to_the_frontend_names()
    {
        Enum.GetValues<AccountPermission>()
            .Select(permission => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(permission)))
            .ShouldBe(ExpectedPermissions);
    }

    [Fact]
    public void Serialized_names_deserialize_back_to_the_same_values()
    {
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            JsonSerializer.Deserialize<AccountRole>(JsonSerializer.Serialize(role)).ShouldBe(role);
        }

        foreach (var permission in Enum.GetValues<AccountPermission>())
        {
            JsonSerializer.Deserialize<AccountPermission>(JsonSerializer.Serialize(permission)).ShouldBe(permission);
        }
    }

    [Fact]
    public void Numeric_or_unknown_values_are_not_accepted_as_permissions()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<AccountPermission>("\"ManageAssistants\""));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<AccountPermission>("\"delete-everything\""));
    }

    [Fact]
    public void Wire_names_match_the_json_names()
    {
        WireNames<AccountRole>.All.ShouldBe(ExpectedRoles);
        WireNames<AccountPermission>.All.ShouldBe(ExpectedPermissions);
        WireNames<AccountPermission>.Parse("read-own-tracking").ShouldBe(AccountPermission.ReadOwnTracking);
        WireNames<AccountRole>.ToWire(AccountRole.SmbAdmin).ShouldBe("smb-admin");
        Should.Throw<FormatException>(() => WireNames<AccountRole>.Parse("SmbAdmin"));
    }

    [Fact]
    public void Names_match_the_unions_in_the_frontend_account_model()
    {
        var source = File.ReadAllText(FindFrontendAccountModel());

        ReadUnion(source, "AccountRole").ShouldBe(ExpectedRoles);
        ReadUnion(source, "AccountPermission").ShouldBe(ExpectedPermissions);
    }

    /// <summary>Extracts the string literals of <c>export type {name} = 'a' | 'b' ...;</c>.</summary>
    private static string[] ReadUnion(string source, string name)
    {
        var declaration = Regex.Match(source, $@"export\s+type\s+{name}\s*=(?<body>[^;]*);");
        declaration.Success.ShouldBeTrue($"`export type {name}` not found in account.model.ts");

        return [.. StringLiteral().Matches(declaration.Groups["body"].Value).Select(match => match.Groups[1].Value)];
    }

    private static string FindFrontendAccountModel()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "admin", "src", "app", "core", "domain", "account.model.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("apps/admin/src/app/core/domain/account.model.ts not found above the test output directory.");
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex StringLiteral();
}
