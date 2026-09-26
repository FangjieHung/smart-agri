# Knowledge extraction fixtures

Files for the text-extraction tests of M2 Slice 6 (ticket #40): `SmartAgri.Api.Tests` copies
them next to the test assembly and reads them through `Knowledge/Extraction/KnowledgeFixtures.cs`,
which also holds the text the tests expect. They are generated, never hand-edited, and contain
no real customer data (安心商行 is the Demo organization). The tests never regenerate them and
need neither network access nor Python.

| File | Bytes | What it is | Expected result |
| --- | ---: | --- | --- |
| `return-policy.pdf` | 28,430 | 3 A4 pages of Chinese text with a text layer (Noto Sans TC, embedded subset with a ToUnicode map) | `ready`; page 2 reads exactly as `KnowledgeFixtures.ReturnPolicyPage2` |
| `delivery-guide-with-scanned-page.pdf` | 21,577 | 3 pages; page 2 is only a full-page greyscale image, like a scan | `partially-readable`, issue names 第 2 頁 |
| `encrypted-return-policy.pdf` | 29,312 | `return-policy.pdf` with a password to open it (AES-256; user password `anxin-1234`, owner password `anxin-owner-5678`) | `failed` after one attempt, 「…請解除密碼後重新上傳」 |
| `product-guide.docx` | 2,336 | Title, Heading 1–3 by every route the extractor supports (zh-TW style id `1` named `heading 1`; `Heading2` with `w:outlineLvl`; a custom 「小節標題」 style with only `w:outlineLvl`; a Normal paragraph with a direct `w:outlineLvl`) and a 3×3 table | `ready`; 7 sections labelled by heading path |
| `delivery-and-prices.xlsx` | 5,318 | Sheets 「配送時間」 (22 rows) and 「商品價格」 (50 rows), shared strings, a units column (天／公斤／元) and number formats `h:mm`, `yyyy/m/d`, `0.0`, `#,##0`, `0%` | `ready`; every chunk starts with its sheet's header row |
| `faq.md` | 414 | UTF-8 **with a byte order mark**; `#`/`##`/`###` headings and a fenced code block containing a `#` line | `ready`; 4 sections |
| `holiday-notice-big5.txt` | 54 | One line of Chinese in Big5 (code page 950) | `failed` after one attempt, issue mentions UTF-8 |

## Font and licence

The PDFs embed a subset of **Noto Sans TC** (Regular), © 2014-2021 Adobe, licensed under the
**SIL Open Font License 1.1** (full text: `generator/OFL.txt`, copied from the same upstream
directory; the font's own name table carries the same notice). Source:
`https://github.com/google/fonts/raw/main/ofl/notosanstc/NotoSansTC%5Bwght%5D.ttf` (Google
Fonts repository, file last changed in commit `b950a7257470b900078f2bf3223823a8602de7e1`,
downloaded 2026-09-26, SHA-256 `864727d210d54f2537bbe23b3a839436c3992af72de9322af5270897246bd44f`).
The subset is a modified version under the OFL; it keeps the name 「Noto Sans TC」 and does not
use the Reserved Font Name 「Source」. No system font (PingFang etc.) is used anywhere.

`generator/NotoSansTC-Regular-subset.ttf` (40,256 bytes, 145 glyphs: exactly the characters
the PDFs draw) is committed so the PDFs can be regenerated offline. It was made with
fontTools 4.60.2:

```sh
python3 -m fontTools.varLib.instancer 'NotoSansTC[wght].ttf' wght=400 --update-name-table -o NotoSansTC-Regular.ttf
dotnet run generate-fixtures.cs -- chars > chars.txt       # in generator/
python3 -m fontTools.subset NotoSansTC-Regular.ttf --text-file=chars.txt --no-hinting --name-IDs='*' \
  --output-file=NotoSansTC-Regular-subset.ttf
```

Keep the default layout features: with `--layout-features=''` PdfPig's own font subsetter
fails (`ArgumentOutOfRangeException`) when it writes the PDF.

## Regenerating

1. `generator/generate-fixtures.cs` is a .NET 10 file-based app using PdfPig and
   DocumentFormat.OpenXml at the versions in `apps/api/Directory.Packages.props` (0.1.16 and
   3.5.1 when this was written). From `generator/`:

   ```sh
   dotnet run generate-fixtures.cs -- build NotoSansTC-Regular-subset.ttf ..
   ```

   It writes every file above except the encrypted PDF. If you change the PDF text, redo the
   font subset first (the `chars` command lists the characters needed) and update
   `KnowledgeFixtures.cs`.
2. The encrypted PDF needs pypdf (BSD-3-Clause) with cryptography (Apache-2.0 OR
   BSD-3-Clause) for AES-256, in a throwaway virtual environment (pypdf 6.1.1 and
   cryptography 50.0.1 were used):

   ```sh
   python3 -m venv /tmp/fixtures-venv && /tmp/fixtures-venv/bin/pip install pypdf cryptography
   /tmp/fixtures-venv/bin/python encrypt-pdf.py
   ```

DOCX and XLSX packages embed timestamps and the encrypted PDF a random salt, so regenerated files differ byte for
byte from the committed ones while reading the same.
