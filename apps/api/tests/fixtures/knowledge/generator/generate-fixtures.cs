#:package PdfPig
#:package DocumentFormat.OpenXml
#:property RestorePackagesWithLockFile=false

// Generates the knowledge extraction fixtures in apps/api/tests/fixtures/knowledge/ (M2 Slice 6,
// ticket #40). See ../README.md for the whole procedure, including the font subset and the
// encrypted PDF (encrypt-pdf.py). Package versions come from apps/api/Directory.Packages.props.
//
//   dotnet run generate-fixtures.cs -- chars                 # every character the PDFs draw
//   dotnet run generate-fixtures.cs -- build <font.ttf> <dir> # write the fixtures into <dir>
//
// The tests never run this: they read the committed files.

using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using W = DocumentFormat.OpenXml.Wordprocessing;

switch (args)
{
    case ["chars"]:
        var characters = Fixtures.PdfPages.SelectMany(page => page).SelectMany(line => line.Text)
            .Append(' ')
            .Distinct()
            .Order()
            .ToArray();
        Console.Out.Write(new string(characters));
        return 0;

    case ["build", var fontPath, var outputDirectory]:
        var font = File.ReadAllBytes(fontPath);
        Directory.CreateDirectory(outputDirectory);
        Write(outputDirectory, "return-policy.pdf", Fixtures.ReturnPolicyPdf(font));
        Write(outputDirectory, "delivery-guide-with-scanned-page.pdf", Fixtures.DeliveryGuidePdf(font));
        Write(outputDirectory, "product-guide.docx", Fixtures.ProductGuideDocx());
        Write(outputDirectory, "delivery-and-prices.xlsx", Fixtures.DeliveryAndPricesXlsx());
        Write(outputDirectory, "faq.md", Fixtures.FaqMarkdownWithBom());
        Write(outputDirectory, "holiday-notice-big5.txt", Fixtures.HolidayNoticeBig5());
        return 0;

    default:
        Console.Error.WriteLine("usage: dotnet run generate-fixtures.cs -- chars | build <font.ttf> <output-directory>");
        return 2;
}

static void Write(string directory, string name, byte[] content)
{
    File.WriteAllBytes(Path.Combine(directory, name), content);
    Console.WriteLine($"{name}: {content.Length} bytes");
}

internal sealed record Line(string Text, double Size = 12);

internal static class Fixtures
{
    // --- PDF text: the integration tests compare page 2 of return-policy.pdf with
    // ReturnPolicyPage2 line for line (joined with "\n"). ------------------------------------

    public static readonly Line[] ReturnPolicyPage1 =
    [
        new("安心商行 退換貨政策", 18),
        new("第 2 版，2026 年 9 月 1 日起適用"),
        new("本政策說明退貨、換貨與退款的條件及流程。"),
    ];

    public static readonly Line[] ReturnPolicyPage2 =
    [
        new("一、退貨條件"),
        new("收到商品後七天內可申請退貨，商品須保持完整包裝與附件。"),
        new("生鮮蔬果因品質容易變化，恕不接受退貨。"),
        new("到貨時如有損壞，請於到貨當日拍照並聯繫客服，我們將補寄或退款。"),
    ];

    public static readonly Line[] ReturnPolicyPage3 =
    [
        new("二、退款方式"),
        new("退款將於收到退回商品後五個工作天內，退回原付款方式。"),
        new("客服專線：02-2345-6789（週一至週五 9:00–18:00）"),
    ];

    public static readonly Line[] DeliveryGuidePage1 =
    [
        new("安心商行 配送說明", 18),
        new("訂單於下午三點前完成付款，當天出貨。"),
        new("本島地區約一至兩天送達。"),
    ];

    public static readonly Line[] DeliveryGuidePage3 =
    [
        new("離島地區"),
        new("金門、馬祖與澎湖的訂單另收運費 150 元，約需五天送達。"),
    ];

    public static IEnumerable<Line[]> PdfPages =>
        [ReturnPolicyPage1, ReturnPolicyPage2, ReturnPolicyPage3, DeliveryGuidePage1, DeliveryGuidePage3];

    /// <summary>Three A4 pages, each line a separate text object 24 pt apart.</summary>
    public static byte[] ReturnPolicyPdf(byte[] font)
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = "安心商行 退換貨政策";
        builder.DocumentInformation.Producer = "SmartAgri fixture generator";
        var added = builder.AddTrueTypeFont(font);
        foreach (var page in new[] { ReturnPolicyPage1, ReturnPolicyPage2, ReturnPolicyPage3 })
        {
            AddLines(builder.AddPage(595, 842), page, added);
        }

        return builder.Build();
    }

    /// <summary>Page 1 and 3 have text; page 2 is only a full-page image, like a scanned page.</summary>
    public static byte[] DeliveryGuidePdf(byte[] font)
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = "安心商行 配送說明";
        builder.DocumentInformation.Producer = "SmartAgri fixture generator";
        var added = builder.AddTrueTypeFont(font);
        AddLines(builder.AddPage(595, 842), DeliveryGuidePage1, added);
        builder.AddPage(595, 842).AddPng(ScannedPagePng(), new PdfRectangle(0, 0, 595, 842));
        AddLines(builder.AddPage(595, 842), DeliveryGuidePage3, added);
        return builder.Build();
    }

    private static void AddLines(PdfPageBuilder page, Line[] lines, PdfDocumentBuilder.AddedFont font)
    {
        var y = 760.0;
        foreach (var line in lines)
        {
            page.AddText(line.Text, line.Size, new PdfPoint(60, y), font);
            y -= 12 + (line.Size * 1.5);
        }
    }

    /// <summary>A grey page with dark bars where lines of text would be: pixels only, as an
    /// 8-bit greyscale PNG (PdfPig's own PNG builder is internal).</summary>
    private static byte[] ScannedPagePng()
    {
        const int width = 298;
        const int height = 421;
        var scanlines = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            scanlines[y * (width + 1)] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var inLine = x is >= 30 and < 268 && y is >= 40 and < 380 && (y - 40) % 24 < 9;
                scanlines[(y * (width + 1)) + 1 + x] = inLine ? (byte)60 : (byte)(236 + (((x * 7) + (y * 13)) % 9));
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // greyscale
        WriteChunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(scanlines);
            }

            WriteChunk(png, "IDAT", compressed.ToArray());
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typeAndData);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typeAndData));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    // --- DOCX: headings by every route the extractor supports --------------------------------

    /// <summary>
    /// Heading 1 is the zh-TW Word convention (style id "1", built-in name "heading 1");
    /// Heading 2 the English one (id "Heading2", name "heading 2"); the level-3 heading uses a
    /// custom style (「小節標題」) that only has <c>w:outlineLvl</c>; 「3 聯絡我們」 is a Normal
    /// paragraph with a direct <c>w:outlineLvl</c>. The Title paragraph is not a heading.
    /// </summary>
    public static byte[] ProductGuideDocx()
    {
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(
                ParagraphStyle("Normal", "Normal", basedOn: null, outlineLevel: null, isDefault: true),
                ParagraphStyle("Title", "Title", "Normal", outlineLevel: null),
                ParagraphStyle("1", "heading 1", "Normal", outlineLevel: null),
                ParagraphStyle("Heading2", "heading 2", "Normal", outlineLevel: 1),
                ParagraphStyle("a5", "小節標題", "Normal", outlineLevel: 2));

            main.Document = new W.Document(new W.Body(
                Paragraph("安心商行 商品指南", "Title"),
                Paragraph("本指南適用於線上商店的所有商品。"),
                Paragraph("1 商品介紹", "1"),
                Paragraph("安心商行販售產地直送的有機蔬果與米糧。"),
                Paragraph("1.1 有機蔬菜箱", "Heading2"),
                Paragraph("每箱約 3 公斤，內含當季蔬菜 6 至 8 種。"),
                Paragraph("每週二、五出貨。"),
                Paragraph("2 退換貨", "1"),
                Paragraph("2.1 退貨條件", "Heading2"),
                Paragraph("收到商品後七天內可申請退貨；生鮮蔬果恕不退貨。"),
                Paragraph("2.1.1 退貨流程", "a5"),
                Paragraph("請先聯繫客服取得退貨編號，再將商品寄回。"),
                Paragraph("2.2 運費", "Heading2"),
                Table(
                    ["地區", "運費", "免運門檻"],
                    ["本島", "100 元", "1,500 元"],
                    ["離島", "150 元", "3,000 元"]),
                OutlineParagraph("3 聯絡我們", outlineLevel: 0),
                Paragraph("客服信箱：service@anxin.example")));
        }

        return buffer.ToArray();
    }

    private static W.Style ParagraphStyle(string id, string name, string? basedOn, int? outlineLevel, bool isDefault = false)
    {
        var style = new W.Style { Type = W.StyleValues.Paragraph, StyleId = id, Default = isDefault ? true : null };
        style.Append(new W.StyleName { Val = name });
        if (basedOn is not null)
        {
            style.Append(new W.BasedOn { Val = basedOn });
        }

        if (outlineLevel is { } level)
        {
            style.Append(new W.StyleParagraphProperties(new W.OutlineLevel { Val = level }));
        }

        return style;
    }

    private static W.Paragraph Paragraph(string text, string? styleId = null)
    {
        var paragraph = new W.Paragraph();
        if (styleId is not null)
        {
            paragraph.Append(new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }));
        }

        paragraph.Append(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    private static W.Paragraph OutlineParagraph(string text, int outlineLevel) =>
        new(
            new W.ParagraphProperties(new W.OutlineLevel { Val = outlineLevel }),
            new W.Run(new W.Text(text)));

    private static W.Table Table(params string[][] rows)
    {
        var table = new W.Table(
            new W.TableProperties(new W.TableWidth { Width = "0", Type = W.TableWidthUnitValues.Auto }),
            new W.TableGrid(rows[0].Select(_ => new W.GridColumn { Width = "2400" })));
        foreach (var row in rows)
        {
            table.Append(new W.TableRow(row.Select(cell => new W.TableCell(new W.Paragraph(new W.Run(new W.Text(cell)))))));
        }

        return table;
    }

    // --- XLSX: two sheets with a units column, shared strings and number formats -------------

    private const uint General = 0;
    private const uint Thousands = 1; // #,##0
    private const uint OneDecimal = 2; // custom 0.0
    private const uint Percent = 3; // 0%
    private const uint Date = 4; // custom yyyy/m/d
    private const uint Time = 5; // h:mm

    private static readonly (string Region, int Days, string Cutoff)[] DeliveryRegions =
    [
        ("台北市", 1, "15:00"), ("新北市", 1, "15:00"), ("基隆市", 1, "14:00"), ("桃園市", 1, "15:00"),
        ("新竹市", 1, "14:00"), ("新竹縣", 2, "14:00"), ("苗栗縣", 2, "14:00"), ("台中市", 1, "15:00"),
        ("彰化縣", 2, "14:00"), ("南投縣", 2, "13:00"), ("雲林縣", 2, "13:00"), ("嘉義市", 2, "14:00"),
        ("嘉義縣", 2, "13:00"), ("台南市", 1, "15:00"), ("高雄市", 1, "15:00"), ("屏東縣", 2, "13:00"),
        ("宜蘭縣", 2, "13:00"), ("花蓮縣", 3, "12:00"), ("台東縣", 3, "12:00"), ("澎湖縣", 5, "11:00"),
        ("金門縣", 5, "11:00"), ("連江縣", 5, "10:00"),
    ];

    private static readonly string[] Produce =
    [
        "有機高麗菜", "有機青江菜", "有機地瓜葉", "有機空心菜", "有機菠菜", "有機小白菜", "有機大白菜", "有機花椰菜",
        "有機胡蘿蔔", "有機白蘿蔔", "有機馬鈴薯", "有機洋蔥", "有機南瓜", "有機絲瓜", "有機苦瓜", "有機小黃瓜",
        "有機番茄", "有機玉米", "有機茄子", "有機青椒", "有機甜椒", "有機地瓜", "有機芋頭", "有機山藥",
        "有機牛蒡", "有機蓮藕", "有機香菇", "有機杏鮑菇", "有機金針菇", "有機木耳", "有機蘆筍", "有機四季豆",
        "有機毛豆", "有機豌豆", "有機芹菜", "有機韭菜", "有機青蔥", "有機大蒜", "有機老薑", "有機辣椒",
        "有機香蕉", "有機芭樂", "有機木瓜", "有機鳳梨", "有機柳丁", "有機檸檬", "有機蘋果", "有機奇異果",
        "有機糙米", "有機白米",
    ];

    /// <summary>
    /// 「配送時間」: region, fastest delivery and its unit (天), cut-off time (h:mm) and
    /// effective date (yyyy/m/d). 「商品價格」: item, weight (0.0) in 公斤, price (#,##0)
    /// in 元, discount (0%) — 50 rows, so its chunks split and each repeats the header.
    /// Text cells use the shared string table.
    /// </summary>
    public static byte[] DeliveryAndPricesXlsx()
    {
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var sharedStrings = new List<string>();

            var stylesPart = workbook.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new Stylesheet(
                new NumberingFormats(
                    new NumberingFormat { NumberFormatId = 164, FormatCode = "0.0" },
                    new NumberingFormat { NumberFormatId = 165, FormatCode = "yyyy/m/d" })
                { Count = 2 },
                new Fonts(new Font()) { Count = 1 },
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
                new Borders(new Border()) { Count = 1 },
                new CellStyleFormats(new CellFormat()) { Count = 1 },
                new CellFormats(
                    new CellFormat { NumberFormatId = 0 },
                    new CellFormat { NumberFormatId = 3, ApplyNumberFormat = true },
                    new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true },
                    new CellFormat { NumberFormatId = 9, ApplyNumberFormat = true },
                    new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true },
                    new CellFormat { NumberFormatId = 20, ApplyNumberFormat = true })
                { Count = 6 });

            var effective = new DateTime(2026, 9, 1).ToOADate();
            var delivery = new List<Row> { TextRow(1, sharedStrings, "地區", "最快到貨", "單位", "截單時間", "生效日期") };
            foreach (var (region, days, cutoff) in DeliveryRegions)
            {
                var number = (uint)delivery.Count + 1;
                var time = TimeSpan.Parse(cutoff, System.Globalization.CultureInfo.InvariantCulture).TotalDays;
                delivery.Add(new Row(
                    StringCell("A", number, region, sharedStrings),
                    NumberCell("B", number, days, General),
                    StringCell("C", number, "天", sharedStrings),
                    NumberCell("D", number, time, Time),
                    NumberCell("E", number, effective, Date))
                { RowIndex = number });
            }

            var prices = new List<Row> { TextRow(1, sharedStrings, "品項", "重量", "單位", "售價", "單位", "折扣") };
            for (var i = 0; i < Produce.Length; i++)
            {
                var number = (uint)prices.Count + 1;
                var weight = 0.5 + (i % 6 * 0.5);
                var price = i == Produce.Length - 1 ? 1280 : 60 + (i * 15);
                var discount = i % 5 == 0 ? 0.1 : 0;
                prices.Add(new Row(
                    StringCell("A", number, Produce[i], sharedStrings),
                    NumberCell("B", number, weight, OneDecimal),
                    StringCell("C", number, "公斤", sharedStrings),
                    NumberCell("D", number, price, Thousands),
                    StringCell("E", number, "元", sharedStrings),
                    NumberCell("F", number, discount, Percent))
                { RowIndex = number });
            }

            var sheets = workbook.Workbook.AppendChild(new Sheets());
            AddSheet(workbook, sheets, 1, "配送時間", delivery);
            AddSheet(workbook, sheets, 2, "商品價格", prices);

            var sharedStringPart = workbook.AddNewPart<SharedStringTablePart>();
            sharedStringPart.SharedStringTable = new SharedStringTable(
                sharedStrings.Select(text => new SharedStringItem(new Text(text))))
            {
                Count = (uint)sharedStrings.Count,
                UniqueCount = (uint)sharedStrings.Count,
            };
        }

        return buffer.ToArray();
    }

    private static void AddSheet(WorkbookPart workbook, Sheets sheets, uint id, string name, IEnumerable<Row> rows)
    {
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        worksheet.Worksheet = new Worksheet(new SheetData(rows));
        sheets.Append(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = id, Name = name });
    }

    private static Row TextRow(uint number, List<string> sharedStrings, params string[] texts) =>
        new(texts.Select((text, index) => StringCell(((char)('A' + index)).ToString(), number, text, sharedStrings))) { RowIndex = number };

    private static Cell StringCell(string column, uint row, string text, List<string> sharedStrings)
    {
        var index = sharedStrings.IndexOf(text);
        if (index < 0)
        {
            index = sharedStrings.Count;
            sharedStrings.Add(text);
        }

        return new Cell
        {
            CellReference = $"{column}{row}",
            DataType = CellValues.SharedString,
            CellValue = new CellValue(index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    private static Cell NumberCell(string column, uint row, double value, uint style) =>
        new()
        {
            CellReference = $"{column}{row}",
            StyleIndex = style,
            CellValue = new CellValue(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        };

    // --- Plain text ------------------------------------------------------------------------

    public const string FaqMarkdown =
        """
        # 常見問題

        以下整理顧客最常詢問的問題。

        ## 訂購與付款

        ### 可以使用哪些付款方式？

        信用卡、ATM 轉帳與超商代碼繳費。

        ### 可以開立統一編號嗎？

        可以，結帳時填寫統一編號即可。

        ## 配送

        ### 多久會收到商品？

        本島約 1 至 2 天，離島約 5 天。

        ```bash
        # 這一行在程式碼區塊內，不是標題
        echo "tracking"
        ```

        """;

    /// <summary>UTF-8 with a byte order mark, as older Windows Notepad saved it.</summary>
    public static byte[] FaqMarkdownWithBom() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(FaqMarkdown)];

    public const string HolidayNotice = "安心商行公告：春節期間暫停出貨，二月五日起恢復出貨。\r\n";

    /// <summary>Big5 (code page 950), which the extractor must refuse.</summary>
    public static byte[] HolidayNoticeBig5()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(950).GetBytes(HolidayNotice);
    }
}
