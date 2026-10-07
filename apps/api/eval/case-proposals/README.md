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

## How a run is judged

Each question is decided twice per trigger:

- **The case layer alone**: the keyword rule, or one `propose_case` call with the production
  declaration and prompt (`CaseProposalRules.Declaration`, `SelectionPrompt`, `ParseCall`), as if the
  layers before it had said no.
- **The whole proposal stage** (decision L: database query → form → case → answer): the query layer
  is `DatabaseQueryTools.AsksForStatistics` (the gate that offers the query tools; the query model's
  own choice is not judged here); in keyword mode `AssistantFormRequestRules.AsksForForm` and then the
  case layer; in model mode, since #286, the one combined call production makes for an assistant with a
  form and case types (`ProposalSelectionRules`: `request_database_form` and `propose_case` offered
  together, the model calls one or neither).

Missed = a `case` question with no case proposal; false trigger = a `none`, `form` or `query` question
with one; wrong type = a `case` question proposed under another type. A failed model call is counted,
not judged.

The set is built to probe the keyword rule's known weaknesses, so its rates are not field rates.
Changing the set changes the report's fingerprint; keep old reports comparable by adding questions
rather than relabelling, and say why when a label changes.
