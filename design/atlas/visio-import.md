# Import Visio

## Screen
One dialog on two existing screens (`design/atlas/document-list.md`, `design/atlas/editor-shell.md`): an **Import Visio** button beside Import draw.io on the home screen, and an **Import Visio…** File-menu action (and palette entry) in the editor. No new screen: it is Import draw.io's twin (`design/atlas/drawio-import.md`) and reuses the same dialog layout, classes and keyboard contract (`web/src/editor/ImportDrawioDialog.tsx`, `ImportSqlDialog.tsx`). The questions it must answer: how a person brings a binary file in (there is nothing to paste), how they see what was guessed or lost before anything exists, what happens when the file has several pages, and how someone coming from Lucidchart finds the way in.

## Mobbin queries
None run. This adds one dialog that reuses a shipped one, and Mobbin holds no Lucidchart, Whimsical or Visio screens (atlas README, coverage gap); `drawio-import.md` and `sequence-new-and-import.md` cover the pattern.

## References
Not researched in the products beyond what is common knowledge of them, so nothing here goes further (not on Mobbin; from the product): Visio shows a drawing's pages as tabs along the bottom, each its own canvas, which is why a multi-page import is one page at a time with a page choice rather than a merge, as for draw.io. Lucidchart's export to Visio is a menu command that downloads a `.vsdx`, so "coming from Lucidchart" is the same dialog with the help text naming it, not a second importer.

## What Draughtsman does
- **A file, only.** A file chooser (`.vsdx`, `.vsdm`) with `Loaded <name> (<size>).` under it. There is no paste box, because a `.vsdx` is a ZIP package: the chosen file itself is sent as the request body. Choosing another file discards a read. A file over the 3 MB the server reads is refused in the dialog, saying what to do, without being uploaded.
- **Two steps, as Import draw.io.** **Read diagram** imports and saves nothing; **Create diagram** then saves a new document and opens it. A change to the file or the **Diagram type** discards the read; a change to the title does not.
- **Help text that says the routes.** One paragraph names what is read (a `.vsdx` from Visio 2013 or later, or exported to Visio from Lucidchart), what is kept (positions, sizes, text), what happens to the rest (a plain box, listed below), and that an older `.vsd` must be saved as `.vsdx` first.
- **Diagram type** (Flowchart, Architecture, Freeform) with a one-line hint, exactly as for draw.io: the choice decides what an ellipse or a cylinder means, and Freeform guesses nothing.
- **The summary and the list.** `Found N shapes and M connectors.`, then what the warnings add up to in words: kinds the importer chose are "a check to make", anything left out or drawn as a plain box is "skipped or only partly understood". A loss that covers many shapes (a stencil master, a custom outline, shape data) is one line with a count, so the list stays short. The page note is listed but not counted as skipped, because the page choice already says it.
- **Pages.** A file with more than one page shows **Page to import** above the result, named as the file names them (`Page N` when it did not). Choosing another reads again at once. A one-page file shows nothing.
- **No layout.** Positions come with the file, so reading never calls the layout endpoint, and the new diagram opens as drawn.
- **Same guard as the other imports.** From a dirty editor, Create asks the leave question first (`confirmLeave`), and a Cancel leaves no document behind.

## Keyboard and accessibility
As the draw.io dialog: `role="dialog"` and `aria-modal`, labelled by its heading, with the file chooser focused on open; Tab stays inside it and Escape closes it unless a read or a create is running; every key is stopped from reaching the canvas behind. The summary is a `role="status"` region and an error a `role="alert"`; the page list sits outside the status region because it is a control. Tested with axe in `web/src/a11y/editorAxe.test.tsx`.

## What Draughtsman deliberately won't do
- **Re-layout on import.** A drawing's own positions are the point of bringing it in. Tidy layout is an explicit command afterwards.
- **A preview picture.** The list of what was guessed or lost is what a person needs to decide; a thumbnail would be a second renderer to keep right.
- **Import several pages at once.** One page is one diagram; each import is its own.
- **A "From Lucidchart" button.** There is one importer and one dialog; the docs page and the help text carry the route.

## Open questions
None.

*Written 2026-10-02 by the import-visio-lucid crewmate.*
