using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// The assistant enums are a contract with the frontend
/// (<c>apps/admin/src/app/core/domain/assistant.model.ts</c>,
/// <c>assistant-draft.model.ts</c>): the API serializes them and the database stores them, so
/// their names must match the frontend's unions.
/// </summary>
public partial class AssistantWireNameTests
{
    [Fact]
    public void Tones_match_the_frontend_union()
    {
        AssertMatchesFrontendUnion<AssistantTone>(
            "assistant-draft.model.ts", "AssistantTone", ["friendly", "professional", "concise"]);
    }

    [Fact]
    public void Knowledge_scopes_match_the_frontend_union()
    {
        AssertMatchesFrontendUnion<AssistantKnowledgeScope>(
            "assistant-draft.model.ts", "AssistantKnowledgeScope", ["company-data-only", "allow-general-knowledge"]);
    }

    [Fact]
    public void Statuses_are_a_subset_of_the_frontends_wider_status_union()
    {
        // The frontend's AssistantStatus also has "draft" (an AssistantDraft row, never an
        // Assistant) and "published" (M5): an Assistant row is only ever ready or paused
        // (M3 plan §4), so this checks containment, not equality, unlike the other two.
        var wire = Enum.GetValues<AssistantStatus>()
            .Select(value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value))!)
            .ToList();
        wire.ShouldBe(["ready", "paused"]);
        WireNames<AssistantStatus>.All.ShouldBe(wire);

        var frontendUnion = ReadUnion(File.ReadAllText(FindFrontendModel("assistant.model.ts")), "AssistantStatus");
        frontendUnion.ShouldContain("ready");
        frontendUnion.ShouldContain("paused");
    }

    [Fact]
    public void Website_brand_colors_match_the_frontend_union()
    {
        AssertMatchesFrontendUnion<WebsiteBrandColor>(
            "publishing.model.ts", "WebsiteBrandColor", ["forest", "ocean", "amber", "plum"]);
    }

    [Fact]
    public void Website_launcher_positions_match_the_frontend_union()
    {
        AssertMatchesFrontendUnion<WebsiteLauncherPosition>(
            "publishing.model.ts", "WebsiteLauncherPosition", ["bottom-right", "bottom-left"]);
    }

    [Fact]
    public void Website_channel_and_serving_states_have_the_plans_wire_names()
    {
        // M5a plan §3 C; no frontend union yet (the admin maps them in #202).
        WireNames<WebsiteChannelState>.All.ShouldBe(["draft", "published", "paused"]);
        WireNames<ChannelServingState>.All.ShouldBe(
            ["not-published", "paused", "suspended-acceptance", "suspended-knowledge", "suspended-quota", "serving"]);
    }

    [Fact]
    public void Line_channel_states_and_connection_checks_have_the_plans_wire_names()
    {
        // M5b plan §3 A/B (#229); the admin maps them in #233.
        WireNames<LineChannelState>.All.ShouldBe(["draft", "published", "paused"]);
        WireNames<LineConnectionCheckKind>.All.ShouldBe(["access-token", "webhook-endpoint", "webhook-test"]);
        WireNames<LineConnectionCheckState>.All.ShouldBe(["pending", "passed", "failed", "skipped"]);
    }

    private static void AssertMatchesFrontendUnion<TEnum>(string fileName, string unionName, string[] expected)
        where TEnum : struct, Enum
    {
        Enum.GetValues<TEnum>()
            .Select(value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value)))
            .ShouldBe(expected);
        WireNames<TEnum>.All.ShouldBe(expected);
        ReadUnion(File.ReadAllText(FindFrontendModel(fileName)), unionName).ShouldBe(expected);
    }

    /// <summary>Extracts the string literals of <c>export type {name} = 'a' | 'b' ...;</c>.</summary>
    private static string[] ReadUnion(string source, string name)
    {
        var declaration = Regex.Match(source, $@"export\s+type\s+{name}\s*=(?<body>[^;]*);");
        declaration.Success.ShouldBeTrue($"`export type {name}` not found");

        return [.. StringLiteral().Matches(declaration.Groups["body"].Value).Select(match => match.Groups[1].Value)];
    }

    private static string FindFrontendModel(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "admin", "src", "app", "core", "domain", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"apps/admin/src/app/core/domain/{fileName} not found above the test output directory.");
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex StringLiteral();
}
