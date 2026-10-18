# Try page (paste and run)

## Screen
The `/try` route: a textarea holding DSL or Mermaid source, a format toggle, a Run button, and the rendered diagram
below. It is a prototype surface that proves the chain import -> layout -> save end to end, not a screen from the
eight in `design/atlas/README.md` and not the eventual paste/import UI. Key pattern questions: where pasted source
lives relative to its rendered output; how a format choice is offered before anything is parsed; how a partial
result (imported but not laid out) is shown honestly rather than as a blank frame.

This note is deliberately short. The screen is scaffolding: it exists so the whole stack can be driven from a
standing start, and it is expected to be replaced by a real import flow inside the editor shell.

## Mobbin queries
- `search_screens "paste text into a large code editor textarea with a Run button and a rendered diagram preview beside it"` (web, standard)

## References
1. Gemini Notebook — "Paste copied text" source dialogue — https://mobbin.com/screens/202a6d8f-9615-4e20-abdc-e84c9f8a8f99 — a plain monospace-ish textarea as the whole of the paste affordance, with a single confirming action ("Insert") and no formatting chrome around it.
2. SchoolAI — Prompt editor with student preview — https://mobbin.com/screens/7b360ccd-fe0b-4de6-b93d-14195e0042c2 — source text on the left, its rendered result on the right, with a "Restart preview" action: the edit-then-render split this screen needs.
3. Mintlify — Docs editor with embed dialogue — https://mobbin.com/screens/343b8b74-65da-4fad-85ff-553131b1bbc3 — source and rendered page side by side, source in monospace; included for the source/render pairing, not its chrome.

Coverage note (2026-09-21): no diagram tool in Mobbin's web library ships a paste-a-diagram screen, and the gap
recorded in `design/atlas/README.md` (no Lucidchart, Whimsical or FigJam) still holds. The four-to-six reference
set the atlas contract asks for is not met here, and deliberately so: this is a prototype page, and inventing
citations would be worse than naming the gap. The editor shell's real import flow must write a full note before it
is built.

## What the references agree on
- Pasted source goes in one plain, monospace, generously sized text field. No syntax highlighting or toolbars around it while the format is still unknown.
- One explicit action commits the paste; nothing parses as you type.
- Source and its rendered result are shown together, source first in reading order.

## What Draughtsman does
- **Controls on top, canvas below.** A compact control block (title, format radio group, textarea, Run) with the full-width `DocumentViewer` filling the rest. Vertical rather than side by side because the canvas needs the width.
- **Format is chosen before Run,** as a two-option radio group (DSL, Mermaid). Each format keeps its own seeded example so switching never loses what was typed in the other.
- **Run chains import -> layout -> save** and every step degrades rather than blanking the screen: an import failure keeps the previous diagram on the canvas and shows the parser's message with its line and column; a layout failure still draws the diagram, placing on a plain grid only the nodes the source left unplaced and leaving every authored position and container intact (a grid cell cannot honestly stand for a container's box, so grouping is dropped only where a position had to be invented), and the banner says which of those two happened rather than claiming a layout ran; warnings from import and layout are listed on one line under the banners.
- **A run is saved unless it invented a position.** Placeholder grid positions are scaffolding, and a stored snapshot has no banner to mark them provisional, so a run that had to place a node draws but does not save, and the banner says so. A source that positioned every node has nothing provisional about it, so a layout failure does not stop it being saved.
- **Saving is an upsert against the id this page last saved**, so a second run of either source kind updates that diagram rather than leaving another behind (a Mermaid import mints a fresh id each time, so it would otherwise never collide). It moves the address bar to `/d/{id}`, replacing rather than pushing once it is already there, so Back still goes back to `/try` in one press.
- **An id this page did not create is never replaced.** A DSL id comes from the title alone, so the id a pasted source asks for may already belong to someone else's document; when the create collides, the page takes a server-assigned id instead of writing over them, and says so next to the saved-id readout.
- **A run saves from the version it last wrote**, carried as the base version the server requires (`docs/api.md`, Base versions). If the document was written to in between — by an agent, or by this page in another tab — the save is refused and the failure banner says so, rather than the run quietly replacing that work. Recovering from a refusal is the editor's open decision, not this prototype's: the page reports it and stops.

## Keyboard and accessibility
- The radio group is a native `radiogroup` with a label; the textarea carries an `aria-label` naming the current format.
- Failure banners are `role="alert"`; the layout notice and the warnings line are `role="status"` so they are announced without stealing focus.
- Run is disabled while a run is in flight and while the box is empty; its label changes to "Running…".
- Canvas keyboard behaviour and the zoom cluster come from `editor-shell.md` by way of `DocumentViewer`.

## What Draughtsman deliberately won't do
- **No live parse-as-you-type.** Import is a server round trip; running it per keystroke would spam the API for no gain on a demo page.
- **No format auto-detection.** The toggle is explicit, so a wrong guess never silently maps a flowchart into another vocabulary.
- **No document-type selector** for Mermaid. The endpoint's own default (flowchart) stands; forcing a type here would map pasted flowcharts into the architecture vocabulary.
- **No editor chrome** — no tool rail, shape panel or properties panel. This page is not the editor shell and must not accrete one.

## Open questions
- Does the real import flow live in the editor shell (a dialogue over the canvas) or stay a route of its own? That decision needs its own note before it is built, and this page should be retired when it lands.

---
2026-09-22 · Claude (fm/put-last-write-wins; first written 2026-09-21, fm/proto-glue)
