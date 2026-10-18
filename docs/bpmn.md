# BPMN shapes

Draughtsman draws the everyday subset of BPMN 2.0 process notation in `flowchart` and `freeform` documents. It is
built from the swimlane `pool` and `lane` containers and the typed-glyph technique the generic icon library uses; the
shape data is `src/Draughtsman.Core/Schemas/bpmn-shapes.json`, which the web canvas and the server SVG renderer both
read, and `get_schema` returns it as `bpmnShapes` so an agent can draw a correct process without guessing.

The notation is original line art drawn to the standard's conventions. No branded or third-party artwork is used.

## What ships

A shape is placed as the presentation shape `custom:bpmn/<name>` and names its own flowchart kind, `bpmn-<name>`, whose
default shape it is. The DSL therefore prints and parses by kind (`node n_1 bpmn-user-task "Review"`), and
`critique_document` treats a BPMN process as a first-class flowchart.

| Family | Shapes | How it is drawn |
| --- | --- | --- |
| Start events (4) | `start`, `start-message`, `start-timer`, `start-signal` | Circle, thin ring, trigger glyph inside |
| Intermediate events (9) | `intermediate`, `intermediate-message`, `-message-throw`, `-timer`, `-error`, `-signal`, `-signal-throw`, `-link`, `-link-throw` | Circle with a double ring |
| End events (4) | `end`, `end-message`, `end-error`, `end-signal` | Circle, thick ring |
| Activities (10) | `task`, `user-task`, `service-task`, `script-task`, `manual-task`, `send-task`, `receive-task`, `business-rule-task`, `sub-process`, `call-activity` | Rounded rectangle; task-type marker top-left, plus marker at the bottom for a sub-process, thick border for a call activity |
| Gateways (5) | `exclusive-gateway`, `parallel-gateway`, `inclusive-gateway`, `event-gateway`, `complex-gateway` | Diamond with X, +, O, pentagon in a double ring, or asterisk |
| Artefacts (3) | `data-object`, `data-store`, `annotation` | Folded page, cylinder with two lips, open bracket |

Border weight tells the event position apart (single = start, double = intermediate, thick = end). A **filled** glyph
means the event throws or the task sends (`intermediate-message-throw`, `end-message`, `send-task`); an outline glyph
means it catches or receives.

Edges:

- Sequence flow is the existing `flow` edge kind (solid, filled arrow), used between nodes of one pool.
- `message-flow` is new: dashed, open circle at the tail, open arrowhead. Use it between different pools.
- `bpmn-association` is new: dotted, no arrowheads, from a text annotation to what it comments on.
- `data-association` is new: dotted with an open arrowhead, from a data object or store to the activity that reads it, or
  from an activity to the data it writes.

Pools and lanes are the existing `pool` and `lane` kinds (`custom:swimlane/pool`, `custom:swimlane/lane`; see
`web/src/model/swimlanes.ts`). Parent a BPMN node to the lane it belongs to: dragging one into a lane reparents it, and
a BPMN node parented to a lane draws no `non-container-parent` finding.

## Behaviour worth knowing

- Events and gateways fill their box (an ellipse and a diamond), so the palette places them square (40 x 40 and 50 x 50)
  through `NodeTemplate.size`; stretch one and it becomes an oval. Ports and the clip point of an unnamed edge end sit on
  the true outline.
- Events, gateways and data shapes draw their label in a band **below** the outline (BPMN's convention), so the label is
  outside the node's box. Export and fit-to-screen bounds include the band. A node placed near the bottom edge of a lane
  clips its label with the lane, as the lane clips every child, so leave room under it.
- The server SVG renderer draws the same shapes but, like every other node, does not resolve theme tokens: it draws a
  message flow as an ordinary arrow, where the canvas draws it dashed.

## Deliberately left out

- Markers: compensation, loop, multi-instance (parallel and sequential) and ad-hoc.
- Event types: conditional, escalation, cancel, compensation, terminate, multiple and parallel-multiple, and the
  non-interrupting (dashed) variants of a boundary event. Only the message, timer, error, signal and link triggers ship,
  in the positions BPMN allows them. Boundary events are placed by hand on an activity's edge; nothing attaches them.
- Expanded sub-processes, event sub-processes and transactions: use a `container` and parent the steps to it. There is
  a collapsed `sub-process` only.
- Choreography and conversation diagrams.
- Data object variants (input, output, collection) and a data store reference.
- Default and conditional sequence flow markers (the slash and the diamond at the tail): label the flow instead.
- Group artefacts and message envelopes on message flows.
- Per-kind theme colours: BPMN kinds take the theme's `default` node style, so a start or end event is not tinted.
