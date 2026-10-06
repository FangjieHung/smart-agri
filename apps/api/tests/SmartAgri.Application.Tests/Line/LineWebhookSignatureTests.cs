using System.Text;
using Shouldly;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Line;

/// <summary>
/// LINE's webhook signature (M5b plan §2.1, #231): base64(HMAC-SHA256(channel secret, raw body)). The
/// expected values were computed independently with LINE's documented recipe,
/// <c>printf '%s' '&lt;body&gt;' | openssl dgst -sha256 -hmac '&lt;secret&gt;' -binary | openssl base64</c>.
/// </summary>
public class LineWebhookSignatureTests
{
    /// <summary>An obviously fake secret of the right shape (32 hexadecimal digits).</summary>
    internal const string Secret = "8c9f1d2e3a4b5c6d7e8f90a1b2c3d4e5";

    /// <summary>Console's "Verify" request: no events.</summary>
    internal const string VerifyBody = """{"destination":"U0123456789abcdef0123456789abcdef","events":[]}""";

    internal const string VerifySignature = "KqxlZTTVv23r68dKJUaSc9MAlERzNasoe0r1LCO003o=";

    private const string ChineseBody =
        """{"destination":"U0123456789abcdef0123456789abcdef","events":[{"type":"message","message":{"type":"text","id":"1","text":"退貨期限？"}}]}""";

    private const string ChineseSignature = "sLM/9DWLkhCkKCC9HI0kumK1+5BYLieiNI6eSfygy08=";

    private static byte[] Key => Encoding.UTF8.GetBytes(Secret);

    [Fact]
    public void The_verify_request_signature_matches_openssls()
    {
        LineWebhookSignature.Compute(Secret, Encoding.UTF8.GetBytes(VerifyBody)).ShouldBe(VerifySignature);
        LineWebhookSignature.IsValid(Key, Encoding.UTF8.GetBytes(VerifyBody), VerifySignature).ShouldBeTrue();
    }

    [Fact]
    public void A_body_with_chinese_text_is_signed_over_its_utf8_bytes()
    {
        var body = Encoding.UTF8.GetBytes(ChineseBody);
        body.Length.ShouldBe(141);
        LineWebhookSignature.Compute(Secret, body).ShouldBe(ChineseSignature);
        LineWebhookSignature.IsValid(Key, body, ChineseSignature).ShouldBeTrue();

        // The same JSON with the text escaped is another body, so another signature.
        var escaped = Encoding.UTF8.GetBytes(ChineseBody.Replace("退", "\\u9000", StringComparison.Ordinal));
        LineWebhookSignature.IsValid(Key, escaped, ChineseSignature).ShouldBeFalse();
    }

    [Fact]
    public void Any_single_changed_byte_fails()
    {
        var body = Encoding.UTF8.GetBytes(VerifyBody);
        for (var index = 0; index < body.Length; index++)
        {
            var changed = (byte[])body.Clone();
            changed[index] ^= 0x01;
            LineWebhookSignature.IsValid(Key, changed, VerifySignature).ShouldBeFalse($"byte {index}");
        }

        LineWebhookSignature.IsValid(Key, [.. body, (byte)' '], VerifySignature).ShouldBeFalse("a trailing space");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("KqxlZTTVv23r68dKJUaSc9MAlERzNasoe0r1LCO003o")]
    [InlineData("KqxlZTTVv23r68dKJUaSc9MAlERzNasoe0r1LCO003oA")]
    [InlineData("KqxlZTTVv23r68dKJUaSc9MAlERzNasoe0r1LCO003o=KqxlZTTV")]
    [InlineData("/zHMTVYo3kLP+GW+x0iM2Y4+Uy9eoS8LRq+qMoARKOw=")]
    public void A_missing_malformed_truncated_or_other_secrets_signature_fails(string? header)
    {
        LineWebhookSignature.IsValid(Key, Encoding.UTF8.GetBytes(VerifyBody), header).ShouldBeFalse();
    }

    [Fact]
    public void Another_secret_fails()
    {
        LineWebhookSignature.IsValid(
                Encoding.UTF8.GetBytes("wrongsecretwrongsecretwrongsecr0"), Encoding.UTF8.GetBytes(VerifyBody), VerifySignature)
            .ShouldBeFalse();
        LineWebhookSignature.Compute("wrongsecretwrongsecretwrongsecr0", Encoding.UTF8.GetBytes(VerifyBody))
            .ShouldBe("/zHMTVYo3kLP+GW+x0iM2Y4+Uy9eoS8LRq+qMoARKOw=");
    }
}
