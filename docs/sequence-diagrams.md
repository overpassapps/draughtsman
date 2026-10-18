# Sequence diagrams

Status: milestone 1a (the model, derived layout, renderers, DSL and agent path), the Mermaid `sequenceDiagram` importer,
generate-from-text, the Mermaid exporter, the editor half of milestone 1b (palette, connector-drag insert, reorder, message
type) and milestone 2 (combined fragments, notes, `create`/`destroy`, the activation toggle) are implemented. This note is
the reference for the `sequence` document type. It is not a spec; the build spec and `docs/spec/` are unchanged except for
dated lines in the spec's Amendments section. The design and its evidence are the 2026-09-29 spike; the rest of milestone 3
(`autonumber`, `cross` heads, participant gaps) is a follow-up listed at the end.

## What a sequence diagram is

A sequence diagram is a document of its own type, `sequence`. **Participants are the nodes and messages are the edges.**
Two orders carry the whole meaning:

- **The order of `edges[]` is time.** The first edge is the first message, the last edge the last. Insert a message
  mid-stream by inserting the edge at that position. The DSL keeps edges in file order, the Yjs shape keeps them in the
  `order` field, and `patch_document` has `addEdge` with `before`/`after` and `moveEdge` for it.
- **The order of `nodes[]` is left to right.** The first node is the leftmost lifeline.

Nothing about a position is stored. A `sequence` node has no `geometry` (a stored one is ignored and never written) and
a message has no `routing` (one is ignored; `curved` and waypoints do not apply). Geometry is derived by a pure function
on every render, the way an orthogonal route is derived, so inserting a message re-flows every row below it with no write
but the edge order, and an agent writes messages in the order they happen without computing a coordinate. `schemaVersion`
stays 1: `sequence` is one more value of `type`, which an older client that validates against the schema will reject.

## The example

`docs/examples/checkout-sequence.dsl` and `.json` are the same document (the golden tests pin it byte for byte, as they do
`order-service`):

```
draughtsman 1
id doc_checkout
type sequence
title "Checkout"
meta created=2026-09-29T00:00:00.000Z updated=2026-09-29T00:00:00.000Z by=eric

node p_user actor "Customer"
node p_web participant "Web app"
node p_api participant "Orders API"
node p_pay participant "Payments"

edge m_1 sync p_user -> p_web "Place order"
edge m_2 sync p_web -> p_api "POST /orders" activate=true
edge m_3 sync p_api -> p_pay "charge(total)" activate=true
edge m_4 return p_pay -> p_api "receipt" deactivate=true
edge m_5 async p_api -> p_web "declined"
edge m_6 return p_api -> p_web "201 Created" deactivate=true
edge m_7 sync p_api -> p_api "audit()"
```

The DSL needs no grammar change beyond the word `sequence` on the `type` line. A string prop value is quoted, a
boolean is not (`activate=true`); the parser's error for the common slip already teaches the fix.

## Kinds (`src/Draughtsman.Core/Schemas/sequence.json`)

`get_schema type=sequence` returns this file; its descriptions are what teaches an agent.

| Kind | Node or edge | Meaning | Look |
| --- | --- | --- | --- |
| `participant` | node | A lifeline: a service, component or system. | a box header, `rectangle` |
| `actor` | node | A person or role. | a UML stick figure in the left 28 of its header, no box, the name beside it |
| `sync` | edge | A call the sender waits on. | solid line, filled head |
| `async` | edge | Fire and forget, an event, a publish. | solid line, open head |
| `return` | edge | A reply, drawn responder to caller. | dashed line, open head |
| `note` | edge | A row of text across the participants from `from` to `to`. | a folded box |
| `interaction-use` | edge | A `ref` frame across the participants from `from` to `to`, its label the name of another interaction that runs at this point. | a frame with a `ref` tab and the name under it |
| `create` | edge | A message that makes its target exist from this row. | dashed line, open head, ending at the header |
| `destroy` | edge | A message that ends its target's lifeline. | solid line, filled head, an X on the lifeline |
| `fragment` | node | A combined fragment (`alt`, `opt`, `loop`, `par`, ...): a frame around a run of messages. | a frame with an operator tab and guards; never a column |

The message look is the vocabulary's `defaultStyle` (dash and arrowhead), read by the **server renderer**, which does not
resolve theme tokens; the three `themes/diagram-*.yaml` files carry the same values (plus each theme's stroke colour) for
the **web canvas and the client export**, and `web/src/model/sequenceKinds.test.ts` fails if the two drift. A message's own
sparse `style` still wins over both.

Message props (plain `props`, no schema change, like a UML class's `attributes`):

| Prop | Meaning |
| --- | --- |
| `activate` | `true` starts an activation bar on the **receiver** (`to`) at this message. |
| `deactivate` | `true` ends the **sender's** (`from`) innermost open bar at this message. |

| `in` | The id of the `fragment` node this message is a member of (a quoted string). A message that names none, or a node that is not a fragment, is in no frame. |
| `operand` | Which of that fragment's operands the message is under: a whole number from 0 (the default, so it is written only when it is not 0). |

A prop is on when it is JSON `true` or the string `"true"` (any case); anything else is off. A `deactivate` with nothing
open is ignored by the drawing. A bar nothing closes runs to the end of the last message row.

## Fragments, notes, create and destroy

Milestone 2 adds the frames real sequence diagrams need. Every position is still derived; all of it is props and order.

**A fragment is a node of kind `fragment`**, listed anywhere in `nodes[]` (it takes no column) with `props.operator` (the
tab text: `alt`, `opt`, `loop`, `par`, `break`, `critical`, `neg`, `assert`, `ref`, `seq`, `strict`; any string is drawn, an
unknown one is a critique warning) and `props.operands`, an array of guard strings, one per operand (`["card ok","declined"]`
for an alt with an else; drawn `[card ok]`, `[declined]`. A guard stored with its brackets, as older documents and imports have it, draws identically). **Membership is written on each message, never on the fragment:** `in="f_1"` and, for a message
under a later operand, `operand=1`. The frame is derived as the first to the last member row, so deleting a message from the
middle keeps the frame (one row shorter), and deleting every member leaves a fragment that draws as an empty one-row frame
after the last row and is flagged `empty-fragment`. In the editor, deleting the last message of one operand of an alt/par removes that operand (its guard included) and renumbers the later ones, in the same undo step; deleting a frame's last message removes the frame. A fragment nests in another through the node's `parent`; the operand of
the outer fragment it sits in is its own `props.operand`, or, when it has none, the operand of the outer fragment's nearest
earlier message (0 when there is none). A message in a nested fragment is a member of every fragment around it.

```
node f_1 fragment "alt" operator="alt" operands=["[card ok]","[declined]"]
edge m_3 sync p_api -> p_pay "charge(total)" activate=true in="f_1" operand=0
edge m_5 async p_api -> p_web "declined" in="f_1" operand=1
```

**A note is an edge of kind `note`**: a row in the order, with `from` the first participant it covers and `to` the last
(either order; the same participant for a note over one lifeline) and its `label` the text. Notes take no activation.
**`create`** is a message whose target's header is drawn at that row (level with the message, which ends at the header's
near edge) and whose lifeline starts under it; **`destroy`** is a message that ends its target's lifeline at that row with
an X and ends every bar still open on it. Only the first `create` and the first `destroy` of a participant count; later
ones draw as plain messages, and a `create` from a participant to itself is a self message.

`docs/examples/checkout-fragments.{json,dsl}` is the shared example (an alt, a note, a created and destroyed mailer);
`web/src/model/sequenceFragments.test.ts` pins its numbers and the C# server-render test asserts the same ones.

## The derived layout (`web/src/model/sequenceScene.ts`)

`sequenceScene(document)` returns plain data: a header box per participant, a lifeline each, a polyline per message, the
activation bars and the picture's bounds. It is the single implementation: `indexDocument` reads it to fill `placed`,
`nodeBounds`, `edgePaths` and `contentBounds` for a `sequence` document (so selection, hit-testing, fit and the client
export work unchanged), `DiagramSvg` draws its lifelines and bars (`SequenceLayer.tsx`) and puts each message's label
above its line, and the sidecar's `sequence.scene` method bundles this very module for the server, exactly as
`route.document` bundles the router, so the editor, the export and `render_document` cannot draw different pictures.
Text width is `estimateTextWidth` everywhere (never a DOM measurement) for the same reason.

| Rule | Value |
| --- | --- |
| Header | 120 wide (wider for a long label: text width + 24), 40 high, 20 from the top and left. |
| Columns | Lifelines at least a header width and a 20 gap apart (140 for two default headers). A message label widens the gaps it crosses to `(label width + 24) / lifelines spanned` each; a self message reserves `label width + 24` to its lifeline's right. |
| Rows | The first row starts 30 below the headers. A message takes 36, a self message 52; its line is at the row's middle (a self message's top segment 20 below the row's top). |
| Bottom | The end of the last row plus 20; every lifeline runs to it. |
| Activation | Bars are 10 wide; nested bars step 5 to the right. A message's ends stop at the edge of a bar open on that lifeline at that moment. |
| Self message | A 30 by 16 bracket to the lifeline's right; its label sits over the top segment. |
| Actor | The header is the same size as a participant's for the same name: the stick figure (head radius 0.16 of the figure's height, figure = the top `actorFigureHeight` (26) less 2 either side, so 22) is centred over the lifeline and the name is centred in the 14 under it. Only the `actor` kind, whose stored shape is `rounded` (the kind's default) or `custom:sequence/actor`, draws it (`participantShape`); any other stored shape is kept. |
| Interaction use | A row of 60 with a 48 high frame centred in it, reaching 10 beyond the outer two lifelines it covers (over one lifeline: at least 100 wide, centred on it); the `ref` tab (18 high) at its top left, the name centred in the 30 under it. Takes no activation and no label plate, so nothing pushes it down after a frame closes. |
| Note | A row of 36 with a 28 high box centred in it, reaching 10 beyond the outer two lifelines it covers (the gaps widen to hold its text) or, over one lifeline, at least 100 wide and centred on it. Its label is centred in the box. |
| Create | A row of 48; the target's header (40 high) is centred in it, level with the message, and its lifeline starts under the header. |
| Destroy | The target's lifeline ends at the message's line (a self message: at the bracket's foot), with an X 16 across. |
| Fragment | A frame opening adds a **guard row** of 22 above its first member (the operator tab and the first guard sit in it); a change of operand adds a guard row and a dashed divider; a frame closing adds 8 below its last member, and nested frames close one after another. **A message row straight after a frame closes is pushed down until its label plate is 2 clear of the closing border** (the plate reaches 8 above a message's row, 6 above a self message's), so no border cuts through a label: 10 for a message, 8 for a self message; a note or interaction use has no plate above its row and a frame opening in between already has its guard row. **A frame that opens straight after another closes starts 6 (`fragmentFrameGap`) below its border**, so two frames never share one. A frame reaches 20 beyond the outermost lifelines its members touch (10 further than the frame nested in it) and is widened to hold its tab and guards; a message that is not a member but sits between two members is drawn inside it. |
| Labels | Centred above the line, 14 up. |

Worked numbers for the checkout example (pinned by `sequenceScene.test.ts`, the C# and sidecar tests, and the server end-to-end
test): lifelines at x = 80, 220, 360, 500; messages at y = 108, 144, 180, 216, 252, 288 and 326 (the self message's top);
bars `p_api` (x 355, y 144, 10 by 144) and `p_pay` (x 495, y 180, 10 by 36); the picture is 580 by 378.

## Server render colours

`render_document`, the PDF and `GET /api/documents/{id}/svg`-style exports draw a sequence document with the **shipped diagram theme's
tokens**, as the client's export does, through `DiagramTheme` (`src/Draughtsman.Mcp/Rendering/DiagramTheme.cs`): the kind's default
from `themes/diagram-*.yaml` (embedded in the Mcp assembly; the web build compiles the same files, so there is one source of the
colours), `token:<name>` read from the palette, the document's dotted-path `theme` overrides and the element's own sparse style laid over
that, in `web/src/theme/resolveStyle.ts`'s order. A kind the theme does not name takes the vocabulary's `defaultStyle`
(`sequence.json`), then the theme's `default` edge. The picture is painted on the theme's `exportBackground`, edge labels, notes and
frame text sit on it in `exportText` (else black or white by the paper's luminance) and a see-through fragment's text follows the
surface unless the node or an override named a colour (`withSurfaceText`). Pinned by the literals in
`web/src/canvas/sequenceDiagram.test.tsx` (the client export) and `McpHttpToolsTests` / `SequenceToolsTests` (the server), which must
change together.

What it deliberately does not do: **the theme is the shipped default, `overpass`** (the one the editor opens with), because the
client lets each viewer pick a theme in view state and the server has no "the user's theme" (`theme.diagram` in `draughtsman.yaml` is
not read by either side yet); a document that wants another look sets `theme` overrides, which are honoured. **Fonts are not
resolved** (the server draws `sans-serif`, the width the scene assumes). **Other document types still draw with the renderer's neutral
defaults**: porting the resolution to them is the remaining half of the same job. This added `YamlDotNet` (already used by the server)
to the Mcp project to read the theme files.

## Where each part lives

| Part | Place |
| --- | --- |
| Type in five places | `docs/schema/document.schema.json`, `DocumentType.Sequence` (Core), `web/src/model/types.ts`, `sidecar/src/model/types.ts`, `dsl.grammar`'s `type-line` |
| Vocabulary | `src/Draughtsman.Core/Schemas/sequence.json`, `KindVocabulary`, `SequenceMessages` (the prop rule, the kind names, the known operators) |
| Scene | `web/src/model/sequenceScene.ts` (`sequenceMemberships` is the one reading of `in`/`operand`/`parent`, which the C# critique rule mirrors); `textEstimate.ts` is the DOM-free half of `text.ts` split out so the sidecar can bundle it |
| Canvas | `documentIndex.ts` (`indexSequence`, `DocumentIndex.sequence`; a fragment is placed but is not a root node), `SequenceLayer.tsx` (lifelines, bars, frames, notes, the X), `DiagramSvg.tsx`; editing is by order only, see "Editing on the canvas" |
| Fragment edits | `model/sequenceFragments.ts` (wrap, add operand, add note, toggle activation, guard, operator, membership by slot; pure, one `SequenceEdit` per command), the hook's `wrapInFragment`/`addFragmentOperand`/`addNote`/`toggleActivation`/`setFragmentGuard`/`setFragmentOperator`; `design/atlas/sequence-fragments.md` |
| Editing | `model/typeCapabilities.ts` (the gate), `model/sequenceEdit.ts` (slot maths, insert, reorder), `canvas/sequenceHit.ts`, `DiagramCanvas.tsx`, `useDocumentEditor` `insertMessage`/`reorderElement`; the interaction design is `design/atlas/sequence-editor.md` |
| Export | client PNG/SVG through the existing path (`exportDiagram.tsx`); server SVG/PDF through `SvgRenderer.Sequence.cs` |
| Server scene | sidecar method `sequence.scene`, `ISidecarClient.SequenceSceneAsync`, `SequenceScene` records |
| Layout | `POST /api/layout` and MCP `layout` return a sequence document unchanged, with no warning (`NodeSidecarClient.LayoutAsync`, `layout.elk`) |
| Mermaid export | `src/Draughtsman.Core/Import/MermaidExporter.cs`; `POST /api/export/mermaid`, MCP `read_document format=mermaid`, the editor's File > Export Mermaid and Copy as Mermaid; see "Mermaid export" |
| Mermaid import | `src/Draughtsman.Core/Import/MermaidSequenceImporter.cs`, dispatched by `MermaidImporter`; `POST /api/import/mermaid`, MCP `create_document format=mermaid` |
| Agent path | `patch_document` `addEdge before/after` and `moveEdge`; `diff_documents` `movedEdges`/`movedNodes`; `read_document` outline numbers the messages; `critique_document` `unbalanced-activation`, `empty-fragment`, `non-contiguous-fragment`, `unknown-fragment` |

## Editing on the canvas

Nothing a gesture could write is a position, so every edit is a change of order, one undo step each
(`web/src/editor/undoCoverage.test.ts` has a case per edit).

| To | Do |
| --- | --- |
| Add a participant | Shapes panel, group **Sequence** (Participant, Actor), or the palette commands Add participant and Add actor. The lifeline is appended at the right end (last in `nodes[]`), selected, and its label opens for editing. Dragging a glyph onto the canvas is not offered: a drop point means nothing here. |
| Add a message | Connector tool, drag from a lifeline (or its header) to another lifeline; the release **y** picks the slot: the message goes before the first message whose row midpoint is below the pointer, else last. Release on the lifeline you started from for a self message. It is a `sync` message, selected, with its label editor open above its line. A dashed preview and the slot follow the pointer; a release off every lifeline, or Escape, creates nothing. |
| Change the type | The Connector section's Kind picker (`sync`, `async`, `return`); its Routing picker is not shown. |
| Reorder messages | Press and drag a message vertically (a horizontal insertion line shows where it lands; release writes once), or **Arrange**: Bring forward is **Move later** (Cmd/Ctrl+]), Send backward is **Move earlier** (Cmd/Ctrl+[), and Bring to front / Send to back are Move to end / Move to start. The menu, palette and Arrange panel say so for this type. |
| Reorder participants | Drag a header sideways (a vertical insertion line; the drop y is ignored), or the same Arrange commands. |
| Delete | Delete or Backspace; deleting a participant also removes every message to or from it. |
| Edit a label | Double-click or Enter: over the header for a participant, **above the line** for a message. |
| Wrap in fragment | Select messages that are next to each other (Shift-click adds), then Wrap in fragment: alt, opt, loop or par (menu Edit > Insert group, or the palette). One undo step creates the frame and sets `in` on each; its first guard opens for editing. Disabled when the rows are not adjacent or would cross another frame's border. |
| Add operand | Select a message directly inside an alt, par or critical that is not the first of its operand, then Add operand to fragment: the message and what follows move under a new `[else]` operand (empty for a par), and its guard opens. With the frame itself selected it splits before the last row of its last operand, so the new operand ends the frame; a last operand of one row cannot be split (an operand with no row is not drawn). |
| Edit a guard | Double-click a frame (the guard whose row is under the pointer), or select it and press Enter. Set fragment operator: alt, opt, loop, par changes the tab. |
| Add a note | Add note (menu, palette) inserts an empty note after the selected message over the same participants, and opens its text. |
| Toggle an activation | Toggle activation on a selected message turns the bar it opens on its receiver on (closing it at the receiver's next return) or off (removing the `deactivate` that closed it), as one undo step. |
| Walk by keyboard | Tab and Shift+Tab visit the participants left to right, then the messages top to bottom (the only keyboard route to a message). |

**A message keeps the membership of where it lands.** A message inserted between two members of a frame, or moved between two of them (dragged or with Arrange), is a member of that frame (the innermost fragment both neighbours are in, under the operand of the earlier one); one moved above the first member or below the last is outside it. The membership is written in the same undo step as the move.

Only the `order` fields that change are written when a message is inserted or moved; the later rows shift on the next
render because the scene is recomputed. Two writers inserting at once both keep their message (`remoteMerge.ts`
`placeInserted`; `remoteMerge.test.ts`, "two writers inserting at once").

**The gate.** `typeCapabilities(type).derivedGeometry` is true for `sequence`. `DiagramCanvas` reads it from the document
type; while it is set there is no resize or rotate handle, no free move, no snapping, no connection handles from ports,
no segment or waypoint handle or drag, no re-attach, no nudge (the arrow keys are disabled), and group, duplicate,
align, distribute, Tidy, routing and clipboard commands are disabled or absent. A stored `routing` is ignored, as it always
was. The Yjs writers for geometry and routing, and `useDocumentEditor.moveNodes`, refuse the document too.
`web/src/canvas/DiagramCanvasSequence.test.tsx` proves each gesture inert against a freeform control that does write.

## Behaviour worth knowing

- **`render_document` and PDF need the sidecar** for a sequence document, which is where its layout comes from.
  `render_document` refuses with a message naming the failure (`Cannot render: a sequence diagram's layout is derived by
  the sidecar, and the sidecar is unavailable: ...`) rather than drawing a guess, and `GET /api/documents/{id}/pdf` answers
  `503`. This differs from an orthogonal connector, which degrades to a straight line with a warning: a straight line is
  still the diagram, whereas a sequence with no derived layout has no picture at all.
- **The canvas edits by order only** (see above). Sequence is on the New diagram menu (home screen, editor File menu and
  palette): it creates a `sequence` document opening with a `User` actor, a `System` participant and one `sync` message
  (`SEQUENCE_STARTER` in `web/src/app/newDocument.ts`), so the first edit is obvious; the other types still start blank.
  **Import Mermaid** (home screen, File menu and palette; `editor/ImportMermaidDialog.tsx` over
  `model/mermaidImport.ts`) is the paste box for `POST /api/import/mermaid`: it previews without saving, shows the
  importer's warnings, and creates the document on confirmation. Agents and the DSL create them too.
- **Not supported on the canvas yet**: dragging a glyph from the palette (click only), re-attaching a message's end,
  copy, paste and duplicate, group, dragging or resizing a frame (its extent is its members), interaction uses (`ref`
  frames with a name), and touch or pen drags of a message or participant (a finger pans, as it does for every node; the
  connector tool and the Arrange commands work with touch).
- **A reorder is a change.** `diff_documents` reports edges (and nodes) that only changed place as `movedEdges` /
  `movedNodes`, each `{ id, after }` naming what it now follows (`null` when first): the fewest moves, so moving one
  message reports that message and not the ones it jumped over. The live-sync merge (`remoteMerge.ts`) follows an agent's
  `moveEdge`, and places an agent's mid-stream `addEdge` where the agent put it, not after everything local.
- **Critique**: `unbalanced-activation` (`warning`) fires on a `deactivate` with nothing open (evidence: the message and
  the sender) and on an `activate` nothing closes (evidence: the opening message and the receiver). The generic
  `isolated-node` rule flags a participant no message touches.
- **Generated from text**: `POST /api/ai/generate-from-text` takes `diagramType: "sequence"`. The prompt swaps the
  flowchart cheat-sheet for a sequence one (the order of the node lines is left to right, the order of the edge lines
  is time, `activate=true`/`deactivate=true` are unquoted booleans, no `@ x,y`), and the write is never laid out. An
  update sends the whole current DSL and applies the reply as ops: `moveEdge` for the messages it reordered (the fewest,
  from `diff_documents`' `movedEdges`), `addEdge` with `before`/`after` for the ones it inserted, no geometry. The AI
  panel offers a Sequence chip and preselects it for a sequence document.

## Mermaid import

A Mermaid `sequenceDiagram` (bare, in a ```` ```mermaid ```` fence, CRLF or LF, after front matter or `%%` comments)
imports as a `sequence` document through `POST /api/import/mermaid`, MCP `create_document format=mermaid type=sequence`
and the `/try` page, which posts to that endpoint and needs no change. The header picks the type when the caller gave
none; a caller that named a type that disagrees with the source is refused, never relabelled (`create_document` sets the
type from its argument after import, so the check lives in `MermaidImporter`, where both callers share it).

| Mermaid | Becomes |
| --- | --- |
| `participant A as Alice`, `actor U` | node `p_A` (kind `participant`, label `Alice`), `p_U` (kind `actor`); the label is the name when there is no `as` |
| `A->>B`, `A-->>B` | `sync`, `return` |
| `A-)B`, `A--)B` | `async` (`--)` adds `dash: dashed`) |
| `A->B`, `A-->B` | `sync` with `arrowEnd: none` (`-->` adds `dash: dashed`) |
| `A->>+B: x`, `B-->>-A: y` | `activate: true` on the first message, `deactivate: true` on the second |
| `A->>A: x` | a self message |
| `alt g` / `else g` / `end`, `opt`, `loop`, `par` / `and`, `critical` / `option`, `break` | a `fragment` node (`f_1`…, operator the keyword, one operand per branch, guards written `[g]`); the messages and notes inside carry `in` and `operand`; a block in a block is a fragment whose `parent` is the outer one |
| `Note over A,B: t`, `Note over A: t` | an edge `n_1`…, kind `note`, over the participants named |
| `Note left of A: t`, `Note right of A: t` | a note over `A` (warned: the model has no side) |
| `create participant D as Dave` then `A->>D: hi` | the participant, and the message is kind `create` |
| `destroy D` then `A-xD: bye` | the message is kind `destroy` |
| `title X`, front matter `title:` | the title |

Ids are `p_<mermaidId>` and `m_1`, `m_2`… (the Mermaid id, sanitised to the id pattern with an `id-renamed` warning when that
changes it, so a message that names `Web App` reaches `p_Web_App`). A participant only mentioned in a message is created
at its first appearance, which is its left-to-right place. Only the style that differs from the kind's default is
written. A message label is one line (`<br/>` becomes a space).

It is a tolerant reader (the posture of the SQL importer): a fragment is created the first time a message or note lands inside
its block, so a block with none is left out with one `unsupported-syntax` warning, and a block still open at the end is closed
there with a `parse-error` warning. One `unsupported-syntax` warning, with its line, for each of `rect`/`box` blocks (the messages
inside still import in order, without the frame), `autonumber`, `link`/`links`/`properties`/`details`, a participant's `@{ ... }`
type config, `Note left of`/`right of` (drawn over the lifeline), a `create` or `destroy` whose next message does not go to the
participant (the model ends the lifeline of a message's *receiver*, so a destroyed sender is warned), and the lost-message cross
outside a `destroy`; a line that is none of these is a `parse-error` warning and is skipped. Where this goes beyond the letter of the milestone (each
recorded as an assumption, each cheap to undo):

- `-x` / `--x` (lost message) imports as a plain `sync` message with its warning (none on the message that destroys, where the cross is how Mermaid spells it), rather than dropping the message: a
  skipped message would change the story the diagram tells, an approximated arrowhead does not. It is exact once the
  `cross` head lands (milestone 3).
- A standalone `activate B` / `deactivate B` attaches to the message just before it when that message goes to (comes
  from) `B`, the one shape the model can say; any other placement is a warning. LLMs write these as often as `+`/`-`.
- `<<->>` and `<<-->>` (bidirectional) import as `sync`/`return` with the head at the sender too, since the style can say it.
- A `create`/`destroy` message keeps the arrow's own look: only the style that differs from the create (dashed, open) or destroy
  (solid, filled) default is written, so `A->>D` that creates `D` stays solid.
- A guard is stored as its bare text (`card ok`); the brackets are drawn, not stored, so an imported guard and one typed in the editor look the same. A guard the author already bracketed loses its brackets; a block with no text has an empty guard.

## Mermaid export

`MermaidExporter` (Core, next to the importers) writes a `sequence` document as a `sequenceDiagram` for a README: participants
in node order with `as` aliases, then the messages in edge order. The full mapping is docs/api.md's `POST /api/export/mermaid`;
the sequence half is the importer's table read backwards, so an export imports back as the same participants, the same
messages in the same order with the same kinds, and the same activation bars (pinned by `MermaidExporterTests`):

| Message | Mermaid |
| --- | --- |
| `sync` | `A->>B: text` |
| `return` | `A-->>B: text` |
| `async` | `A-)B: text` (`--)` dashed) |
| `activate=true` on the message | `A->>+B: text` |
| `deactivate=true` on the message | `B-->>-A: text` (the sender's bar closes) |
| both on one message | `A->>+B: text`, then `deactivate A` |
| self message | `A->>A: text` |
| a fragment (`alt`, `opt`, `loop`, `par`, `critical`, `break`) | a block around its messages: `alt card ok` … `else declined` … `end` (`and` for a `par`, `option` for a `critical`; the guard's brackets are dropped and nested fragments nest) |
| `note` | `Note over A,B: text` (`Note over A: text` for one lifeline) |
| `interaction-use` | `Note over A,B: ref: Name`, with a warning (Mermaid has no interaction use; it reads back as a note) |
| `create` | `create participant B as Bob` before the message, which is written `A-->>B: text`; the participant is not declared up top |
| `destroy` | `destroy B` before the message, which is written `A-xB: text` |

A fragment whose operator Mermaid has no block for (`neg`, `assert`, `ref`, ...) is flattened with a warning and a `%%` comment,
as is a second operand of an `opt`, `loop` or `break`, which have no continuation word.

Ids are plain Mermaid ids (`p_user` is `user`). Two round-trip differences are by design: the importer gives back its own
ids, and a message whose look Mermaid cannot tell from another (a dashed `sync` with a filled head is `-->>`, which the importer
reads as `return`) comes back as that other kind. Fragments, notes and `create`/`destroy` round-trip: `MermaidExporterFragmentTests` exports the
fragments example and imports it again to the same frames, notes and lifecycles.

## Follow-ups (not built)

Milestone 1b, remaining: a `moveNode` patch op (participant order is only editable by rewriting the document, or on the
canvas, from an agent). Also open from the editor: a remote merge that places an agent's insert when the user has reordered
locally (today it lands after everything local, which `remoteMerge.test.ts` pins). The AI panel's Sequence chip has no atlas
entry of its own (`design/atlas/ai-panel.md` lists two chips and is unchanged); it belongs with
`design/atlas/sequence-editor.md`. Selecting a participant by
clicking its lifeline still needs the lifeline to be a hit target of the header's `<g data-id>`; the connector tool
hit-tests a lifeline itself (`canvas/sequenceHit.ts`).

Milestone 2, remaining: an interaction use that links to the other diagram (its label is text; no document is resolved or
opened), a Wrap in fragment that accepts a frame's own node in the selection (today select its messages), and the same theme
resolution for the other diagram types in the server renderer (a sequence document has it, see "Server render colours"). A
guard row does not yet keep the first member's label plate clear of the operator tab when a label runs close to the frame's left edge. The activation toggle closes a new bar at the receiver's next return; with other bars open on the same
receiver bars pair innermost first, so the closer it picks may not be the one an author would.

Milestone 3: a `cross` arrowhead for lost messages, `autonumber`, an optional participant gap, `box`/`rect` groups.
