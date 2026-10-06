using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// <see cref="Database"/> and <see cref="DatabaseFormVersion"/> invariants, and the enums that
/// are a contract with the frontend (<c>apps/admin/src/app/core/domain/database.model.ts</c>).
/// </summary>
public partial class DatabaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Field_types_match_the_frontend_union()
    {
        // Declared in libs/chat since #198 (chat replies show form fields); admin re-exports it.
        AssertMatchesFrontend<DatabaseFieldType>(
            "DatabaseFieldType", ["text", "number", "date", "single-choice", "multiple-choice", "scale"], ChatViewModel);
    }

    [Fact]
    public void Template_ids_match_the_frontend_union()
    {
        AssertMatchesFrontend<DatabaseTemplateId>(
            "DatabaseTemplateId",
            ["template-customer-profile", "template-periodic-report", "template-satisfaction", "template-progress", "template-blank"],
            AdminDatabaseModel);
    }

    [Fact]
    public void Create_trims_and_starts_with_created_equal_to_updated()
    {
        var organizationId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        var database = Database.Create(organizationId, ownerId, "  門市滿意度 ", " 收集評分 ", DatabaseTemplateId.Satisfaction, Now);

        database.Id.ShouldNotBe(Guid.Empty);
        database.OrganizationId.ShouldBe(organizationId);
        database.OwnerAccountId.ShouldBe(ownerId);
        database.Name.ShouldBe("門市滿意度");
        database.Purpose.ShouldBe("收集評分");
        database.TemplateId.ShouldBe(DatabaseTemplateId.Satisfaction);
        database.CreatedAt.ShouldBe(Now);
        database.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Create_refuses_what_validation_should_have_caught()
    {
        Should.Throw<ArgumentException>(() => Database.Create(Guid.NewGuid(), Guid.NewGuid(), "  ", "", DatabaseTemplateId.Blank, Now));
        Should.Throw<ArgumentException>(() => Database.Create(
            Guid.NewGuid(), Guid.NewGuid(), new string('名', Database.NameMaxLength + 1), "", DatabaseTemplateId.Blank, Now));
        Should.Throw<ArgumentException>(() => Database.Create(Guid.Empty, Guid.NewGuid(), "名", "", DatabaseTemplateId.Blank, Now));
        Should.Throw<ArgumentException>(() => Database.Create(Guid.NewGuid(), Guid.NewGuid(), "名", "", (DatabaseTemplateId)99, Now));
    }

    [Fact]
    public void A_form_version_belongs_to_its_database_and_copies_the_fields()
    {
        var database = Database.Create(Guid.NewGuid(), Guid.NewGuid(), "名", "", DatabaseTemplateId.Blank, Now);
        var fields = new List<DatabaseFormField> { Text("field-item", "項目名稱") };

        var version = DatabaseFormVersion.Create(database, 1, fields, database.OwnerAccountId, Now);
        fields.Add(Text("field-other", "其他"));

        version.DatabaseId.ShouldBe(database.Id);
        version.OrganizationId.ShouldBe(database.OrganizationId);
        version.VersionNumber.ShouldBe(1);
        version.CreatedByAccountId.ShouldBe(database.OwnerAccountId);
        version.Fields.Select(field => field.Id).ShouldBe(["field-item"]);
        Should.Throw<ArgumentOutOfRangeException>(() => DatabaseFormVersion.Create(database, 0, fields, database.OwnerAccountId, Now));
    }

    public static TheoryData<string, DatabaseFormField[]> InvalidForms() => new()
    {
        { "no fields", [] },
        { "repeated id", [Text("field-a", "甲"), Text("field-a", "乙")] },
        { "malformed id", [Text("item", "甲")] },
        { "repeated label", [Text("field-a", "甲"), Text("field-b", "甲")] },
        { "blank label", [Text("field-a", "  ")] },
        { "untrimmed label", [Text("field-a", " 甲")] },
        { "choice with one option", [new("field-a", "甲", DatabaseFieldType.SingleChoice, false, ["一"], null, "")] },
        { "choice with repeated options", [new("field-a", "甲", DatabaseFieldType.MultipleChoice, false, ["一", "一"], null, "")] },
        { "text with options", [new("field-a", "甲", DatabaseFieldType.Text, false, ["一", "二"], null, "")] },
        { "scale without range", [new("field-a", "甲", DatabaseFieldType.Scale, false, [], null, "")] },
        { "scale min not below max", [new("field-a", "甲", DatabaseFieldType.Scale, false, [], new(5, 5, "", ""), "")] },
        { "scale with 12 steps", [new("field-a", "甲", DatabaseFieldType.Scale, false, [], new(0, 11, "", ""), "")] },
        { "date with range", [new("field-a", "甲", DatabaseFieldType.Date, false, [], new(1, 5, "", ""), "")] },
        { "text with unit", [new("field-a", "甲", DatabaseFieldType.Text, false, [], null, "元")] },
        { "undeclared type", [new("field-a", "甲", (DatabaseFieldType)42, false, [], null, "")] },
    };

    [Theory]
    [MemberData(nameof(InvalidForms))]
    public void Invalid_forms_are_refused(string because, DatabaseFormField[] fields)
    {
        Should.Throw<ArgumentException>(() => DatabaseFormVersion.EnsureValid(fields), because);
    }

    [Fact]
    public void Every_field_type_has_a_valid_shape()
    {
        DatabaseFormVersion.EnsureValid(
        [
            Text("field-text", "文字"),
            new("field-number", "數字", DatabaseFieldType.Number, true, [], null, "元"),
            new("field-date", "日期", DatabaseFieldType.Date, false, [], null, ""),
            new("field-single", "單選", DatabaseFieldType.SingleChoice, true, ["一", "二"], null, ""),
            new("field-multiple", "多選", DatabaseFieldType.MultipleChoice, false, ["一", "二", "三"], null, ""),
            new("field-scale", "量尺", DatabaseFieldType.Scale, true, [], new(0, 10, "最差", "最好"), ""),
        ]);
    }

    private static DatabaseFormField Text(string id, string label) =>
        new(id, label, DatabaseFieldType.Text, false, [], null, string.Empty);

    private static readonly string[] AdminDatabaseModel = ["apps", "admin", "src", "app", "core", "domain", "database.model.ts"];
    private static readonly string[] ChatViewModel = ["libs", "chat", "src", "lib", "chat-view.model.ts"];

    private static void AssertMatchesFrontend<TEnum>(string unionName, string[] expected, string[] frontendFile)
        where TEnum : struct, Enum
    {
        Enum.GetValues<TEnum>()
            .Select(value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value)))
            .ShouldBe(expected);
        WireNames<TEnum>.All.ShouldBe(expected);
        var path = FindFrontendFile(frontendFile);
        ReadUnion(File.ReadAllText(path), unionName, Path.GetFileName(path)).ShouldBe(expected);
    }

    private static string[] ReadUnion(string source, string name, string fileName)
    {
        var declaration = Regex.Match(source, $@"export\s+type\s+{name}\s*=(?<body>[^;]*);");
        declaration.Success.ShouldBeTrue($"`export type {name}` not found in {fileName}");
        return [.. StringLiteral().Matches(declaration.Groups["body"].Value).Select(match => match.Groups[1].Value)];
    }

    private static string FindFrontendFile(string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{string.Join('/', segments)} not found above the test output directory.");
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex StringLiteral();
}
