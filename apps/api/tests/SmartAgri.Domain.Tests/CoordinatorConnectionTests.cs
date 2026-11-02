using Shouldly;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Domain.Tests;

/// <summary>A shop's credential for an external service (#376): write-only, active until refused, active again when set.</summary>
public class CoordinatorConnectionTests
{
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset At = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));
    private static readonly ProtectedSecret Secret = new("CfDJ8-ciphertext-of-the-token", "wxyz", At);

    [Fact]
    public void A_connection_is_active_with_its_account_and_never_shows_the_credential()
    {
        var connection = CoordinatorConnection.Create(Organization, CoordinatorService.Line, Secret, "U" + new string('a', 32), "  王記麵店（@123abcde）  ", At);

        connection.ShouldSatisfyAllConditions(
            row => row.Id.ShouldNotBe(Guid.Empty),
            row => row.Status.ShouldBe(CoordinatorConnectionStatus.Active),
            row => row.DisplayName.ShouldBe("王記麵店（@123abcde）"),
            row => (row.CreatedAt, row.UpdatedAt).ShouldBe((At.ToUniversalTime(), At.ToUniversalTime())),
            row => row.UpdatedAt.Offset.ShouldBe(TimeSpan.Zero));
        connection.ToString().ShouldNotContain("ciphertext");
        connection.ToString().ShouldNotContain("wxyz");
        CoordinatorConnection.PurposeOf(CoordinatorService.Line).ShouldBe("coordinator.line-access-token");
        CoordinatorConnection.PurposeOf(CoordinatorService.Facebook).ShouldBe("coordinator.facebook-credential");
        CoordinatorConnection.PurposeOf(CoordinatorService.Google).ShouldBe("coordinator.google-credential");
        WireNames<CoordinatorService>.All.ShouldBe(["line", "facebook", "google"]);
        connection.ReauthorizationRequestedAt.ShouldBeNull();
        WireNames<CoordinatorConnectionStatus>.All.ShouldBe(["active", "revoked"]);
    }

    [Fact]
    public void Setting_it_again_replaces_the_credential_and_a_long_label_is_shortened()
    {
        var connection = CoordinatorConnection.Create(Organization, CoordinatorService.Line, Secret, "Uold", "舊帳號", At);
        var later = At.AddDays(1);
        var replacement = new ProtectedSecret("CfDJ8-another", "abcd", later);

        connection.Set(replacement, "Unew", new string('名', 150), later);

        (connection.Secret, connection.ExternalId, connection.Status, connection.UpdatedAt)
            .ShouldBe((replacement, "Unew", CoordinatorConnectionStatus.Active, later.ToUniversalTime()));
        connection.DisplayName.Length.ShouldBe(CoordinatorConnection.DisplayNameMaxLength);
    }

    [Fact]
    public void Bad_arguments_are_refused()
    {
        Should.Throw<ArgumentException>(() => CoordinatorConnection.Create(Guid.Empty, CoordinatorService.Line, Secret, "U1", "x", At));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorConnection.Create(Organization, (CoordinatorService)7, Secret, "U1", "x", At));
        Should.Throw<ArgumentException>(() => CoordinatorConnection.Create(Organization, CoordinatorService.Line, Secret, "U 1", "x", At));
        Should.Throw<ArgumentException>(() => CoordinatorConnection.Create(Organization, CoordinatorService.Line, Secret, new string('U', 65), "x", At));
        Should.Throw<ArgumentException>(() => CoordinatorConnection.Create(Organization, CoordinatorService.Google, Secret, "loc\u00001", "x", At));
        CoordinatorConnection.IsValidExternalId("locations/123").ShouldBeTrue();
        CoordinatorConnection.IsValidExternalId(new string('1', 64)).ShouldBeTrue();
        CoordinatorConnection.IsValidExternalId(string.Empty).ShouldBeFalse();
        CoordinatorConnection.IsValidExternalId(null).ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => CoordinatorConnection.Create(Organization, CoordinatorService.Line, null!, "U1", "x", At));
    }
}
