# Empty, loading and error states

## Screen
Not one screen but the state every screen above takes when it has nothing, is waiting, or has failed: first run with no users; home with no documents; an empty document; an empty shape search; the properties panel with nothing selected; the AI panel with no provider; export waiting on the server; a document that fails to load; the sidecar or sync connection dropping; admin pages with nothing configured. Key pattern questions: what an empty state offers to do next; how loading is shown without motion; how errors are worded and where they sit; how "offline" is shown for a self-hosted, no-phone-home product.

## Mobbin queries
- `search_screens "empty state with illustration, no documents yet message and create first document button"`
- `search_screens "error state with something went wrong message and retry button inside an app panel"`
- `search_screens "skeleton loading placeholder for a list of cards while content loads"`
- `search_screens "Miro board editor with left vertical toolbar and canvas zoom controls"` (first board with coaching overlay)

Coverage note: Lucidchart and Whimsical are not on Mobbin and are cited from the products.

## References
1. Sketch — Workspace, no documents — https://mobbin.com/screens/4574f9b6-e17f-4b0e-8a8a-b4ed23e77339 — a heading, one sentence, two buttons (primary action, templates) at the top-left of the content area, no illustration. The plainest empty state in the set.
2. Frame — Empty folder — https://mobbin.com/screens/d554032b-7553-4911-a891-9250d970deee — "This folder is empty, create a new Doc" followed by three creation cards (from scratch, from template, from AI) and an "or Import" row. An empty state as a chooser.
3. Typeform — Something went wrong — https://mobbin.com/screens/5794c9e8-ca65-40a4-b2d3-8ed618245921 — illustration, "Oops! Something went wrong", one plain sentence about what failed, Retry and Start over. A full-page error with a clear next step.
4. Google AI Studio — Failed action in a side panel — https://mobbin.com/screens/bd6669d2-d3df-476e-b933-e44bb25e5259 — the error stays inside the panel that failed with the exact message in a bordered box, Cancel and Try again; a toast repeats it briefly. The in-place pattern.
5. Fabric — Skeleton loading — https://mobbin.com/screens/46846b8b-226e-4952-a05a-97f5b7bb11a1 — grey blocks in the shape of the coming layout, no spinner.
6. Chronicle — Generating with skeleton — https://mobbin.com/screens/fbeab89c-f418-442b-9fa2-ba67efa47c56 — a one-line status ("Creating storyline…") above a skeleton, for a wait that has a known cause.

Not on Mobbin (cited from the product, unverified against the library):
- Lucidchart and Whimsical — both show a template picker on a new empty document and a plain "no documents" home with a New button.

## What the references agree on
- An empty state names what is missing in one line and offers the action that fills it as a button in the state itself, not a pointer to chrome elsewhere.
- Good empty states offer two or three ways forward (blank, template, import or AI), as cards or buttons.
- Loading uses skeletons shaped like the coming content, with a one-line status when the wait has a cause; spinners are for buttons only.
- Errors stay where they happened: inside the panel or dialog, in a bordered box, with the message and a Retry. Full-page errors are reserved for the document itself failing to load.
- Error copy says what failed and what to do; a support ID is included when the user cannot fix it.
- Illustrations and mascots are common and add nothing to the action.

## What Draughtsman does
### Empty states, per screen
- **First run (no users):** the login card becomes "Create the first administrator" with name, email, password and one button. The setup-code field's hint no longer says only "the server log": it says the code is printed when the server starts, and a "Where do I find it?" disclosure lists where that is for each way of running Draughtsman: in a terminal (the window that started it), Docker (`docker logs <container>`), Linux systemd (`journalctl -u draughtsman`), macOS service (`<data folder>/logs/draughtsman.log`) and Windows service (Event Viewer, Windows Logs, Application, source "draughtsman"; filter for Warning). The detected one (from the server, without any secret) is opened and marked "This server looks like ...". Below the card, one line: "Draughtsman is running from <data folder>." The path is sent by `GET /api/auth/status` only while no user exists, and never afterwards. The code itself is never shown in the UI (maintainer decision: it is the gate against a network attacker).
- **Home, no documents:** in the content area, heading "No documents yet", one line "Start with a blank flowchart or architecture diagram, or paste text and let Draughtsman draw it.", then three cards: Flowchart, Architecture, From text (creates a blank architecture diagram with the AI panel open; shown only while an AI provider is configured, because the card is the only way to reach it from here). Under the cards, text buttons "Import SQL" and "Import Mermaid" (the importers that exist; there is no JSON import) and, on an instance with no documents only, "Add four sample diagrams" (see help-about-status.md). No illustration.
- **Home, no search results:** "Nothing matches '<query>'." with a "Clear search" button.
- **New document, empty canvas:** the canvas is empty and unobstructed. A single dismissible line in the centre of the canvas, in the muted text token: "Press S for shapes, C for connectors, or Cmd/Ctrl+K for everything." (the modifier named for the platform). It has a small close button, disappears on the first node, and never returns for that browser once dismissed or once a node has been drawn (kept in `localStorage`, read and written inside try/catch so a blocked store only means it shows again). It ignores pointer events so it never blocks a click on the canvas.
- **Shape panel, no matches:** "No shapes match '<query>'." with the query kept in the field.
- **Properties panel, nothing selected:** the Document section (title, type, diagram theme, Tidy), not an empty pane. One line under it: "Select a node or connector to edit it."
- **AI panel, no provider:** the AI tab stays hidden for an Editor (nothing they can do about it). For an Admin the tab is shown and its body reads "AI is off. Add a provider in Admin to describe diagrams in words, or paste DSL or Mermaid." with a link to `/admin/ai`, so the feature is discoverable. DSL and Mermaid keep working. (The fuller "disabled Text prompt" layout in ai-panel.md applies once a provider exists.)
- **Admin pages with nothing configured:** each page shows its form with defaults; a page that depends on a component (Export component) says so in a status band with the install or upload action.
- **Bin, empty:** "The bin is empty."

### Loading
- **Home list:** skeleton rows shaped like the list (icon, title, date) for up to 400ms; after that the skeleton stays but a status line "Loading documents…" appears. No spinner.
- **Opening a document:** the shell renders immediately with the title; the canvas shows a skeleton of the last thumbnail at 30% opacity, then the real render replaces it. If the sidecar layout is running (import, Tidy), the top bar saved-state text reads "Laying out…" and the canvas is interactive but the affected nodes show a dashed outline.
- **Export PDF:** the dialog's primary button becomes "Exporting…" with a small spinner inside the button; the dialog stays open and cancellable. This is the only spinner in the product.
- **AI generate:** the preview streams; the Generate button becomes "Stop".
- **Thumbnails** on home load lazily; a missing thumbnail shows the type icon on the surface token, never a broken image.

### Errors
- **In-place first.** Panel and dialog errors render inside the panel in a bordered box with the error token border: one sentence on what failed, the raw message in monospace if there is one, and Retry. Example: "Could not reach the AI provider. Check the endpoint in Admin." A toast never carries an error on its own.
- **Document failed to load:** the shell stays, the canvas area shows "This document could not be opened." with the reason, "Try again", and "Back to documents". Schema migration failures name the version and point at the pre-migration copy.
- **Save or sync lost:** the saved-state text in the top bar changes to "Offline, changes kept locally" with the `cloud-off` icon; editing continues; a band under the top bar appears only if reconnect fails for 30 seconds, with "Retry now". The word "offline" is fine for a self-hosted product; it means the browser lost the server.
- **Sidecar unavailable** (layout, including the layout run after an import): the action's button reports "Layout service is not running" inline; the document stays editable; a Retry appears. Never a modal.
- **Fatal render error:** a full-canvas box "Something went wrong drawing this document." with an error ID, "Reload" and "Download DSL" so the model is never lost with the picture.
- **Validation:** field errors appear under the field and the field gets the error border; the first invalid field receives focus on submit.
- **Unknown kinds from an import** are not errors; they are lint findings listed in the AI or import panel and badged on the node.

### Copy rules for all three
- Plain British English, present tense, no exclamation marks, no "Oops", no apologies, no mascots. Say what happened and what to do, in that order.
- The action verb is the button label: "Retry", "Create flowchart", "Clear search".

## Keyboard and accessibility
- Empty-state primary actions are the first focusable element in the content area, so `Tab` from the sidebar lands on them.
- Skeletons are `aria-hidden`; a visually hidden `role="status"` announces "Loading documents" once and "Loaded, 12 documents" on completion.
- In-place errors use `role="alert"` and receive focus when they appear; the Retry button is inside the alert region.
- The offline band is `role="status"`, not an alert, because editing continues.
- No shimmer or pulse on skeletons; they are static grey blocks. Nothing animates under reduced motion, and by default only the export spinner animates.
- Every state keeps the command palette available so a keyboard user is never trapped in an empty or failed screen.

## What Draughtsman deliberately won't do
- **No illustrations, mascots or emoji in empty and error states** (Rive, Typeform, SuperHi, Deputy). They add height and no action.
- **No coaching overlays on the canvas** (Miro "Spread to zoom"). One muted line, dismissed by drawing.
- **No limit, quota or upgrade cards** (Figma, Sana AI, Sketch trial banner). There are no seat limits.
- **No blocking modal for waits under a few seconds** (Customer.io). Waits live in the button or the status text.
- **No error toasts as the only report of a failure** (Google AI Studio's toast is fine only because the panel also shows it).
- **No "Refresh page" as the only recovery** (Retool). Retry the failed call first; reload is the last option and sits next to "Download DSL".
- **No shimmer animations on skeletons.** Static blocks respect the minimal-motion rule.

## Open questions
- Should the first-run administrator step also offer to import an existing data folder or backup? Default for the build: no; restoring is copying the folder before first start, as the deployment section says.

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)

## Amendment 2026-10-01 (final usability QA: P12, P17, P23)

- First-run setup-code help starts closed; the matching host is still marked when it is opened.
- A page an Editor may not see is a heading, the sentence and a link back, not a bare line.
- Home and the Bin say the outcome of an action in one visible status line (also the live region): moved to the bin with Undo, restored, duplicated, deleted for good. There is no second hidden announcement, and the empty bin does not repeat the retention sentence.
