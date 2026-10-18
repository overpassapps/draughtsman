# Fly-through: the camera path and Present

A document can carry a **flythrough**: an ordered camera path over the diagram, played back as an animated tour. It is a
presentation of the graph, never part of it. The 2D path is what ships; the spec's 3D idea stays a marketing prototype
(see [Not built](#not-built)).

The design decisions the spec left open are listed in [Design decisions](#design-decisions). The UI note is
`design/atlas/flythrough.md`.

## The model

An optional top-level member of the document, added the way the `sequence` type was: additive, `schemaVersion` stays 1, a
document without it is unchanged byte for byte. It lives in the four places the document model must change together
(`docs/schema/document.schema.json`, `src/Draughtsman.Core/Models`, `web/src/model/types.ts`, `sidecar/src/model/types.ts`
with the Yjs shape in `sidecar/src/yjs/shape.ts`), and the DSL, the editor's Y.Doc and the live-sync merge carry it too.

```jsonc
"flythrough": {
  "keyframes": [
    {
      "id": "k_web",                                   // unique within the tour; conventionally k_<n>
      "view": { "x": 0, "y": 0, "w": 400, "h": 250 }, // the world rectangle the camera frames; w and h above zero
      "caption": "Traffic enters through the web app", // optional; shown over the diagram while the camera is here
      "durationMs": 1600,   // optional, 0 to 600000: time to travel HERE from the previous keyframe (default 1500)
      "holdMs": 3000,       // optional, 0 to 600000: how long autoplay stays here (default 2500)
      "easing": "easeOut"   // optional: cut | linear | easeIn | easeOut | easeInOut (default easeInOut)
    }
  ],
  "loop": true            // optional: return from the last keyframe to the first and play on (default: stop at the last)
}
```

- **A keyframe is a rectangle, not a camera.** The camera fits the whole rectangle into whatever window plays the tour
  (contain, never crop), so a shot captured in a small editor pane plays as framed on a projector. Capturing stores the
  rectangle the canvas shows (`viewRectOf`, `web/src/canvas/camera.ts`).
- **Timings and easing are sparse.** Absent means the default; the editor writes only what the author chose, and choosing
  the default (Smooth) clears the field. The defaults are in `web/src/model/flythrough.ts` and are embedded in an export, so
  the file plays with the editor's numbers.
- `durationMs` is the arrival time, so the first keyframe's is used only on the trip back when the tour loops (the UI
  calls it "Return"). `cut` jumps and ignores `durationMs`.
- **A tour with no keyframes is absent, never empty** (schema `minItems: 1`, at most 500). Deleting the last keyframe removes
  the member.
- Nothing refers to a keyframe and a keyframe refers to no node, so it cannot dangle when an element is deleted or an
  agent rewrites the graph. The price is that a keyframe does not follow a node that moves: re-capture it ("Use current
  view") or regenerate.
- Every renderer (canvas, export, the server's `render_document`), layout and the Mermaid export ignore it.

### DSL

```
flythrough loop=true
keyframe k_overview @ -40,8 680x500 caption="Order service: a web app, an API, a database and a cache"
keyframe k_web @ 0,0 400x250 caption="Traffic enters through the web app over HTTPS" duration=1600
keyframe k_data @ 16,320 568x160 duration=1800 easing=easeOut
```

`keyframe <id> @ x,y wxh [caption=] [duration=] [hold=] [easing=]`, printed after the edges in tour order; the optional
`flythrough loop=true|false` line carries the tour-level setting. The view is required, size included. A view with no
area, or a time outside 0 to 600000, is a lint warning and the keyframe (or the value) is dropped, never a rejected
document; an unknown field, a duplicate id or an unknown easing is a syntax error. `docs/spec/dsl.md` and `dsl.grammar`
have the rest, and `DslRoundTripFuzzTests` generates tours.

### MCP and the API

The tour travels in every document body (`POST/PUT /api/documents`, `read_document`, `create_document` with `dsl` or
`json`), and the schema refuses a bad one with `400` and the pointer (`/flythrough/keyframes/0/view/w`). `patch_document`
gains one op, `setFlythrough`: `{ "op": "setFlythrough", "flythrough": { "keyframes": [...] } }` replaces the path whole,
`"flythrough": null` removes it; the schema checks the result once the batch is applied, and the write is checkpointed
like any other. Every other op leaves the tour alone. `diff_documents` reports a changed path as `flythroughChange`
`{ from, to }` beside the graph's changes (absent when the two revisions have the same path). `get_schema` carries the
definitions, so an agent can write one.

The AI panel's update mode shows the model the document's own DSL, tour included, and applies its reply as a diff of nodes
and edges only: a reply that drops or rewrites the keyframe lines leaves the author's tour exactly as it was
(`GenerateFromTextEndpointTests`).

## Playing it

Progress along the tour is a number `p` in keyframe units: `2.4` is 40% of the way from the third keyframe to the fourth.
The camera is `flyBetween(keyframe[i], keyframe[i+1], ease(easing of keyframe i+1, p - i))`, where the fly is van Wijk and
Nuij's "smooth and efficient zooming and panning" (the curve d3-zoom uses, hand-written, no dependency): a far pan zooms out,
crosses and zooms in at a steady perceived speed, where interpolating the viewport's translation and scale directly
sweeps the diagram past at an absurd speed. A near pan is a plain pan, a same-place change of scale a plain zoom.

The state machine (`web/src/editor/tourPlayer.ts`, pure functions; `usePresentation.ts` is the hook) has `p`, a `target`
the camera is travelling to, `playing`, and the dwell left:

- Next/previous set a target one keyframe on or back; further presses while it is still travelling send it one keyframe
  further, through the one it passes without stopping; a press the other way turns it round. At the ends of a tour that
  does not loop they do nothing; on a loop they go round.
- Playing adds the dwell: arrive, wait the hold, set off for the next; stop at the last (Replay starts again) unless looping.
- Scrubbing and jumping (Home, End, a number key) move the camera with no travel.
- A `cut`, a travel time of 0 and `prefers-reduced-motion: reduce` make a segment instant, and nothing waits on a segment of
  0 ms (a looping tour of zeroes is bounded, not a hang). Reduced motion keeps the holds, so autoplay is a slideshow.
- A frame longer than 100 ms (a background tab waking) counts as 100, so a show never skips a keyframe because the page slept.

## The editor

**Tour panel**: a third view of the right-hand panel beside Properties and Text. Add keyframe (captures the canvas view,
after the selected keyframe or at the end), Generate from containers (an overview, each top-level container in document
order captioned with its label, then everything outside a container; disabled with its reason when the diagram has no
container; with a tour already there it says Regenerate and replaces it, undoably), Present, Loop, and the keyframe list.
Selecting a row cuts the canvas camera to it and opens its fields: caption (commits on blur or Enter, Escape discards),
Arrive/Return and Hold in seconds (empty clears to the default, text that is not a time is refused and the field goes back),
easing, and Use current view. Reorder with Move up/down or Alt+Up/Down on a focused row; Duplicate; Delete. Every change is
one undo step (`editFlythrough` in `useDocumentEditor`, covered in `undoCoverage.test.ts`). Palette and menu entries:
Present fly-through (Cmd/Ctrl+Enter), Toggle Tour panel, Add fly-through keyframe, Export fly-through (HTML).

**Present** (`web/src/editor/PresentationStage.tsx`): a full-window read-only overlay over a snapshot of the document taken
when it opened (another writer's live-synced change never moves a show), drawn by the same `DiagramSvg`, with one control bar
(previous, play/pause/replay, next, a scrubber with a tick per keyframe, "3 / 7", full screen) and the current caption above
it. Controls and cursor fade after 2.5 s while playing or in full screen and come back on any move or key. Keys: Right, Down,
PageDown, Space, Enter next; Left, Up, PageUp, Backspace back; Home, End; 1 to 9; P play/pause; F full screen; Escape leaves
full screen first and the presentation second. A presentation clicker sends PageUp/PageDown and needs no setup. It works in a
read-only preview of a document that already has a tour.

**Export fly-through (HTML)** (`exportFlythrough.tsx`, `flythroughPlayer.ts`): one self-contained file, about 23 KB for the
example (the export SVG plus about 9 KB of player): no server, no network, no library. It plays the same tour with the same
controls and keys; adding `#play` to its URL starts it playing (a kiosk, or an iframe). The player is a hand-written port of
the camera, the state machine and the controls, written twice on purpose because the file must stand alone, and
`flythroughPlayer.test.tsx` holds the two to the same numbers (a grid of progress values for the camera, seeded random
traces of operations for the state machine, in motion and with reduced motion). A caption or title that tries to close the
script or forge an element is text, not markup.

## Frame rate

The checklist item 29 benchmark is `docs/examples/perf-2000n-2500e.json`: 2,000 nodes, 2,500 edges. A flight over it is a pan
and zoom of the viewport, the path the canvas's perf work already covers: `DiagramSvg` keeps its element tree while the
visible set still covers the view, so a frame that mounts nothing new reconciles no element, and below 25% zoom it draws
nodes and edges simplified. Two measurements:

1. **jsdom, the React half of a frame** (`PresentationStage.perf.test.tsx`, reports numbers and fails only on a gross
   regression, like `DiagramSvg.perf.test.tsx`): a tour from the whole diagram in to a close view, across to the far corner and
   back out, 532 frames of 16 ms: median 0.14 ms, p95 4.95 ms, worst 80.8 ms (the frames where the visible set changes and
   thousands of elements mount or unmount). It also pins, without timing, that a frame inside a mounted set changes no markup.
2. **Real Chrome, the whole frame** (`web/scripts/measure-flythrough.mjs`: a headed Chrome, since a hidden tab never runs
   `requestAnimationFrame`, recording the interval between animation frames and the long tasks during an autoplaying loop of
   the same tour; 120 Hz display, so a frame is 8.3 ms, M-series Mac, Chrome for Testing channel `chrome`):

   | CPU throttle | median | p95 | p99 | worst | frames over 20 ms | long tasks |
   | --- | --- | --- | --- | --- | --- | --- |
   | none (warm) | 8.3 ms | 9.2 ms | 16.7 ms | 25.5 ms | 0% | 0 |
   | none (warm, second run) | 8.3 ms | 9.3 ms | 16.9 ms | 25.1 ms | 0% | 0 |
   | 2x | 8.5 ms | 18 ms | 25.9 ms | 66.8 ms | 4% | 0 |
   | 4x | 9.9 ms | 41.7 ms | 59.4 ms | 117.7 ms | 17% | 42 (longest 66 ms) |

   So an unthrottled machine holds the display's own 120 Hz through the worst case the repo has (every element mounted in
   the overview) and never misses a 60 Hz frame; at 2x throttle (a mid-range laptop's proxy) 4% of frames miss a 60 Hz
   deadline, all of them the frames where the mounted set changes. The first flight after a cold page load has one hitch of
   100 to 400 ms (measured once in each cold run: the first mount of the full overview); every later loop is clean. At 4x the
   flight still completes but is not smooth over this document, which is the same document where the editor's own pan is
   known to be at its limit.

   Method: `BASE_URL=... DOC_ID=... EMAIL=... PASSWORD=... PLAYWRIGHT_CORE=... node web/scripts/measure-flythrough.mjs
   --throttle 1,2,4 --seconds 20` against a server with the benchmark document and a tour of four keyframes (overview, close
   view top left, close view far corner, overview), holds of 0.5 s and arrivals of 1.8 to 2.2 s.

## Design decisions

The spec left these open; each was decided, not deferred.

1. **One path per document, as an object** (`flythrough: { keyframes, loop }`), not the spec's `presentations[]`. One path is
   what the tour panel, the DSL and Present need now, and a list is an additive change later (`flythrough` can stay the
   default path).
2. **Keyframes are world rectangles captured from the view, with no element targets and no focus/dim pass.** It keeps the
   model closed over itself (nothing dangles), keeps an agent's job to numbers it can read off `geometry`, and keeps Present
   a pure function of the viewport. Dimming off-step elements and following a node that moved are the obvious next features,
   and both are additive.
3. **The camera fits the rectangle into the window (contain)**, so the same tour plays in any window shape.
4. **The fly is van Wijk's zoom-pan curve with cubic easings and a per-keyframe `cut`**, hand-written: no animation
   dependency (the spec warns off GSAP for a no-code animation editor, and `d3-interpolate` would be one more dependency for
   forty lines).
5. **`durationMs` is the arrival time and `holdMs` the dwell**, both per keyframe and sparse; the first keyframe's duration
   is the return trip when looping. Progress is one number along the path, so the scrubber, stepping and autoplay are one
   mechanism.
6. **The scrubber is progress along the path, not time**: holds are flat stretches in time, and a scrubber with flat stretches
   is useless.
7. **Stepping by hand is never held and does not stop at the keyframe it passes**; autoplay is the only thing that dwells.
8. **Present plays a snapshot**, and is read-only. A live-synced edit during a talk must not move the show.
9. **Reduced motion means cuts, not no-tour**: the holds still apply, so autoplay is a slideshow.
10. **`loop` is stored in the document**, not a player setting, because it is the author's intent for a kiosk export; the
    viewer starts paused unless the URL says `#play`.
11. **Capture is the authoring gesture; generation is a shortcut.** "Generate from containers" needs at least one container and
    replaces the tour (undoably) rather than appending, so pressing it twice is not a duplicate tour.
12. **The export is one HTML file with a hand-written player**, parity-tested against the editor's TypeScript, not a bundled
    copy of it.
13. **Cmd/Ctrl+Enter presents** (Google Slides' present-from-start), because Cmd/Ctrl+Alt+letter is unreliable on macOS (Option
    changes the character).
14. **Keyframe ids are `k_<n>`, the first unused**, unique within the tour only.
15. **MCP: one op, `setFlythrough`, replacing the whole path**, not per-keyframe ops. The path is a member, not a list of
    elements, and one atomic replace is what an agent that reads the path and rewrites it wants; per-keyframe ops can follow.
    It touches no node or edge, so it adds no ids to the write's provenance entry.

## Not built

- **3D** (the spec's CSS-3D "cheap" variant): a marketing prototype, a separate task.
- **Video or GIF export** (`MediaRecorder` over the canvas): the spec's stretch goal.
- **Focus/dim of off-step elements, speaker notes, multiple paths, per-step element targets, a click-to-advance or swipe
  gesture, a touch-friendly control bar**, and **a server-side render of a tour** (`render_document` draws the diagram, not
  the camera).
- **A `critique_document` rule for a keyframe framing empty space.**

## Proposed spec amendment

`docs/spec/build-spec-v0.1.md` is maintainer-owned and was not edited. For its Amendments section:

> Dated 2026-09-30, fly-through presentation (additive; `schemaVersion` stays 1). Sections 3, 4, 5, 7, 9, 10: `Document`
> gains the optional `flythrough: { keyframes[], loop? }`, a camera path over the diagram; each keyframe is a world
> rectangle `view` with an optional `caption`, `durationMs`, `holdMs` and `easing` (`cut|linear|easeIn|easeOut|easeInOut`).
> It is carried by the schema, the C# records, both TypeScript models, the sidecar's Yjs header and the DSL (`flythrough`
> and `keyframe` lines); every renderer and the Mermaid export ignore it. The editor gains the Tour panel (capture, reorder,
> caption, timings, generate from containers), a Present mode (play, pause, step, scrub, full screen, keyboard and clicker
> control, reduced motion) and Export fly-through (HTML), a standalone player; `patch_document` gains `setFlythrough` and
> `diff_documents` reports `flythroughChange`. The 2,000-node benchmark holds the display's own frame rate in Chrome
> unthrottled. See `docs/flythrough.md` and `design/atlas/flythrough.md`.
