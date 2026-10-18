# Editor shell

## Screen
The frame around the canvas: top bar, tool rail, shape panel, properties panel, zoom controls and the command palette. Every other editor screen sits inside it. Key pattern questions: toolbar placement; left shape panel versus floating palette; right properties panel versus floating contextual toolbar; zoom controls; where the menu lives so every canvas action is reachable without the mouse; how the DSL text view fits (the model is the truth, the picture is a render).

## Mobbin queries
- `search_screens "diagram editor canvas with top toolbar, left shape library panel and right properties panel"`
- `search_screens "Miro board editor with left vertical toolbar and canvas zoom controls"`
- `search_screens "FigJam whiteboard editor with bottom floating toolbar and zoom controls"`
- `search_screens "Whimsical flowchart editor canvas with shape toolbar"`
- `search_screens "Lucidchart diagram editor with shape library and canvas"` (also `search_flows "Lucidchart creating a flowchart diagram"`)
- `search_screens "Linear command menu overlay opened with keyboard shortcut listing actions and their shortcuts"`

Coverage note (2026-09-21): Mobbin's web library holds no Lucidchart, Whimsical or FigJam screens. Deep, standard and flow searches naming each app returned other products. Miro, Figma (design editor), Linear and Notion are present. Lucidchart and Whimsical below are cited from the products themselves and marked as such; the Mobbin substitutes are the closest shipped diagram editors in the library.

## References
1. Miro — Board editor, Diagramming panel open with a shape selected — https://mobbin.com/screens/2b962fa0-25a9-4b9f-8dd0-d2ddf51e117b — left vertical tool rail, shape panel docked beside it with search and collapsible categories, floating contextual toolbar above the selection, zoom cluster bottom right (fit, −, %, +).
2. Figma — Design editor, embed selected — https://mobbin.com/screens/b62f7a88-50de-4ef0-bd2f-a238d83dd36a — persistent right Design panel with Position, Alignment, Rotation and Layout sections; bottom floating tool strip; layers on the left; zoom percentage top right.
3. Mural — Shapes and connectors panel — https://mobbin.com/screens/ee365d61-c367-4e4e-a2ae-c0d9850bfd46 — dark left rail; a panel that groups connectors above shapes; zoom cluster bottom right with a fit button.
4. Fibery — Whiteboard with shapes popover — https://mobbin.com/screens/b18920c5-581d-4dfb-941c-44c2191e1ad5 — bottom floating tool strip with a shapes popover above it; zoom top right; Fit to screen and Fullscreen in the header. Shows the cost of a bottom strip: the popover covers the canvas.
5. Slite (Excalidraw) — Whiteboard with flowchart and context menu — https://mobbin.com/screens/a5afd333-f825-457e-8391-5f52b4dbd49e — top centre tool strip with number shortcuts on each tool, left floating properties card, right-click menu that lists every action with its shortcut, zoom and undo bottom left.
6. Linear — Onboarding, "Meet the command menu" — https://mobbin.com/screens/1e783aca-8b8e-4ae5-994d-284a8ddae491 — Cmd+K taught as the primary way to do anything; the dense SaaS reference for the palette and for compact chrome.

Not on Mobbin (cited from the product, unverified against the library):
- Lucidchart — Editor — left shape library with search and pinned libraries, top formatting toolbar, right-hand collapsible panels, zoom bottom right, page tabs at the bottom.
- Whimsical — Flowchart editor — left vertical tool rail, floating contextual toolbar above the selection, quick-add arrows on hover, zoom bottom right.

## What the references agree on
- One slim top bar holds document identity (back, title, saved state) at the left and share, export and account at the right. Nobody puts tools in it.
- The primary tools live in a single strip: left vertical (Miro, Mural, Whimsical, Lucidchart) or bottom floating (Figma, Fibery, Craft, Confluence). Left rails pair with a docked shape panel; bottom strips use popovers that cover the canvas.
- Shape libraries dock beside the tool rail and open on demand. They have a search field and collapsible categories.
- Properties are either a persistent right panel (Figma, Rive, Framer, Webflow, Jitter, Lucidchart) or a floating bar above the selection (Miro, Mural, Whimsical, Fibery, Canva). Design tools with many properties use the panel; whiteboards with few use the bar.
- Zoom controls sit bottom right as a compact cluster: fit, minus, percentage, plus. Percentage is a button that opens presets.
- Undo and redo are always visible, in the tool rail or the top bar.
- Every editor with a rich action set exposes a right-click menu that repeats shortcuts (Slite, Mural, Linear), and dense SaaS tools make Cmd/Ctrl+K the front door (Linear, Notion's slash menu).
- Tools show their shortcut letter or number on hover or in the icon (Slite, ClickUp).

## What Draughtsman does
- **Layout: four regions.** Top bar (compact 40px), left tool rail (40px) with a docked shape panel beside it (240px, collapsible), canvas, right properties panel (280px, collapsible). Zoom cluster floats bottom right of the canvas. No bottom bar in the MVP; page tabs are phase 2.
- **Top bar, left to right:** menu button (Lucide `menu`) opening File, Edit, View, Arrange and Help groups; back to documents; document title (click to rename); saved state text ("Saved", "Saving", "Offline"). Right: Tidy (ELK layout), AI panel toggle, Export, Share (phase 2, hidden until then), account.
- **The menu button is how every canvas action is reachable without a mouse gesture** (M2 checklist item 28). Each item shows its shortcut. The same items appear in the right-click context menu and in the command palette.
- **Left tool rail, top to bottom:** Select (V), Pan (H), Shape (S, opens the shape panel), Connector (C), Text (T), Container (F), Sticky note (N), Image (I). Undo and redo at the bottom of the rail. Active tool is filled with the accent token; every icon has a tooltip with name and shortcut.
- **Shape panel** docks between the rail and the canvas, opened by the Shape tool or `Cmd/Ctrl+Shift+S`, and closes with Escape. Detail in `shape-connector-palette.md`.
- **Right properties panel is the single place to edit selection properties.** It is persistent (not floating) because architecture nodes carry a kind, props and provenance as well as style, and that does not fit a floating bar. Two tabs: **Properties** and **Text**. Text shows the live document’s DSL with Copy and explicit Apply controls. Maintainer’s decision D5 (2026-09-24): ship read-only first, then make Text two-way as the last M2 editor task; apply a parsed diff in `PatchApplier.cs`'s op vocabulary (plus browser-side order, header and meta ops) through local Y.Doc operations as one undo step, preserving surviving selections. Parse errors leave the canvas unchanged. Detail in `properties-panel.md`.
- **Zoom cluster** bottom right: fit (`Cmd/Ctrl+0`), zoom out (`Cmd/Ctrl+-`), percentage button (`Cmd/Ctrl+1` for 100%; click opens 50, 100, 200, fit, fit selection), zoom in (`Cmd/Ctrl+=`). Grid toggle (`Cmd/Ctrl+'`) sits in the same cluster.
- **Nodes awaiting layout** appear with dashed outlines in a provisional stack below positioned nodes, or at the origin if none are positioned. Their attached edges and provisional bounds participate in rendering and Fit to screen. Each unplaced node emits a browser-console warning naming its id. If either a node or its parent lacks geometry, that node renders at the top level to avoid container clipping. These positions and grouping adjustments are display-only; saved geometry and parent relationships stay unchanged.
- **Panels collapse independently:** `Cmd/Ctrl+\` toggles the left rail and shape panel, `Cmd/Ctrl+.` toggles the properties panel, `Cmd/Ctrl+Shift+F` hides all chrome for a clean view. Panel state persists per user.
- **Command palette** (`Cmd/Ctrl+K`) lists every action in the menus, tool switches, "Go to node" by label or id, and recent documents. Each row shows its shortcut. Executing from the palette has the same effect as the shortcut (checklist item 23).
- **Selection feedback** on the canvas is handles and smart guides only. No floating contextual toolbar in the MVP; the properties panel and the context menu cover it. Revisit after the M4 month of daily use if the panel round-trip hurts.
- **Density:** compact by default (28px controls, 12px labels in the chrome); comfortable adds 4px to control height and 2px to type. The canvas is unaffected.
- **Icons:** Lucide only, one stroke width, 16px in the chrome and 20px in the rail.

During a node drag, moving roots render above the normal drawing outside their former container clips; children of a moving container keep its clip. The container a dragged node would drop into on release is outlined. This is view state only. Ungroup refuses a container with connections of its own rather than delete them, still ungrouping the other selected containers, and names the refused ones in a brief `role="status"` notice in the top bar. Id-less previews allow navigation but no selection or editing. Save is disabled while a request is in flight.

Another writer's change to the open document (an agent's `patch_document`, another tab's save) appears on the canvas without a reload (M2 checklist item 30) and is never undone by the local user's undo. When it merged without touching anything the user had edited, it is announced in the same brief `role="status"` top-bar notice ("Changes from agent:… are now shown."). When it clashed with an unsaved local edit, the local version stays on the canvas and a `role="status"` band under the top bar, not an alert or a modal because editing continues, names each clash and says the user's version is shown and saving keeps it, with "Keep my version" and "Use their version"; taking theirs is one undo step. Nothing is ever dropped silently.

## Keyboard and accessibility
- Every rail tool, menu item and zoom action has a shortcut, and every shortcut appears in the command palette. Single-letter tool shortcuts are suppressed while a text field or label editor has focus.
- `F6` cycles focus between top bar, tool rail, shape panel, canvas and properties panel; `Escape` returns focus to the canvas from any panel. Arrow keys move within a rail or panel; `Tab` moves between controls inside a panel.
- On the canvas, `Tab` and `Shift+Tab` walk nodes in document order and announce the node label and kind through an `aria-live` region; `Enter` edits the label; arrow keys nudge (checklist items 20 and 21).
- Focus rings use the focus token and are never removed. The active tool has `aria-pressed`; collapsed panels use `aria-expanded` on their toggles.
- Contrast for chrome text and icons is checked against WCAG AA when the theme tokens are compiled; warnings are printed and never fail the build (themes/README.md).
- Motion: panel open and close is an instant state change or at most a 120ms opacity fade honouring `prefers-reduced-motion`. The canvas animates nothing but snap feedback.
- Copy in the chrome is plain British English: "Tidy layout", "Fit to screen", "Saved", never jokes.

## What Draughtsman deliberately won't do
- **No bottom floating tool strip** (Figma, FigJam, Fibery, Craft, Confluence). It overlaps the canvas, fights the zoom cluster on small screens, and its popovers hide the work. A left rail pairs with a docked shape panel and stays keyboard-reachable.
- **No floating contextual toolbar above the selection** (Miro, Mural, Whimsical, Canva) in the MVP. It duplicates the properties panel, moves with the selection so it cannot be learned by position, and hides connectors behind it.
- **No presence avatars, Present, timers, or reactions in the top bar** (Miro, Mural). Single editor in the MVP; collaboration chrome arrives with realtime in phase 2.
- **No tutorial overlays or "spread to zoom" coaching cards** (Miro). First-run guidance is in the empty document state, not a modal on the canvas.
- **No upgrade, trial or marketplace prompts in the shell** (Miro, ClickUp, Canva). The shell never sells.
- **No mixed icon sets or hand-drawn styling** (Excalidraw-based Slite and Craft). Structured diagrams only.
- **No hidden shortcuts.** If an action has no menu entry, it does not ship.

## Open questions
- Resolved 2026-09-24 (maintainer, D5): Text is two-way in M2, sequenced last after the read-only view; see the panel decision above.
- Lucidchart and Whimsical are absent from Mobbin. Do the maintainers want them captured another way (own screenshots in `design/atlas/refs/`), or is citing the products directly acceptable for the atlas rule?

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)

## Amendment 2026-10-01 (final usability QA: S3, S5, P13)

- Under 930 px the right panel is a sheet over the canvas (320 px at most), closed to begin with and opened by the bar's **Panel** button or by choosing a panel view; the bar wraps instead of clipping. The panel is a labelled region, not a complementary landmark, because the shell's `<main>` contains the whole editor.
- The keyboard focus ring is the theme's text colour (`--chrome-focus-ring`), never the red used for errors.

## Amendment 2026-10-01: final usability QA (editor and drawing)
Fixes from the fresh-eyes QA (`data/final-usability-qa/report.md`: S1, S2, S5, S7, P1 to P11). No new screen and no new visual pattern; each item reuses an existing control, so no new Mobbin queries (the Lucidchart, Whimsical and FigJam gap in `README.md` stands).
- **Connectors (S1).** A press or release on a shape's body picks the port facing the other end (the closest pair of ports, `canvas/ports.ts` `pairPorts`); only a press or release inside a port's ring (`PORT_HIT_RADIUS`) pins that port. A re-attached end faces the end that stays put. **Connect selected shapes (S5)**: `L`, the palette and Edit menu join the selection in selection order, one undo step, on facing ports; a pair already joined in that direction is left alone.
- **Label editing (P1).** While a label is edited a one-line `role="note"` at the foot of the canvas says how it ends: "Enter adds a line. Cmd+Enter or a click away finishes; Esc cancels." (a connector's one-line label: "Enter to finish, Esc to cancel."). Screen space, so it is the same size at every zoom.
- **Shortcut hints (P6).** The registry keeps `Cmd/Ctrl+Z`; what is drawn (palette, menu, shortcut sheet, tooltips) is `Cmd` and `Option` on a Mac, `Ctrl` and `Alt` elsewhere (`editor/platform.ts`). Edit attributes / operations / columns are in the registry only while a class or table is in the document.
- **Icons (P2).** Palette icons are placed 120 x 96 and their default ports sit on the drawn glyph (`model/icons.ts` `iconPortFractions`), not the invisible box.
- **Sequence palette (P3).** A participant dragged from the palette lands in the lifeline slot of the drop's x (`addParticipant`); a click still appends at the right-hand end. Existing lifelines never move when one is added; lifelines to the right of a renamed one shift as its header widens (the scene is derived).
- **Swimlanes (P4).** A pool from the palette is 640 x 320 with two lanes ("Lane 1", "Lane 2") in one undo step.
- **New diagram (P5).** The menu gains a "More diagram types" group (ER diagram, UML class diagram, BPMN process, Swimlane diagram, Org chart), also on the empty home screen and as `New ...` palette and File actions. Each is an ordinary flowchart or architecture document that opens with its shapes placed (`app/diagramTemplates.ts`).
- **Properties and text (P7).** Style swatches are named ("Red deep") with the hex in the tooltip and the in-use name beside the hex field; Font size shows the size in use. The Text tab widens the right panel to 480px (45vw at most). Applying DSL that names another id keeps the diagram's id and lists a note.
- **Export (P8).** File > Export Mermaid saves `<title>.mmd`; a lossy export opens the Export Mermaid dialog listing what was simplified (`mermaid-export.md`). Copy as Mermaid is the clipboard action and Export DSL saves `<title>.dsl`.
- **AI, tour, history (P9 to P11).** To document shows a citation as the node's label (the id stays in the click and the prompt keeps ids inside the brackets). Every tour travel time is "Arrive (s)"; the first keyframe's says it only applies when the tour loops. A restore checkpoint's reason shows its time in local time.

