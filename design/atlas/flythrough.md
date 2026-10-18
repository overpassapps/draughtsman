# Flythrough: the Tour panel and Present mode

## Screen
Two surfaces over one piece of data, the camera path (`Document.flythrough`, `docs/flythrough.md`). The **Tour panel** is a
third view of the editor's right-hand panel (Properties, Text, AI, Tour) where a person captures the canvas view as
keyframes, orders them, captions them and sets how the camera arrives and stays. **Present** is a full-window overlay that
plays the path over the diagram, read-only, with transport controls, a caption, a scrubber, fullscreen and keyboard
control. Key pattern questions: where a capture action lives and what it captures; how a step list is reordered and
edited without a drag library; what the controls of a presentation are and when they get out of the way; how keyboard,
clicker and reduced-motion users drive the same thing; what happens when the diagram changes under a running show.

## Mobbin queries
- `search_screens "full-screen slideshow presentation mode with previous next buttons, progress and exit control"` (web, standard)
- `search_screens "editor side panel listing an ordered sequence of steps with reorder handles and an add step button"` (web, standard)

Mobbin holds no diagram tool's presentation mode (the coverage gap in `design/atlas/README.md` still stands: no Lucidchart,
Whimsical or FigJam). The results are used for the transport chrome and the step-list pattern only; the diagram-tool
references are cited from the products, marked "(not on Mobbin; from the product)".

## References
1. Lucidchart: Presentation Builder, Present and Paths modes (not on Mobbin; from the product). Slide frames cropped to a region of the canvas, a Present mode the presenter drives, arrow keys to advance. The closest diagram-tool precedent for a path over one canvas.
2. Miro: Presentation Mode (not on Mobbin; from the product). Frames in a panel order; a full-screen mode that steps between them, with a small bottom bar and a progress count.
3. Figma / FigJam: Present and Prototype view (not on Mobbin; from the product; Figma's design editor fills the Figma/FigJam slot). Arrow keys and clicker keys step, Esc exits, a slim bottom bar, controls that fade.
4. Whimsical: flow sharing and view mode (not on Mobbin; from the product). A read-only view with a minimal corner control; nothing editable on the surface while viewing.
5. Gemini Notebook: slide viewer, [mobbin.com/screens/af9c1d3b-3ad9-4db3-934b-1eccac821991](https://mobbin.com/screens/af9c1d3b-3ad9-4db3-934b-1eccac821991). A modal viewer with the current slide large, a numbered thumbnail rail and play, share and close in the header: a step list beside a stage.
6. GetYourGuide: photo viewer, [mobbin.com/screens/a002c1c4-730a-4cfc-8ce9-fae8960a2ed0](https://mobbin.com/screens/a002c1c4-730a-4cfc-8ce9-fae8960a2ed0). Close top left, "24 / 32" counter top centre, previous and next at the vertical middle of each side: the smallest useful set of stage controls.
7. Apple Music: full-screen player, [mobbin.com/screens/7bd2ef54-565a-42a6-ad5d-2fa21b98f2a3](https://mobbin.com/screens/7bd2ef54-565a-42a6-ad5d-2fa21b98f2a3). Play, previous, next grouped under a thin scrubber, close at the corner, everything else absent.
8. Magnific: slide editor, [mobbin.com/screens/41467825-f8f8-4e80-ad88-b8c2f9b7580d](https://mobbin.com/screens/41467825-f8f8-4e80-ad88-b8c2f9b7580d). A numbered filmstrip under the stage with the current step outlined and a page count.
9. SchoolAI: agenda steps, [mobbin.com/screens/dcedcbdd-aa1f-41e7-a0e6-c1ec3e652425](https://mobbin.com/screens/dcedcbdd-aa1f-41e7-a0e6-c1ec3e652425). A numbered list of steps, each a card with its own fields and a "More" menu, and an Add step button under the last: the authoring list.
10. Linear: issue sub-list and properties rows (not on Mobbin; from the product). Dense rows, arrow-key movement between them, inline edit on Enter, no drag handles needed for order because Alt+arrow reorders.

## What the references agree on
- The stage is the diagram and nothing else; controls are small, sit at the edges, and can be hidden without losing the keyboard.
- A counter ("3 / 7") is always somewhere; previous and next are the two most visible controls; close is at a corner.
- Arrow keys and PageUp/PageDown step, Esc leaves, a clicker needs no setup because it sends those keys.
- The authoring surface is a numbered list beside or under the stage, each row a card, the current one outlined, an "Add" button under the last.
- Nothing in presentation mode is editable; leaving returns to exactly where the person was.

## What Draughtsman does
**Tour panel** (`editor/FlythroughPanel.tsx`; the third button of the panel's view switch, after Text and AI, present whenever the document can be edited):
- Top: "Add keyframe" (captures the canvas's current view, `viewRectOf`, and selects the new row), "Generate from containers" (an overview, then each top-level container, then the loose nodes; disabled, with the reason in its title, when the diagram has no container or the tour is full), a Present button (enabled with at least one keyframe) and a Loop checkbox.
- The list is an ordered list (`<ol>`): each row a number, the caption (or "Keyframe N"), and the row's buttons: Move up, Move down, Duplicate, Delete. Selecting the row is the go-to: the canvas camera cuts to that keyframe, so one can be checked without presenting. Reorder is by the Move buttons and by Alt+ArrowUp/Down on the focused row; no drag library, no drag handle.
- Selecting a row opens its fields under it: Caption (a textarea, committed on blur), Arrive (seconds, 0 to 600), Hold (seconds; plain text fields that take a decimal point or comma, commit on blur or Enter, and put the stored value back when the text is not a time), Easing (Smooth, Ease in, Ease out, Even, Cut), and "Use current view" to re-capture. The first keyframe's Arrive is labelled "Return" and shown only while the tour loops, since it is the trip back from the last one.
- Every change is one undo step (`editFlythrough`); typing in the caption is one step on commit, never one per key. Deleting the last keyframe removes the tour.
- Empty state: one sentence ("Frame the diagram the way you want it shown, then add a keyframe.") and the two add buttons.
- In a read-only preview the panel is absent; Present still works on a document that already has a tour.

**Present** (`editor/PresentationStage.tsx`), entered from the Tour panel, File/View menu ("Present fly-through"), the palette, and Cmd/Ctrl+Enter (Google Slides' present-from-start; Cmd/Ctrl+Alt+letter is unreliable on macOS, where Option changes the character):
- A full-window overlay (`position: fixed`, above the editor, `role="dialog"` `aria-modal`, labelled "Presentation") holding the diagram drawn by `DiagramSvg` on the canvas surface, read-only, with the camera driven frame by frame. It draws a snapshot of the document taken on entry, so another writer's live-sync change never moves a show under way.
- Bottom centre, one control bar: Previous, Play/Pause, Next, the scrubber (a range over the path with a tick for each keyframe), "3 / 7", Fullscreen, Close. Above it, centred, the caption of the keyframe the camera is nearest (an `aria-live="polite"` region), hidden when there is none.
- The bar and the close button fade out after 2.5 seconds without pointer movement while the tour is playing or in fullscreen, and return on any pointer move or key; they never fade while paused windowed. The caption stays.
- Starts paused on the first keyframe. Enter from the Tour panel with a row selected starts on that row.
- The camera fits each keyframe's whole rectangle into the window, so a shot captured in the editor plays as framed in a projector's window of another shape.
- When the tour ends (no loop) the Play button becomes Replay.

## Keyboard and accessibility
- In Present: Right, Down, PageDown, Space and Enter go on; Left, Up, PageUp and Backspace go back; Home and End go to the first and last keyframe; 1 to 9 go to that keyframe; P plays and pauses; F toggles fullscreen; Escape leaves full screen first and Present second (a browser that hands the page the key before leaving full screen itself does not close the show with the same press). Presentation clickers send PageUp/PageDown, so they work unmodified.
- Focus moves to the overlay on entry and back to where it was (the canvas, or the Present button that opened it) on leaving; when nothing had focus, the canvas takes it. Tab cycles the control bar and never reaches the editor behind (the overlay is modal and the rest is `inert`). The scrubber is a native range with `aria-valuetext` "Keyframe 3 of 7".
- Every key the overlay uses is stopped from the editor's own shortcuts while it is open.
- `prefers-reduced-motion: reduce` turns every transition into a cut and the control bar's fade into an instant change; the dwell times still apply, so autoplay is a slideshow. A keyframe with easing Cut does this for itself.
- Axe runs over the Tour panel and the overlay (`a11y/editorAxe.test.tsx`); the control bar meets the same focus-ring rule as the rest (`scripts/focus-rings.test.ts`).
- The Tour panel is a `data-region="properties"` child like the other panel views, so F6 reaches it; its list rows move with the arrow keys (`editor/focusNavigation.ts`).

## What Draughtsman deliberately won't do
- **No slide frames on the canvas.** Lucidchart and Miro make a frame object that lives in the diagram; a keyframe here is a rectangle in the document header, so nothing on the canvas changes and nothing in the graph refers to it.
- **No per-object animation, transitions library or video editor.** Cut and four easings; nothing else.
- **No presenter notes window.** A caption is shown to the audience; speaker notes are a later, separate thing.
- **No drag-to-reorder.** Buttons and Alt+Arrow are keyboard-accessible and need no drag library; a drag can be added later without a model change.
- **No editing in Present** and no laser pointer or drawing.
- **No 3D** in this version (a CSS-3D variant is a separate task, see `docs/flythrough.md`).
- **No click-to-advance and no swipe.** A click on the stage only brings the controls back, so a stray click during a talk does not skip a keyframe.
- **No audio, no timing recorded by presenting**: the durations are authored, not captured.

## Open questions
- Whether a tour should be able to name more than one path per document (for different audiences). The model has one object so a list is an additive change later.
- Whether "Generate from containers" should also offer the topological walk the spec mentions for diagrams with no containers.

---
2026-09-30 · flythrough-2d crewmate
