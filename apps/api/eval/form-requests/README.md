# Form-request trigger set

The labelled questions of M4 #164: should this conversation question get the assistant's form
(`request_database_form`)? `eval-form-requests` runs them against the keyword gate and, with a chat
model, against the model choosing the tool (apps/api/README.md, "Evaluating form-request triggers").

**The repository is public.** Every question is invented farm-assistant phrasing; no real person,
farm or place.

## Format

`questions.json`:

- `form`: the sample form the model is offered — `title` and `purpose` (what a member would see),
  offered under a fixed id. The questions are labelled against this form (田間異常回報).
- `questions[]`: `id` (unique), `question`, `expected` (`form` or `none`), `category`, `note`.
  - `positive`: must be `expected: form` — the member wants to report or hand in something this
    form collects. Several deliberately use words the keyword gate does not know (通報、提報、記一筆…).
  - `negative`: must be `expected: none` — knowledge questions, questions about rules, progress or
    past records, or a fill-in word used for something else (報名講習、登記有機驗證…).
  - `ambiguous`: either; `expected` is the labeller's best judgement. Reported on its own
    (agreement), never in the missed/false-trigger rates.

## How a run is judged

- Missed trigger = a `positive` question that got no form; false trigger = a `negative` question
  that got the form. Rates are over judged questions; a failed model call is counted, not judged.
- The model's call counts as a form only if it is `request_database_form` with the offered id
  (`AssistantFormRequestRules.ParseCall`, the same check production makes); another tool or id is
  "no form" and flagged in the per-question table.
- The set is built to probe the gate's known weaknesses, so its rates are not field rates. Questions
  in scope of #149's record queries (e.g. n17 「本月回報了幾筆異常？」) are judged for the form trigger
  alone; in production the query runs first.

Changing the set changes the report's fingerprint; keep old reports comparable by adding questions
rather than relabelling, and say why when a label changes.
