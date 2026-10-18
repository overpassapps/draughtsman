# Importing a draw.io diagram

Draughtsman reads a draw.io (diagrams.net) file into a new diagram, so you can bring in what you already drew. It keeps what draw.io
stores best, the shapes with their labels, sizes and positions, the containers they sit in and the connectors between them, and it
says so plainly about what it could not carry. Nothing is saved until you create the diagram.

## From the editor

1. Open **Import draw.io**: the button next to **Import Mermaid** on the home screen, or **File, Import draw.io…** in the editor
   (or the command palette, Ctrl/Cmd+K).
2. Choose a `.drawio` or `.xml` file, or paste its XML. Both of draw.io's spellings are read: plain XML, and the compressed form draw.io
   saves by default (the file's `<diagram>` holds base64 text rather than XML).
3. Choose the **Diagram type** (below), then **Read diagram**. The dialog shows what it found (`Found 12 shapes and 14 connectors.`)
   and lists every warning, so a wrong file or a wrong type is caught before a document exists.
4. A file with several pages imports one at a time: the first, with a **Page to import** list to pick another (which reads again at
   once). Each import makes its own diagram.
5. **Create diagram** saves it and opens it. The diagram is **not** laid out: draw.io already placed every shape, and moving them would
   undo the work of whoever drew it. **Tidy layout** is there if you want it.

## What comes across

| In draw.io | In Draughtsman |
| --- | --- |
| A shape's label | The node's label, as plain text. An HTML label (`html=1`, which is nearly all of them) is reduced to its text: line breaks (`<br>`, paragraphs, list items, table rows) stay, every tag, script and style is dropped, and a character reference such as `&amp;` is read once. A label that is not HTML is kept exactly as written. |
| A shape's position and size | The node's `geometry`, absolute: a shape inside a container is relative to it in draw.io, and the container's origin is added. A rotation is kept. A shape drawn as an icon (a stick figure, a cloud) is grown to at least 96 × 96 about its centre, because its label sits in a band of the node's own width (a 30-wide figure would stack its label one letter per line). |
| A swimlane, a group, a `container=1` shape, or any shape with other shapes inside it | A `container` node that holds those shapes (`parent`), written before them so it is drawn underneath. A plain shape that turns out to hold others is flagged, since its kind was a judgement. |
| A connector | An edge between the same two shapes (a shape or a container), with its label, `routing` mode, line colour and width, dash and arrowheads. |
| A connector's extra label (a separate label cell dragged onto it) | The edge's label, when the connector has none of its own; any further ones are not carried, and the connector says so. |
| An orthogonal connector (`edgeStyle=…`) | `routing: orthogonal` with no stored bends: draw.io's points for it are hints for its own router, and the editor's router finds the route from the shapes. |
| A straight or curved connector with points | `routing: straight` or `curved` with those points as its waypoints, in absolute positions. |
| Fill, outline, text colour, font, size and bold, dashed | The node's sparse style. Colours are carried only as hex (`#dae8fc`); `none` becomes `transparent`; the light half of a `light-dark(light, dark)` pair is used. |
| A shape's custom data (`object` and `UserObject` wrappers) | `props`, one entry per attribute (`link` as `url`, only when it is a web or mail address; `%name%` placeholders in the label are filled in first). Up to 50 entries of 2,000 characters each. |
| ER and UML arrowheads (`ERone`, `ERmany`, `ERzeroToMany`, a hollow triangle, a hollow or thin diamond …) | The matching head (one, many, zero or many, inheritance, aggregation, composition …), and the connector becomes an `association`. A connector with no arrowhead at either end is an `association` too; any other is a `flow`. |

## What is guessed, and what is left out

A drawing has no types, so the kind of each shape is a guess from how it is drawn. A guess that is a real judgement (an ellipse might be a
start, an end, a state or a system) is flagged `kind-guessed` with the shape's label, and the dialog counts those apart from what was lost,
so you know which kinds to check. A plain box, a diamond, a cylinder, a parallelogram and the ISO 5807 flowchart symbols are not flagged.

| In draw.io | In a flowchart | In an architecture |
| --- | --- | --- |
| Rectangle, rounded rectangle | process | service |
| Ellipse (flagged) | terminator | service |
| Diamond (rhombus) | decision | service (flagged) |
| Cylinder | stored data | database |
| Parallelogram | data (io) | queue (flagged) |
| Hexagon (flagged) | process | queue (flagged) |
| Stick figure (actor) | process, drawn as a user icon (flagged) | actor, drawn as a user icon |
| Cloud | process, drawn as a cloud icon (flagged) | external system, drawn as a cloud icon (flagged) |
| Document, predefined process, manual input, delay, display and the other flowchart symbols | the symbol's own shape, on its kind | the symbol's own shape on a service |
| Note (the folded-corner shape) | note, drawn as a folded note | note, drawn as a folded note |
| Trapezoid, step (chevron), plus and cross, single and double arrows, cube, folder, component and module | the matching [basic shape](basic-shapes.md), on a process | the matching basic shape, on a service |
| Callout | note, drawn as a callout | note, drawn as a callout |
| Triangle pointing up (`direction=north`) | process, drawn as a triangle | service, drawn as a triangle |
| A folder that holds other shapes | a container drawn as a folder (a UML package) | a container drawn as a folder |
| Text | text | text |

**Freeform** has no kind vocabulary, so nothing is guessed: every shape is a plain `shape` of the shape it looks like, and nothing is
flagged. Choose it when you only want the picture and its positions.

Whatever Draughtsman has nothing for is a warning, never a failure. The shape is drawn as a plain box and the warning names what it was:

| Code | When |
| --- | --- |
| `unsupported-syntax` | A stencil or any other shape without an equivalent (`mxgraph.aws4.lambda_function`, a star, a curly bracket, a table, a triangle or arrow draw.io has turned another way), a picture (an image shape: pictures are not imported and nothing is fetched from their address), a second label on a connector, a link that is not a web or mail address, a label cut at 10,000 characters, a shape in a cycle of parents, a repeated id, several layers, and, in one warning with a count, everything that was hidden. |
| `missing-reference` | A connector with a loose end, or attached to a cell that is not in the file, to another connector or to a hidden shape, is left out. A shape whose parent is not in the file sits at the top level. |
| `style-approximated` | A colour that is not hex (`lightblue`, `rgb(…)`), a font name that is not plain, or an arrowhead with no equivalent (it becomes a plain arrowhead). |
| `page-selected` | The file has several pages and one was imported. |
| `id-renamed` | An id was changed to fit the id rules: ids are `n_<draw.io id>`, `c_<id>` for containers and `e_<id>` for connectors. |
| `empty-diagram` | The file or the page has no shapes. |

At most 500 warnings are listed; a last one says how many more there were.

## Safety and limits

A `.drawio` file is XML from whoever sent it, and compressed XML can be a thousand times bigger than the file. It is read to refuse rather
than to cope:

- **No `DOCTYPE`.** draw.io never writes one, and it is how an XML file smuggles in entity bombs ("billion laughs") and reads of other
  files (XXE). The file is refused before anything in it is read; no entity is expanded, nothing is looked up on the disk or the
  network, and a picture's address is only ever text.
- **A compressed page stops at 16 MiB.** The decompressor is read through a stream that stops there, so a small upload that would
  inflate to gigabytes is refused after the first 16 MiB, not after it has used the memory.
- **Counted limits**, each refused with a message that names it: 8,388,608 characters of XML, 150,000 elements, nesting 32 levels deep,
  1,000 attributes on one element, 10,000 shapes and connectors, 500 pages. A real file is nowhere near any of them; a file past one
  is probably worth splitting into pages.
- **Labels are text.** A label's HTML is reduced to its text in one pass, so a `<script>`, an `onerror`, a `javascript:` link or an
  `<iframe>` in a draw.io label never reaches a diagram.
- **At most 1,000,000 characters** per request, as for the other importers, and the same sign-in applies as to every other route.

A refused source is a `422` from the API (an error in the editor) with the reason in the message; it is never a half-read diagram.

## From a program or an agent

```sh
curl -s -X POST 'http://localhost:5180/api/import/drawio?type=architecture&page=1&title=Orders' \
  -H "Authorization: Bearer $DRAUGHTSMAN_TOKEN" -H 'Content-Type: application/xml' \
  --data-binary @orders.drawio
```

answers `{ "document": …, "warnings": […], "pages": […] }`. The document is placed, so save it as it is (`POST /api/documents`); there is
no layout step. The same file can be sent as one file of a `multipart/form-data` form. The full contract, the response, the codes and
the error statuses are in [`api.md`](api.md#post-apiimportdrawio).

An agent uses MCP `create_document` with `format: "drawio"`, `content` the file's XML, `type` one of `flowchart`, `architecture` or
`freeform`, and `page` (from 1) for a multi-page file. It is laid out only if `layout` is `true`. Each warning comes back as
`<code>: <message>`.

## Not built

- Export to draw.io. Visio files import through their own route, and a Lucidchart document by exporting it to Visio first: see [`moving-from-lucidchart-visio-drawio.md`](moving-from-lucidchart-visio-drawio.md).
- draw.io's connection-point constraints (`exitX`, `entryX`): a connector attaches wherever the editor's router puts it.
- draw.io stencil libraries (AWS, Azure, Cisco, …): those shapes are plain boxes with their label and a warning.
- Pictures, shadows, gradients, glass, sketch mode, text alignment and rotation of text.
- Several pages in one go: each page is its own import.
