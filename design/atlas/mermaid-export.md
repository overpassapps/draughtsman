# Mermaid export

## Screen
File > Export Mermaid (also in the Cmd/Ctrl+K palette): copies the open diagram as Mermaid text for a README, and, only when
the browser refuses the clipboard, a small dialog that shows the text to copy by hand. It is an action and one fallback
dialog, not a screen from the eight in `design/atlas/README.md`. Key pattern questions: how a copy action confirms itself;
what a failed clipboard write shows so the export is never lost.

This note is deliberately short. The dialog is a fallback that reuses the Import SQL dialog's chrome (`sql-import-*`
classes, `editor/ImportSqlDialog.tsx`), so it adds no new visual pattern; the note records the decisions and the
references it could not run.

## Mobbin queries
None run. The task that added this action was written with the maintainer away and no Mobbin session available, and the
gap recorded in `design/atlas/README.md` (no Lucidchart, Whimsical or FigJam on Mobbin) would leave the required
reference set unmet anyway. The four-to-six reference set is therefore not met, deliberately: naming the gap is better
than citing screens that were not looked at. The first task that grows this into a real export flow (a format picker,
`design/atlas/export-dialog.md` already lists DSL and JSON) must write a full note.

## References
In-app precedent only, all read in this repository:
1. `editor/ImportSqlDialog.tsx` and `design/atlas/export-dialog.md` — the modal pattern (backdrop, Tab kept inside, Escape closes, every key stopped from the canvas behind it) and the "export makes a file or text, no share actions" rule.
2. File > Export SQL (`viewer.exportSql.*`) — the precedent for an export that asks the server for text and reports a failure as a viewer notice.
3. The copy/paste rule in `AGENTS.md` (`editor/systemClipboard.ts`) — the clipboard is a permission-gated, secure-context-only API that can hang or be absent, so a path that needs it must have a fallback.

## What the references agree on
- An export action is a menu/palette entry named for the verb and the format; it does not open a dialog when it can just do the job.
- Confirmation is a brief polite notice, not a modal.
- A failure is a notice that names the cause; the modal is reserved for what needs a decision or a manual step.

## What Draughtsman does
- **Action:** `viewer.exportMermaid`, label "Export Mermaid", group Document (so File > Export Mermaid and the palette), enabled whenever the diagram has content, also in a read-only preview.
- **Success:** the text is on the clipboard and the viewer notice (a `role="status"` live region) says "Copied the diagram as Mermaid."
- **Clipboard refused** (no secure context, no permission, a browser that wants the write inside the click): a modal "Export Mermaid" dialog with the text in a read-only, selected, monospace textarea, one Close button, and a sentence saying the browser would not copy it. The write is started with a pending `ClipboardItem` where the browser has one, so Safari's click-only rule does not force the dialog needlessly.
- **Export failed** (the server refused the document): the viewer notice "Couldn’t export Mermaid: <server detail>", no dialog.
- The text comes from the server (`POST /api/export/mermaid`); nothing about Mermaid syntax is in the browser.

## Keyboard and accessibility
- The dialog opens with focus on the text box and its text selected, so Cmd/Ctrl+C copies it immediately; Tab cycles the text box and Close; Escape and a click on the backdrop close it; focus returns to the canvas.
- The dialog is `role="dialog"` `aria-modal="true"` labelled "Export Mermaid"; the textarea has the visible label "Mermaid text"; axe runs over it (`a11y/editorAxe.test.tsx`).
- The confirmation is in the existing `role="status"` region, so it is announced without moving focus.

## What Draughtsman deliberately won't do
- **No preview or format options** (a flowchart direction, styling): Mermaid lays the diagram out itself and the exporter writes no layout.
- **No download of a `.mmd` file** and no "open in mermaid.live" link: the use is paste into a README, and a link would send the diagram to a third party.
- **No silent success on a refused clipboard:** the text is always shown rather than lost.

## Open questions
- Whether a `.mmd`/`.md` download alongside the copy is wanted once the export dialog in `design/atlas/export-dialog.md` is built.

Written 2026-09-29 by the m5-sequence-mermaid-export crewmate.

## Amendment 2026-10-01 (QA P8)
Export Mermaid now saves a file; the clipboard is its own action.
- **Export Mermaid** (`viewer.exportMermaid`, File menu) downloads `<title>.mmd` and says "Saved <file>." A lossy export instead opens the same dialog as the clipboard fallback, with an opening sentence ("Saved as x.mmd. Mermaid has no spelling for N things in this diagram, so they were simplified:") and a list (`aria-label` "Simplified in the Mermaid export") of what changed, one line per thing, from the `X-Draughtsman-Export-Notes` response header (at most 25, then "and N more").
- **Copy as Mermaid** (`viewer.copyMermaid`) is the old behaviour: copy, the same dialog when the browser refuses the clipboard, and the list when the copy was lossy.
- **Export DSL** (`viewer.exportDsl`) downloads the document's DSL as `<title>.dsl`.

