using SmartAgri.Domain.Accounts;

namespace SmartAgri.Infrastructure.Seeding;

/// <summary>One account <see cref="DevelopmentSeeder"/> ensures exists in an organization.</summary>
/// <remarks>
/// <see cref="LoginName"/> only needs to be ASCII letters/digits/<c>/</c> (authentication
/// ADR, and the M1 skeleton plan's note for this slice): it becomes part of the account's
/// internal Identity user name (<c>Account.ComposeUserName</c>), and anything else there
/// risks failing later writes (lockouts, password changes) that touch that column.
/// </remarks>
public sealed record DevelopmentSeedAccount(
    string LoginName,
    string DisplayName,
    AccountRole Role,
    IReadOnlyList<AccountPermission> Permissions);

/// <summary>One organization <see cref="DevelopmentSeeder"/> ensures exists.</summary>
public sealed record DevelopmentSeedOrganization(
    string Name,
    string Code,
    IReadOnlyList<DevelopmentSeedAccount> Accounts);

/// <summary>
/// The fixed set of organizations and accounts <see cref="DevelopmentSeeder"/> creates in
/// Development (M1 skeleton plan, Slice 6). The "安心商行" accounts' display names, roles
/// and permissions are copied from the frontend's
/// <c>apps/admin/src/app/core/repositories/demo-seed.ts</c> (<c>DEMO_SEED.accounts</c>) —
/// <c>DevelopmentSeedDataTests</c> parses that file and fails if the two drift apart.
/// "對照組織" only exists so a developer can sign in as two different organizations'
/// <c>admin</c> and confirm neither can see the other's data; it has no frontend
/// counterpart, so its display name and permission set are this seed's own choice, not a
/// contract with anything.
/// </summary>
public static class DevelopmentSeedData
{
    public const string AnxinOrganizationCode = "anxin";
    public const string ControlOrganizationCode = "control";

    /// <summary>The permissions demo-seed.ts gives <c>account-smb-admin</c>, and what this
    /// seed also gives every <c>admin</c> account (including 對照組織's).</summary>
    private static readonly AccountPermission[] AdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    public static readonly IReadOnlyList<DevelopmentSeedOrganization> Organizations =
    [
        new DevelopmentSeedOrganization(
            Name: "安心商行",
            Code: AnxinOrganizationCode,
            Accounts:
            [
                // demo-seed.ts: DEMO_SEED.accounts[0] ('account-smb-admin').
                new DevelopmentSeedAccount("admin", "安心商行管理者", AccountRole.SmbAdmin, AdminPermissions),

                // demo-seed.ts: DEMO_SEED.accounts[1] ('account-internal-employee').
                new DevelopmentSeedAccount(
                    "internal",
                    "安心商行客服同仁",
                    AccountRole.InternalEmployee,
                    [AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions]),

                // demo-seed.ts: DEMO_SEED.accounts[2] ('account-external-customer').
                new DevelopmentSeedAccount(
                    "customer",
                    "外部客戶",
                    AccountRole.ExternalCustomer,
                    [AccountPermission.SubmitAuthorizedForms, AccountPermission.ReadOwnTracking]),
            ]),

        new DevelopmentSeedOrganization(
            Name: "對照組織",
            Code: ControlOrganizationCode,
            Accounts: [new DevelopmentSeedAccount("admin", "對照組織管理者", AccountRole.SmbAdmin, AdminPermissions)]),
    ];
}
