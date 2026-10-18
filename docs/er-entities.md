# ER entities (database tables)

Status: implemented (shape library batch 7). This note is the reference for the entity-relationship table shapes. It is
not a spec; the build spec and `docs/spec/` are unchanged. It applies the compartment mechanism of
`docs/uml-class-compartments.md` to a table; read that note for the shared parts (derived layout, the three consumers of
the metrics, the member editor).

## What an entity is

A table is **one node**. The name is the node `label`; the rows live in `props.columns`, an array of strings with **one
column per string**. There is no new field on `Node`, no schema change and no DSL grammar change: `props` already
carries any JSON, and the DSL already prints a JSON array on the node line. Documents saved before this change are
unaffected.

| Shape | Architecture kind | Look |
| --- | --- | --- |
| `custom:er/entity` | `entity` | a name band, then one row per column |
| `custom:er/weak-entity` | `weak-entity` | the same box with a double border, for a table that cannot be identified without its owner |

Both are usable in **architecture** documents (each kind names its shape as its `defaultShape`, so the DSL elides
`shape=`) and in **freeform** documents. Freeform has no kind vocabulary, so a kind alone gives a plain rectangle there:
write `shape=custom:er/entity` explicitly (the palette does, and the printer writes it back). No `er` document type was
added: the smaller change was a palette group and two architecture kinds, the same route as the UML class shapes and the
org chart. A dedicated type is a later, separate decision.

## The column line

```
[PK] [FK] name type [not null | null]
```

| Part | Rule |
| --- | --- |
| Key markers | `PK` and `FK`, any case, first, in either order, each at most once. A column that is both writes both (`PK FK OrderId int not null`). A word is a marker only while something else follows it. |
| Name | One word. A name with spaces is double quoted: `PK "Order Id" int not null`. Anything else that is not a space or tab belongs to the word. |
| Type | Everything after the name up to the nullability, spaces collapsed. It may hold spaces, commas and parentheses: `nvarchar(50)`, `decimal(10, 2)`, `datetime2`, `uniqueidentifier`, `rowversion`, `double precision`. It may be empty. |
| Nullability | A trailing `not null` or `null`, any case. Omit it when unknown; nothing is drawn. |

Examples: `PK Id int not null`, `FK UserId nvarchar(50) not null`, `Email nvarchar(100) null`, `PK FK OrderId int not null`.

A **composite primary key** is several `PK` rows, and a foreign key that is part of it is `PK FK`. A **composite foreign
key** is one `FK` row per column. The shape does not group them; the rows carry the meaning.

The line is stored exactly as typed. Parsing (`parseColumn` in `web/src/model/erShapes.ts`, `ErShapeLibrary.ParseColumn`
in Core, `parseColumn` in `sidecar/src/layout/erSize.ts`) only derives what is drawn, so a column always round-trips
through JSON and the DSL unchanged. The three parsers are pinned by the same list: the `examples` array in
`src/Draughtsman.Core/Schemas/er-shapes.json`, which each implementation's tests replay. A line that would be read
differently is a parser bug, not a test to edit.

Note on markers: a leading `pk` or `fk` word is always a marker, so a column that is really *named* `pk` or `fk` must be
double quoted (`"pk" int not null`). Bare `PK` alone is a column named `PK`, and `PK PK int` is a primary key column
named `PK`; neither is expected in real schemas, and both are pinned so the three parsers cannot disagree.

## Drawing

Rows are drawn as real, left-aligned columns: key marker (bold), name, type, then `NULL` / `NOT NULL`. Each column
starts at one x for every row; a column no row fills (no markers, or no nullability anywhere) is left out entirely and
takes no space. A rule sits under the name band, and a divider under the **leading run of `PK` rows** when other rows
follow it (none when there is no primary key, when every row is a key, or when a `PK` row sits below a plain one). The
name is centred and bold in the name band so the ordinary label editor covers it exactly.

Layout is derived, never stored, with the numbers in the library's `metrics` block (same three consumers as the class
shapes: `web/src/canvas/shapes.tsx` `erRender`, `SvgRenderer.ErMarkup`, `sidecar/src/layout/erSize.ts`):

- The name band is `2 x padY + rowHeight` = 28 tall; the row area is `2 x padY + rowHeight x max(1, rows)`.
- Each column is as wide as its widest cell at `charWidth` per character (key markers and the upper-case `NULL` / `NOT NULL` at `boldWidth` times that), with
  `columnGap` between neighbours; the natural width is the columns plus `2 x padX`, never under the bold name's width or
  `minWidth`.
- A box taller than its natural height gives the extra to the bottom of the row area; the lines above never move.
- A weak entity draws an inner rectangle `doubleInset` inside the outer one and pulls its rules in to it.

The palette's default entity (three columns) is 270 by 92.

## Editing

- **Name**: double-click the name band, or select the table and press Enter.
- **Columns**: double-click the row area, or select the table and run *Edit columns* from the Edit menu or the command
  palette. A text field opens over the row area, one column per line, in the line format above; Enter adds a line,
  Cmd/Ctrl+Enter or clicking away commits, Escape discards. Blank lines are dropped. This is the class member editor
  (`MembersEditor`), generalised through `web/src/model/memberParts.ts` `memberPartsOf`.
- A commit is `useDocumentEditor.setClassMembers(id, 'columns', lines)` (the name predates entities): the list, the refit
  (`fitEntity`: height exactly the natural height, width only ever grown) and the prop removal (an empty list deletes the
  prop) are one undo step; an unchanged list writes nothing.
- The palette's **ER entity** group (architecture and freeform, closed by default) places a table with three starter
  columns, or a weak entity with a composite `PK FK` key.

## Relationships

Draw a relationship as an ordinary edge between two entity nodes (or between two of their rows, see Column ports below), kind `association`, whose `style.arrowStart` and
`style.arrowEnd` are the crow's-foot ends already in the schema: `one`, `zeroOrOne`, `oneAndOnlyOne`, `many`,
`zeroOrMany`, `oneOrMany` (the connector panel groups them under ER). One customer to many orders:

```
draughtsman 1
type architecture
title "Shop"

node t_customer entity "Customer" @ 0,0 270x92 columns=["PK Id int not null","Name nvarchar(100) not null","Email nvarchar(100) null"]
node t_order entity "Order" @ 400,0 270x92 columns=["PK Id int not null","FK CustomerId int not null","Placed datetime2 not null"]
edge e_places association t_customer -> t_order style.arrowStart=oneAndOnlyOne style.arrowEnd=zeroOrMany
```

Geometry may be omitted; layout then sizes each table from its columns. An agent edits columns with `patch_document`:
`setProp` with `key` `columns` and a JSON array as `value`; like class members, a patch does not refit the box (only the
editor does), so give the node a taller `geometry` as well, or leave geometry off and let layout size it.

`get_schema` returns the library as `erShapes` whenever a `type` is named; its `description` is the full column grammar
and the `entity` kind's one-line description repeats the essentials, so an agent can write a correct table from
`get_schema` alone.

## Column ports

A relationship can leave from the foreign key's own row and arrive at the referenced key's row, instead of attaching to the
table box. A row is addressed as a **derived port**, `col:<column name>`, on an edge endpoint:

```
edge e_places association t_order.col:CustomerId -> t_customer.col:Id
```

- **Derived, never stored.** The port is not in the node's `ports` (those are fixed fractions of the box, which go stale the
  moment a column is added, removed or reordered). It is resolved against the entity's own layout every time it is used, so
  an edge stays attached to its row through any edit of the column list.
- **Where it attaches.** At the vertical middle of the row (a distance `2 x padY + rowHeight + padY + rowHeight x (i + 0.5)`
  below the top of the box: 42, 60, 78 ... for the default metrics), on the left or right edge of the table, whichever faces
  the other end (its centre, or its first waypoint on a pinned connector). An orthogonal connector therefore leaves and
  arrives horizontally. Two edges on the same row fan out along it a few units, as edges sharing a declared port do.
- **Spelling.** `col:` then the column name with every byte outside letters, digits, `_` and `-` written `%XX` (UTF-8,
  upper-case hex): `col:CustomerId`, `col:Order%20Id` for `"Order Id"`, `col:a%2Eb`. That keeps the id inside the schema's
  port pattern (`portRef` in `docs/schema/document.schema.json`) and the DSL's bare `node.port` token, with no quoting rule
  and no dot to confuse the parser's node/port split. A name whose id would pass 128 characters has no port and cannot be
  addressed; the SQL importer then leaves that end on the table.
- **A column that does not exist** (renamed by hand, deleted, or a typo) is never a failure: that end is drawn at the table
  exactly as an end with no port. The DSL parser warns (`dsl-lint`) and `critique_document` reports it as the `unknown-column`
  warning, so an agent can find and fix it.
- **The editor keeps it attached.** Renaming a column in the column editor re-points every edge end on that row in the same
  undo step (`columnRenames` pairs a retyped name with the name it replaced when the counts match; adding, removing or
  reordering rows renames nothing, and an edge on a removed row falls back to the table).
- **Connecting.** In the connector tool a pointer over an entity's rows highlights the row under it and a press or release there
  attaches to that row (a touch or pen press is the same); over the name band it attaches to a table port as before. Dragging a
  selected connector's end onto another row re-attaches it. The select tool's connection handles stay on the table's ports.
  There is no keyboard route to connect at all (as for every connector), so none to a column.
- **SQL import** draws each foreign key from its FK column's row to the referenced key's row (for a composite key the first
  column pair, the label still naming them all; with no column list on the `REFERENCES`, the parent's primary key). A column the
  table does not have leaves that end on the table. SQL export ignores ports.
- **Layout** (`POST /api/layout`) does not pin a route for an edge with a column end: ELK routed to the table box, so its bend
  points would miss the row, and the shared router (`web/src/canvas/routeDocument.ts`, also bundled into the sidecar for the
  server render and the PDF) places the end instead.

Where the numbers live: `web/src/model/columnPorts.ts` (with `canvas/ports.ts` `endpointPort` for the canvas index, the
router's attachments and the export) and its C# twin `ErShapeLibrary.ColumnPort` (server render, DSL lint, critique, SQL
import); both are pinned with the same literals.

## Known limits and follow-ups

- Column ports are entity-only: a UML class's attribute and operation rows are not addressable the same way (the mechanism
  would carry over: a derived `attr:<name>` port resolved against `layoutClass`), and a column row is one whole row wide, so a
  connector cannot attach to the type or nullability cell alone.
- Text does not wrap or clip: a cell wider than the box overflows the right edge, and shrinking a box below its natural
  height overflows the bottom. The next column edit refits it.
- A column change made outside the editor (an agent patch, the Text panel) does not refit the box until the next editor
  column edit; a table inside a container does not grow the container.
- `critique_document` has one entity rule, `entity-without-primary-key` (warning, every document type): a `custom:er/*` node
  none of whose column lines starts with a `PK` marker, or with no columns. It is structural like the others; it does not judge
  whether a key is a good one.
- SQL `CREATE TABLE` import and DDL export are built (docs/api.md `POST /api/import/sql`, `POST /api/export/sql`; the code
  is `src/Draughtsman.Core/Import/Sql/`). Unique/index markers (`UK`), column defaults and identity are not modelled: a
  `UNIQUE` constraint or `CREATE UNIQUE INDEX` is read only to tell a one-to-one relationship from a one-to-many one (its
  columns must be exactly the foreign key's), and is written back as a `UNIQUE` constraint.
- SQL import reads a `[TAG]` in the comment block directly above a `CREATE TABLE` (`-- [V2 NEW] ...`, or inside `/* */`; a
  blank line ends the block) as the table's tag: it is kept as `props.tag` and colours the table, and a "Legend"
  container with one swatch note per tag is added. A tag containing "shared" stays neutral; every other distinct tag takes
  the next of six fixed colours in order of first appearance, with fill, stroke and text colour explicit so exports read
  without the theme. SQL with no tags imports exactly as before; `?tags=off` (docs/api.md), `SqlImportOptions.Tags` and the
  Import SQL dialog's checkbox turn it off (MCP `create_document` always reads them).
- SQL import marks a **weak entity**: a table whose primary key is composite and includes a foreign key column to another table
  in the source is placed as `weak-entity` (`custom:er/weak-entity`, the double border); every other table is an `entity`. The
  test is structural, so a many-to-many link table (a key made of two foreign keys) is drawn weak too; retype it by hand if
  that is wrong. Only real foreign keys count.
- SQL import also reads relationships a comment declares that the database does not enforce (`-- -> UserTypes.Id (no FK)` on a
  column's line, or a listed `dbo.Users.TypeId -> dbo.UserTypes.Id`; the exact accepted shapes are in docs/api.md
  `POST /api/import/sql`) as a **dashed** `association` edge with the same column ports and crow's feet as a foreign key,
  labelled `TypeId (no FK)`, and does not mark the column `FK`. `?implied=off`, `SqlImportOptions.Implied` and the Import SQL
  dialog's checkbox turn it off (MCP `create_document` always reads them). The DDL export ignores such an edge.
- Mermaid `erDiagram` import and export are built (docs/api.md `POST /api/import/mermaid` and `POST /api/export/mermaid`; the
  code is `MermaidErImporter` and the entity branch of `MermaidExporter` in `src/Draughtsman.Core/Import/`). Import makes one
  `entity` per block and one `association` edge per relationship line, table to table (Mermaid has no column-level
  relationships, so no column port), with the two cardinality tokens as `arrowStart`/`arrowEnd` and `..` as a dashed line.
  Nullability is not in Mermaid, so an imported column line has none; `UK` and attribute comments are dropped with a warning.
  Export then import is stable.
- The AI outline used by diagram-to-document carries labels only, so columns are not summarised.
