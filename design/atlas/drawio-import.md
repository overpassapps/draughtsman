# Import draw.io

## Screen
One dialog on two existing screens (`design/atlas/document-list.md`, `design/atlas/editor-shell.md`): an **Import draw.io** button beside Import Mermaid on the home screen, and an **Import draw.io…** File-menu action (and palette entry) in the editor. No new screen: it reuses the Import SQL / Import Mermaid dialog's layout, classes and keyboard contract (`web/src/editor/ImportSqlDialog.tsx`, `ImportMermaidDialog.tsx`). The questions it must answer: how a person brings a file in (not only a paste), how they see what was guessed or lost before anything exists, and what happens when the file has several pages.

## Mobbin queries
None run. This adds one dialog that reuses a shipped one, and Mobbin holds no Lucidchart, Whimsical or draw.io screens (atlas README, coverage gap); `sequence-new-and-import.md` and `shape-connector-palette.md` cover the pattern.

## References
Not researched in the products beyond what is common knowledge of them, so nothing here goes further (not on Mobbin; from the product): draw.io shows a file's pages as tabs along the bottom, each its own canvas, which is why a multi-page import is one page at a time with a page choice rather than a merge; Mermaid Live's read-before-create loop is already the model for Import Mermaid and is reused as it is.

## What Draughtsman does
- **A file or a paste.** A file chooser (`.drawio`, `.xml`, `.dio`) above a paste box; choosing a file fills the box and says `Loaded <name>.`, so the person can still see and edit what will be sent. Editing the box afterwards is no longer "that file".
- **Two steps, as Import SQL and Import Mermaid.** **Read diagram** imports and saves nothing; **Create diagram** then saves a new document and opens it. A change to the XML or the **Diagram type** discards the read; a change to the title does not.
- **Diagram type** (Flowchart, Architecture, Freeform) with a one-line hint under it. A drawing has no type of its own, so the choice is what decides what an ellipse or a cylinder means; Freeform guesses nothing.
- **The summary and the list.** `Found N shapes and M connectors.`, then what the warnings add up to in words: kinds the importer chose are "a check to make" (counted as such), anything left out or drawn as a plain box is "skipped or only partly understood". Every warning is listed with the shape's label. The page note is listed but not counted as skipped, because the page choice already says it.
- **Pages.** A file with more than one page shows **Page to import** above the result, named as the file names them (`Page N` when it did not), with a line saying one is imported at a time. Choosing another reads again at once. A one-page file shows nothing.
- **No layout.** Positions come with the file, so reading never calls the layout endpoint, and the new diagram opens as drawn.
- **Same guard as the other imports.** From a dirty editor, Create asks the leave question first (`confirmLeave`), and a Cancel leaves no document behind.

## Keyboard and accessibility
The dialog is `role="dialog"` and `aria-modal`, labelled by its heading, with the paste box focused on open; Tab stays inside it and Escape closes it unless a read or a create is running; every key is stopped from reaching the canvas behind. The summary is a `role="status"` region and an error a `role="alert"`; the page list sits outside the status region because it is a control. Tested with axe in `web/src/a11y/editorAxe.test.tsx`.

## What Draughtsman deliberately won't do
- **Re-layout on import.** A drawing's own positions are the point of bringing it in; moving them would discard the author's work. Tidy layout is an explicit command afterwards.
- **A preview picture.** The list of what was guessed or lost is what a person needs to decide; a thumbnail would be a second renderer to keep right.
- **Import several pages at once.** One page is one diagram; each import is its own.

## Open questions
None.

*Written 2026-10-02 by the import-drawio crewmate.*
