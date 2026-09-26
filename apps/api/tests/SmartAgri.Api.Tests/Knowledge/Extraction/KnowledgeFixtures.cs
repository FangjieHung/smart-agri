namespace SmartAgri.Api.Tests.Knowledge.Extraction;

/// <summary>
/// The committed knowledge extraction fixtures (<c>apps/api/tests/fixtures/knowledge/</c>,
/// copied next to the test assembly) and what they are known to contain — see the README there
/// for how each was produced.
/// </summary>
internal static class KnowledgeFixtures
{
    /// <summary>3 A4 pages with a text layer in Noto Sans TC.</summary>
    public const string ReturnPolicyPdf = "return-policy.pdf";

    /// <summary>return-policy.pdf with a password to open it (AES-256).</summary>
    public const string EncryptedPdf = "encrypted-return-policy.pdf";

    /// <summary>3 pages; page 2 is only an image.</summary>
    public const string ScannedPagePdf = "delivery-guide-with-scanned-page.pdf";

    /// <summary>Headings of three levels by every supported route, and a table.</summary>
    public const string ProductGuideDocx = "product-guide.docx";

    /// <summary>「配送時間」 (22 rows) and 「商品價格」 (50 rows), both with a units column.</summary>
    public const string DeliveryAndPricesXlsx = "delivery-and-prices.xlsx";

    /// <summary>UTF-8 with a byte order mark; headings of three levels and a fenced code block.</summary>
    public const string FaqMarkdown = "faq.md";

    /// <summary>A notice in Big5.</summary>
    public const string Big5Text = "holiday-notice-big5.txt";

    /// <summary>Page 2 of return-policy.pdf, line by line, exactly as drawn.</summary>
    public const string ReturnPolicyPage2 =
        "一、退貨條件\n" +
        "收到商品後七天內可申請退貨，商品須保持完整包裝與附件。\n" +
        "生鮮蔬果因品質容易變化，恕不接受退貨。\n" +
        "到貨時如有損壞，請於到貨當日拍照並聯繫客服，我們將補寄或退款。";

    public const string ReturnPolicyPage1 =
        "安心商行 退換貨政策\n" +
        "第 2 版，2026 年 9 月 1 日起適用\n" +
        "本政策說明退貨、換貨與退款的條件及流程。";

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "knowledge", name));
}
