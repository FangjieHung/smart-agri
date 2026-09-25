using Shouldly;
using SmartAgri.Api.Team;
using SmartAgri.Domain.Accounts;

namespace SmartAgri.Api.Tests.Team;

/// <summary>
/// <see cref="TeamEndpoints"/>'s pure logic — normalisation and permission-string parsing
/// (M1 plan, Slice 8) — with no database and no host, so these run without Docker. The full
/// endpoints (403 identical-body rules, 422s that write nothing, immediate effect on
/// <c>/me</c>) are <see cref="TeamEndpointsTests"/>, which need real PostgreSQL.
/// </summary>
public class TeamEndpointsUnitTests
{
    [Fact]
    public void Normalize_orders_like_ACCOUNT_PERMISSIONS_and_drops_duplicates()
    {
        // Deliberately reversed and duplicated, like a client might send.
        var input = new[]
        {
            AccountPermission.ReadOwnTracking,
            AccountPermission.ManageAssistants,
            AccountPermission.ReadOwnTracking,
            AccountPermission.ManageDataSources,
            AccountPermission.ManageAssistants,
        };

        TeamEndpoints.Normalize(input).ShouldBe(
        [
            AccountPermission.ManageAssistants,
            AccountPermission.ManageDataSources,
            AccountPermission.ReadOwnTracking,
        ]);
    }

    [Fact]
    public void Normalize_of_empty_input_is_empty()
    {
        TeamEndpoints.Normalize([]).ShouldBeEmpty();
    }

    [Fact]
    public void Normalize_of_every_permission_is_the_full_ACCOUNT_PERMISSIONS_order()
    {
        // Reversed so the input order cannot accidentally match the expected output.
        TeamEndpoints.Normalize([.. AccountPermissionOrder.All.Reverse()]).ShouldBe(AccountPermissionOrder.All);
    }

    [Theory]
    [InlineData("manage-assistants", AccountPermission.ManageAssistants)]
    [InlineData("manage-data-sources", AccountPermission.ManageDataSources)]
    [InlineData("manage-publishing", AccountPermission.ManagePublishing)]
    [InlineData("read-consented-submissions", AccountPermission.ReadConsentedSubmissions)]
    [InlineData("use-shared-assistants", AccountPermission.UseSharedAssistants)]
    [InlineData("submit-authorized-forms", AccountPermission.SubmitAuthorizedForms)]
    [InlineData("read-own-tracking", AccountPermission.ReadOwnTracking)]
    public void TryParsePermission_accepts_every_declared_wire_name(string wireName, AccountPermission expected)
    {
        TeamEndpoints.TryParsePermission(wireName, out var permission).ShouldBeTrue();
        permission.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("manage-assistant")] // near miss (missing 's')
    [InlineData("MANAGE-ASSISTANTS")] // wire names are case-sensitive
    [InlineData("smb-admin")] // a role, not a permission
    public void TryParsePermission_rejects_anything_not_a_declared_wire_name(string? raw)
    {
        TeamEndpoints.TryParsePermission(raw, out var permission).ShouldBeFalse();
        permission.ShouldBe(default);
    }

    [Fact]
    public void Every_AccountPermission_round_trips_through_TryParsePermission_and_WireNames()
    {
        foreach (var permission in Enum.GetValues<AccountPermission>())
        {
            var wireName = Domain.WireNames<AccountPermission>.ToWire(permission);
            TeamEndpoints.TryParsePermission(wireName, out var parsed).ShouldBeTrue();
            parsed.ShouldBe(permission);
        }
    }
}
