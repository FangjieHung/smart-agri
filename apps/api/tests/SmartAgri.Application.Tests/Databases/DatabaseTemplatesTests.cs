using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

/// <summary>
/// The server's templates are the mock's (<c>demo-seed-databases.ts</c> <c>DATABASE_TEMPLATES</c>),
/// so creating from a template gives the same initial form in mock and API mode (#142).
/// </summary>
public partial class DatabaseTemplatesTests
{
    [Fact]
    public void There_is_one_template_per_id_in_declaration_order()
    {
        DatabaseTemplates.All.Select(template => template.Id).ShouldBe(Enum.GetValues<DatabaseTemplateId>());
    }

    [Fact]
    public void Every_template_is_a_valid_initial_form()
    {
        foreach (var template in DatabaseTemplates.All)
        {
            Should.NotThrow(() => DatabaseFormVersion.EnsureValid(template.Fields), template.Name);
        }
    }

    [Fact]
    public void Templates_match_the_frontend_mock()
    {
        var source = File.ReadAllText(FindFrontendFile("apps", "admin", "src", "app", "core", "repositories", "demo-seed-databases.ts"));
        var start = source.IndexOf("export const DATABASE_TEMPLATES", StringComparison.Ordinal);
        var end = source.IndexOf("export const DATABASE_COLLECTIONS", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1);
        var templatesSource = source[start..end];

        var frontend = TemplateHeader().Matches(templatesSource).Select((match, index) =>
        {
            var blockEnd = index + 1 < TemplateHeader().Matches(templatesSource).Count
                ? TemplateHeader().Matches(templatesSource)[index + 1].Index
                : templatesSource.Length;
            var block = templatesSource[match.Index..blockEnd];
            var fields = FieldCall().Matches(block).Select(field => Describe(
                field.Groups["helper"].Value,
                field.Groups["id"].Value,
                field.Groups["label"].Value,
                field.Value.Contains(", true)", StringComparison.Ordinal)));
            return $"{match.Groups["id"].Value}|{match.Groups["name"].Value}|{match.Groups["description"].Value}|{string.Join(";", fields)}";
        }).ToList();

        var backend = DatabaseTemplates.All.Select(template =>
            $"{WireNames<DatabaseTemplateId>.ToWire(template.Id)}|{template.Name}|{template.Description}|" +
            string.Join(";", template.Fields.Select(field => Describe(Helper(field.Type), field.Id, field.Label, field.Required)))).ToList();

        backend.ShouldBe(frontend);
    }

    private static string Describe(string helper, string id, string label, bool required) => $"{helper}:{id}:{label}:{required}";

    private static string Helper(DatabaseFieldType type) => type switch
    {
        DatabaseFieldType.Text => "text",
        DatabaseFieldType.Number => "number",
        DatabaseFieldType.Date => "date",
        DatabaseFieldType.SingleChoice => "single",
        DatabaseFieldType.MultipleChoice => "multiple",
        DatabaseFieldType.Scale => "scale",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string FindFrontendFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{string.Join('/', parts)} not found above the test output directory.");
    }

    [GeneratedRegex(@"id: '(?<id>template-[a-z-]+)',\s*name: '(?<name>[^']+)',\s*description: '(?<description>[^']+)'")]
    private static partial Regex TemplateHeader();

    /// <summary>One helper call such as <c>text('field-phone', '聯絡電話'),</c> up to the end of its line.</summary>
    [GeneratedRegex(@"\b(?<helper>text|date|number|single|multiple|scale)\('(?<id>field-[a-z0-9-]+)', '(?<label>[^']+)'[^\n]*")]
    private static partial Regex FieldCall();
}
