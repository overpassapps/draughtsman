# Export dialog

## Screen
The dialog that turns a document into a file: PNG, SVG, PDF (single page in the MVP), DSL or JSON. Key pattern questions: format; scale; background; page or selection scope; the AI outline option (Markdown outline is phase 2 but the slot should exist); what happens when the optional Chromium PDF component is not installed.

## Mobbin queries
- `search_screens "export dialog with file format choice PNG SVG PDF, scale multiplier and transparent background toggle"`
- `search_flows "exporting a board or design as an image file"`
- `search_screens "Figma export settings in the design panel with scale, suffix and format"`

Coverage note: Lucidchart and Whimsical are not on Mobbin and are cited from the products.

## References
1. Miro — Export image as, flow "Exporting a board" — https://mobbin.com/flows/c4666e33-23db-4b74-ba1a-2b5a2f4c8f74 — a small popover from the board menu: Small, Medium, Large (JPG with pixel sizes) and Vector (PDF); the export area is shown as a highlighted frame on the board while the popover is open.
2. Figma — Export dialog — https://mobbin.com/screens/73932262-c45c-477c-995f-8907615aa39c — preview on the left with page count, settings on the right (Select assets, File type, Size, Colour profile), one primary "Export asset" button.
3. Sketch — Export formats — https://mobbin.com/screens/c362e7d2-82f8-49b9-8154-8dcd364278de — preview with dimensions badge; a row per export (scale, suffix, format dropdown listing PNG, JPG, WEBP, SVG, PDF); add and remove rows.
4. Mural — Download as, flow "Downloading an object" — https://mobbin.com/flows/392b7c9a-30fc-4354-be2a-df45514e9b67 — an inline top bar: Format dropdown, scope segmented (All content, Selection, Outline), Cancel and Download; then a "Downloading as image" toast with "Email me a link too".
5. Zoom Whiteboard — Export dialog — https://mobbin.com/screens/f451009e-23bd-4c1b-a131-66112ff900b4 — preview left; Export area (Selection or whole), Format, Advanced options (include comments), file name; Share as a secondary action.
6. Magnific — Download your project — https://mobbin.com/screens/b1df1588-04af-464c-814b-98ca2b7e1c5b — a popover with page range at the top, then format cards each with a one-line "ideal for" description; PNG with a scale slider and resulting pixel size; "PNG (without background)" as its own option.

Not on Mobbin (cited from the product, unverified against the library):
- Lucidchart — Download dialog — format tabs (PNG, JPEG, PDF, SVG, VDX, CSV), scope (current page, all pages, selection), quality, transparent background, crop to content.
- Whimsical — Export — PNG or PDF, scale 1x to 3x, "transparent background" toggle, scope current board or selection.

## What the references agree on
- Format is the first choice, and the format choice changes which options appear (scale for raster, none for vector, page range for PDF).
- Scope is All or Selection, and the scope is previewed, either as an image in the dialog or as a highlighted area on the canvas.
- Scale is a 1x/2x/3x choice, with the resulting pixel size shown.
- Transparent background is a toggle, sometimes surfaced as a separate format ("PNG without background").
- Preview is shown when the dialog is large enough; small popovers skip it and show dimensions instead.
- One primary action, named for the verb (Export, Download), and a toast on completion.

## What Draughtsman does
- **A modal dialog** (`Cmd/Ctrl+Shift+E`, also in the File menu and the top bar Export button), 640px, preview on the left, options on the right, matching the Figma and Zoom pattern. Preview is the live SVG render of the chosen scope on the diagram theme's export background.
- **Format as a segmented control:** PNG, SVG, PDF, DSL, JSON. The options column changes with the format.
  - PNG: scale 1x, 2x, 3x with resulting pixel size shown; background: Theme (export background from the diagram theme) or Transparent; padding in grid units (default 2).
  - SVG: background as above; "embed fonts" on by default; text stays selectable.
  - PDF: page size (A4, A3, Letter, Fit to content), orientation, background as above. Multi-page is phase 2. If the Chromium component is not installed, the PDF option shows a short inline notice: "PDF needs the optional export component. Install it from Admin, or export SVG." with the admin link, and the primary button switches to export SVG.
  - DSL: no options; the whole document, always.
  - JSON: no options; the whole document, always.
- **Scope:** Whole document or Selection (disabled when nothing is selected). Selection scope is shown on the canvas behind the dialog as a highlighted frame while the dialog is open, as Miro does.
- **AI outline slot:** a fifth-from-last row, "Also copy Markdown outline", is present but disabled with "Phase 2" until the outline export lands, so the dialog does not change shape later. No LLM call is made from this dialog.
- **File name** defaults to the document title, sanitised; editable.
- **Primary button** reads "Export PNG" (the format name updates), and a toast confirms with the file name. Export never phones home; the file is produced client-side for PNG, SVG, DSL and JSON and server-side for PDF.

## What is built: the PDF export dialog (pdf-and-health-truth, 2026-09-30)
The full dialog above is still the target. What exists is its PDF slice, opened by File > Export PDF (and the palette): a 480px modal on the existing `sql-import` dialog chrome, no preview.
- **Options:** Page size (A4, A3, Letter, Fit to diagram), Orientation (Landscape, Portrait; disabled for Fit, where the page takes the drawing's shape) and Margin in millimetres (0 to 50). It opens on A4 landscape with a 10 mm margin, the atlas default. The server endpoint defaults to Fit so a caller with no options still gets one page (`docs/pdf-export.md`).
- **(Superseded by the amendment below.) Availability is proven, not configured:** the File-menu entry "Export PDF…" exists only when `GET /api/health` says `export.pdf` is true, and that is true only after a Chromium launch has actually succeeded (probed lazily, cached for minutes). When health says false, "Export PDF" is not offered at all; a different entry, "PDF export not installed…", opens the same dialog in its notice state, so the reason is discoverable and no request that would return 501 is ever made. While health has not answered, the menu says nothing about PDF.
- **(Superseded by the amendment below.) Notice state** (`role="status"`, not an alert; it is a known state): "PDF export needs the optional export component." then the exact command the running server reports (`npx playwright@<pinned version> install chromium`, in monospace), "then set export.chromium.enabled to true in draughtsman.yaml and restart." and "PNG and SVG export work without it." The primary button becomes "Export SVG instead" and runs the client-side SVG export; Close is the other action. There is no admin link yet: the admin status page is a separate lane.
- **(Superseded as a notice state; the part it names survives as a note, see the amendment below.) Notice state names the part that is the matter (qa-pdf-archive, 2026-10-01):** an administrator's health answer carries `export.pdfProblem` (`disabled` | `driver` | `browser`, with the prose in `pdfHint`). `disabled` and `browser` keep the install-command notice, but `browser` (the component is already enabled) no longer says "then set export.chromium.enabled": it says no browser could be launched, gives the command or the `export.chromium.path` alternative, and that the server checks again within thirty seconds. `driver` (the `.playwright` folder beside the executable is missing, incomplete or not executable) shows the server's own sentence, which says it is not a browser problem and to unpack the release archive again, and **no install command**, because installing Chromium cannot fix it. An Editor gets neither field (the hint names server paths) and sees the generic notice. Same `role="status"`, same two buttons.
- **Failure state:** a server refusal (501 with its hint, 400, 503) shows its `detail` inline in an alert and leaves the dialog open with the options kept; success closes the dialog and the browser downloads `<title>.pdf`.
- **Colours** are the organisation's default diagram theme (Admin > Themes), or shipped Overpass when none is set; the server does not know a viewer's personal theme pick, and the dialog says so in one line under the options.
- Keyboard and focus follow the rules below: focus starts on Page size (or the Export SVG button in the notice state), Tab is trapped, Escape closes, every key is kept from the canvas behind.

## Amendment: browser print is the default (pdf-browser-print-default, 2026-10-05)
The maintainer decided PDF defaults to the browser's own "Save as PDF", with the server's Chromium route optional (`docs/pdf-export.md`). The dialog above keeps its 480px modal on the `sql-import` chrome and its three options; what changes is that it has **no unavailable state**.
- **One form, two routes.** Page size, Orientation (disabled for Fit) and Margin are always shown, opened on A4 landscape, 10 mm. The first line under the title says what the dialog does: "Opens your browser's print dialog: choose Save as PDF." The second says how the drawing sits ("scaled down to fit inside the margins, never up", or "cut to the diagram" for Fit) and that colours follow the diagram theme, as in SVG and PNG export.
- **Server PDF off or failing (the default):** one primary button, "Open print dialog" (busy: "Opening the print dialog…"). Choosing it draws the export SVG on the chosen page and calls the browser's print; the dialog closes once the print dialog is up. If the print view cannot be made, an inline alert says so and the dialog stays open with the options kept. There is no "PDF export not installed" entry and no install command in this dialog.
- **Server PDF working** (health's `export.pdf`, as before): "Export PDF" stays the primary button and asks the server for a file; "Print in browser…" sits beside it, and the first line names both. Cancel is the first button either way.
- **A switched-on server component that is failing** (`export.pdfProblem` `driver` or `browser`, administrators only): a `role="status"` note under the options says it is switched on but not working, with the server's sentence for a driver (no install command: installing Chromium cannot fix it) or the pinned command or `export.chromium.path` for a browser. It sits beside a working form; it is not the dialog's content. `disabled` and an Editor's view show no note.
- **The menu entry** "Export PDF…" is always offered (File menu and palette), enabled when the diagram has content, including in a read-only preview. It no longer waits for health, and "PDF export not installed…" is removed.
- **Focus** starts on Page size. The dialog's Tab trap, Escape and canvas-key rules are unchanged.
- **Print view, not a preview in the dialog:** the view opens in a window made inside the click (so a blocker lets it through) and closes itself after the browser's print dialog; if the window is refused it prints from a hidden frame in the page. `aria-hidden` on that frame; the user's preview is the browser's own.

## Keyboard and accessibility
- The dialog traps focus, opens with focus on the format control, closes with Escape and returns focus to the Export button.
- Format and scope are radio groups navigable by arrow keys; scale is a segmented radio group too.
- Preview is `aria-hidden`; the dimensions and page count are text next to the options so the result is announced.
- The PDF component notice is `role="status"`, not an alert, because it is a known state not an error.
- Every option has a visible label; the transparent background toggle is a switch with the label "Transparent background".

## What Draughtsman deliberately won't do
- **No JPEG, WEBP or size presets named Small/Medium/Large** (Miro, Sketch). Scale factors are what diagrams need; JPEG hurts line art.
- **No "email me a link", share, or publish actions in the export dialog** (Mural, Zoom). Export makes a file. Sharing is a separate, phase 2 feature.
- **No colour profile, suffix or multi-row export lists** (Figma, Sketch). One export at a time.
- **No "ideal for" marketing copy under each format** (Magnific). A one-line hint per format at most, in plain British English.
- **No chrome theme in exports.** Exports always use the document's diagram theme (themes/README.md).

## Open questions
- Should PDF default to "Fit to content" or A4? Default for the build: A4 landscape, because client decks and printed runbooks are the target. (The dialog opens on it; the REST endpoint defaults to Fit.)
- Whether the default PDF path should be the client-side print of the themed SVG rather than server Chromium: decided 2026-10-05, browser print is the default (amendment above; `docs/pdf-export.md`).

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)
