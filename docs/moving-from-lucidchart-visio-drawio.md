# Moving from Lucidchart, Visio and draw.io

You can bring an existing diagram into Draughtsman instead of drawing it again. Draughtsman reads **draw.io** files and **Visio**
(`.vsdx`) files. **Lucidchart** has no import of its own: you export from Lucidchart to Visio and import that file, which is what
this page walks through. Every route keeps what a diagram stores best, the shapes with their text, sizes and positions, the groups they
sit in and the connectors between them, and says plainly what it could not carry. Nothing is saved until you create the diagram, and
the diagram is **not** laid out, because the person who drew it already placed every shape.

| You have | Do this | Read |
| --- | --- | --- |
| A draw.io (diagrams.net) file, `.drawio` or `.xml` | **Import draw.io** | [`drawio-import.md`](drawio-import.md) |
| A Visio drawing, `.vsdx` (Visio 2013 or later) | **Import Visio**, below | [Visio](#visio-vsdx) |
| A Lucidchart document | In Lucidchart, **File, Export, Visio (VSDX)**, then **Import Visio** | [Lucidchart](#lucidchart) |
| An older Visio file, `.vsd` (Visio 2003 to 2010) | Open it in Visio, **File, Save As, Visio Drawing (.vsdx)**, then **Import Visio** | [Visio](#visio-vsdx) |
| Lucidchart's own `.lucid` file | Not read: export to Visio from Lucidchart instead | [Not built](#not-built) |

**Import Visio** and **Import draw.io** sit side by side on the home screen, and in the editor under **File, Import Visio…** and
**File, Import draw.io…** (or the command palette, Ctrl/Cmd+K).

## Visio (.vsdx)

### From the editor

1. Open **Import Visio**: the button next to **Import draw.io** on the home screen, or **File, Import Visio…** in the editor.
2. Choose the `.vsdx` file. There is no paste box: a `.vsdx` is a binary package, so the file itself is what is read. It can be up to
   3,000,000 bytes (about 2.9 MiB); a file with large pictures in it is over that, and the dialog says so before it uploads anything.
3. Choose the **Diagram type** (below), then **Read diagram**. The dialog shows what it found (`Found 12 shapes and 14 connectors.`) and
   lists every warning, so a wrong file or a wrong type is caught before a document exists.
4. A file with several pages imports one page at a time: the first, with a **Page to import** list to pick another (which reads again
   at once). Each import makes its own diagram.
5. **Create diagram** saves it and opens it, as drawn. **Tidy layout** is there if you want it.

### What comes across

| In Visio | In Draughtsman |
| --- | --- |
| A shape's text | The node's label. Paragraph and line breaks stay; text-run and field markers are dropped (a field keeps the text Visio last showed). |
| A shape's position and size | The node's `geometry`. Visio measures in inches from the page's **bottom-left** with y pointing **up**; the node is placed in canvas units (100 to the inch) from the **top-left** with y pointing down, so the picture is the same way up. A page that does not say how tall it is is taken as tall as its content. |
| A shape turned in Visio | The same rotation, about the shape's centre. A shape turned about a pin that is not its centre lands where Visio put it. |
| A group | A `container` node that holds its children, whose positions are worked out through every group they sit in, however deep and however the groups are turned. |
| A shape dropped from a master | Sized and drawn from the master: Visio stores only a dropped shape's position and text, so its size, outline and fill come from the master it is an instance of. |
| A connector glued at both ends | An edge from the shape at its start to the shape at its end, with its text as the label. A connector glued to a part of a stencil shape reaches that whole shape. |
| A connector's line ends | The nearest arrowhead (Visio's numbered line ends: a filled triangle is an arrow, a hollow one is UML generalisation, diamonds are aggregation and composition, 24 to 30 are entity-relationship ends). A connector with no arrowhead at either end is an `association`; any other is a `flow`. |
| A connector's routing | Right-angled (the default of a Visio dynamic connector), straight or curved as Visio says. The editor routes right-angled connectors from the shapes; bends drawn by hand are not carried. |
| Fill, line colour, line weight and pattern, text colour, size and bold | The node's sparse style, only where the shape (or its master) wrote them down as a colour. Visio's own 24 numbered colours and the document's colour table are looked up. |

### What is guessed, and what is left out

A Visio shape is a master with a name, or an outline with no name, so the kind of each is a guess from those. The name of a master wins
(Visio's `Process`, `Decision`, `Start/End`, `Document`, `Data`, `Database`, `Manual input`, `Off-page reference` and the rest of the
flowchart set, `Actor`, `Cloud`, `Note`, `Swimlane`, and Lucidchart's block names such as `ProcessBlock`); otherwise the outline decides: a box (rounded
when the shape has a corner radius), a diamond, an ellipse, a parallelogram, a hexagon, or a label with no outline. A guess that is a
real judgement (an ellipse might be a start, an end, a state or a system) is flagged `kind-guessed`, and the dialog counts those apart
from what was lost. The table is the same as draw.io's: [`drawio-import.md`](drawio-import.md#what-is-guessed-and-what-is-left-out)
lists the kind each becomes in a flowchart and in an architecture. **Freeform** guesses nothing.

Whatever Draughtsman has nothing for is a warning, never a failure, and **one warning says each kind of loss once, with its count**, so
a drawing of three hundred stencil shapes does not answer with three hundred lines:

| What | What happens | Code |
| --- | --- | --- |
| Shapes dropped from a stencil with no equivalent here (a network switch, a cloud-provider icon, a floor-plan symbol) | Drawn as a plain box with their text, named once per master with a count and a few examples. A stencil shape made of several parts is **one** box, not a container of its parts. | `unsupported-syntax` |
| A custom outline (an arrow, a star, a freehand path) | A plain box, counted once. | `unsupported-syntax` |
| Pictures and embedded objects | Not imported; each is a plain box, counted once. | `unsupported-syntax` |
| Shape data (custom properties) and data graphics | Not imported. The callouts a data graphic draws arrive as ordinary shapes; the link to the data does not. | `unsupported-syntax` |
| Themes | Colours Visio worked out for the page are kept where the file stores them; theme effects, shadows and theme fonts are not carried. | `unsupported-syntax` |
| Layers | All the shapes sit on one canvas. A shape on a hidden layer is left out and counted. | `unsupported-syntax` |
| A flipped shape | Drawn unflipped (so the parts of a flipped group may sit in the wrong places). | `unsupported-syntax` |
| A shape with no size, or no outline and no text | Left out, counted once. | `unsupported-syntax` |
| A connector whose arrowhead Visio's theme chooses | Given an arrow at its end, as a flowchart's connector has, counted once. | `unsupported-syntax` |
| A connector with a loose end, or glued to something that is not a shape on the page (another connector, a hidden shape, a shape that is not there) | Left out, with the reason. A line loose at both ends is one warning. | `missing-reference` |
| An arrowhead number past Visio's table | A plain arrowhead. | `style-approximated` |
| The file has several pages | One is imported; the first warning says which, and how many were not. | `page-selected` |
| The page has no shapes, or the file is a stencil with no pages | An empty diagram, and Create stays off. | `empty-diagram` |

At most 500 warnings are listed; a last one says how many more there were. Not carried at all: Visio's named styles (a colour, weight or
font comes from the shape or its master, not from a style sheet), the font a shape names, text alignment and margins, shadows, gradients,
hyperlinks, comments, backgrounds, containers whose members Visio keeps in a separate list, and stencils.

### Safety and limits

A `.vsdx` is a ZIP file of XML from whoever sent it, so it is read to refuse rather than to cope. It is opened in memory: nothing is
unpacked to disk and nothing in it is ever fetched.

- **The ZIP is checked before any part is read.** The file is at most 3,000,000 bytes, with at most 5,000 entries, and at most 64 MiB
  expanded for any one part and 256 MiB for all of them as the directory claims. An entry whose name could leave its folder if the file
  were unpacked (an absolute name, a drive letter, a `..` segment), two entries with the same name, and an encrypted entry each refuse
  the whole file. A relationship that climbs out of the package is refused the same way. A size claim larger than the file itself, or a
  ZIP64 claim of gigabytes, is refused from the directory without inflating anything.
- **A size header that lies is not believed.** Every part is counted as it comes out of the decompressor and must be exactly as long as
  the ZIP said and have the checksum it said; a header that understates or overstates the size refuses the file. What is read in all
  stops at 128 MiB.
- **No `DOCTYPE`.** Visio never writes one, and it is how an XML file smuggles in entity bombs ("billion laughs") and reads of other
  files. A part that has one refuses the file before anything in it is read; no entity is expanded, and nothing is looked up on the
  disk or the network. A relationship to another machine is text and is never followed.
- **Counted limits**, each refused with a message that names it: 2,000,000 XML elements, nesting 64 levels deep, 100 attributes on one
  element, 10,000 shapes on the page, 500 pages, 5,000 masters, 50,000 connections.
- **Only what the chosen page needs is opened.** The other pages, and any part nothing points at, are never read. No regular expression
  reads the file.
- **At most 3,000,000 bytes** per request, and the same sign-in and rate limit apply as to every other import.

A refused file is a `422` from the API (an error in the editor) with the reason in the message; it is never a half-read diagram. An
older binary `.vsd`, a password-protected file and a ZIP that is not a Visio drawing (a Word or Excel file sent by mistake) are each
named as such, with what to do.

### From a program or an agent

```sh
curl -s -X POST 'http://localhost:5180/api/import/visio?type=architecture&page=1&title=Network' \
  -H "Authorization: Bearer $DRAUGHTSMAN_TOKEN" -H 'Content-Type: application/octet-stream' \
  --data-binary @network.vsdx
```

answers `{ "document": …, "warnings": […], "pages": […] }`. The document is placed, so save it as it is (`POST /api/documents`); there is
no layout step. The same file can be sent as one file of a `multipart/form-data` form. The full contract, the response, the codes and
the error statuses are in [`api.md`](api.md#post-apiimportvisio).

An agent uses MCP `create_document` with `format: "visio"`. A `.vsdx` is binary and a tool argument is text, so `content` is **the file
base64-encoded** (`base64 -i network.vsdx` on a Mac, `base64 -w0 network.vsdx` on Linux; a `data:` prefix and line breaks are
tolerated), up to 3,000,000 bytes before encoding. `type` is `flowchart`, `architecture` or `freeform`, `page` (from 1) chooses a page of
a multi-page file, and it is laid out only if `layout` is `true`. Each warning comes back as `<code>: <message>`.

## Lucidchart

Lucidchart can export a document as a Visio file, and Draughtsman imports that. It does not read Lucidchart's own `.lucid` format.

1. In Lucidchart, open the document and choose **File, Export, Visio (VSDX)** (that is where the command has been; if your Lucidchart words it differently, look for
   the Visio export under File). Lucidchart downloads a `.vsdx` file.
2. In Draughtsman, **Import Visio** and choose that file, exactly as above. If the document has several pages the dialog lists them and
   each is imported on its own.
3. Pick the **Diagram type**: **Flowchart** reads Lucidchart's flowchart shapes as flowchart kinds, **Architecture** as services,
   databases and queues, **Freeform** keeps only the picture.
4. **Read diagram**, read the warnings, **Create diagram**.

In the Lucidchart file this page was checked against, a flowchart process shape is a group that is an instance of a master named
`com.lucidchart.ProcessBlock21.<hash>`, with the outline in one part of the group and the text on the group. Draughtsman reads that name
(the prefix, the number and the hash are dropped, and so is a trailing `Block`) and takes the outline from the part, so a process box
comes across as a process box, rounded when Lucidchart gave it a corner radius. The same reading applies to other block names
(`DecisionBlock`, `TerminatorBlock` and so on), but only `ProcessBlock` has been seen in a real Lucidchart file; for a block it does not
know, the outline decides, and a custom outline is a plain box named in a warning.

**What is lost, honestly.** The export is a Visio file that Lucidchart wrote, and Draughtsman reads what Visio files say:

- **Styles beyond a basic fill and line.** Fill colour, line colour, weight and dash, text colour, size and bold are kept. Gradients,
  shadows, line caps, fonts, text alignment and margins, rounded-corner radii beyond "rounded or square" and theme effects are not.
- **Data linking.** Lucidchart's data-linked shapes, conditional formatting, formulas and custom shape data are not carried: shape
  data is counted in one warning and not imported.
- **Lucidchart-specific shapes.** A shape from one of Lucidchart's own libraries (cloud-provider icons, network and floor-plan
  symbols, UI mockups, custom shapes) becomes a plain box with its text, named by its short name in one warning with a count.
- **Other things.** Layers collapse to one canvas; comments, hyperlinks, notes, embedded pictures (a plain box), page backgrounds and
  page settings are not carried; a connector's routing is re-derived by the editor rather than copied.

**Not verified against a live Lucidchart.** No Lucidchart account was used to write this page, so the export menu, what Lucidchart
puts in a file for each of its shapes, and what it does with a multi-page document are unverified. The Lucidchart structure above is from
one `.vsdx` that Lucidchart wrote, found in a public collection of Visio test files, and from files built in the same shape for the
tests; Lucidchart may change its export. If a Lucidchart export does not import the way this page says, the warnings in the dialog are
the first place to look, and the file is an ordinary Visio file that Visio can open to see what Lucidchart wrote.

## draw.io

Choose **Import draw.io** and a `.drawio` or `.xml` file (plain XML or the compressed form draw.io saves by default). draw.io keeps more
of a shape's look than Visio does, so if you can choose, a draw.io file carries more styling than a Visio export. Read
[`drawio-import.md`](drawio-import.md) for the table of what comes across, the limits, and the API.

## Before you rely on an imported diagram

1. **Read the warnings.** `kind-guessed` ones are a check to make, the others are what was drawn as a plain box or left out. The dialog
   counts the two apart.
2. **Check the page.** A multi-page file imports one page; the first warning says which. Import the others one by one.
3. **Check the connectors.** A connector with a loose end, or glued to something that is not a shape, was left out; draw it again between
   the right shapes (select two shapes and press **L**). Check arrowheads: those Visio's theme chose were given an arrow at the end.
4. **Check the kinds** in the type you chose, and change any that are not what the shape is. Freeform guesses no kinds at all.
5. **Check stencil shapes.** Anything named in an "instances of the Visio master" warning is a plain box: swap it for one of
   Draughtsman's shapes or icons.
6. **Tidy if you want to.** Nothing was laid out; **Arrange, Tidy layout** is an explicit command, and it will move every shape.
7. **Keep the original.** Draughtsman does not write Visio, Lucidchart or draw.io files back, so the original stays the master until you
   decide it does not.

## Not built

- Export to Visio, Lucidchart or draw.io.
- Lucidchart's own `.lucid` format, and any direct import from a Lucidchart account.
- The older binary Visio format (`.vsd`): save it as `.vsdx` in Visio first. The same goes for a password-protected file.
- Visio stencil files (`.vssx`), templates and themes; the shapes in a stencil, and a stencil master's own artwork.
- Visio's layers, shape data, data graphics, named styles, flips and hand-drawn connector bends.
- Several pages in one go: each page is its own import.
