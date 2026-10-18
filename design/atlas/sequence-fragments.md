# Sequence diagram fragments, notes and activation

## Screen
The canvas, menu and command palette when the open document is of type `sequence` (`docs/sequence-diagrams.md`), for milestone 2: combined fragments (alt, opt, loop, par), notes, `create` and `destroy` messages and the activation toggle. It extends `design/atlas/sequence-editor.md`, whose derived-geometry gate still holds: every position is derived by `web/src/model/sequenceScene.ts`, so every edit here writes props and order, never a coordinate. Key pattern questions: how do you frame a run of messages when the frame has no geometry of its own; how do you add a branch (an else) to a frame; where do you type the guard text; what does a message dropped into, or dragged out of, a frame do to its membership; how do you turn an activation bar on and off without a bar handle.

## Mobbin queries
- `search_screens "diagram editor with a labelled frame group around a set of steps showing a condition label and a divider between branches"` (web, standard mode, 2026-09-29): six results (AirOps, Mintlify, Klaviyo, MagicPath, Tana, Plain). The nearest are the workflow builders' if/else node (Plain: a node with an `If` block, an `Else` block and an inspector that adds `Else if` rows, AirOps and Klaviyo's condition inspector), which show branches as rows of one container. None is a sequence-diagram frame.
- `search_screens "context menu on selected items with a wrap in group or add note command in a whiteboard or diagram editor"` (web, standard mode, 2026-09-29): six results (fal, Higgsfield, MagicPath, Tana, Magnific, Framer). Tana's Selection menu (Align, Distribute, Group, Ungroup) and Framer's context menu (Add Frame, Add Stack, greyed rows for what does not apply) show a "wrap the selection" command living in a menu with unavailable rows disabled, not hidden.
- `search_flows` was not run: the screens searches returned no sequence editor, and the atlas README records that Mobbin holds no Lucidchart, Whimsical or FigJam screens.

## References
No Mobbin reference is a sequence-diagram editor, so none is invented. From the products (not on Mobbin; from the product, unverified against the library) and the Mobbin results above:
1. Lucidchart — sequence diagram shapes: an "alt" or "loop" frame is a shape you resize around messages; there is no membership, the frame is only geometry (which is what Draughtsman avoids: a frame that does not follow its messages goes stale on every insert).
2. Figma / FigJam — select several layers and "Frame selection" (Cmd/Ctrl+Alt+G) wraps them; the new frame appears around exactly the selection and its name is editable at once. The model for "Wrap in fragment".
3. Miro — a sticky note is added from the toolbar and typed into immediately; the model for Add note.
4. Whimsical — sequence diagrams: notes are rows in the flow (over one or several participants) and the frame is drawn from what is inside it (not on Mobbin; from the product).
5. Tana (Mobbin) — the Selection menu's Group and Ungroup, and Framer (Mobbin) — Add Frame in the context menu with unavailable rows greyed: a wrap command is a menu row that is disabled, not hidden, when it does not apply.
6. Plain (Mobbin) — Configure if/else: branches as `If`, `Else if`, `Else` rows with an "Add condition" button under the last one; the model for Add operand.

## What the references agree on
- Wrapping is one command on a selection, and the new container appears around exactly that selection with its label ready to type.
- A command that does not apply is listed but disabled, so it can be discovered.
- Branches are rows of one container, added at the end with a button, each with its own condition text.
- Notes are typed the moment they are made.

## What Draughtsman does
- **Wrap in fragment.** With one or more messages selected (Shift-click to add), the Edit menu (its Insert group) and the palette each list four commands, Wrap in fragment: alt, opt, loop, par. It applies when the selection is messages or notes only, next to each other in the edge order, and every fragment it touches is inside it entirely (a frame cannot cross another's border); otherwise the rows are disabled. One undo step creates the `fragment` node and sets `in` on each message. The new frame is selected and its first guard opens for editing at once (`[condition]`, or empty for a par). A selection inside an operand of an existing frame nests the new one in it.
- **Add operand.** With a message selected that is directly inside an alt, par or critical and is not the first message of its operand, Add operand splits the frame there: the message and what follows move under a new operand with an `[else]` guard (empty for a par), and later operands shift up. One undo step; the new guard opens for editing.
- **Guard text in place.** Double-click a frame, or select it and press Enter, to edit the guard whose row is under the pointer (the first when it is the keyboard). It is the edge-label editor: one line, Enter or click away commits, Escape discards. Set fragment operator: alt, opt, loop, par, on a selected frame, changes its tab.
- **Add note.** Add note inserts an empty note row after the selected message over the same participants (at the end over the first participant when nothing is selected) and opens its text for editing. Its kind is `note`; changing the connector's Kind picker turns a message into a note or a create or destroy message and back.
- **Toggle activation.** On a selected message, Toggle activation turns the bar it opens on its receiver on or off in one undo step: on sets `activate` and closes the bar at the receiver's next return (or next message out); off removes it and the `deactivate` that closed it.
- **Membership follows the slot.** A message made with the connector tool, or dragged (or moved with Arrange) to a new place, is a member of the fragment both its new neighbours are in, and of none when they share none: a message dropped between two members is inside the frame; one moved above the first member or below the last is outside. Membership is read from neighbours on release, in the same undo step as the move.
- **Frames are selectable by their border and tab only** (a 12px non-scaling target, as a connector has), so the messages inside stay clickable. A frame is a node with no geometry of its own; it is not draggable or resizable.
- **Notes, create and destroy** are drawn by the sequence layer and by the server renderer from the same scene, in the theme's `fragment` node style and `note`, `create` and `destroy` edge styles.

## Keyboard and accessibility
- Tab and Shift+Tab reach a frame as they reach a participant (a fragment is a node), and the live region announces its label and kind. Enter edits its first guard; each command above is an action in the registry, so it has a menu entry and a palette entry (checklist item 28), and each is a case in `web/src/editor/undoCoverage.test.ts`.
- The guard editor is a labelled text box (`Fragment guard`) that owns every key while open, like the other label editors; Escape returns focus to the canvas.
- A frame is drawn with a border, a tab and text, never colour alone.

## What Draughtsman deliberately won't do
- **No frame handles, no resizing a frame, no dragging it.** A frame's extent is its members; dragging one would have to rewrite membership of everything it crossed.
- **No drag-to-wrap.** A frame is made from a selection, not by drawing a rectangle over rows.
- **No interaction-use (`ref`) frames with a name field, no `autonumber`, no `cross` heads**: milestone 3 or later.
- **No per-participant note sides** (left of, right of): a note is over one lifeline or across several.
- **No automatic pairing of a new bar with a hidden message**: the toggle closes at the receiver's next return and says nothing when there is none, the bar then running to the last row and being flagged by the critique rule.

## Open questions
- Should Wrap in fragment also be offered with a fragment node and messages in the selection, treating the node as its rows? Default for the build: no; select the frame's messages instead.
- Should a note added with nothing selected span every participant rather than the first? Default: the first, since one lifeline is the common case.

Written 2026-09-29 by the m5-sequence-fragments crewmate.
