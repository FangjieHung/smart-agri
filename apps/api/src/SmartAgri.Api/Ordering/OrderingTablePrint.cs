using System.Globalization;
using System.Net;
using System.Text;
using QRCoder;

namespace SmartAgri.Api.Ordering;

/// <summary>One table to print: its name and the URL its QR code opens.</summary>
public sealed record OrderingTableCard(string TableName, string Url);

/// <summary>
/// The printable table QR codes of <c>ordering-shop tables --html</c> (online-ordering design D11, #402): a self-contained
/// HTML page — no script, no external file — of A4 sheets with 2 × 3 cards, each with the shop's name, the table's name, the
/// QR code as inline SVG and 「掃描點餐，用餐後至櫃台結帳」.
/// </summary>
/// <remarks>The QR codes are drawn from QRCoder's module matrix as one SVG path each (error correction level M, the standard
/// four-module quiet zone), so they stay sharp at any print size.</remarks>
public static class OrderingTablePrint
{
    /// <summary>Cards on one A4 sheet.</summary>
    public const int CardsPerSheet = 6;

    public const string Instruction = "掃描點餐，用餐後至櫃台結帳";

    /// <summary>The page for <paramref name="tables"/> of the shop <paramref name="shopName"/>.</summary>
    public static string Html(string shopName, IReadOnlyList<OrderingTableCard> tables)
    {
        ArgumentNullException.ThrowIfNull(shopName);
        ArgumentNullException.ThrowIfNull(tables);
        var shop = WebUtility.HtmlEncode(shopName);
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"zh-Hant-TW\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append(CultureInfo.InvariantCulture, $"<title>{shop}・桌號 QR code</title>\n");
        html.Append("""
            <style>
            @page { size: A4; margin: 10mm; }
            * { box-sizing: border-box; }
            html, body { margin: 0; background: #fff; color: #1a1a1a; }
            body { font-family: "Noto Sans TC", "PingFang TC", "Microsoft JhengHei", sans-serif; }
            .sheet { width: 190mm; height: 277mm; display: grid; grid-template-columns: repeat(2, 1fr); grid-template-rows: repeat(3, 1fr);
              gap: 6mm; break-after: page; page-break-after: always; }
            .sheet:last-child { break-after: auto; page-break-after: auto; }
            .card { border: 0.4mm dashed #9a9a9a; border-radius: 3mm; padding: 5mm; display: flex; flex-direction: column; align-items: center;
              justify-content: center; gap: 2mm; text-align: center; break-inside: avoid; }
            .shop { font-size: 11pt; color: #555; }
            .table { font-size: 22pt; font-weight: 700; letter-spacing: 0.05em; }
            .qr { width: 52mm; height: 52mm; }
            .qr svg { width: 100%; height: 100%; display: block; }
            .hint { font-size: 11pt; font-weight: 700; }
            @media screen {
              body { background: #eee; padding: 16px; }
              .sheet { background: #fff; margin: 0 auto 16px; padding: 10mm; width: 210mm; height: 297mm; box-shadow: 0 1px 4px rgba(0,0,0,.2); }
            }
            @media screen and (max-width: 840px) {
              body { padding: 8px; }
              .sheet { width: auto; height: auto; padding: 16px; grid-template-columns: 1fr; grid-template-rows: none; }
            }
            </style>
            </head>
            <body>

            """);
        for (var start = 0; start < tables.Count; start += CardsPerSheet)
        {
            html.Append("<section class=\"sheet\">\n");
            foreach (var table in tables.Skip(start).Take(CardsPerSheet))
            {
                var name = WebUtility.HtmlEncode(table.TableName);
                html.Append("<article class=\"card\">\n");
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"shop\">{shop}</div>\n");
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"table\">桌號 {name}</div>\n");
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"qr\">{Svg(table.Url, $"桌號 {name} 的點餐 QR code")}</div>\n");
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"hint\">{Instruction}</div>\n");
                html.Append("</article>\n");
            }

            html.Append("</section>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>The QR code of <paramref name="text"/> as an SVG element (one path, quiet zone included) labelled
    /// <paramref name="label"/> (already HTML-encoded).</summary>
    public static string Svg(string text, string label)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            var row = matrix[y];
            for (var x = 0; x < size; x++)
            {
                if (!row[x])
                {
                    continue;
                }

                var run = 1;
                while (x + run < size && row[x + run])
                {
                    run++;
                }

                path.Append(CultureInfo.InvariantCulture, $"M{x} {y}h{run}v1h-{run}z");
                x += run - 1;
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"{label}\">"
            + $"<rect width=\"{size}\" height=\"{size}\" fill=\"#fff\"/><path fill=\"#000\" d=\"{path}\"/></svg>");
    }
}
