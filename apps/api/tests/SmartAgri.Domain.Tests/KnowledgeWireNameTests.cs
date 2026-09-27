using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// The knowledge base enums are a contract with the frontend
/// (<c>apps/admin/src/app/core/domain/knowledge-base.model.ts</c>): the API serializes them
/// and the database stores them, so their names must match the frontend's unions exactly.
/// </summary>
public partial class KnowledgeWireNameTests
{
    [Fact]
    public void Sharing_scopes_match_the_frontend_union()
    {
        AssertMatchesFrontend<KnowledgeSharingScope>("KnowledgeSharingScope", ["private", "specific-accounts", "public"]);
    }

    [Fact]
    public void Document_statuses_match_the_frontend_union()
    {
        AssertMatchesFrontend<KnowledgeDocumentStatus>(
            "KnowledgeDocumentStatus", ["queued", "processing", "ready", "partially-readable", "failed"]);
    }

    [Fact]
    public void Item_kinds_match_the_frontend_union()
    {
        AssertMatchesFrontend<KnowledgeItemKind>("KnowledgeItemKind", ["document", "faq"]);
    }

    [Fact]
    public void Activity_actions_have_wire_names()
    {
        // Backend-only (not in the frontend model), but stored by wire name like the others.
        WireNames<KnowledgeActivityAction>.All.ShouldBe(
        [
            "knowledge-base-created", "knowledge-base-updated", "sharing-changed", "knowledge-base-deleted",
            "document-uploaded", "version-retried", "document-deleted", "chunk-excluded", "chunk-included",
            "version-uploaded", "version-approved", "document-disabled", "document-enabled",
        ]);
    }

    [Fact]
    public void Review_states_have_wire_names()
    {
        // Backend-only until the version screen (Slice 13) adds them to the frontend model;
        // the check constraint on KnowledgeDocumentVersions spells them too.
        WireNames<KnowledgeReviewState>.All.ShouldBe(["pending-review", "approved"]);
        Enum.GetValues<KnowledgeReviewState>()
            .Select(value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value)))
            .ShouldBe(WireNames<KnowledgeReviewState>.All);
    }

    [Fact]
    public void Extraction_enums_have_wire_names()
    {
        // Backend-only until the preview screen (Slice 13) adds them to the frontend model.
        WireNames<KnowledgeUnitLocationKind>.All.ShouldBe(["page", "section", "sheet"]);
        WireNames<KnowledgeUnitIssue>.All.ShouldBe(["too-little-text", "garbled-text", "rows-truncated"]);
        Enum.GetValues<KnowledgeUnitIssue>()
            .Select(value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value)))
            .ShouldBe(WireNames<KnowledgeUnitIssue>.All);
    }

    private static void AssertMatchesFrontend<TEnum>(string unionName, string[] expected)
        where TEnum : struct, Enum
    {
        Enum.GetValues<TEnum>()
            .Select(value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value)))
            .ShouldBe(expected);
        WireNames<TEnum>.All.ShouldBe(expected);
        ReadUnion(File.ReadAllText(FindFrontendKnowledgeModel()), unionName).ShouldBe(expected);
    }

    /// <summary>Extracts the string literals of <c>export type {name} = 'a' | 'b' ...;</c>.</summary>
    private static string[] ReadUnion(string source, string name)
    {
        var declaration = Regex.Match(source, $@"export\s+type\s+{name}\s*=(?<body>[^;]*);");
        declaration.Success.ShouldBeTrue($"`export type {name}` not found in knowledge-base.model.ts");

        return [.. StringLiteral().Matches(declaration.Groups["body"].Value).Select(match => match.Groups[1].Value)];
    }

    private static string FindFrontendKnowledgeModel()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "admin", "src", "app", "core", "domain", "knowledge-base.model.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("apps/admin/src/app/core/domain/knowledge-base.model.ts not found above the test output directory.");
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex StringLiteral();
}
