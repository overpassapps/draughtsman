# Draughtsman DSL

The DSL is the diagram an agent reads and writes. It carries the whole document model, ids and geometry included,
so a human can hand-edit it and an agent can patch it without wrecking the layout. The formal grammar is
[`dsl.grammar`](dsl.grammar); this page explains it. The worked example is
[`docs/examples/order-service.dsl`](../examples/order-service.dsl), which is the same document as
[`order-service.json`](../examples/order-service.json).

## API

```csharp
using Draughtsman.Core.Text;

ParseResult result = Dsl.Parse(text);   // result.Document, result.Warnings
string text = Dsl.Print(document);
```

`Dsl.Parse` throws `DslParseException` on the first syntax error, with 1-based `Line` and `Column` and a message
such as `line 7, column 22: rotation needs a position: write @ x,y before it`. A number the grammar admits but that
falls outside the range of a double (`1e400` and friends) is one of those syntax errors, reported at the offending
token, mirroring `Dsl.Print`'s refusal to write NaN or infinity. That rule is about the fields the parser stores as
doubles (geometry, sizes, `rotation`, `style.strokeWidth`, port offsets and waypoints), not about prop values, which
are JSON and keep the raw text they were written with; see [Props](#props). Lint-level problems never stop a parse
and come back as `DslWarning` records: an unknown node or edge kind for the document type, an edge that refers to a
node or port that is not declared, a `parent=` that points at nothing. Freeform documents have no kind vocabulary
and so no kind warnings.

A value outside what `docs/schema/document.schema.json` admits for a field is lint too, never a syntax error: one
bad word in a pasted diagram must not reject the whole of it. The value falls back to the field's default and a
warning names it, so the document `Dsl.Parse` returns always satisfies the schema. This covers an unknown `shape=`
(the kind's default shape is used), a negative width or height (the defaults are used), a negative
`style.strokeWidth` (the override is dropped and the theme's is inherited), and a port offset outside 0 to 1
(clamped).

`Dsl.Print` throws `ArgumentException` when the document cannot be written: an id that does not match the id
pattern, or a NaN or infinite number.

## The round trip

`Dsl.Parse(Dsl.Print(doc)).Document` is structurally equal to `doc`, and `Dsl.Print` of that parsed document is
byte-identical to the first print. Structural equality means the canonical JSON form is identical. The fuzz test
`DslRoundTripFuzzTests` generates seeded random documents (dotted ids, labels with quotes and unicode, optional
geometry, ports, sparse style, reserved-looking prop keys, nested JSON props, containers, dangling references) and
checks this; it runs 500 iterations in CI and more with `DRAUGHTSMAN_FUZZ_ITERATIONS=n`. On a failure it shrinks
the document and prints the smallest offending DSL.

Two normalisations apply, both documented so nobody is surprised:

- Empty collections print as absent. An empty style, props, ports, waypoints, theme, provenance list or flythrough
  reads back as `null`. Nothing else is lost.
- Comments and whitespace are not part of the model. Printing is canonical: single spaces, one statement per line,
  LF line endings.

## Shape of a file

```
draughtsman 1
id doc_order_service
type architecture
title "Order service"
meta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:05:00.000Z by=eric
provenance claude-code updated 2026-09-20T22:05:00.000Z model=claude-fable-5-1 { n_cache e_3 }

node n_web service "Web app" @ 40,40 320x80
container c_vpc "VPC" @ 16,168 568x300 style.dash=dashed { n_api n_db n_cache }
node n_api service "Orders API" @ 40,200 320x80 ports=[in@0.5,0 db@0.25,1 cache@1,0.5] owner="platform"
node n_db database "Postgres" @ 40,360 160x80
node n_cache queue "Redis" @ 400,360 160x80

edge e_1 flow n_web -> n_api.in "HTTPS" routing=orthogonal
edge e_2 data n_api.db -> n_db "read/write" routing=orthogonal style.arrowStart=arrow style.arrowEnd=arrow
edge e_3 data n_api.cache -> n_cache routing=orthogonal waypoints=[480,240]
```

The printer writes the header, a blank line, every node and container in document order, a blank line, every edge
in document order. The parser accepts the header lines in any order and nodes, containers and edges interleaved;
it keeps the order it reads them in. Only `draughtsman 1` (first line) and `type` are required. A short file an
agent might write is therefore:

```
draughtsman 1
type flowchart
node n_start terminal "Start"
node n_check decision "Valid?"
edge e_1 flow n_start -> n_check
```

## Header

| Line | Meaning | When absent |
| --- | --- | --- |
| `draughtsman 1` | Format version. Must be the first line. | Error. |
| `id <id>` | Document id. | `doc_` plus a slug of the title: `"Order service"` becomes `doc_order_service`. |
| `type flowchart\|architecture\|freeform\|sequence` | Selects the kind vocabulary and default shapes. | Error. |
| `title "<text>"` | Title. | Empty. |
| `theme key="value" ...` | Document-level theme token overrides. Keys such as `node.fill` are bare; others quoted. Values are always quoted. | None. |
| `meta created=<ts> updated=<ts> by=<text>` | Timestamps and author. Any subset. | Now (UTC, millisecond precision), created, `dsl`. |
| `provenance <agent> <created\|updated\|removed> <ts> [model=<text>] { ids }` | One AI audit entry per line. | None. |
| `flythrough loop=true\|false` | Whether the tour returns to its first keyframe and plays on. Only meaningful with `keyframe` lines. | The tour stops at the last keyframe. |

*Note, 2026-10-01 (documentation only, no change to the format):* the header table listed three document types; `sequence` has been accepted by the parser, the printer and `dsl.grammar` since the sequence-diagram work, and is documented in [`../sequence-diagrams.md`](../sequence-diagrams.md).

Timestamps have one spelling, read and written: `yyyy-MM-ddTHH:mm:ss.fffZ`, UTC to the millisecond, never quoted,
the same form as the JSON. An offset, a missing fraction or a quoted timestamp is an error that says what to write.
Sub-millisecond precision is not representable in either form.

## Nodes and containers

```
node <id> <kind> "<label>" [@ x,y [wxh]] [key=value ...]
container <id> "<label>" [@ x,y [wxh]] [key=value ...] [{ child ids }]
```

- `container` is a node whose kind is `container`. The ids in braces get `parent` set to the container. Children
  may be declared before or after the container line, and containers may nest by listing a container id. A node
  whose `parent` is not a container-kind node (or is not declared) carries `parent=<id>` on its own line instead.
- Geometry is optional on input. Without `@` the node's `Geometry` is `null` and the layout step places it. `@ x,y`
  alone gives the default size, 160x80. On output geometry is always written when present, with `rotation=<deg>`
  after it when set.
- `shape=` is written only when the shape differs from the kind's default in the document type's vocabulary
  (`rectangle` for unknown kinds and for freeform documents).
- The `shape=` values are those of the schema's `shape` pattern (`docs/schema/document.schema.json`): the core
  shapes `rectangle|rounded|diamond|cylinder|pill|hexagon|parallelogram|text|image`, the ISO 5807 flowchart symbols
  (`document|predefined-process|manual-input|off-page-connector|delay|manual-operation|display|stored-data|`
  `multi-document|merge|extract|on-page-connector|summing-junction|or|collate|sort|loop-limit|internal-storage|card`),
  and `custom:<lib>/<name>` for a library shape. The libraries shipped are `custom:generic/<name>` (unbranded tech
  and network icons, `generic-icons.json`), `custom:org/<name>` (org chart boxes: `role`, `executive`, `assistant`,
  `vacancy`), `custom:state/<name>` (UML state shapes), `custom:swimlane/pool|lane`, `custom:bpmn/<name>` (BPMN events, activities, gateways and data artefacts, each with a flowchart kind `bpmn-<name>`), `custom:basic/<name>` (general outline and annotation shapes: `ellipse`, `triangle`, `trapezoid`, `chevron`, `cross`, `arrow`, `double-arrow`, `note`, `callout`, `brace`, `cube`, `folder`, `component`; no kind of their own, so the shape is always written), `custom:uml/class|interface|lollipop|socket` and `custom:er/entity|weak-entity` (the last two families are architecture kinds `class`, `interface`, `provided-interface`, `required-interface`, `entity`, `weak-entity`). An unknown value is a
  warning and the kind's default shape is used. The kind vocabularies (`src/Draughtsman.Core/Schemas/`, read through
  `get_schema`) name which kind defaults to which shape, so a kind such as `pool`, `lane`, `load-balancer` or
  `composite-state` is written by kind and prints without `shape=`.
- `ports=[id@x,y ...]` lists named attachment points; x and y are fractions of width and height, 0 to 1.
- Style is written only when set: `style.fill`, `style.stroke`, `style.strokeWidth`, `style.textColor`,
  `style.font`, `style.dash`, `style.fontSize` (a number, 0 or more; a negative value is a warning and the default
  size is used) and `style.bold` (`true` or `false`). Colours and fonts are bare when they are single words (`#FFF`, `token:accent`) and
  quoted otherwise (`"rgb(1, 2, 3)"`, `"Inter, 14px"`).
- A kind that is not a plain word is quoted: `node n_x "my kind" "Label"`.

## Edges

```
edge <id> <kind> <from>[.port] -> <to>[.port] ["<label>"] [key=value ...]
```

- `routing=orthogonal|straight|curved`, then `waypoints=[x,y x,y ...]`. Waypoints need a routing mode.
- Style: `style.stroke`, `style.strokeWidth`, `style.dash`, `style.arrowStart`, `style.arrowEnd`, with arrow heads
  `none|arrow|open|diamond|circle` and the notation heads `one|many|zeroOrOne|oneOrMany|zeroOrMany|oneAndOnlyOne`
  (entity-relationship, crow's-foot style) and `inheritance|aggregation|composition` (UML). Any other value is a
  parse error listing the fourteen accepted spellings.
- Ids may contain dots, so `a.b.c` is ambiguous on its face. The parser resolves an endpoint as the whole token when
  that names a declared node; otherwise the longest declared node id that prefixes the token is the node and the
  rest is the port; otherwise the text after the last dot is the port. A document with both a node `n.api.in` and a
  node `n.api` that has a port `in` cannot express an edge to the port. Avoid dots in ids if you use ports.
- A table column is addressed as a derived port, `col:<name>`, on an endpoint of a `custom:er/entity` node: `t_order.col:CustomerId -> t_customer.col:Id`. The name has every byte outside letters, digits, `_` and `-` written `%XX` (UTF-8, upper-case hex), so `"Order Id"` is `col:Order%20Id`, and the id is at most 128 characters. It is never listed in `ports=[...]`. An unknown column parses, with a `dsl-lint` warning (`docs/er-entities.md`, Column ports).

## Flythrough

```
keyframe <id> @ x,y wxh [caption="<text>"] [duration=<ms>] [hold=<ms>] [easing=<name>]
```

A `keyframe` line is one stop of the camera path Present plays (`docs/flythrough.md`): the world rectangle the camera
frames, in canvas units, absolute like a node's geometry. Keyframes are kept in the order they are read, which is the
tour order; the printer writes them after the edges. A file with no keyframes has no tour.

- The view is required, size included, since there is no default to fall back to; a width or height of zero or less
  is a warning and the keyframe is skipped.
- `caption` is a quoted string. `duration` is how long the camera takes to arrive from the previous keyframe, in
  milliseconds (from the last keyframe, for the first, when the tour loops); `hold` is how long an autoplaying tour
  stays. Each is 0 to 600000, and a value outside that is a warning and the default (1500 and 2500). `easing` is
  `cut`, `linear`, `easeIn`, `easeOut` or `easeInOut` (the default); any other word is a syntax error listing them.
- Keyframe ids are unique within a file. Nothing else refers to a keyframe and it refers to no node, so a keyframe
  never dangles when an element is deleted.
- The Mermaid export, the layout and every renderer ignore the tour.

## Props

Any `key=value` whose key is not reserved is a prop and is preserved verbatim. Values are JSON-shaped:

| Written | Stored as |
| --- | --- |
| `owner="platform"` | string |
| `count=3`, `ratio=0.5`, `big=1e21` | number, raw text kept |
| `on=true`, `nothing=null` | boolean, null |
| `tags=["a",1]`, `meta={"k":{"n":[true]}}` | array, object (compact JSON on the one line) |

Two prop conventions carry compartment content for the compound shapes, as JSON arrays of strings with one line per
string, so they need no grammar of their own: `attributes` and `operations` (plus an optional `stereotype` string) on a
UML class or interface, and `columns` on an ER entity, each line `[PK] [FK] name type [not null|null]`. For example
`node c_order class "Order" attributes=["- id: UUID","+ total: money"] operations=["+ pay(): void"]` and
`node t_user entity "User" columns=["PK Id int not null","Email nvarchar(100) null"]`. `docs/uml-class-compartments.md`
and `docs/er-entities.md` define the line forms.

Strings are always quoted, so `"42"` stays a string and `owner=platform` is an error:
`owner=platform: string values must be quoted, write owner="platform"`. Prop string values, the title, theme values
and timestamps each have exactly one spelling, the one the printer writes.

Reserved bare keys are `id`, `kind`, `label`, `shape`, `parent`, `rotation`, `ports`, `routing`, `waypoints`,
`style` and anything starting `style.`. A prop with one of those names, or a name that is not a plain identifier,
is written with a quoted key: `"style.fill"="not a style"`, `"owner name"="x"`. A quoted key is always a prop.

## Strings and comments

Quoted strings are exactly JSON strings: `\"`, `\\`, `\n`, `\t`, `\r`, `\uXXXX`. Unicode is written raw. Labels
and titles are always quoted.

A `#` starts a comment when it begins a line or follows whitespace, outside quotes. `style.fill=#FF0000` and
`"C# service"` are not comments. Comments are dropped on parse; put durable notes in props.

## Design notes

- The header carries id and meta so the round trip is lossless. An agent writing a fresh diagram can omit them.
- Document order is the truth: nodes and containers print in the order they sit in `Document.Nodes`, so the
  spec's habit of writing the container after its children is one valid order among many.
- `Node.Geometry` became nullable for this format (C# record, JSON Schema and TypeScript type together). A
  node with null geometry has not been placed. For rendering before layout, see the
  [editor shell’s canvas behaviour](../../design/atlas/editor-shell.md#what-draughtsman-does).
