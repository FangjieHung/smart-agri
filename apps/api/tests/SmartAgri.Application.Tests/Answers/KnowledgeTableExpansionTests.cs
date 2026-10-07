using Shouldly;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Application.Knowledge;

namespace SmartAgri.Application.Tests.Answers;

/// <summary>
/// <see cref="KnowledgeTableExpansion"/> (#324): a selected table row brings the other rows of
/// its table, in order, once per table, within the character budget nearest the selected row;
/// every other passage is sent as before, and the passage stays the selected row's for citing.
/// </summary>
public sealed class KnowledgeTableExpansionTests
{
    private static readonly Guid KnowledgeBase = Guid.CreateVersion7();
    private static readonly Guid Document = Guid.CreateVersion7();
    private static readonly Guid Version = Guid.CreateVersion7();

    private const string Name = "項目：店名\n內容：安心商行青禾門市";
    private const string Address = "項目：地址\n內容：示範縣青禾鄉安和路 18 號";
    private const string Phone = "項目：電話\n內容：(03) 012-3456";
    private const string Hours = "項目：營業時間\n內容：09:00–18:00";
    private const string Closed = "項目：公休日\n內容：每週三";

    private readonly InMemoryKnowledgeTableRows _store = new();

    [Fact]
    public void A_selected_row_brings_the_other_rows_of_its_table_in_their_original_order()
    {
        var rows = BasicInformation();
        var closed = Passage(rows[4], 0.457);

        var (passages, expansion) = Expand([closed]);

        var sent = passages.ShouldHaveSingleItem();
        sent.Text.ShouldBe(string.Join(KnowledgeTableExpansion.RowSeparator, Name, Address, Phone, Hours, Closed));
        (sent.ChunkId, sent.DocumentId, sent.VersionId, sent.LocationLabel, sent.Score, sent.Table)
            .ShouldBe((closed.ChunkId, closed.DocumentId, closed.VersionId, closed.LocationLabel, closed.Score, closed.Table), "cited as the row retrieval found");
        expansion.ShouldBe(new GroundedTableExpansion(1, 4, Length(Name, Address, Phone, Hours), false));
    }

    [Fact]
    public void Excluded_rows_are_not_sent()
    {
        var rows = BasicInformation();
        _store.Rows[1] = (rows[1], true);

        var (passages, expansion) = Expand([Passage(rows[4], 0.457)]);

        passages.ShouldHaveSingleItem().Text.ShouldBe(string.Join(KnowledgeTableExpansion.RowSeparator, Name, Phone, Hours, Closed));
        expansion.AddedRows.ShouldBe(3);
    }

    [Fact]
    public void Two_selected_rows_of_one_table_are_one_passage_at_the_first_ones_place()
    {
        var rows = BasicInformation();
        var returns = Other("退換貨辦法.md", "收到商品後七天內可申請退貨。", 0.5);
        var closed = Passage(rows[4], 0.457);
        var hours = Passage(rows[3], 0.42);

        var (passages, expansion) = Expand([returns, closed, hours]);

        passages.Count.ShouldBe(2, "the table is expanded once");
        passages[0].ShouldBeSameAs(returns);
        passages[1].ChunkId.ShouldBe(closed.ChunkId);
        passages[1].Text.ShouldBe(string.Join(KnowledgeTableExpansion.RowSeparator, Name, Address, Phone, Hours, Closed), "every row once");
        expansion.ShouldBe(new GroundedTableExpansion(1, 3, Length(Name, Address, Phone), false));
    }

    [Fact]
    public void Rows_of_another_table_unit_or_version_are_not_mixed_in()
    {
        var rows = BasicInformation();
        _store.AddTable(Version, unitOrdinal: 1, tableIndex: 1, firstOrdinal: 9, "品項：糙米飯糰\n價格：45 元");
        _store.AddTable(Version, unitOrdinal: 2, tableIndex: 0, firstOrdinal: 0, "外送範圍：門市周邊 3 公里內");
        _store.AddTable(Guid.CreateVersion7(), unitOrdinal: 1, tableIndex: 0, firstOrdinal: 0, "項目：舊電話\n內容：(03) 000-0000");

        var (passages, _) = Expand([Passage(rows[2], 0.5)]);

        passages.ShouldHaveSingleItem().Text.ShouldBe(string.Join(KnowledgeTableExpansion.RowSeparator, Name, Address, Phone, Hours, Closed));
    }

    [Fact]
    public void Over_the_budget_the_rows_nearest_the_selected_one_are_kept_and_the_expansion_is_marked_truncated()
    {
        var rows = BasicInformation();

        // The closed day is selected; room for two of the four other rows: the nearest, hours
        // (one row before) and phone (two before). The address does not fit and ends the table.
        var budget = Length(Hours, Phone);
        var (passages, expansion) = KnowledgeTableExpansion.Expand([Passage(rows[4], 0.457)], [.. _store.Rows.Select(row => row.Row)], budget);

        passages.ShouldHaveSingleItem().Text.ShouldBe(string.Join(KnowledgeTableExpansion.RowSeparator, Phone, Hours, Closed));
        expansion.ShouldBe(new GroundedTableExpansion(1, 2, budget, true));
    }

    [Fact]
    public void On_a_tie_the_earlier_row_is_kept_first_and_the_budget_is_shared_by_every_table_in_passage_order()
    {
        var rows = BasicInformation();
        var meals = _store.AddTable(Version, unitOrdinal: 3, tableIndex: 0, firstOrdinal: 0, "品項：當日蔬菜湯\n價格：60 元", "品項：糙米飯糰\n價格：45 元");

        // The phone is selected: address (before) and hours (after) are both one row away.
        var budget = Length(Address);
        var (passages, expansion) = KnowledgeTableExpansion.Expand(
            [Passage(rows[2], 0.5), Passage(meals[0], 0.45)], [.. _store.Rows.Select(row => row.Row)], budget);

        passages[0].Text.ShouldBe(string.Join(KnowledgeTableExpansion.RowSeparator, Address, Phone));
        passages[1].Text.ShouldBe(meals[0].Text, "nothing left for the second table");
        expansion.ShouldBe(new GroundedTableExpansion(1, 1, budget, true));
    }

    [Fact]
    public void Passages_that_are_not_table_rows_are_sent_as_they_are_and_need_no_read()
    {
        var text = Other("退換貨辦法.md", "收到商品後七天內可申請退貨。", 0.6);
        var page = Other("退換貨辦法.pdf", "退款於五個工作天內退回。", 0.5);

        KnowledgeTableExpansion.TablesOf([text, page]).ShouldBeEmpty();
        var (passages, expansion) = Expand([text, page]);

        passages.ShouldBe([text, page]);
        expansion.ShouldBe(GroundedTableExpansion.None);
    }

    [Fact]
    public void A_table_with_no_other_row_leaves_its_row_as_it_is()
    {
        var only = _store.AddTable(Version, unitOrdinal: 4, tableIndex: 0, firstOrdinal: 1, "項目：停車\n內容：對面公有停車場");
        var passage = Passage(only[0], 0.6);

        var (passages, expansion) = Expand([passage]);

        passages.ShouldHaveSingleItem().ShouldBeSameAs(passage);
        expansion.ShouldBe(GroundedTableExpansion.None);
    }

    [Fact]
    public void Tables_of_lists_each_table_once_in_passage_order()
    {
        var rows = BasicInformation();
        var meals = _store.AddTable(Version, unitOrdinal: 3, tableIndex: 0, firstOrdinal: 0, "品項：當日蔬菜湯");

        KnowledgeTableExpansion.TablesOf([Passage(meals[0], 0.6), Other("a.md", "文字", 0.5), Passage(rows[0], 0.4), Passage(rows[1], 0.3)])
            .ShouldBe([meals[0].Table, rows[0].Table]);
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>門市資訊's 基本資訊 table: unit 1 (after the intro text's chunk 0), table 0,
    /// ordinals 1–5.</summary>
    private List<KnowledgeTableRow> BasicInformation() =>
        _store.AddTable(Version, unitOrdinal: 1, tableIndex: 0, firstOrdinal: 1, Name, Address, Phone, Hours, Closed);

    private (IReadOnlyList<RetrievedKnowledgePassage> Passages, GroundedTableExpansion Expansion) Expand(IReadOnlyList<RetrievedKnowledgePassage> passages) =>
        KnowledgeTableExpansion.Expand(passages, [.. _store.Rows.Where(row => !row.Excluded).Select(row => row.Row)], GroundedAnswerPrompt.TableRowsMaxCharacters);

    private static RetrievedKnowledgePassage Passage(KnowledgeTableRow row, double score) => new(
        row.ChunkId, KnowledgeBase, Document, "門市資訊.md", row.Table.VersionId, 1, KnowledgeVersionState.Effective,
        "青禾門市 AI 客服參考資料 › 基本資訊", row.Text, score, DateTimeOffset.UnixEpoch,
        new KnowledgeTablePosition(row.Table.UnitOrdinal, row.Table.TableIndex, row.Ordinal));

    private static RetrievedKnowledgePassage Other(string documentName, string text, double score) => new(
        Guid.CreateVersion7(), KnowledgeBase, Guid.CreateVersion7(), documentName, Guid.CreateVersion7(), 1, KnowledgeVersionState.Effective,
        "全文", text, score, DateTimeOffset.UnixEpoch);

    private static int Length(params string[] texts) => texts.Sum(text => text.EnumerateRunes().Count());
}
