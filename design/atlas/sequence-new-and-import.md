# Sequence diagram: New menu entry and Import Mermaid

## Screen
Two entry points on existing screens (`design/atlas/document-list.md`, `design/atlas/editor-shell.md`): a **Sequence** item in the New diagram menu, and an **Import Mermaid** button and File-menu action beside Import SQL, opening a paste dialog. No new screen; the dialog reuses the Import SQL dialog's layout, classes and keyboard contract (`web/src/editor/ImportSqlDialog.tsx`).

## Mobbin queries
None run. This adds one menu item and reuses a shipped dialog; the references in `document-list.md` (New document menu) and `sequence-editor.md` cover the pattern, and Mobbin holds no Lucidchart or Whimsical screens (atlas README, coverage gap).

## References
From the products themselves (not on Mobbin; from the product): Lucidchart's New menu offers a sequence diagram as a peer of flowchart; Mermaid Live's paste-and-preview loop is the model for read-before-create.

## What Draughtsman does
- **New diagram → Sequence** (home screen, File menu, Cmd/Ctrl+K "New sequence diagram") creates a `sequence` document titled "Untitled sequence" that opens with a `User` actor, a `System` participant and one `sync` message, so the derived canvas is not empty and the first edit (rename a label, add a message) is obvious. The other types stay blank.
- **Import Mermaid** is two steps like Import SQL: Read diagram (imports, shows what it found and every importer warning with its line, saves nothing), then Create diagram. There is no type picker: the Mermaid header decides between flowchart and `sequenceDiagram`, and a flowchart is laid out on read while a sequence diagram derives its layout.
- The document list already labels and draws a `sequence` document with its own icon; that label now comes from the New menu's option list like the other types.

## What it does not commit to
Mermaid export, a type picker on import, and importing DSL from the home screen.
