# chat (`@smart-agri/chat`)

The conversation pieces shared by the admin app and the visitor chat widget (`apps/widget`, M5a):
message rendering, the streaming reply, the citation drawer, and the AG-UI streaming core.
Extracted from `apps/admin` in M5a Slice 7 (#198) without changing behaviour.

## Rules

- **No import from `apps/*`** and no dependency on another workspace lib. The `scope:chat` tag plus
  `@nx/enforce-module-boundaries` (root `eslint.config.mjs`) enforce it.
- **No Angular Material.** Components use plain elements and CSS custom properties; the only
  Angular dependency beyond core is `@angular/cdk/a11y` (focus trap in the citation drawer).
- **`@ag-ui/client` is imported dynamically** (`AgUiChatRunner` loads it on the first `run()`), so it
  stays out of the host app's initial bundle. Never add a static import of it.
- Component selectors keep the original `app-` prefix (`app-chat-message`, `app-streaming-reply`,
  `app-citation-drawer`): admin specs and Cypress select on them.

## Public API (`src/index.ts`)

| Export | What it is |
| --- | --- |
| `ChatMessageComponent` (`app-chat-message`), `CitationRequest`, `WithdrawRequest` | One message bubble; outputs `openCitations`, `startForm`, `withdrawSubmission` |
| `StreamingReplyComponent` (`app-streaming-reply`) | The in-progress answer; `[n]` markers show as "checking" until the final reply |
| `CitationDrawerComponent` (`app-citation-drawer`) | Modal list of citation excerpts; focus trap, Esc to close, caller restores focus |
| `AgUiChatRunner`, `AgUiChatRunnerDeps`, `apiChatRunsPath`, `REPLY_EVENT`, `THREAD_EVENT`, `FORM_CHECK_EVENT` | Streams `POST .../chat/runs` (AG-UI SSE) into `ChatRunEvent`s |
| `ChatRunner`, `ChatRunRequest`, `ChatRunEvent`, `ChatRunError`, `ChatRunErrorKind`, `ChatHistoryEntry` | Runner contract |
| `toChatMessage`, `toChatReply`, `ChatMessageWire`, `ChatReplyWire`, `ChatReplyExtensionMapper` | Wire (`ChatMessageView` JSON) to view-model mapping |
| `ChatMessageView`, `ChatReplyView`, `ChatCitationView`, `ChatFormView`, ... `REPLY_KIND_LABELS`, `Database*` display types | View-model types |

`AgUiChatRunnerDeps` lets a host choose the endpoint (`runsPath`, default is admin's
`apiChatRunsPath`), the auth (`accessToken`, `onUnauthorized`) and extra reply kinds
(`replyExtension`). Admin maps `form-request`, `submission-receipt` and `database-query` itself
(`toAdminChatReply` in `hybrid-demo-repository.ts`); the widget needs no extension because the
visitor API only returns `company-data`, `general-knowledge` and `no-result`.

## What the host must provide

### CSS custom properties

Component styles read only these variables (admin defines them in `apps/admin/src/styles/_tokens.scss`;
the widget ships its own small token file and overrides the brand color from the publishing settings):

| Group | Variables |
| --- | --- |
| Color | `--color-surface`, `--color-surface-muted` (optional, falls back to a mix of `--color-accent`), `--color-text`, `--color-text-secondary`, `--color-accent`, `--color-accent-soft`, `--color-on-accent`, `--color-success`, `--color-warning` |
| Border, shadow, focus | `--border-default` (a full `border` shorthand), `--shadow-subtle`, `--focus-ring` (a full `outline` shorthand), `--focus-offset` |
| Type | `--font-size-caption`, `--font-size-heading`, `--line-height-body`, `--line-height-heading` |
| Shape and spacing | `--radius-container`, `--radius-control`, `--space-1`, `--space-2`, `--space-3`, `--space-4`, `--space-6`, `--target-size` |

### Global utility class

Templates use `.visually-hidden` (screen-reader-only text). Admin defines it in
`apps/admin/src/styles/_utilities.scss`; the host's global stylesheet must define it too.

## Tests

`nx test chat` (Vitest). `ag-ui-chat-runner.spec.ts` parses the recorded streams in
`tools/agui-contract/fixtures/`, the same files `tools/agui-contract/check-agui-stream.mjs` checks in CI.
