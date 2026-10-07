using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

/// <summary><see cref="KnowledgeRechunking.Plan"/>: what <c>rechunk</c> changes, and which
/// exclusions carry over (#301).</summary>
public class KnowledgeRechunkingTests
{
    private const string Label = "青禾門市 › 基本資訊";
    private const string OldTableChunk = "門市資料如下。\n\n| 項目 | 內容 |\n| --- | --- |\n| 地址 | 示範縣安和路 18 號 |\n| 電話 | (03) 012-3456 |";

    private static readonly ExtractedTable Table = new(["項目", "內容"], [["地址", "示範縣安和路 18 號"], ["電話", "(03) 012-3456"]]);

    [Fact]
    public void A_unit_whose_chunks_come_out_the_same_is_left_alone_and_a_changed_one_is_replaced()
    {
        var (processed, units) = Read(
            ExtractedUnit.Section(["退換貨"], "七天內可申請退貨。"),
            ExtractedUnit.Section(["青禾門市", "基本資訊"], OldTableChunk, "門市資料如下。", [Table]));
        var stored = new[] { Chunk(0, 0, "退換貨", "七天內可申請退貨。"), Chunk(1, 0, Label, OldTableChunk) };

        var plan = KnowledgeRechunking.Plan(KnowledgeDocumentStatus.Ready, null, units, stored, processed);

        plan.Mismatch.ShouldBeNull();
        var unit = plan.Units.ShouldHaveSingleItem();
        unit.UnitOrdinal.ShouldBe(1);
        unit.OldChunkIds.ShouldBe([stored[1].Id]);
        unit.Chunks.Select(chunk => (chunk.Ordinal, chunk.Text, chunk.Excluded)).ShouldBe(
        [
            (0, "門市資料如下。", false),
            (1, "項目：地址\n內容：示範縣安和路 18 號", false),
            (2, "項目：電話\n內容：(03) 012-3456", false),
        ]);
        (plan.OldChunkCount, plan.NewChunkCount, plan.ChunksToEmbed).ShouldBe((2, 4, 3));
        plan.UnmappedExclusions.ShouldBeEmpty();
    }

    [Fact]
    public void Nothing_changes_for_a_version_without_tables()
    {
        var (processed, units) = Read(ExtractedUnit.Section(["退換貨"], "七天內可申請退貨。"), ExtractedUnit.Page(1, "第一頁的內容，足夠長。"));
        var stored = new[] { Chunk(0, 0, "退換貨", "七天內可申請退貨。"), Chunk(1, 0, "第 1 頁", "第一頁的內容，足夠長。", excluded: true) };

        var plan = KnowledgeRechunking.Plan(KnowledgeDocumentStatus.Ready, null, units, stored, processed);

        plan.Units.ShouldBeEmpty();
        (plan.OldChunkCount, plan.NewChunkCount, plan.ChunksToEmbed).ShouldBe((2, 2, 0));
        plan.UnmappedExclusions.ShouldBeEmpty("an unchanged unit keeps its chunks, exclusions included");
    }

    [Fact]
    public void Excluding_a_whole_table_section_keeps_every_row_excluded()
    {
        var (processed, units) = Read(ExtractedUnit.Section(["青禾門市", "基本資訊"], OldTableChunk, "門市資料如下。", [Table]));

        var plan = KnowledgeRechunking.Plan(KnowledgeDocumentStatus.Ready, null, units, [Chunk(0, 0, Label, OldTableChunk, excluded: true)], processed);

        plan.Units.Single().Chunks.ShouldAllBe(chunk => chunk.Excluded);
        plan.ExclusionsKept.ShouldBe(3);
        plan.UnmappedExclusions.ShouldBeEmpty();
    }

    [Fact]
    public void Only_rows_inside_an_excluded_chunk_inherit_its_exclusion_and_one_nothing_inherits_from_is_reported()
    {
        var (processed, units) = Read(ExtractedUnit.Section(["青禾門市", "基本資訊"], OldTableChunk, "門市資料如下。", [Table]));
        var stored = new[]
        {
            Chunk(0, 0, Label, "門市資料如下。\n\n| 項目 | 內容 |\n| --- | --- |\n| 地址 | 示範縣安和路 18 號 |"),
            Chunk(0, 1, Label, "| 電話 | (03) 012-3456 |", excluded: true),
            Chunk(0, 2, Label, "已經不存在的舊段落", excluded: true),
        };

        var plan = KnowledgeRechunking.Plan(KnowledgeDocumentStatus.Ready, null, units, stored, processed);

        plan.Units.Single().Chunks.Select(chunk => chunk.Excluded).ShouldBe([false, false, true]);
        plan.Units.Single().ExcludedOldChunkIds.ShouldBe([stored[1].Id, stored[2].Id]);
        plan.UnmappedExclusions.ShouldBe([stored[2]]);
    }

    [Fact]
    public void A_file_that_reads_differently_from_what_is_stored_is_a_mismatch()
    {
        var (processed, units) = Read(ExtractedUnit.Section(["退換貨"], "七天內可申請退貨。"));
        var stored = new[] { Chunk(0, 0, "退換貨", "七天內可申請退貨。") };

        KnowledgeRechunking.Plan(KnowledgeDocumentStatus.Ready, null, [units[0] with { Text = "十天內" }], stored, processed).Mismatch.ShouldNotBeNull();
        KnowledgeRechunking.Plan(KnowledgeDocumentStatus.Ready, null, [.. units, units[0] with { Ordinal = 1 }], stored, processed).Mismatch.ShouldNotBeNull();
        KnowledgeRechunking.Plan(KnowledgeDocumentStatus.PartiallyReadable, "第 2 頁找不到可讀文字", units, stored, processed).Mismatch.ShouldNotBeNull();
        var mismatch = KnowledgeRechunking.Plan(KnowledgeDocumentStatus.PartiallyReadable, "x", units, stored, processed);
        (mismatch.Units.Count, mismatch.ChunksToEmbed).ShouldBe((0, 0));
    }

    private static (ProcessedVersion Processed, List<StoredUnit> Units) Read(params ExtractedUnit[] units)
    {
        var processed = KnowledgeVersionProcessing.Process(new ExtractedDocument(units), ExtractionLimits.Default, ChunkingOptions.Default);
        return (processed, [.. processed.Units.Select(unit => new StoredUnit(unit.Ordinal, unit.Kind, unit.LocationLabel, unit.Text, unit.Readable))]);
    }

    private static StoredChunk Chunk(int unit, int ordinal, string label, string text, bool excluded = false) =>
        new(Guid.CreateVersion7(), unit, ordinal, label, text, excluded);
}
