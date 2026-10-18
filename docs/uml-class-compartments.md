# UML class shapes and the compartment model

Status: implemented (shape library batch 6). This note is the reference for how a *compound node* works in
Draughtsman, so the ER entity batch can reuse the mechanism instead of inventing a second one. It is not a spec; the
build spec and `docs/spec/` are unchanged.

## What a class is

A UML class is **one node**, not a group of nodes. The content that a class box shows lives in the node itself:

| Part of the box | Where it lives in the model |
| --- | --- |
| Name (bold, centred) | `label` |
| Stereotype line, `«interface»` (optional) | `props.stereotype`; an interface shape defaults to `interface` |
| Attributes compartment | `props.attributes`, an array of strings, one member per string |
| Operations compartment | `props.operations`, an array of strings, one member per string |

There is no new field on `Node`, no schema change and no DSL grammar change: `props` already carries any JSON, and the
DSL already prints a JSON array on the node line. Documents saved before this change are unaffected.

A member is plain text. A leading `+ - # ~` is its visibility glyph (public, private, protected, package); the renderers
draw the glyph in its own column so member names line up. `+ save(): void` and `save(): void` are both valid; a line
with no glyph is just drawn without one. Nothing is parsed further (no type or parameter model), so a member always
round-trips exactly as typed, apart from surrounding whitespace.

## Shapes and kinds

`src/Draughtsman.Core/Schemas/uml-shapes.json` is the library (embedded; `get_schema` returns it as `umlShapes`).

| Shape | Architecture kind | Look |
| --- | --- | --- |
| `custom:uml/class` | `class` | name, attributes and operations compartments |
| `custom:uml/interface` | `interface` | the same box with an `«interface»` stereotype row above the name |
| `custom:uml/lollipop` | `provided-interface` | a ball on a stem (provided interface) |
| `custom:uml/socket` | `required-interface` | a half-circle on a stem (required interface) |

Lollipop and socket are small fixed symbols (72 by 48). The symbol fills the top half of the box, its stem starts at the
left edge, and the label sits in the bottom half. They declare only a `left` and a `right` default port, both at the
symbol's mid-height, so a realisation or dependency line connects to the stem end. The relationship line-ends
(inheritance, aggregation, composition, realisation, dependency) are edge styles, not shapes, and already exist.

They are usable in **architecture** documents (each kind names its shape as its `defaultShape`, so the DSL elides
`shape=`) and in **freeform** documents. Freeform has no kind vocabulary, so a kind alone gives a plain rectangle there:
write `shape=custom:uml/class` explicitly (the palette does, and the printer writes it back). A dedicated `class`
document type is deferred.

## The compartment model

Compartments are **derived, never stored**. `layoutClass` (TypeScript, `web/src/model/umlShapes.ts`) and
`UmlShapeLibrary.LayoutClass` (C#, `src/Draughtsman.Core/Schemas/UmlShapeLibrary.cs`) compute them from the content with
the numbers in the library's `metrics` block:

- The name band is `2 × padY + rowHeight` tall, plus one `rowHeight` when there is a stereotype row.
- Each member compartment is `2 × padY + rowHeight × max(1, members)`: an empty compartment keeps one row.
- With the shipped metrics (`padY` 5, `rowHeight` 18) a class with one attribute and one operation is 28 + 28 + 28 = 84
  tall; an interface's header is 46.
- The natural width is the longest line at `charWidth` per character (the bold name at 1.1 times that), plus the glyph
  column when the compartment has glyphs and `2 × padX`, never under `minWidth`.
- A box **taller** than its natural height gives the extra to the operations compartment, whose bottom edge is the
  box's own; the lines above never move. A box shorter than its content is not squeezed: the text overflows the bottom.

The same metrics are read by three consumers, and a test in each pins the same literal numbers:

1. the web canvas and exports (`web/src/canvas/shapes.tsx` `umlRender`, over `umlShapes.ts`);
2. the server SVG renderer (`SvgRenderer.UmlMarkup`), used by `render_document` and PDF export;
3. the layout sidecar (`sidecar/src/layout/umlSize.ts`), which sizes a class that arrives without geometry (a DSL or
   agent-written class) so ELK leaves room for its members. The sidecar image is built from `sidecar/` alone, so it
   keeps a copy of the metrics and `sidecar/test/umlSize.test.ts` fails if they drift from the JSON.

"Sized from its content" means: a placed class gets its natural size (`UML_OPTIONS`, via `NodeTemplate.size` and
`NodeTemplate.props`), and every member edit refits the box in the same undo step (`setClassMembers` →
`fitClass`: height exactly the natural height, width only ever grown).

## Editing

- **Name**: double-click the name band, or select the class and press Enter (the ordinary label editor).
- **Members**: double-click the attributes or operations compartment, or select the class and run *Edit attributes* /
  *Edit operations* from the Edit menu or the command palette. A text field opens over the compartment, one member per
  line; Enter adds a line, Cmd/Ctrl+Enter or clicking away commits, Escape discards. Blank lines are dropped.
- A commit is `useDocumentEditor.setClassMembers`: the list, the refit and the prop removal (an empty list deletes the
  prop) are one undo step; an unchanged list writes nothing.

## DSL spelling

```
draughtsman 1
type architecture
title "Orders"

node c_order class "Order" @ 0,0 156x120 attributes=["- id: UUID","+ total: money"] operations=["+ pay(): void","# audit(): Log"]
node c_repo interface "OrderRepo" operations=["+ find(id: UUID): Order"]
node c_ip provided-interface "IOrder"
edge e_impl dependency c_order -> c_ip
```

In a freeform document add `shape=custom:uml/class` (or `interface`, `lollipop`, `socket`). Geometry may be omitted;
layout then sizes the box from its members.

An agent edits members with `patch_document`: `setProp` with `key` `attributes` (or `operations`) and a JSON array as
`value`. The patch does not refit the box (only the editor does), so an agent that adds members should also give the node
a taller `geometry` (`updateNode`), or leave geometry off and let layout size it.

## Reusing the mechanism for ER entities

An entity is the same shape of problem: a name plus a list of rows. Reuse, don't copy:

- a library JSON with its own `metrics` and shapes (`custom:er/entity`, `custom:er/weak-entity`), read by the same three
  consumers;
- row lists as `props` string arrays (a PK/FK marker can be a leading glyph in the row text, exactly as visibility is
  here) or, if rows need structure, arrays of small objects, which the DSL prints as JSON just the same;
- a derived layout function mirrored in TypeScript and C#, a `fit…` function, and `setClassMembers`-style editor commands
  that write the list and refit in one undo step;
- the `ShapeContent` argument of `renderShape` (label, props, and which part the editor covers) and the `editingPart`
  plumbing through `DiagramSvg`/`NodeView`, which already exist for this;
- `compartmentAt`/`MembersEditor` generalise by swapping the class layout for the entity's.

## Known limits and follow-ups

- Text does not wrap or clip: a member wider than the box overflows the right edge, and shrinking a box below its natural
  height by dragging overflows the bottom. The next member edit refits it.
- A member change made outside the editor (an agent patch, the Text panel) does not refit the box until the next editor
  member edit.
- A class inside a container does not grow its container when a member edit makes it larger.
- `critique_document` has no class-specific rule (for example, an empty class); it stays purely structural.
- The AI outline used by diagram-to-document carries labels only, so members are not summarised; `generate-from-text` in
  update mode sees them because it sends the full DSL.
- Mermaid `classDiagram` import, and a dedicated `class` document type with DSL sugar, are not built.
