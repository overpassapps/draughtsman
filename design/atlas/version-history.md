# Version history, compare, restore and bin references

## Screen
The History tab of the editor's right-hand panel, plus the bin it shares its restore pattern with. It answers PR-08 (audit: "no version-history or restore UI; critique and diff are MCP-only") and OP-02 (hard delete, no bin). Key pattern questions: how a list of restore points reads when most are automatic (an agent's pre-edit checkpoint) and a few are named by a person; how to compare a restore point with the document in front of you without leaving the canvas; how to make a restore obviously undoable; how a bin entry differs from a version.

## Mobbin queries
- `search_screens "version history side panel listing saved versions with timestamps and author, restore this version button"` (web, standard)
- `search_screens "trash page listing deleted items with restore and delete forever actions and a retention notice"` (web, standard)
- `search_screens "accessibility or design checker side panel listing issues grouped by severity, clicking an issue selects the element on the canvas"` (web, standard), used for the Review panel note in `critique-panel.md`.

Coverage note: Lucidchart, Whimsical and FigJam are not on Mobbin (see `README.md`); they are cited from the products.

## References
1. Notion — Version history — https://mobbin.com/screens/780626a3-20f5-4795-ac3b-bdbaef325210 — a right-hand list of timestamps with the editors' names under each, one selected row previewing in the page, a single primary Restore button at the foot.
2. Framer — Version History — https://mobbin.com/screens/748ea589-c270-4e7e-9e97-41a8e25fa669 — a right-hand panel distinguishing "Autosave" rows from "Published N changes" rows; expanding a row lists what changed, each with a NEW badge. The model for separating automatic from named restore points.
3. Mistral AI — earlier version banner — https://mobbin.com/screens/e3ea7439-9dac-441f-95a9-25a7f03aebd3 — the viewed older version shows additions in green and deletions struck through in red, under the sentence "You are currently looking at an earlier version. To make changes, restore this version." with Restore at the right. The model for compare.
4. Descript — Version history — https://mobbin.com/screens/2b3c08f5-223b-4296-b17b-50a945120b33 — a dense card list, each with author avatar, absolute time and "35 minutes ago"; rows with conflicts are flagged in the accent colour.
5. AirOps — Trash — https://mobbin.com/screens/6fac6f5e-4963-4483-9a1c-889d60c898fb — a table with title and "Date trashed", a per-row Restore and Delete, a yellow line "Items in Trash for over 30 days will be automatically deleted", and Empty Trash at top right.
6. Zoho CRM — Recently Deleted — https://mobbin.com/screens/c06d32ad-44cf-4a87-8d60-48f644e20a6e — Deleted By and Days Remaining columns, a red-outlined "Delete all permanently" kept visually apart from the safe actions.
7. Airtable — Trash — https://mobbin.com/screens/dc4558ef-905d-44fc-8cfc-c483e5fe70f1 — a sentence per entry ("You deleted a base: …") with a link-style Restore, and a description linking to what is and is not recoverable.

Not on Mobbin (from the products): Lucidchart — Revision history, a right-hand list with a preview of the selected revision and "Restore this revision"; Figma — Version history, named versions with descriptions above autosaves.

## What the references agree on
- A newest-first list of time-stamped entries with the author, in a side panel beside the work (not a modal), with the current state at the top.
- Automatic restore points and deliberate, named ones are different kinds of row; the named ones carry a title and sometimes a note.
- Restore is one explicit button, acting on the selected entry, and is reversible because it produces a new entry rather than erasing the later ones.
- Trash shows when it was deleted, who by, and how long is left, with Restore as the easy action and permanent deletion set apart.

## What Draughtsman does
- **Placement:** a "History" button in the panel's view switcher (Properties, Text, AI, Tour, History, Review). It is available to everyone who can open the document.
- **List:** newest first, one row per stored version (`GET /api/documents/{id}/versions`). A row's title is the name a person gave it; an automatic checkpoint (name `checkpoint`) reads "Automatic checkpoint" with its reason beneath (for example "Before replace by agent:claude-code/…" or "Before restoring "v1" (…)"). Every row shows the relative time (absolute on hover), who (the actor, with an agent marked as such), and the size of the snapshot in KB. A "Load more" button pages the list (50 at a time).
- **Name this version:** a button at the top opens an inline name field (and an optional note); Save writes a named snapshot of the document as currently stored. The panel saves unsaved edits first, so the snapshot is what is on the canvas. The name `checkpoint` is reserved (the server refuses it). Named versions are never pruned by retention.
- **Compare:** each row has Compare. It asks the server for the structured diff between that version and the saved document (`GET /api/documents/{id}/diff?from=<version>`, the `diff_documents` tool's own shape, reusing `DocumentDiff`) and shows, in the panel, a summary line ("Since <name>: 2 added, 1 removed, 3 changed") and a readable list in three groups, nodes then connectors: **Added** (label and kind), **Removed** (label and kind, from the old version), **Changed** (label and the names of the fields that differ, "label, geometry"). Clicking an entry centres it on the canvas when it still exists. On the canvas, added elements get a solid accent outline with a + badge, changed ones a dashed outline with a ~ badge, and removed ones a dotted danger-colour outline with a − badge, drawn at the position they had in the old version, so a deletion is visible where it happened. No new colour tokens: the chrome theme has no success or warning token, and the three states are told apart by line style and badge first, colour second, which is also what keeps them readable in either theme. "Stop comparing" (and Escape inside the panel) clears the marks, as does leaving the History tab. The marks are view state only: they are never saved and never enter undo.
- **Restore:** each row has Restore, which opens a small confirmation: "Put the document back to "<name>" from <time>? The document as it is now is saved as an automatic checkpoint first, so you can undo this from this list." Confirm saves unsaved edits, calls `POST /api/documents/{id}/versions/{versionId}/restore` with the base version the editor holds (`If-Match`, so a restore over an edit the editor never saw is refused, not applied), and the editor adopts the restored document as the saved one. The restore appears at the top of the list as a new "Automatic checkpoint" whose reason names the version it replaced: restoring that checkpoint undoes the restore. Because it is a server-side, checkpointed change, it is deliberately not an entry in the local Cmd/Ctrl+Z stack (the same rule remote and agent edits follow).
- **Bin** is described in `document-list.md`: the same Restore and confirm pattern, with Delete forever set apart and confirmed separately, a retention line, and "deleted by" and "days left" columns.
- **Who can do what (the Editor rule):** anyone signed in can delete a document (it is recoverable); an Editor sees, and restores, the bin entries they created or deleted; an Admin sees all; permanently deleting is for an Admin or the document's creator, and only from a browser session, never with an API token. Stated once in `docs/api.md` ("Bin").

## Keyboard and accessibility
- The panel is reachable by F6 like the rest (`data-region="properties"`); the view switcher's History button is `aria-pressed`.
- The list is a real list; each row's Compare and Restore are buttons whose accessible names include the version name ("Compare with Before the rework", "Restore Before the rework"). The compare result is a labelled region; its entries are buttons. Announcements: a polite live region says "Comparing with …: 2 added, 1 removed, 3 changed" and "Restored …".
- The confirmation dialogs (restore, delete forever) are `role="dialog"` with `aria-modal`, focus on the safe button (Cancel), Escape cancels, focus returns to the control that opened them.
- Canvas marks use outline style and a badge as well as colour (solid for added, long dash for changed, dotted for removed), and the badge text is the brand-on-brand-text pair.

## What Draughtsman deliberately won't do
- **No auto-preview of a selected version in the canvas** (Notion, Lucidchart). Compare overlays the current document instead: one canvas, no mode where the thing on screen is not the thing being edited, so nothing can be edited on an old version by mistake.
- **No per-keystroke history** (Google Docs). Restore points are the server's checkpoints and a person's named snapshots, which is what agents and reviewers need and what is cheap to store.
- **No "Empty bin" button** (AirOps, Zoho). The retention period empties it, and one document at a time is the deliberate friction for a permanent step.
- **No branching or merging of versions.** Restore is whole-document.

## Open questions
- Whether Delete forever should also be allowed to a document's creator with an API token for automation: the build says no (cookie session only); say if a script needs it.
- A version preview as a thumbnail per row is not built; it would need server rendering per version.

---
2026-10-01 · Claude (fm/history-bin-list crewmate)
