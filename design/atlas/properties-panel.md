# Properties panel

## Screen
The persistent right panel that edits whatever is selected: node, edge, container, several things, or nothing (document). Key pattern questions: contextual sections; colour picker; text controls; editing kind and free props; showing AI provenance; how it behaves with a mixed or empty selection.

## Mobbin queries
- `search_screens "right side design panel showing fill colour, stroke, opacity and text alignment controls for a selected shape"`
- `search_screens "colour picker popover with swatch grid, hex input and opacity slider"`
- `search_screens "Linear issue detail view with dense right sidebar of properties and command menu"`
- `search_screens "diagram editor canvas with top toolbar, left shape library panel and right properties panel"`

Coverage note: Lucidchart and Whimsical are not on Mobbin and are cited from the products.

## References
1. Figma — Design panel, embed selected — https://mobbin.com/screens/b62f7a88-50de-4ef0-bd2f-a238d83dd36a — sections in a fixed order (Position, Layout, then appearance); numeric fields with unit labels; alignment row at the top; Design and Prototype as tabs.
2. Framer — Style panel, ellipse selected — https://mobbin.com/screens/5ebc7e84-878f-4f37-9ba8-2299af67712f — Shape, Styles, Stroke as collapsible sections; every row is label left, control right; "+" adds an optional property such as Fill.
3. Rive — Right panel with Stroke Options popover — https://mobbin.com/screens/6b2e483d-0818-478f-beed-b17550b4b0df — collapsed section headers with a "+" to add Fills or Strokes; rarely used options live in a popover, not the panel.
4. Miro — Floating format bar with colour popover — https://mobbin.com/screens/2b962fa0-25a9-4b9f-8dd0-d2ddf51e117b — a swatch grid of about twenty colours, thickness and opacity sliders; the whiteboard alternative to a panel.
5. Slite (Excalidraw) — Floating properties card — https://mobbin.com/screens/540454fe-e07a-474a-b7b7-cb0d672d41cf — stroke and background swatches with a hex field, then segmented controls for fill, width, style, edges, opacity, layers and actions. Compact and entirely segmented.
6. Linear — Issue detail with properties sidebar — https://mobbin.com/screens/62c94bcd-0559-4356-9d0e-bce71c021d88 — Properties, Labels and Project as small grouped sections of key-value rows; each row is a button that opens a picker. The dense non-diagramming reference for props editing.

Not on Mobbin (cited from the product, unverified against the library):
- Lucidchart — Right panels — collapsible panels for shape properties, custom data (key-value) and layers.
- Whimsical — Floating bar — a short row of colour, text and connector style controls above the selection, with no numeric geometry.

## What the references agree on
- Panels are a stack of collapsible sections with a fixed order; the first section is what the user edits most.
- Rows are label on the left, control on the right; numeric fields are small and accept typed values with units.
- Colour is a swatch that opens a popover: theme or document colours first, then a picker and a hex field. Opacity is a slider next to the swatch.
- Rarely used options go in a popover from a section header, not in the panel body.
- Segmented controls beat dropdowns for two-to-four option choices (fill style, stroke width, alignment).
- Key-value properties (Linear, Lucidchart custom data) are shown as rows with a picker per row and an "add" affordance at the bottom.
- With nothing selected, the panel shows document or page settings, not an empty pane (Webflow says "Make a selection"; Figma shows page background).

## What Draughtsman does
- **Persistent right panel, 280px, collapsible** (`Cmd/Ctrl+.`), two tabs: Properties and Text (DSL). Properties is described here; Text is the document's DSL, two-way with explicit Apply (decision D5 in `editor-shell.md`).
- **Sections by selection type, in this order.**
  - Node: **Node** (label, kind picker from the document's vocabulary, id shown read-only in monospace), **Geometry** (x, y, w, h, rotation as numeric fields; lock aspect), **Style** (fill, stroke, stroke width, dash, text colour, font; every control shows "Theme" until overridden and has a reset-to-theme action), **Properties** (free key-value rows from `props`, add and remove), **Provenance** (present only when `aiProvenance` exists: agent or model and time, with a "View checkpoint" link).
  - Edge: **Connector** (label, kind, from and to shown read-only), **Routing** (orthogonal or straight segmented control), **Style** (stroke, width, dash, arrowheads at each end), **Properties**, **Provenance**.
  - Container: as Node plus a **Children** count and "Fit to contents" button, with Group (`Cmd/Ctrl+G`) and Ungroup (`Cmd/Ctrl+Shift+G`) beside it so both are reachable without their shortcuts.
  - Multiple: **Arrange** (align six ways, distribute two ways, order), then the Style section applying to all, with mixed values shown as a dash.
  - Nothing: **Document** (title, type read-only, diagram theme picker, grid spacing from theme shown read-only) and **Layout** (Tidy with algorithm shown, direction).
- **Kind is the first control after the label** because switching kind restyles the node from the theme and changes edge arrowheads (M2 checklist item 12). It is a picker listing the vocabulary with the kind's glyph; unknown kinds already on the node are kept and flagged with a lint badge.
- **Colour picker** is a popover: the diagram theme palette first (named tokens), then document colours in use, then a hue and saturation picker with a hex field and an opacity slider. Choosing a theme colour writes nothing to the node; choosing anything else writes `style`.
- **Style controls show inheritance.** An unset value renders the theme value greyed with the word Theme; a set value renders normally with a small reset icon. This is how the "documents store only deviations" rule becomes visible.
- **Numeric fields** commit on Enter or blur, support arrow keys (Shift for 10), and accept expressions like `+20`.
- **Text controls** are only font family, size, colour and alignment. Rich text inside nodes is not in the MVP.
- Every panel action is also a command in the palette ("Set kind to database", "Align left"), and align, distribute and order are also in the Arrange menu and the context menu (checklist item 28).

## Keyboard and accessibility
- `Cmd/Ctrl+.` toggles the panel; `F6` reaches it; `Tab` moves through rows, arrow keys move within segmented controls; section headers are buttons with `aria-expanded`.
- Colour swatches carry the colour name or hex in `aria-label`; the popover traps focus and closes with Escape, returning focus to the swatch.
- Mixed values in a multi-selection are announced as "Mixed", not left blank.
- Kind and props changes are single undo steps; typing in a numeric field is one step per commit, not per keystroke.
- Contrast of greyed "Theme" placeholders still meets 4.5:1 against the panel surface token; inheritance is shown by the word as well as the colour.

## What Draughtsman deliberately won't do
- **No floating properties bar or card over the canvas** (Miro, Mural, Whimsical, Slite) in the MVP. See `editor-shell.md`.
- **No "sloppiness", hand-drawn or sketch styles** (Slite, Craft). Structured diagrams only.
- **No layers section** (Figma, Lucidchart). Order is bring forward and send back; layers are phase 2.
- **No shadows, blur, blend modes, effects** (Magnific, Rive). Not part of the style model.
- **No "Change all" bulk recolour from the picker** (Canva). Switching the diagram theme is the supported bulk restyle and is one click in the Document section.
- **No hex values as the first thing in the picker.** Theme tokens come first so people stay on theme.

## Open questions
- Should `props` support typed values (number, url, list) in the MVP, or plain strings only? Default for the build: strings, with url values rendered as links.


## Amendment 2026-10-02: the Text pane, beside the canvas

Decision D5 made Text two-way with explicit Apply; this settles how it behaves around the people and agents who edit the same diagram, and how it is reached. The user-facing description is `docs/editor.md`. No Mobbin round was run: the pattern is the code-beside-canvas split that Mermaid Live and every diagram-as-code tool uses, and the controls are the editor's own (the segmented control from "What the references agree on", a `</>` icon button in the top bar beside Save).

- **Reached three ways, one state.** The `</>` button in the top bar (`aria-pressed`, `aria-controls="viewer-panel"`), View > Toggle Text (DSL) and the panel's Text tab all set the panel view. Whether the pane is open, and whether it shows DSL or Mermaid, is remembered per viewer in `localStorage` (`editor/textPanePrefs.ts`, every access guarded; a blocked store means "starts closed, on DSL"). A pane restored open at load does not take keyboard focus from the canvas; one the person opens does, the first time.
- **DSL is the editable view; Mermaid is read only.** Mermaid cannot carry positions, ports or props, so typed Mermaid could not be applied faithfully (Mermaid import stays its own dialog). The Mermaid view is `POST /api/export/mermaid` of the diagram as it is now, lists what Mermaid simplified, and never contains unapplied DSL. Both views stay mounted, so a draft survives a look at the other.
- **A line gutter, because every message names a line.** The textarea never wraps (`wrap=off`), so line numbers and text lines share one grid (`--dsl-line`, `--dsl-pad`, set from `DslPanel.tsx`). A parse error is `Line N, column M: ...` and a warning `Line N: ...`; both are buttons that put the caret on that line, and the line is marked. Nothing is applied while there is an error. Warnings number the text as typed, so the typed text is left alone after Apply (not reprinted) until the diagram becomes something else.
- **Apply is Ctrl/Cmd+Enter or the button,** and is one undo step in every case.
- **Unapplied text is never overwritten.** When the diagram changes under a dirty draft the pane shows a non-modal notice, "Diagram changed. Your unapplied text is kept here.", with Reload text (drop the draft) and Keep mine. After Keep mine, Apply is a three-way merge (`model/textMerge.ts`, built on `remoteMerge.ts` with the roles turned round): what the text changed lands, what the diagram changed stays, and a field both changed keeps the diagram's value and is listed, with Use my text for these as a second undo step. Without Keep mine, Apply is the exact diff (`applyDocumentDiff`) and refuses a diagram that has moved on.
- **Selection and lines.** The canvas selection marks the line of each selected node or edge (found by reading `node|container|edge <id>` from the text itself, so it works on unapplied text), scrolling the first into view; a click on a line selects its element when the diagram has it. The mark is a tint plus a bar at the left edge, so it does not rely on colour.
- **Read only.** `readOnly` shows and copies the text with no Apply or Reset. Today only a preview (no document id) is read only, and a preview has no right panel, so the prop is the seam for a viewer role, not something a person can reach yet.
- **Sequence diagrams** use the same pane: order of `edges[]` is time, so a message line typed between two others lands between them, also when a collaborator inserted another meanwhile (`remoteMerge`'s `placeInserted`).

Not built: syntax highlighting beyond the line marks, completion, applying Mermaid text, a line-level diff view of what Apply will change.

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)
