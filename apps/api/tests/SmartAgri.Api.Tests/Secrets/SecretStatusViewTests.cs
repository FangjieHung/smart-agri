using System.Reflection;
using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Secrets;
using SmartAgri.Api.Secrets;

namespace SmartAgri.Api.Tests.Secrets;

/// <summary>
/// What the Api can ever say about a stored secret: set or not, last four, when. No plaintext and
/// no ciphertext, by construction (secrets-storage ADR).
/// </summary>
public class SecretStatusViewTests
{
    private const string Plaintext = "super-secret-channel-token-9876";
    private const string Ciphertext = "CfDJ8-opaque-ciphertext-payload";
    private static readonly DateTimeOffset SetAt = new(2026, 10, 6, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_json_has_exactly_configured_lastFour_and_updatedAt_and_no_value()
    {
        var secret = new ProtectedSecret(Ciphertext, ProtectedSecret.LastFourOf(Plaintext), SetAt);

        var json = JsonSerializer.Serialize(SecretStatusView.From(secret), JsonSerializerOptions.Web);

        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["configured", "lastFour", "updatedAt"]);
        document.RootElement.GetProperty("configured").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("lastFour").GetString().ShouldBe("9876");
        document.RootElement.GetProperty("updatedAt").GetDateTimeOffset().ShouldBe(SetAt);
        json.ShouldNotContain(Plaintext);
        json.ShouldNotContain("super-secret");
        json.ShouldNotContain(Ciphertext);
    }

    [Fact]
    public void A_value_shorter_than_four_characters_has_a_null_lastFour()
    {
        var secret = new ProtectedSecret(Ciphertext, ProtectedSecret.LastFourOf("abc"), SetAt);

        var json = JsonSerializer.Serialize(SecretStatusView.From(secret), JsonSerializerOptions.Web);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("configured").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("lastFour").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void No_secret_means_not_configured()
    {
        var json = JsonSerializer.Serialize(SecretStatusView.From(null), JsonSerializerOptions.Web);

        json.ShouldBe("""{"configured":false,"lastFour":null,"updatedAt":null}""");
    }

    [Fact]
    public void The_view_type_has_no_member_that_could_hold_a_value()
    {
        typeof(SecretStatusView).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .Order()
            .ShouldBe(["Configured", "LastFour", "UpdatedAt"]);
    }
}
