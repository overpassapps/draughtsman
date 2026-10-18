# Shape and connector palette

## Screen
The panel that docks beside the left tool rail and offers the ~15 core shapes, containers, text, image, sticky note and the connector kinds. Key pattern questions: search; recents; categories; drag versus click-to-place; how kind (semantics) and shape (presentation) are both offered without confusing the user.

## Mobbin queries
- `search_screens "shape library side panel with search field, collapsible categories and recently used shapes"`
- `search_flows "adding a shape from the shape library and connecting it to another shape with a connector"`
- `search_screens "Miro board editor with left vertical toolbar and canvas zoom controls"` (Diagramming panel)
- `search_screens "Whimsical flowchart editor canvas with shape toolbar"`

Coverage note: as in `editor-shell.md`, Lucidchart and Whimsical are not on Mobbin and are cited from the products.

## References
1. Miro — Diagramming panel — https://mobbin.com/screens/2b962fa0-25a9-4b9f-8dd0-d2ddf51e117b — search field at the top, "My Shapes" first, then Basic Shapes and Flowchart as collapsible groups, a colour row per group, "Manage shapes" at the bottom. Shapes are outlined glyphs in a dense grid.
2. Mural — Shapes and connectors panel, flow "Adding shape and connector" — https://mobbin.com/flows/ae4ce805-6cc8-46b8-8717-d4e2daf49a0b — connectors are listed above shapes with a toggle for connector points; clicking a shape places it at the canvas centre, then a floating bar appears.
3. Fibery — Whiteboard shapes popover — https://mobbin.com/screens/b18920c5-581d-4dfb-941c-44c2191e1ad5 — Basic shapes and Flowchart in a compact two-row popover; three connector styles in a mini popover above the line tool.
4. Canva — Elements panel, flow "Adding shapes" — https://mobbin.com/flows/fcb2ac6f-95c5-41ed-925b-5fca1cf96270 — search, quick filter chips, "Recently used" row first, then categories with "See all"; hover arrows on a placed shape add a connected shape in one click.
5. Figma — Design editor tool strip — https://mobbin.com/screens/b62f7a88-50de-4ef0-bd2f-a238d83dd36a — shapes live under one tool with a dropdown of variants, not a panel; the dense non-diagramming pattern for a small shape set.
6. Pitch — Shape insert menu — https://mobbin.com/screens/8d15f1e8-2498-498b-acd8-e4cf8a1bff2a — categories as a left list (Essential, Lines, Buttons and labels, Process) with filled and outlined variants side by side.

Not on Mobbin (cited from the product, unverified against the library):
- Lucidchart — Shape library — search across libraries, pinned libraries in collapsible sections, drag to place, "More shapes" manager.
- Whimsical — Shape tool — a short list of shapes per tool; hover arrows on a node create a connected node.

## What the references agree on
- Search at the top, recents first, then collapsible categories.
- Shapes are shown as small outlined glyphs in a grid, six to eight per row. References name them on hover only; the audit (QA-05) found that on a 240px panel with three glyphs per row this leaves a new user guessing, so Draughtsman departs from the references here.
- Both drag-to-place and click-to-place are supported; click places at the viewport centre or at the last click.
- Connectors are a first-class group, separate from shapes, with two to three routing styles.
- Whiteboards add "quick-add" arrows on a selected node to create a connected neighbour in one gesture (Canva, Whimsical, Miro).
- The panel never scrolls the canvas; it has its own scroll region.

## What Draughtsman does
- **Panel docks beside the tool rail**, 240px wide, opened by the Shape tool (S) or `Cmd/Ctrl+Shift+S`, closed by Escape or the tool switching. Its own scroll region.
- **Sections in order:** search field (focused when the panel opens by keyboard); Recent (last eight, per user); then one group per kind vocabulary of the current document type, loaded from `KindVocabulary`: for `architecture` that is Service, Database, Queue, Actor, Note, Container; for `flowchart` Process, Decision, Terminal, Data / IO, Note, Container, plus the standard flowchart presentations Document, Predefined process, Manual input and Off-page connector (each an existing kind with an explicit `shape`). A final group, Shapes, offers the presentation shapes (rectangle, rounded, diamond, cylinder, pill, hexagon, image) for `freeform` or for overriding a kind's default shape.
- **Kind first, shape second.** Placing "Database" creates a node with `kind: database` and the theme's default shape for it. The user thinks in meaning; the theme decides the glyph. The Shapes group exists so a kind can still be drawn differently, and that writes `shape` on the node.
- **Connectors group** sits above the kind groups: Flow, Data, Dependency, Association, each with the edge kind's default arrowheads drawn in the glyph. Routing (orthogonal or straight) is a property, not a palette entry.
- **Drag and click both work.** Drag drops at the pointer with snap; click places at the viewport centre offset by one grid step from the last placed node and selects it, so repeated clicks stack neatly. Enter on a focused glyph does the same as click.
- **Quick-add arrows** on a selected node (four sides) create a connected node of the same kind and an edge of the document's default edge kind. This is the single most-used gesture in Whimsical and Canva and it maps cleanly onto the model.
- **Every glyph carries a visible name** (QA-05, amends the earlier "names on hover"): a caption under the drawing, 11px in the muted text token, at most two lines then an ellipsis, in a cell that grows to about 68px; the grid stays three columns so the arrow-key grid logic is unchanged. Hover still shows the full name and `aria-label` carries it.
- **Basic shapes group** (amendment 2026-10-12): the shapes draw.io, Lucidchart and Visio keep in their basic palettes (ellipse, triangle, trapezoid, chevron, cross, two block arrows, folded note, callout, brace, cube, folder, component), for every document type but sequence, after Shapes and closed by default like the other library groups. Each glyph is the shape drawn by the canvas's own renderer at the size it will land, placed on a kind the document type already has (`process`, `service`, `shape`; `note` for the three annotation marks), so no new kind enters a vocabulary. Search also matches the names people use for them (`circle`, `use case`, `package`, `speech bubble`, `curly bracket`). Reference points: draw.io's General and UML palettes, Lucidchart's Shape Library, Visio's Basic Shapes stencil; the Mobbin coverage gap in `README.md` applies, so this is from the tools themselves.
- **Custom libraries** (SVG upload, "Manage shapes") are phase 2; the panel shows no entry for them.

## Keyboard and accessibility
- `S` opens the panel and focuses search; typing filters across kinds and shapes; arrow keys move through the grid; `Enter` places, `Escape` closes and returns focus to the canvas.
- Each group header is a button with `aria-expanded`; collapsed state persists per user.
- Every entry is reachable from the command palette as "Add <kind>" so no one needs the panel to draw.
- Glyph contrast is checked against the panel surface token; the accent fill on hover and focus is never the only indicator, a focus ring is drawn too.
- No motion on open beyond an optional 120ms fade; none when reduced motion is set.

## What Draughtsman deliberately won't do
- **No per-group colour rows or "apply colours" toggles in the palette** (Miro). Colour comes from the diagram theme by kind; documents store only deviations.
- **No filled/outlined duplicates of every shape** (Pitch, GoDaddy). Fill is a style property, not a separate shape.
- **No marketplace, stickers, photos or icon store tabs** (Canva, Semrush, GoDaddy). Cloud provider icon libraries are phase 2 and will be a kind vocabulary, not a sticker sheet.
- **No popover palette over the canvas** (Fibery). It covers the work and cannot hold search and groups.
- **No "connector points" on/off toggle** (Mural). Ports are always on for the connector tool; in the select tool a hovered or lone selected node shows connection handles set out from its ports (Lucidchart, draw.io), and they are hidden otherwise. The gesture contract is the `web/src/canvas/DiagramCanvas.tsx` module comment.

## Open questions
- Should "Sticky note" be in the palette at all for `architecture` documents, or only for `freeform`? Default for the build: everywhere, as the `note` kind, because reviewers use it.

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)
