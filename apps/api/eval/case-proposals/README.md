# Case-proposal trigger set

The labelled questions of M7-12 #257: should this conversation question get a case proposal
(`propose_case`), and of which type? `eval-case-proposals` runs them against the keyword rule
(`CaseProposalRules.KeywordProposal`, decision T) and, with a chat model, against the model choosing
the tool (apps/api/README.md, "Evaluating case-proposal triggers").

**The repository is public.** Every question is invented farm and small-business phrasing; no real
person, farm or place.

## Format

`questions.json`:

- `form`: the sample form the assistant also offers — `title` and `purpose`, under a fixed id. The
  form layer runs before the case layer (decision L), so some questions belong to it.
- `caseTypes[]`: `key` (used in labels), `name`, `description` — what an admin would write on the
  case type. Offered in this order under fixed ids (keyword mode with more than one type needs the
  type's name in the question).
- `questions[]`: `id` (unique), `question`, `expected`, `category`, `note`.
  - `case`: `expected: case:<key>` — the member describes something someone has to act on that fits
    that type. Several deliberately avoid the type names and the case words (叫貨、換一盒、退貨款…).
  - `none`: `expected: none` — knowledge questions, questions about a type's rules, progress or
    fields, a case word used for something else (申請補助、安排採收), or an explicit 「不用開案」.
    Several put the type's own name in a question (「採購申請要多久？」): the keyword rule's known
    false trigger.
  - `form`: `expected: form` — the sample form should be shown instead of a case.
  - `query`: `expected: query` — a statistics question; the database query runs before any proposal.
  - `ambiguous`: any of the above; `expected` is the labeller's best judgement. Reported on its own
    (agreement), never in the missed/false-trigger rates.

## A second set of type descriptions (`--types`, #293)

`types-with-exclusions.json` holds the same three types (same keys, same order) with descriptions
that say both what the type covers and what it does not, naming where the excluded things go
(「不包括作物病蟲害、作物生長異常……這些請用田間異常回報」). It measures the admin-side fix of
#257's recommendation 3 without changing a prompt:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- eval-case-proposals --types types-with-exclusions.json --report "$PWD/docs/evals/<date>-case-proposals-<model>-<ticket>.md"
```

- The file is JSON (comments allowed) with one `caseTypes` array in `questions.json`'s format. Its
  keys must be the set's keys in the same order (labels and the fixed type ids refer to them); names
  and descriptions may differ. A description longer than production's limit (500 characters) is
  refused.
- A relative path is looked up in the working directory, then next to `questions.json`.
- Without `--types` the set's own descriptions are offered, exactly as before. The report names the
  types file and its own fingerprint next to the set's.
- Write the descriptions the way an admin would — a sentence or two of scope, then the confusions —
  not as a list of this set's questions; otherwise the run measures the questions, not the advice.

## How a run is judged

Each question is decided twice per trigger:

- **The case layer alone**: the keyword rule, or one `propose_case` call with the production
  declarations and prompt (`CaseProposalRules.Declarations`, `SelectionPrompt`, `ParseCall`; since #297
  `no_matching_type` is offered next to `propose_case`), as if the layers before it had said no.
- **The whole proposal stage** (decision L: database query → form → case → answer): the query layer
  is `DatabaseQueryTools.AsksForStatistics` (the gate that offers the query tools; the query model's
  own choice is not judged here); in keyword mode `AssistantFormRequestRules.AsksForForm` and then the
  case layer; in model mode, since #286, the one combined call production makes for an assistant with a
  form and case types (`ProposalSelectionRules`: `request_database_form`, `propose_case` and, since #297,
  `no_matching_type` offered together; the model calls one, or none).

The report counts how often the model chose `no_matching_type` in each call and marks those rows
「（都不符合）」.

Missed = a `case` question with no case proposal; false trigger = a `none`, `form` or `query` question
with one; wrong type = a `case` question proposed under another type. A failed model call is counted,
not judged.

The set is built to probe the keyword rule's known weaknesses, so its rates are not field rates.
Changing the set changes the report's fingerprint; keep old reports comparable by adding questions
rather than relabelling, and say why when a label changes.
