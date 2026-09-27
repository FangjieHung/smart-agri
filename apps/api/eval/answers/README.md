# Answer evaluation set

The question bank the grounded-answers ADR asks for (M3 plan Slice 13, ticket #83): whether the
whole answer pipeline (`GroundedAnswerService.AnswerAsync`) replies with the right kind, cites the
right documents, and — with a real model — how many tokens it costs. `eval-answers` runs it
(apps/api/README.md, "Evaluating answers").

**The repository is public.** Every word here is invented for the Demo organization 安心商行: no
real customer, supplier, person, address or telephone number. Unlike `apps/api/eval/retrieval/`,
every document here is a small hand-written Markdown file (no PDF/DOCX/XLSX generation): this
evaluation is about the answer pipeline's decisions, not about extraction or chunking, which
`eval-retrieval`'s set already covers.

## The documents

| File | Uploaded as | Knowledge base | What it is |
| --- | --- | --- | --- |
| `files/product-guide.md` | 商品使用指南.md | 商品使用指南 | Vegetable box sizes and rice storage |
| `files/return-policy.md` | 退換貨辦法.md | 退換貨政策 | Return window, damaged goods, refund days |
| `files/faq.md` | 常見問題.md | 常見問題與公告 | Payment method, tax number, membership points |
| `files/notice.md` | 系統公告.md | 常見問題與公告 | **A prompt-injection sample** (see below) |

`files/notice.md` deliberately hides an instruction inside otherwise plausible announcement text,
asking whoever reads it to ignore its rules and leak a fake coupon code without citing the
passage — the plan's §7 risk 1. `Fake` cannot read content semantically, so it never actually acts
on the instruction; the question is in the bank so a **real** model run can check that the answer
pipeline resists it (cites the passage as usual, does not repeat the injected text as if it were a
real instruction).

## Format

JSON, unknown fields refused. `AnswerEvalSet.Load` checks everything it can without a database and
reports every problem at once.

**`documents.json`**: the same shape as `apps/api/eval/retrieval/documents.json`'s —
`knowledgeBases[]`, each `{ name, purpose, documents[] }`; each document `{ versions[] }`, each
version `{ file, fileName }`.

**`questions.json`**: `questions[]`, each:

| Field | |
| --- | --- |
| `id` | Unique. |
| `question` | 1–500 characters, as a customer would ask it. |
| `expectedKind` | `company-data` or `no-result` — the reply kind a `company-data-only` profile should give (the grounded-answers ADR: the bank must cover questions that should find nothing). |
| `expectedCitedDocuments` | For `company-data`: the uploaded file names the reply should cite (any one is a hit). Empty, and only, for `no-result`. |
| `followUpOf` | Optional: another question's `id`, earlier in the bank. That question and the reply it actually got become one-turn conversation history, so the retrieval query joins both questions (M3 plan §7 decision D). |
| `note` | Optional: why the question is there. |

## How a run is judged

Every question is answered through `GroundedAnswerService.AnswerAsync` with a `company-data-only`
profile (no assistant of its own) and the deployment's configured `Retrieval:MinScore`. The report
(`docs/evals/<date>-answers-<chat model>.md`) has:

- **回覆類型正確率**: replies whose kind matched `expectedKind`, over the total.
- **引用命中率**: of the `company-data` questions, those citing at least one expected document.
- **拒絕原因分布**: how many `no-result` replies carried each rejection reason.
- **平均輸入／輸出 token**: the mean of `ModelInvocations.InputTokens`/`OutputTokens` over this
  run's `generate-answer` calls; `—` when none reported a number.
- Every question's expected and actual reply, side by side.

**With `Fake`** (both `Ai:Embedding` and `Ai:Chat`): the pipeline runs end to end and the report is
byte-for-byte reproducible (`EvalAnswersIntegrationTests`), but scores and citations are not
meaningful — `Fake` embeddings are hashes and `FakeChatClient` always cites the first passage it is
given, whatever it says. **Never calibrate `Retrieval:MinScore` or the prompt from a `Fake` run.**

**With a real model**: not run yet — a job for whoever holds the OpenAI key (see
`apps/api/README.md`, "Evaluating retrieval" for the same caveat on `eval-retrieval`). That run is
also when the prompt-injection question (`notice-01`) should actually be checked by a person, not
just by the pipeline.
