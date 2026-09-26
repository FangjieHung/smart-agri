#:package PdfPig
#:package DocumentFormat.OpenXml
#:property RestorePackagesWithLockFile=false

// Generates the retrieval evaluation's demo documents in apps/api/eval/retrieval/files/ (M2 Slice
// 16, ticket #50). See ../README.md for the procedure, including the font subset. Modelled on the
// extraction fixtures' generator (apps/api/tests/fixtures/knowledge/generator/generate-fixtures.cs);
// package versions come from apps/api/Directory.Packages.props.
//
//   dotnet run generate-documents.cs -- chars                 # every character the PDFs draw
//   dotnet run generate-documents.cs -- build <font.ttf> <dir> # write the documents into <dir>
//
// Every word of these documents is invented for the Demo organization 安心商行: no real
// customer, supplier, person, address or telephone number. Neither the evaluation nor the tests
// run this: they read the committed files.

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
        var characters = Documents.PdfPages.SelectMany(page => page).SelectMany(line => line.Text)
            .Append(' ')
            .Distinct()
            .Order()
            .ToArray();
        Console.Out.Write(new string(characters));
        return 0;

    case ["build", var fontPath, var outputDirectory]:
        var font = File.ReadAllBytes(fontPath);
        Directory.CreateDirectory(outputDirectory);
        Write(outputDirectory, "product-guide.docx", Documents.ProductGuideDocx());
        Write(outputDirectory, "return-policy-v1.pdf", Documents.ReturnPolicyPdf(font, Documents.ReturnPolicyV1, "第 1 版"));
        Write(outputDirectory, "return-policy-v2.pdf", Documents.ReturnPolicyPdf(font, Documents.ReturnPolicyV2, "第 2 版"));
        Write(outputDirectory, "delivery-timetable.xlsx", Documents.DeliveryTimetableXlsx());
        Write(outputDirectory, "faq.md", Encoding.UTF8.GetBytes(Documents.FaqMarkdown));
        return 0;

    default:
        Console.Error.WriteLine("usage: dotnet run generate-documents.cs -- chars | build <font.ttf> <output-directory>");
        return 2;
}

static void Write(string directory, string name, byte[] content)
{
    File.WriteAllBytes(Path.Combine(directory, name), content);
    Console.WriteLine($"{name}: {content.Length} bytes");
}

internal sealed record Line(string Text, double Size = 12);

internal static class Documents
{
    // --- 退換貨辦法: version 1 (2025) and version 2 (2026), three A4 pages each ------------------
    // Version 2 shortens the return window from ten days to seven, stops taking back fresh produce
    // and refunds in five working days instead of ten, so the evaluation can tell which version a
    // passage came from. Lines stay under 40 characters: PdfPig does not wrap text.

    public static readonly Line[][] ReturnPolicyV1 =
    [
        [
            new("安心商行 退換貨辦法", 18),
            new("第 1 版，2025 年 3 月 1 日起適用"),
            new("本辦法說明退貨、換貨與退款的條件及流程。"),
            new("對商品有任何疑問，請透過會員中心的線上客服與我們聯繫。"),
        ],
        [
            new("一、退貨條件"),
            new("收到商品後十天內可申請退貨，商品須保持完整包裝與附件。"),
            new("生鮮蔬果如有品質問題，請於到貨後三天內拍照並聯繫客服，"),
            new("我們將補寄或退款。"),
            new("退貨運費由安心商行負擔。"),
        ],
        [
            new("二、換貨與退款"),
            new("換貨次數不限，換貨運費由安心商行負擔。"),
            new("退款將於收到退回商品後十個工作天內，退回原付款方式。"),
        ],
    ];

    public static readonly Line[][] ReturnPolicyV2 =
    [
        [
            new("安心商行 退換貨辦法", 18),
            new("第 2 版，2026 年 9 月 1 日起適用"),
            new("本辦法說明退貨、換貨與退款的條件及流程。"),
            new("本版的主要變更：退貨期限由十天改為七天；"),
            new("生鮮蔬果不再接受退貨；退款天數縮短為五個工作天。"),
            new("對商品有任何疑問，請透過會員中心的線上客服與我們聯繫。"),
        ],
        [
            new("一、退貨條件"),
            new("收到商品後七天內可申請退貨，商品須保持完整包裝與附件。"),
            new("生鮮蔬果因品質容易變化，恕不接受退貨。"),
            new("到貨時如有損壞，請於到貨當日拍照並聯繫客服，"),
            new("我們將補寄或退款。"),
            new("退貨運費由安心商行負擔。"),
        ],
        [
            new("二、換貨與退款"),
            new("同一筆訂單的換貨以一次為限，換貨運費由安心商行負擔。"),
            new("退款將於收到退回商品後五個工作天內，退回原付款方式。"),
        ],
    ];

    public static IEnumerable<Line[]> PdfPages => [.. ReturnPolicyV1, .. ReturnPolicyV2];

    /// <summary>One A4 page per entry of <paramref name="pages"/>, each line a separate text
    /// object 24 pt or more apart.</summary>
    public static byte[] ReturnPolicyPdf(byte[] font, Line[][] pages, string edition)
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = $"安心商行 退換貨辦法（{edition}）";
        builder.DocumentInformation.Producer = "SmartAgri retrieval evaluation generator";
        var added = builder.AddTrueTypeFont(font);
        foreach (var page in pages)
        {
            var y = 760.0;
            var pageBuilder = builder.AddPage(595, 842);
            foreach (var line in page)
            {
                pageBuilder.AddText(line.Text, line.Size, new PdfPoint(60, y), added);
                y -= 12 + (line.Size * 1.5);
            }
        }

        return builder.Build();
    }

    // --- 商品使用指南: Heading 1 and Heading 2 by their built-in names -----------------------------

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
                ParagraphStyle("Heading1", "heading 1", "Normal", outlineLevel: 0),
                ParagraphStyle("Heading2", "heading 2", "Normal", outlineLevel: 1));

            main.Document = new W.Document(new W.Body(
                Paragraph("安心商行 商品使用指南", "Title"),
                Paragraph("本指南說明安心商行各類商品的規格、訂購方式與保存方法。"),
                Paragraph("1 蔬菜箱", "Heading1"),
                Paragraph("蔬菜箱由產地直送，內容依當季收成調整。"),
                Paragraph("1.1 規格", "Heading2"),
                Paragraph("小箱約 3 公斤，內含當季蔬菜 6 至 8 種，適合 2 至 3 人的家庭一週食用。"),
                Paragraph("大箱約 5 公斤，內含當季蔬菜 10 至 12 種，適合 4 至 5 人的家庭一週食用。"),
                Paragraph("1.2 訂閱與暫停", "Heading2"),
                Paragraph("蔬菜箱每週二、五出貨，可以選擇每週或隔週配送。"),
                Paragraph("要暫停或跳過一期，請在出貨前兩天的晚上 12 點前，到會員中心的「我的訂閱」設定。"),
                Paragraph("1.3 保存方式", "Heading2"),
                Paragraph("葉菜類請用沾濕的廚房紙巾包好，放入保鮮袋冷藏，建議 3 至 5 天內食用完畢。"),
                Paragraph("根莖類（地瓜、馬鈴薯、洋蔥）請放在陰涼通風處，不需冷藏，可存放約兩週。"),
                Paragraph("2 米糧", "Heading1"),
                Paragraph("2.1 有機白米與糙米", "Heading2"),
                Paragraph("每包 2 公斤，真空包裝，未開封可在常溫下保存六個月。"),
                Paragraph("開封後請將米倒入密封罐並放入冰箱冷藏，一個月內食用完畢。"),
                Paragraph("2.2 產地與檢驗", "Heading2"),
                Paragraph("米糧來自花蓮的契作農友，每一批都通過 250 項農藥殘留檢驗。"),
                Paragraph("檢驗報告可以在商品頁面下載。"),
                Paragraph("3 禮盒", "Heading1"),
                Paragraph("3.1 季節水果禮盒", "Heading2"),
                Paragraph("中秋節與春節前一個月開放預購，可以指定到貨日期並附上手寫卡片。"),
                Paragraph("3.2 企業訂購", "Heading2"),
                Paragraph("企業一次訂購 20 盒以上享九折優惠，請來信 service@anxin.example 洽詢。")));
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

    // --- 運費與配送時間表: two worksheets with units columns ------------------------------------

    private const uint General = 0;
    private const uint Thousands = 1; // #,##0
    private const uint Time = 2; // h:mm

    private static readonly (string Region, int Days, string Cutoff, string Method)[] DeliveryRegions =
    [
        ("台北市", 1, "15:00", "本島宅配"), ("新北市", 1, "15:00", "本島宅配"), ("基隆市", 1, "14:00", "本島宅配"),
        ("桃園市", 1, "15:00", "本島宅配"), ("新竹市", 1, "14:00", "本島宅配"), ("新竹縣", 2, "14:00", "本島宅配"),
        ("苗栗縣", 2, "14:00", "本島宅配"), ("台中市", 1, "15:00", "本島宅配"), ("彰化縣", 2, "14:00", "本島宅配"),
        ("南投縣", 2, "13:00", "本島宅配"), ("雲林縣", 2, "13:00", "本島宅配"), ("嘉義市", 2, "14:00", "本島宅配"),
        ("嘉義縣", 2, "13:00", "本島宅配"), ("台南市", 1, "15:00", "本島宅配"), ("高雄市", 1, "15:00", "本島宅配"),
        ("屏東縣", 2, "13:00", "本島宅配"), ("宜蘭縣", 2, "13:00", "本島宅配"), ("花蓮縣", 3, "12:00", "本島宅配"),
        ("台東縣", 3, "12:00", "本島宅配"), ("澎湖縣", 4, "11:00", "離島宅配"), ("金門縣", 5, "11:00", "離島宅配"),
        ("連江縣", 5, "10:00", "離島宅配"),
    ];

    private static readonly (string Method, int Fee, int? FreeFrom)[] ShippingFees =
    [
        ("本島常溫", 100, 1500),
        ("本島冷藏", 160, 2000),
        ("離島常溫", 150, 3000),
        ("離島冷藏", 260, null),
    ];

    /// <summary>
    /// 「配送時間」: region, fastest delivery and its unit (天), order cut-off time (h:mm) and
    /// delivery method. 「運費」: method, fee (#,##0) in 元, free-shipping threshold in 元 (or
    /// 「不提供」). Text cells use the shared string table.
    /// </summary>
    public static byte[] DeliveryTimetableXlsx()
    {
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var sharedStrings = new List<string>();

            var stylesPart = workbook.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new Stylesheet(
                new Fonts(new Font()) { Count = 1 },
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
                new Borders(new Border()) { Count = 1 },
                new CellStyleFormats(new CellFormat()) { Count = 1 },
                new CellFormats(
                    new CellFormat { NumberFormatId = 0 },
                    new CellFormat { NumberFormatId = 3, ApplyNumberFormat = true },
                    new CellFormat { NumberFormatId = 20, ApplyNumberFormat = true })
                { Count = 3 });

            var delivery = new List<Row> { TextRow(1, sharedStrings, "地區", "最快到貨", "單位", "截單時間", "配送方式") };
            foreach (var (region, days, cutoff, method) in DeliveryRegions)
            {
                var number = (uint)delivery.Count + 1;
                var time = TimeSpan.Parse(cutoff, System.Globalization.CultureInfo.InvariantCulture).TotalDays;
                delivery.Add(new Row(
                    StringCell("A", number, region, sharedStrings),
                    NumberCell("B", number, days, General),
                    StringCell("C", number, "天", sharedStrings),
                    NumberCell("D", number, time, Time),
                    StringCell("E", number, method, sharedStrings))
                { RowIndex = number });
            }

            var fees = new List<Row> { TextRow(1, sharedStrings, "配送方式", "運費", "單位", "免運門檻", "單位") };
            foreach (var (method, fee, freeFrom) in ShippingFees)
            {
                var number = (uint)fees.Count + 1;
                fees.Add(new Row(
                    StringCell("A", number, method, sharedStrings),
                    NumberCell("B", number, fee, Thousands),
                    StringCell("C", number, "元", sharedStrings),
                    freeFrom is { } threshold ? NumberCell("D", number, threshold, Thousands) : StringCell("D", number, "不提供", sharedStrings),
                    StringCell("E", number, freeFrom is null ? "—" : "元", sharedStrings))
                { RowIndex = number });
            }

            var sheets = workbook.Workbook.AppendChild(new Sheets());
            AddSheet(workbook, sheets, 1, "配送時間", delivery);
            AddSheet(workbook, sheets, 2, "運費", fees);

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

    // --- 常見問題: Markdown, UTF-8 without a byte order mark -----------------------------------
    // Stands in for FAQ entries until #44 adds them as their own kind of knowledge item.

    public const string FaqMarkdown =
        """
        # 常見問題

        以下整理顧客最常詢問的問題。商品、退換貨與配送的詳細規定，請以商品使用指南、退換貨辦法與運費與配送時間表為準。

        ## 訂購與付款

        ### 可以使用哪些付款方式？

        信用卡、ATM 轉帳、超商代碼繳費；本島訂單另可選擇貨到付款。

        ### 可以開立統一編號嗎？

        可以，結帳時填寫統一編號與公司抬頭，電子發票會寄到訂購人的電子信箱。

        ### 訂單成立後可以修改嗎？

        出貨前可以在會員中心修改收件地址與到貨時段；訂單出貨後就無法修改。

        ## 配送

        ### 連假期間會出貨嗎？

        連續假期前一天下午三點以後成立的訂單，會在連假結束後的第一個工作天出貨；蔬菜箱會自動順延一期。

        ### 可以指定到貨時段嗎？

        宅配可以指定上午（9 點至 13 點）或下午（14 點至 18 點），無法指定確切的時間。

        ### 收件人不在家怎麼辦？

        物流會再配送一次；兩次都無法送達的冷藏商品會退回，恕不退款。

        ## 會員

        ### 如何取消蔬菜箱訂閱？

        在會員中心的「我的訂閱」按「取消訂閱」即可；已經付款的當期仍會照常出貨。

        ### 會員點數怎麼使用？

        每消費 100 元累積 1 點，每點可折抵 1 元，點數自取得日起一年內有效。

        """;
}
