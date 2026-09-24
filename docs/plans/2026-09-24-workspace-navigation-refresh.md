# Workspace navigation and management refresh

## Goal

Make the demo login, assistant ownership, recent chats, data lists, and settings layout match the agreed navigation model.

## Decisions

- Demo credentials are `admin`, `internal`, and `customer`, all with password `1234`; they select the existing three demo accounts and do not imply server authentication.
- My assistants has two groups: assistants and drafts made by the active account, and assistants made by others that the active account may use.
- An account can save multiple independent drafts. Legacy single drafts are migrated into the new collection.
- The global sidenav shows ten most recently updated saved chat threads across usable assistants. The chat page retains its full per-assistant rail and management controls.
- The publishing overview route remains available from assistant publishing pages after its sidenav entry is removed.
- The screenshot defines the settings row layout: title and explanation on the left, control on the right. Its English text is reference content, not product copy.

## Implementation sequence

1. Update visible terminology and remove the requested informational blocks.
2. Add demo credential mapping and a labeled, validated login form.
3. Add named draft records with migration, draft-specific wizard routes, and grouped assistant listing.
4. Add cross-assistant recent chat summaries to the sidenav and organize navigation sections.
5. Align `libs/ui` data-table with the car-rental reference and use it for knowledge, assistant sources, and databases.
6. Move database creation into a dialog with keyboard focus and validation.
7. Extract shared detail navigation and settings rows; apply responsive side navigation above 1024px and the requested settings layout.
8. Create the functional map and glossary, then run relevant unit tests, lint, and build.

## Validation

Exercise login validation and each demo identity, multiple drafts and legacy migration, assistant group permissions, recent chat ordering and account isolation, table links, database dialog focus, and 1024px navigation. Verify with Nx tests and build.
