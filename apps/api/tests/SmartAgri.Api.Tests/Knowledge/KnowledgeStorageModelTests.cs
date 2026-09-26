using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tests.Tenancy;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>How documents and their files are stored (original-files-in-postgresql ADR),
/// checked on the EF model and the options; no database needed.</summary>
public class KnowledgeStorageModelTests
{
    [Fact]
    public void Original_files_are_a_table_of_their_own_that_nothing_navigates_to()
    {
        using var dbContext = TenancyTestContexts.Create();
        var model = dbContext.Model;

        var files = model.FindEntityType(typeof(KnowledgeFileContent)).ShouldNotBeNull();
        files.GetTableName().ShouldBe("KnowledgeFileContents");
        files.FindProperty(nameof(KnowledgeFileContent.Bytes))!.GetColumnType().ShouldBe("bytea");

        // No entity reaches the bytes by navigation, and no other table has a binary
        // column, so loading documents or versions (e.g. with Include) cannot load files.
        model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetNavigations())
            .ShouldNotContain(navigation => navigation.TargetEntityType.ClrType == typeof(KnowledgeFileContent));
        model.GetEntityTypes()
            .Where(entityType => entityType.ClrType != typeof(KnowledgeFileContent))
            .SelectMany(entityType => entityType.GetProperties())
            .ShouldNotContain(property => property.ClrType == typeof(byte[]) && property.DeclaringType.ClrType.Namespace!.StartsWith("SmartAgri", StringComparison.Ordinal));
    }

    [Fact]
    public void The_list_and_detail_query_reads_versions_but_never_file_contents()
    {
        using var dbContext = TenancyTestContexts.Create(Guid.NewGuid());

        var sql = KnowledgeItemStates.Of(dbContext.KnowledgeDocuments, dbContext.KnowledgeDocumentVersions, DateTimeOffset.UtcNow).ToQueryString();

        sql.ShouldContain("\"KnowledgeDocumentVersions\"");
        sql.ShouldNotContain("KnowledgeFileContents");
        sql.ShouldNotContain("\"Bytes\"");
        TestContext.Current.SendDiagnosticMessage(sql);
    }

    [Fact]
    public void Duplicate_rules_are_unique_indexes_per_knowledge_base()
    {
        using var dbContext = TenancyTestContexts.Create();

        UniqueIndexes(dbContext, typeof(KnowledgeDocument)).ShouldContain("KnowledgeBaseId,Name");
        UniqueIndexes(dbContext, typeof(KnowledgeDocumentVersion)).ShouldContain("KnowledgeBaseId,Sha256");
        UniqueIndexes(dbContext, typeof(KnowledgeDocumentVersion)).ShouldContain("DocumentId,VersionNumber");
    }

    [Fact]
    public void Knowledge_options_default_to_20_MB_and_refuse_unusable_limits()
    {
        var options = new KnowledgeOptions();

        options.MaxFileBytes.ShouldBe(20L * 1024 * 1024);
        options.MaxRequestBodyBytes.ShouldBe(options.MaxFileBytes + KnowledgeOptions.MultipartOverheadBytes);
        options.Validate().ShouldBeNull();
        new KnowledgeOptions { MaxFileBytes = 0 }.Validate().ShouldNotBeNull();
        new KnowledgeOptions { MaxFileBytes = KnowledgeOptions.MaxFileBytesLimit + 1 }.Validate().ShouldNotBeNull();
    }

    [Fact]
    public void Extraction_limits_default_to_2000_units_and_5000_rows_per_sheet_and_refuse_unusable_ones()
    {
        var options = new KnowledgeOptions();

        options.ExtractionLimits.ShouldBe(new ExtractionLimits(2000, 5000));
        new KnowledgeOptions { MaxExtractedUnits = 0 }.Validate().ShouldNotBeNull();
        new KnowledgeOptions { MaxExtractedUnits = KnowledgeOptions.ExtractedUnitsLimit + 1 }.Validate().ShouldNotBeNull();
        new KnowledgeOptions { MaxSheetRows = 0 }.Validate().ShouldNotBeNull();
        new KnowledgeOptions { MaxSheetRows = KnowledgeOptions.SheetRowsLimit + 1 }.Validate().ShouldNotBeNull();
        new KnowledgeOptions { MaxExtractedUnits = 1, MaxSheetRows = 1 }.Validate().ShouldBeNull();
    }

    [Fact]
    public void Units_and_chunks_go_with_their_version_and_a_chunks_repeated_ids_are_held_to_its_versions()
    {
        using var dbContext = TenancyTestContexts.Create();
        var model = dbContext.Model;

        var units = model.FindEntityType(typeof(KnowledgeExtractedUnit)).ShouldNotBeNull();
        units.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(KnowledgeDocumentVersion))
            .DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);

        var chunks = model.FindEntityType(typeof(KnowledgeChunk)).ShouldNotBeNull();
        var toVersion = chunks.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(KnowledgeDocumentVersion));
        toVersion.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
        toVersion.Properties.Select(property => property.Name)
            .ShouldBe([nameof(KnowledgeChunk.VersionId), nameof(KnowledgeChunk.DocumentId), nameof(KnowledgeChunk.KnowledgeBaseId), nameof(KnowledgeChunk.OrganizationId)]);
        var toUnit = chunks.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(KnowledgeExtractedUnit));
        toUnit.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
        toUnit.Properties.Select(property => property.Name)
            .ShouldBe([nameof(KnowledgeChunk.VersionId), nameof(KnowledgeChunk.UnitOrdinal), nameof(KnowledgeChunk.OrganizationId)]);
        UniqueIndexes(dbContext, typeof(KnowledgeChunk)).ShouldContain("VersionId,UnitOrdinal,Ordinal");
    }

    [Fact]
    public void Every_file_format_has_exactly_one_text_extractor()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddKnowledge(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var extractors = provider.GetServices<IDocumentTextExtractor>().ToList();

        foreach (var format in Enum.GetValues<KnowledgeFileFormat>())
        {
            extractors.Count(extractor => extractor.CanExtract(format)).ShouldBe(1, format.ToString());
        }
    }

    private static List<string> UniqueIndexes(DbContext dbContext, Type entity) =>
    [
        .. dbContext.Model.FindEntityType(entity)!.GetIndexes()
            .Where(index => index.IsUnique)
            .Select(index => string.Join(',', index.Properties.Select(property => property.Name))),
    ];
}
