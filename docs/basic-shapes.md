# Basic shapes

Draughtsman's flowchart, architecture and freeform documents can use thirteen general-purpose outline shapes and annotation
marks: the ones draw.io, Lucidchart and Visio put in their basic palettes, and the UML marks that are just an outline. They
are placed from the shape panel's **Basic shapes** group (closed until you open it or search), written in the DSL and JSON as
the presentation shape `custom:basic/<name>`, and returned by `get_schema` as `basicShapes`. The data is
`src/Draughtsman.Core/Schemas/basic-shapes.json`, which the web canvas and the server SVG renderer both read, so a shape
draws the same in the editor, in an export and in `render_document`.

A basic shape needs **no kind of its own**. It is a way to draw a box, so put it on whatever kind the thing is: `process`
in a flowchart, `service` in an architecture diagram, `note` for an annotation. The palette does exactly that, and a node
on a real kind raises no `unknown-kind` finding. The shape is written out in the DSL because it is not the kind's default:

```
node n_gate process "Check stock" shape=custom:basic/trapezoid
node n_why note "Rate-limited upstream" shape=custom:basic/callout
```

## What ships

| Shape | Name | Use |
| --- | --- | --- |
| Ellipse | `custom:basic/ellipse` | A general oval or circle; a UML **use case**. A connector attaches to its outline. |
| Triangle | `custom:basic/triangle` | Apex up. |
| Trapezoid | `custom:basic/trapezoid` | Narrow at the top. |
| Chevron | `custom:basic/chevron` | A process step or phase in a row of arrows. |
| Cross | `custom:basic/cross` | A plus sign. |
| Arrow | `custom:basic/arrow` | A block arrow pointing right; rotate the node for another direction. |
| Double arrow | `custom:basic/double-arrow` | A block arrow pointing both ways. |
| Folded note | `custom:basic/note` | A note with a folded corner; a UML **note**. An annotation. |
| Callout | `custom:basic/callout` | A speech bubble with a tail at the bottom left. An annotation. |
| Brace | `custom:basic/brace` | A curly bracket `{` whose label sits to its right, to group or comment on a run of shapes; rotate it for `}` or a horizontal brace. A line, with no fill. An annotation. |
| Cube | `custom:basic/cube` | A 3D box; a UML **node** in a deployment diagram. |
| Folder | `custom:basic/folder` | A tabbed folder; a UML **package**. Give it the `container` kind (or parent shapes to it) and it holds them, with its name under the tab. |
| Component | `custom:basic/component` | A box with the UML component glyph in its top right corner. |

An **annotation** is a mark that comments on the diagram instead of taking part in it. The palette puts the note, callout and
brace on the `note` kind; everything else goes on the document type's plain box kind. `critique_document` is silent about
all of them.

## How a shape is defined

A shape is data, not code, so adding one means editing the JSON file and nothing else: both renderers, the palette, the
default connection points and the tests all read it. Each entry has:

- `parts`: the outline first (painted with the node's fill and stroke), then any details (painted with its stroke, and its
  fill too when the part says `"fill": true`). A part is path data in the node's box.
- `labelBox`: the rectangle the label is laid out in, as `[left, top, right, bottom]`.
- `ports`: where a connector lands, as plain fractions of the box (`top`, `right`, `bottom`, `left`). Only the ports a
  shape has are listed: the brace has just its tip. These are the default ports a node without declared ports offers.
- `size`: the box the palette places the shape at. `keywords`: other names the palette search matches (`package`, `use case`).
- `annotation`: true for a note-like mark.

Path data uses the absolute commands `M`, `L`, `C`, `H`, `V` and `Z` only. A coordinate is a **fraction of the box** (x of the
width, y of the height) and may add or subtract a fixed length in pixels: `0.5` is the middle, `1-16` is 16 px in from the far
edge, `0+8` is 8 px in from the near edge. A fixed length never exceeds half the box, so a small box squeezes a fold or a
tab instead of turning the outline inside out. Coordinates are rounded to two decimals, half up, in both renderers.

## Importing from draw.io

draw.io shapes with a counterpart here arrive as that shape with nothing to warn about: `trapezoid`, `step` (a chevron),
`plus` and `cross`, `singleArrow`, `doubleArrow`, `callout`, `cube`, `folder`, `component` and `module`, and the folded-corner
`note`. A folder that holds other shapes stays a folder, as a container. draw.io turns some of these with `direction=`,
so one is mapped only when it faces the way the shape here does (east, or north for a triangle); a turned one stays a
plain box named in an `unsupported-syntax` warning, as `star`, `tape`, `wave` and `curlyBracket` do. See
[`drawio-import.md`](drawio-import.md).

## Left out

Shapes considered and not added: the star and other polygons (pentagon, octagon), the wave and tape, a cloud (the generic
icon library has one), a stick-figure actor (the `user` icon), and further block-arrow variants (bent, U-turn). The
thirteen above cover what the three tools' basic palettes are mostly used for; the data format makes any of the others a
single entry if it turns out to be wanted.
