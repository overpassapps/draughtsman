# The editor

A short guide to the parts of the editor that are not obvious from looking at the canvas. The shortcuts shown here are for
Windows and Linux; on a Mac, `Ctrl` is `Cmd`. Every command is also in the menu (the button at the top left) and in the
command palette (`Ctrl+K`).

## The Text pane

The Text pane shows the diagram you are looking at as text, beside the canvas, and lets you change the diagram by editing
that text. Open it with the `</>` button in the top bar, or **View > Toggle Text (DSL)** in the menu, or the **Text** tab of
the right-hand panel. The editor remembers, in this browser, whether you left it open and which text it was showing.

The text is the diagram's own text form, [the DSL](spec/dsl.md): one line for each node, container and edge, with the
positions and styles you drew. A node you add to the text without a position (`@ x,y`) is drawn with a dashed outline in a
provisional place until you move it or use **Arrange > Tidy layout**.

### Editing and applying

Edit the text, then press **Apply** (or `Ctrl+Enter` with the cursor in the text). Nothing changes on the canvas until you
do, so half-typed text never reaches the diagram.

- **Apply changes only what the text changed.** A node, edge or setting you did not touch keeps its position, style and
  identity, so anyone else looking at the diagram keeps their place and selection. A node or connector whose line you remove
  from the text is removed from the diagram. Remove a node's connectors too: a connector left pointing at a node that is no
  longer there is kept, with a warning.
- **It is one undo step.** `Ctrl+Z` takes the whole apply back, however many lines you changed.
- **The text you typed stays as you typed it** until the diagram changes again, at which point it is printed afresh from the
  diagram.
- **Reset text** throws away your edits and prints the text again from the diagram.
- **Unapplied text is yours to lose, not the editor's.** Closing or reloading the tab asks first. Leaving the diagram by a
  link inside the app does not: apply or copy your text before you go.
- **The diagram keeps its own id.** If the text names another id, the diagram keeps its own and the pane says so.

### When the text is wrong

If the text cannot be read, nothing is applied and the pane shows the line and column, for example
`Line 11, column 20: unterminated string for the label`. The line is marked in the text, and the message is a link that puts
the cursor there. Keep typing; the message clears when you do.

Some things are accepted with a warning, such as a node kind the diagram type does not know. They are applied, and listed
under the text with their line numbers.

### When the diagram changes while you are typing

If someone else (or an AI agent, or you on the canvas) changes the diagram while you have unapplied text, the pane never
overwrites your typing. It shows **Diagram changed. Your unapplied text is kept here.** with two choices:

- **Reload text** drops your edits and shows the diagram as it is now.
- **Keep mine** keeps your edits. When you then press Apply, the pane folds your changes into the diagram as it is now: what
  you changed lands, and what they changed stays. If you both changed the same thing (the same node's label, say), the
  diagram's version stays, the pane lists what clashed, and **Use my text for these** takes yours instead. Both are single
  undo steps.

With no unapplied text, the pane simply follows the diagram.

### Mermaid

The **Mermaid** button shows the diagram as [Mermaid](https://mermaid.js.org/) text, the same text as **File > Export
Mermaid**. It is read-only and for copying: Mermaid cannot carry positions, ports or properties, so text typed there could
not be applied faithfully. Where Mermaid has no spelling for something in your diagram it is simplified, and the pane lists
what. Your unapplied DSL is kept while you look at Mermaid.

### Text and canvas together

Select a node or connector on the canvas and its line in the text is marked (the text scrolls to it if it is out of view).
Click a line in the text and its node or connector is selected on the canvas.

### Sequence diagrams

A sequence diagram's order is the order of its lines: the nodes are the participants, left to right, and the edges are the
messages, top to bottom. Edit and apply in the Text pane as for any other diagram; to add a message between two others, put
its line between theirs.

### Who can use it

Anyone who can edit the diagram. Where you can look but not edit, the text is shown and can be copied, and there is no Apply.

## The Documents page

The page you land on after signing in lists your documents. By default it shows them as a **grid of cards**, each with a small
picture of the diagram, its type, its title, when it was last changed and a menu of the actions (Rename, Duplicate, Delete). Press
**List** beside the search box for the compact table instead, and **Grid** to come back; the page remembers your choice in this
browser. Sort the grid from the **Sort by** menu (the table sorts on its column headers); the order is the same in both. Clicking
anywhere on a card opens the diagram, and the keyboard goes to the title, then the actions menu, as it does along a table row.

The pictures are drawn by the server from the diagram itself, in the organisation's default diagram theme, so they match what an
export looks like. They need no browser or extra component, nothing about them leaves the server, and they work on an instance
with no internet. A picture is drawn the first time it is wanted and kept, so a page of fifty diagrams is quick on every visit
after the first, and it follows the diagram: edit and save, and the card shows the new picture the next time the page loads.

A very large diagram is drawn as plain boxes and lines with no text, and an enormous one as a grey placeholder, so one huge
document cannot slow the page down; an empty document shows a dashed empty frame. If a picture cannot be had (the server is busy, or
the layout engine is not running), the card shows the diagram type's symbol instead and tries again on the next visit. The size limits
are in [the API reference](api.md#get-apidocumentsidthumbnail).
