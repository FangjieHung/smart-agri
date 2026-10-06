using Shouldly;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Domain.Tests.Secrets;

public class ProtectedSecretTests
{
    [Theory]
    [InlineData("abcd", "abcd")]
    [InlineData("0123456789", "6789")]
    [InlineData("line-secret-xyz1", "xyz1")]
    public void The_last_four_characters_of_a_long_enough_value_are_kept(string plaintext, string expected)
    {
        ProtectedSecret.LastFourOf(plaintext).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abc")]
    public void A_value_shorter_than_four_characters_has_no_last_four(string plaintext)
    {
        ProtectedSecret.LastFourOf(plaintext).ShouldBeNull();
    }

    [Fact]
    public void A_surrogate_pair_is_never_cut_in_half()
    {
        // Three ASCII characters plus one emoji (two UTF-16 chars): four characters, the last
        // four being all of them, and the emoji intact.
        ProtectedSecret.LastFourOf("x\U0001F600yz1").ShouldBe("\U0001F600yz1");
        ProtectedSecret.LastFourOf("ab\U0001F600").ShouldBeNull();
    }

    [Fact]
    public void ToString_never_shows_the_ciphertext()
    {
        var secret = new ProtectedSecret("CfDJ8-ciphertext-payload", "1234", new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));

        secret.ToString().ShouldNotContain("ciphertext");
        secret.ToString().ShouldContain("1234");
    }

    [Fact]
    public void A_blank_ciphertext_is_refused()
    {
        Should.Throw<ArgumentException>(() => new ProtectedSecret(" ", null, DateTimeOffset.UnixEpoch));
    }
}
