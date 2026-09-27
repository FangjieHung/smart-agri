using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Knowledge.Extraction;

namespace SmartAgri.Api.Tests.Knowledge.Extraction;

public class XlsxTextExtractorTests
{
    private static readonly XlsxTextExtractor Extractor = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Reads_xlsx_only()
    {
        Enum.GetValues<KnowledgeFileFormat>().Where(Extractor.CanExtract).ShouldBe([KnowledgeFileFormat.Xlsx]);
    }

    [Fact]
    public void Each_worksheet_is_a_unit_whose_rows_show_cells_as_excel_displays_them()
    {
        var document = Extractor.Extract(
            KnowledgeFileFormat.Xlsx, KnowledgeFixtures.Read(KnowledgeFixtures.DeliveryAndPricesXlsx), ExtractionLimits.Default, CancellationToken);

        document.Units.Select(unit => (unit.Kind, unit.LocationLabel)).ShouldBe(
        [
            (KnowledgeUnitLocationKind.Sheet, "工作表『配送時間』"),
            (KnowledgeUnitLocationKind.Sheet, "工作表『商品價格』"),
        ]);

        var delivery = document.Units[0].Sheet.ShouldNotBeNull();
        delivery.Header.ShouldBe(new SheetRow(1, "地區 | 最快到貨 | 單位 | 截單時間 | 生效日期"));
        delivery.DataRows.Count.ShouldBe(22);
        delivery.DataRows[0].ShouldBe(new SheetRow(2, "台北市 | 1 | 天 | 15:00 | 2026/9/1"));
        delivery.DataRows[^1].ShouldBe(new SheetRow(23, "連江縣 | 5 | 天 | 10:00 | 2026/9/1"));
        delivery.RowsTruncated.ShouldBeFalse();

        var prices = document.Units[1].Sheet.ShouldNotBeNull();
        prices.Header.Text.ShouldBe("品項 | 重量 | 單位 | 售價 | 單位 | 折扣");
        prices.DataRows.Count.ShouldBe(50);
        prices.DataRows[0].Text.ShouldBe("有機高麗菜 | 0.5 | 公斤 | 60 | 元 | 10%");
        prices.DataRows[1].Text.ShouldBe("有機青江菜 | 1.0 | 公斤 | 75 | 元 | 0%");
        prices.DataRows[^1].Text.ShouldBe("有機白米 | 1.0 | 公斤 | 1,280 | 元 | 0%");

        document.Units[0].Text.ShouldStartWith("地區 | 最快到貨 | 單位 | 截單時間 | 生效日期\n台北市 | 1 | 天 | 15:00 | 2026/9/1\n");
    }

    [Fact]
    public void Hidden_and_empty_sheets_are_skipped_inline_strings_booleans_and_errors_are_read_and_columns_start_at_the_first_used_one()
    {
        var content = Workbook(
            ("隱藏", SheetStateValues.Hidden, [Row(1, Inline("B", "不應出現"))]),
            ("空白", null, []),
            ("資料", null,
            [
                Row(2, Inline("B", "品項"), Inline("D", "有貨")),
                Row(3, Inline("B", "糙米"), new Cell { CellReference = "D3", DataType = CellValues.Boolean, CellValue = new CellValue("1") }),
                Row(4, Inline("B", "白米"), new Cell { CellReference = "C4", DataType = CellValues.Error, CellValue = new CellValue("#N/A") }),
            ]));

        var unit = Extractor.Extract(KnowledgeFileFormat.Xlsx, content, ExtractionLimits.Default, CancellationToken).Units.ShouldHaveSingleItem();

        unit.LocationLabel.ShouldBe("工作表『資料』");
        unit.Text.ShouldBe("品項 |  | 有貨\n糙米 |  | TRUE\n白米 | #N/A".Replace("  ", " ", StringComparison.Ordinal));
        unit.Sheet!.DataRows.Select(row => row.RowNumber).ShouldBe([3, 4]);
    }

    [Fact]
    public void Rows_past_the_limit_and_sheets_past_the_unit_limit_are_not_read()
    {
        var rows = Enumerable.Range(1, 10).Select(number => Row((uint)number, Inline("A", number.ToString(CultureInfo.InvariantCulture)))).ToArray();
        var content = Workbook(("一", null, rows), ("二", null, rows), ("三", null, rows));

        var document = Extractor.Extract(KnowledgeFileFormat.Xlsx, content, new ExtractionLimits(2, 3), CancellationToken);

        document.Units.Select(unit => unit.LocationLabel).ShouldBe(["工作表『一』", "工作表『二』"]);
        document.UnitsTruncated.ShouldBeTrue();
        var sheet = document.Units[0].Sheet!;
        sheet.Header.Text.ShouldBe("1");
        sheet.DataRows.Select(row => row.Text).ShouldBe(["2", "3", "4"]);
        sheet.RowsTruncated.ShouldBeTrue();
    }

    [Fact]
    public void A_zip_that_is_not_a_workbook_is_damaged()
    {
        Should.Throw<DocumentExtractionException>(() =>
                Extractor.Extract(KnowledgeFileFormat.Xlsx, KnowledgeFixtures.Read(KnowledgeFixtures.ProductGuideDocx), ExtractionLimits.Default, CancellationToken))
            .Failure.ShouldBe(DocumentExtractionFailure.Damaged);
    }

    private static Cell Inline(string column, string text) =>
        new() { CellReference = column + "0", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(text)) };

    private static Row Row(uint number, params Cell[] cells)
    {
        foreach (var cell in cells)
        {
            cell.CellReference = new string([.. cell.CellReference!.Value!.TakeWhile(char.IsAsciiLetter)]) + number.ToString(CultureInfo.InvariantCulture);
        }

        return new Row(cells) { RowIndex = number };
    }

    private static byte[] Workbook(params (string Name, SheetStateValues? State, Row[] Rows)[] sheets)
    {
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(buffer, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            uint id = 1;
            foreach (var (name, state, rows) in sheets)
            {
                var worksheet = workbook.AddNewPart<WorksheetPart>();
                worksheet.Worksheet = new Worksheet(new SheetData(rows.Select(row => (Row)row.CloneNode(true))));
                workbook.Workbook.Sheets!.Append(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = id++, Name = name, State = state });
            }
        }

        return buffer.ToArray();
    }
}
