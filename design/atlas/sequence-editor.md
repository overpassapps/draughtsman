# Sequence diagram editor

## Screen
The canvas and its palette, connector tool, connector panel and Arrange commands, when the open document is of type `sequence` (`docs/sequence-diagrams.md`). Participants are the nodes and messages the edges; the order of `edges[]` is time and the order of `nodes[]` is left to right, and every position is derived by `web/src/model/sequenceScene.ts` on each render, never stored. Key pattern questions: how do you add a lifeline; how do you add a message *at a point in time* when the user has no y coordinate to give; how do you reorder either axis; which of the free-form editor's gestures must be switched off because nothing is stored to write.

## Mobbin queries
- `search_screens "diagram editor canvas where a connector is dragged between two vertical lifelines to add a message, with insertion line"` (web, standard mode, 2026-09-29): six results (Klaviyo, Shopify, fal, Higgsfield, MagicPath, Tana), none a sequence-diagram editor. They are node-graph and whiteboard editors and add nothing about lifelines or time slots.
- `search_flows` for the same journey was not run: the screens search returned no sequence editor, and the atlas README records that Mobbin holds no Lucidchart, Whimsical or FigJam screens either.

## References
No Mobbin reference exists for this screen, so none is invented. What the design follows, from the products themselves (not on Mobbin; from the product, unverified against the library):
1. Lucidchart — sequence diagram shape library and auto-layout: participants along the top, messages drawn by dragging from one lifeline to another; the message snaps to the nearest row and the rows below shift down.
2. Mermaid Live Editor and PlantUML — the text form: messages are lines in a file, so *file order is time*; this is the model Draughtsman's DSL already has.
3. Figma / FigJam (design editor, auto layout) — dragging within an auto-layout list shows an insertion line between siblings and commits on release; the pattern for reordering derived positions.
4. Miro — connector tool: press on a shape, drag to another, release; the tool stays armed only while chosen. The gesture contract is `web/src/canvas/DiagramCanvas.tsx`'s module comment.
5. Linear — a dense list where the row you drag shows one insertion line and the rest hold still; the model for the vertical reorder of messages.

## What the references agree on
- A message is made by dragging between two lifelines; the release point, not a typed number, says where in time it lands.
- Rows below an insertion shift down; nothing the user placed by hand is disturbed, because nothing was placed by hand.
- A reorder shows one insertion line while dragging and writes once on release.
- Labels sit above the message line and are edited in place.

## What Draughtsman does
- **Derived-geometry mode.** One capability flag, `derivedGeometry` (`web/src/model/typeCapabilities.ts`, true only for `sequence`), read by the canvas from the document's type. While it is set, resize handles, the rotate handle, free move, snapping, connection handles from ports, segment and waypoint handles and drags, end re-attach and curved routing do not exist: they are not drawn and a press on where they would be does nothing. Nudge arrows, group, align, distribute, Tidy and the routing controls are disabled. The Yjs writers for geometry and routing (`setNodeGeometries`, `setEdgeWaypoints`, `setEdgeRoutingMode`, `clearEdgeWaypoints`) also refuse a derived-geometry document, so a stray call cannot store a position the scene would ignore.
- **Add a participant.** The palette has one group, **Sequence**, holding Participant and Actor (no Shapes group, no other vocabulary). A click appends the lifeline at the right end (last in `nodes[]`), selects it and opens its label editor. Dragging a glyph onto the canvas is not offered for this type: the drop point has no meaning. Menu and palette: Add participant, Add actor.
- **Add a message.** Connector tool (`C`): press on a lifeline or header, drag to another lifeline (or the same one for a self message), release. The release **y** picks the slot: the message is inserted before the first message whose row midpoint is below the release point, or at the end. While dragging, a horizontal insertion line shows the slot and the preview runs along it. The new message is a `sync` message, is selected, and its label editor opens above its line. Only the `order` fields that changed are written, in one undo step. A release off any lifeline, or Escape, creates nothing.
- **Change the message type.** The existing Connector section's Kind picker, listing `sync`, `async`, `return`. The Routing picker and Reset route are removed for this type.
- **Reorder messages.** In the select tool press on a message and drag vertically past the drag threshold; the insertion line shows where it would land among the other messages; release moves it (one undo step). The existing Arrange commands mean move later / earlier here: Bring forward = **Move later** (Cmd/Ctrl+]), Send backward = **Move earlier** (Cmd/Ctrl+[), Bring to front = **Move to end**, Send to back = **Move to start**; the menu and palette rows are relabelled for this type.
- **Reorder participants.** Press on a header and drag horizontally (vertical is locked: the header does not follow the pointer up or down, only the insertion line moves); release moves the participant to the nearest lifeline's place. The same Arrange commands move it right or left.
- **Delete.** Delete or Backspace, as elsewhere. Deleting a participant also removes every message to or from it.
- **Labels.** Double-click or Enter edits a participant's label over its header and a message's label **above its line**, where it is drawn, not on the line.
- **Live changes.** A message inserted mid-stream moves every later row down on the next render, since the scene is recomputed from the edge order. A saved change from another writer is merged as `web/src/model/remoteMerge.ts` describes: an insert lands where its writer put it, and two inserts made at once both survive in a stable order.

## Keyboard and accessibility
- Tab and Shift+Tab walk the participants left to right, then the messages top to bottom; the live region announces the label (or id) and kind. Enter edits the label, Delete removes, Cmd/Ctrl+] and [ move a selected message or participant later or earlier, Cmd/Ctrl+Z undoes each of these as one step.
- Every command above is an action in the registry, so each is in the menu and the command palette (checklist item 28); nothing is pointer-only except the two drags, and each drag has its Arrange equivalent.
- The connector tool works with touch and pen as it does elsewhere: the hit target around a lifeline is at least 24 CSS pixels for them, and a second finger cancels the gesture into a pinch.
- The insertion line is a line plus position, never colour alone; it is drawn in the snap-guide style and hidden for reduced motion only in the sense that nothing animates.

## What Draughtsman deliberately won't do
- **No free positioning of lifelines or messages, and no stored y.** That would make every insert rewrite every later row and would fight the CRDT order (spike report section 2, option A).
- **No drag-from-palette placement, no resize or rotate of a header.** Header width comes from its label.
- **No activation toggling, fragments, notes, create/destroy** in this milestone: milestone 2. Activation bars still draw from the `activate` and `deactivate` props.
- **No curved or orthogonal message routing,** whatever a stored `routing` says.
- **No re-attaching a message's end by dragging** in this milestone; delete and redraw, or edit the DSL.

## Open questions
- Should a drop on a lifeline *between* two headers of a very wide diagram scroll the canvas while dragging? Default for the build: no, the canvas does not auto-scroll for any connector gesture today.
- Should Tab walk messages at all, given other document types walk nodes only? Default for the build: yes, for a sequence document it is the only keyboard route to a message.

Written 2026-09-29 by the m5-sequence-editor crewmate.

## Discoverability and cleanup (added 2026-09-29, `fm/qa-fix-sequence-ux`)

Additions after a real-browser QA pass; nothing above is changed.

- **Guards have one stored form.** A fragment's `operands` hold bare text (`card ok`); the scene draws `[card ok]`, and the in-place editor shows the drawn form, so an authored, an imported and an older bracketed guard look the same. Typing brackets is harmless (they are dropped on commit). The Mermaid importer stores bare text; nothing is migrated, since a stored `[x]` still draws `[x]`.
- **Deleting the last message of a frame removes the frame** in the same undo step (a deleted participant's messages included, nested frames cascading); one undo restores the frame and its messages. A frame that was already empty is not touched, and deleting a frame alone still keeps its messages.
- **Menus list only what applies.** A sequence diagram registers no Edit attributes / operations / columns and no Group / Ungroup, so neither the menu nor the palette shows them.
- **Add operand also works from the frame.** With an alt, par or critical selected it splits before the last row of its last operand (the new operand ends the frame); a last operand of a single row cannot be split, because an operand with no row is not drawn, and the command is then disabled.
- **The message Kind picker offers sync, async and return.** `note`, `interaction-use`, `create` and `destroy` are made by their own commands, the DSL or the Mermaid import; a selected row of one of those kinds still shows its own kind in the picker.
- **The Shapes panel has a Fragments and notes group** of labelled buttons, each with the selection it needs written under it: Fragment: alt, opt and loop (wrap the selected messages), Add operand, Note and Interaction use. They are the menu's own actions, so they enable and disable together.
- **Add note after the last message of a frame stays inside the frame** (the innermost frame that message is in), rather than landing just under the border.
