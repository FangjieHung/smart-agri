# Retrieval evaluation set

The question bank the testing ADR asks for (M2 plan Slice 16, ticket #50): the relevance
threshold (`Retrieval:MinScore`) and the embedding model are chosen by running these questions,
not by feel, and the bank covers questions that should find nothing (grounded-answers ADR).
`eval-retrieval` runs it (apps/api/README.md, "Evaluating retrieval"), and with
`SEED_DEMO_KNOWLEDGE=true` the development seeder puts the same documents into 安心商行
("Development seed data").

The build copies `documents.json`, `questions.json` and `files/` next to the Api assembly
(`eval/retrieval/`, never published); `RetrievalEvalSet` reads them from there.

**The repository is public.** Every word here is invented for the Demo organization 安心商行: no
real customer, supplier, person, address or telephone number (the only e-mail address uses the
reserved `.example` domain).

## The documents

| File | Uploaded as | Knowledge base | What it is | After import |
| --- | --- | --- | --- | --- |
| `files/product-guide.docx` (2,732 bytes) | 商品使用指南.docx | 商品使用指南 | Vegetable boxes, rice and gift boxes: Title, Heading 1 and Heading 2 by their built-in names | version 1 in effect; sections labelled 「1 蔬菜箱 › 1.1 規格」 |
| `files/return-policy-v1.pdf` (28,210 bytes) | 退換貨辦法.pdf | 退換貨政策 | The 2025 edition, 3 A4 pages: returns within **ten** days, fresh produce returnable, refunds in ten working days | version 1, **archived** |
| `files/return-policy-v2.pdf` (34,562 bytes) | 退換貨辦法 2026 版.pdf | 退換貨政策 | The 2026 edition: **seven** days, no fresh produce, refunds in five working days; page 1 lists the changes | version 2 of the same document, **in effect** |
| `files/delivery-timetable.xlsx` (3,693 bytes) | 運費與配送時間表.xlsx | 配送常見問題 | Sheets 「配送時間」 (22 regions: days with a 天 unit column, cut-off `h:mm`, method) and 「運費」 (4 methods: fees and free-shipping thresholds in 元, `#,##0`) | version 1 in effect; one chunk per sheet, header first |
| `files/faq.md` (1,357 bytes) | 常見問題.md | 配送常見問題 | Eight questions and answers under `#`/`##`/`###` headings, UTF-8 without a byte order mark | version 1 in effect; labelled 「常見問題 › 配送 › 連假期間會出貨嗎？」 |

- The two editions of the return policy say different things in the same places, so the bank can
  tell which version a passage came from: every question about the policy expects version 2 and a
  phrase that version 1 does not contain (a test checks this), and a leak of the archived version
  shows up in the report's 「取回非有效版本的段落」.
- **FAQ entries** are #44's; until they exist, the FAQ is this Markdown document.
- **Consistent with the frontend Demo** (`apps/admin/src/app/core/repositories/demo-seed.ts`):
  the knowledge bases have its names and purposes; its three trial questions are in the bank —
  「收到商品後幾天內可以申請退貨？」 expects the 2026 edition's seven days, and the two it answers
  without organization data (「皮革商品平常要怎麼保養？」, 「可以幫我訂下週的機票嗎？」) should find
  nothing — and its FAQ 「連假期間會出貨嗎？」 is answered. A test parses demo-seed.ts and fails when
  they drift apart. Its fourth knowledge base, 同仁個人筆記, belongs to the internal employee and no
  customer-facing assistant searches it, so the set leaves it out.

## Format

JSON, read with unknown fields refused (a typo fails loudly). `RetrievalEvalSet.Load` checks
everything below and reports every problem at once; `RetrievalEvalSetTests` also processes every
file with the real extractors and chunker and checks each expected location and phrase against
the chunks — so the bank cannot silently rot when a document changes.

**`documents.json`**: `knowledgeBases[]`, each `{ name, purpose, documents[] }`; each document is
`{ versions[] }`, oldest first, each version `{ file, fileName }` — `file` its path in this
directory, `fileName` the name it is uploaded under. A document is called by its first version's
file name, as an upload names it. Every version is uploaded and approved in order, so the last is
in effect and the others are archived. Knowledge base names, document names (across the set, so
a question can name one) and contents (per knowledge base) are unique, and every file must pass
the upload rules.

**`questions.json`**: `questions[]`, each:

| Field | |
| --- | --- |
| `id` | Unique, e.g. `returns-01`. |
| `category` | `returns`, `product`, `delivery`, `faq`, or `unanswerable` for a question the organization's data should not answer. The report sums up hits per category. |
| `question` | 1–500 characters, as a customer would ask it. |
| `expected` | The passages that answer it, any one of which in the top 5 is a hit: `{ document, version, location, evidence? }`. `document` a document's name; `version` its **effective** (last) version — an archived one is never retrieved; `location` text the passage's location label must contain (「第 2 頁」, a heading path, 「工作表『運費』」 for any rows of that sheet); `evidence` an optional phrase the passage's text must contain, required for documents with several versions. Empty for `unanswerable`, and only then. |
| `note` | Optional: why the question is there. |

The committed bank has 34 questions: 7 `returns`, 8 `product`, 6 `delivery`, 6 `faq` and 7
`unanswerable` — among them 「安心商行的統一編號是多少？」, whose words appear in the FAQ about a
customer's tax number, to catch a threshold set too low.

## How a run is judged

Each question goes through `KnowledgeRetriever` over the set's knowledge bases: top 5, current
effective versions only. A passage is a hit when its document, version number, location and
phrase match an expected one (`RetrievalEvalScoring.Matches`).

- **hit@5 / hit@1**: answerable questions with a hit in the top 5 / at rank 1; ticket #50 asks for
  hit@5 ≥ 90% with the chosen model.
- **「應查無結果」題的最高分**: the highest top-1 score among `unanswerable` questions; **正確命中的最低分**:
  the lowest score of an expected passage among the hits.
- **建議門檻**: a threshold judges an answerable question correctly when its expected passage is in
  the top 5 and scores at least the threshold, and an `unanswerable` one when its top passage scores
  below it (the assistant then answers 「查無結果」 without calling a model). Every gap between
  neighbouring scores is tried; the gap judging the most questions correctly wins, the widest on a
  tie, and its midpoint is suggested — when the two groups separate, the midpoint between the two
  scores above.

## Regenerating the documents

The documents are generated, never hand-edited. `generator/generate-documents.cs` is a .NET 10
file-based app modelled on the extraction fixtures' generator (`apps/api/tests/fixtures/knowledge/`),
using PdfPig and DocumentFormat.OpenXml at the versions in `apps/api/Directory.Packages.props`
(0.1.16 and 3.5.1 when this was written). From `generator/`:

```sh
dotnet run generate-documents.cs -- build NotoSansTC-Regular-subset.ttf ../files
```

The PDFs embed a subset of **Noto Sans TC** (Regular), © 2014-2021 Adobe, licensed under the
**SIL Open Font License 1.1** (`generator/OFL.txt`). It was made from the same upstream file as the
fixtures' subset (Google Fonts repository, `ofl/notosanstc/NotoSansTC[wght].ttf`, commit
`b950a7257470b900078f2bf3223823a8602de7e1`, SHA-256
`864727d210d54f2537bbe23b3a839436c3992af72de9322af5270897246bd44f`) with fontTools 4.60.2 —
`generator/NotoSansTC-Regular-subset.ttf` (39,744 bytes, 149 glyphs: the 141 characters the PDFs
draw) keeps the name 「Noto Sans TC」 and does not use the Reserved Font Name 「Source」:

```sh
python3 -m fontTools.varLib.instancer 'NotoSansTC[wght].ttf' wght=400 --update-name-table -o NotoSansTC-Regular.ttf
dotnet run generate-documents.cs -- chars > chars.txt       # in generator/
python3 -m fontTools.subset NotoSansTC-Regular.ttf --text-file=chars.txt --no-hinting --name-IDs='*' \
  --output-file=NotoSansTC-Regular-subset.ttf
```

If you change the PDF text, redo the subset first. Keep lines under 40 characters (PdfPig does not
wrap), then update the questions and run `RetrievalEvalSetTests`.

The DOCX and XLSX packages embed timestamps, so a regenerated file differs byte for byte even when
it reads the same — and the importer recognizes an uploaded version by its SHA-256. Regenerate only
to change content. `eval-retrieval` starts from scratch every run, but a development database
seeded with the old files keeps them: delete 安心商行's three demo knowledge bases and run `migrate`
again to get the new ones.

## Private samples: `private/`

`private/` is ignored by Git (its own `.gitignore`). The project owner's de-identified samples
(docs/plans/2026-09-26-m2-owner-action-items.md, item 2) that are not marked public go there, as a
set of the same format, and are evaluated locally only:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- eval-retrieval \
  --set apps/api/eval/retrieval/private/<name> --report apps/api/eval/retrieval/private/<name>/report.md
```

A report quotes every question and the passages of each miss, so keep a private set's report in
`private/` too; `docs/evals/<date>-real-samples.md` records file code names and statistics only.
Only samples explicitly marked public may be added to this committed set.
