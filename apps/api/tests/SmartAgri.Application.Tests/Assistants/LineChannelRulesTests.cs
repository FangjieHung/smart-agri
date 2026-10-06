using System.Text.Json;
using Shouldly;
using SmartAgri.Application.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>
/// <see cref="LineChannelRules"/> (issue #229): the connection fields' validation ported from the
/// frontend mock, and the write-only credentials of a settings save.
/// </summary>
public sealed class LineChannelRulesTests
{
    private const string Secret = "ffffffffffffffffffffffffffffffff";
    private static readonly string Token = new('A', 40);

    // --- The same cases as the frontend's lineChecks() spec --------------------------------------

    [Fact]
    public void Every_shared_line_field_case_gives_the_same_result_as_the_frontend()
    {
        // apps/admin/src/app/core/domain/line-field-cases.json is also run by
        // apps/admin/src/app/core/repositories/publishing-channels.line-fields.spec.ts.
        var cases = SharedLineFieldCases();
        cases.Count.ShouldBeGreaterThan(30);
        cases.Select(testCase => testCase.GetProperty("field").GetString()).Distinct()
            .ShouldBe(LineChannelRules.Fields.Select(field => field.Field), ignoreOrder: true);

        foreach (var testCase in cases)
        {
            var name = testCase.GetProperty("name").GetString()!;
            var field = testCase.GetProperty("field").GetString()!;
            var value = testCase.GetProperty("value").GetString()!;
            var expected = testCase.GetProperty("error");

            var error = LineChannelRules.ValidateField(field, value);

            if (expected.ValueKind == JsonValueKind.Null)
            {
                error.ShouldBeNull(name);
                LineChannelRules.Normalize(value).ShouldBe(testCase.GetProperty("normalized").GetString(), name);
            }
            else
            {
                error.ShouldBe(expected.GetString(), name);
            }
        }
    }

    // --- A settings save --------------------------------------------------------------------------

    [Fact]
    public void The_first_save_needs_every_field_and_reports_them_all_at_once()
    {
        var result = LineChannelRules.ForUpdate(null, " ", null, "", null, credentialsStored: false);

        result.IsValid.ShouldBeFalse();
        result.Failures.Select(failure => failure.Field).ShouldBe(
            ["officialAccountId", "channelId", "channelSecret", "accessToken", "welcomeMessage"]);
        result.Failures.Select(failure => failure.Message).ShouldBe(
        [
            "請填寫 官方帳號 ID。",
            "請填寫 Channel ID。",
            "請填寫 Channel secret。",
            "請填寫 Channel access token。",
            "請填寫歡迎訊息。",
        ]);
    }

    [Fact]
    public void A_valid_first_save_is_trimmed()
    {
        var result = LineChannelRules.ForUpdate(" @anxin-demo ", "1650000000 ", $" {Secret}", $"{Token}\n", " 歡迎 ", credentialsStored: false);

        result.IsValid.ShouldBeTrue();
        result.Value.OfficialAccountId.ShouldBe("@anxin-demo");
        result.Value.ChannelId.ShouldBe("1650000000");
        result.Value.ChannelSecret.ShouldBe(Secret);
        result.Value.AccessToken.ShouldBe(Token);
        result.Value.WelcomeMessage.ShouldBe("歡迎");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Once_stored_an_empty_credential_keeps_the_stored_one(string? empty)
    {
        var result = LineChannelRules.ForUpdate("@anxin-demo", "1650000000", empty, empty, "歡迎", credentialsStored: true);

        result.IsValid.ShouldBeTrue();
        result.Value.ChannelSecret.ShouldBeNull();
        result.Value.AccessToken.ShouldBeNull();
    }

    [Fact]
    public void Once_stored_a_new_credential_is_still_validated()
    {
        var result = LineChannelRules.ForUpdate("@anxin-demo", "1650000000", "not-hex", "short token", "歡迎", credentialsStored: true);

        result.Failures.Select(failure => failure.Field).ShouldBe(["channelSecret", "accessToken"]);
    }

    [Fact]
    public void The_welcome_message_is_at_most_120_characters()
    {
        LineChannelRules.ForUpdate("@anxin-demo", "1650000000", null, null, new string('歡', 120), credentialsStored: true)
            .IsValid.ShouldBeTrue();

        var tooLong = LineChannelRules.ForUpdate("@anxin-demo", "1650000000", null, null, new string('歡', 121), credentialsStored: true);
        tooLong.Failures.Single().Field.ShouldBe("welcomeMessage");
        tooLong.Failures.Single().Message.ShouldBe("歡迎訊息請在 120 個字以內。");
    }

    [Fact]
    public void Settings_never_print_a_credential()
    {
        var settings = LineChannelRules.ForUpdate("@anxin-demo", "1650000000", Secret, Token, "歡迎", credentialsStored: false).Value;

        settings.ToString().ShouldNotContain(Secret);
        settings.ToString().ShouldNotContain(Token);
    }

    [Fact]
    public void An_unknown_field_is_a_programming_error()
    {
        Should.Throw<ArgumentException>(() => LineChannelRules.ValidateField("welcomeMessage", "x"));
    }

    private static List<JsonElement> SharedLineFieldCases()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "admin", "src", "app", "core", "domain", "line-field-cases.json");
            if (File.Exists(candidate))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(item => item.Clone())];
            }
        }

        throw new FileNotFoundException("apps/admin/src/app/core/domain/line-field-cases.json not found above the test output directory.");
    }
}
