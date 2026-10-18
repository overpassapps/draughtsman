# Draughtsman sidecar

A small Node 22 process, written in TypeScript and bundled with esbuild to one file (`dist/sidecar.js`), that `Draughtsman.Server` spawns as a child process. It hosts the two pieces of the product that only have mature JavaScript implementations: ELK auto-layout and the Yjs document model. The decision and its reasoning are in [ADR 0002](../docs/adr/0002-crdt-and-layout-engine.md); the .NET side is `src/Draughtsman.Sidecar` (`ISidecarClient`).

The sidecar opens no network ports. It reads newline-delimited JSON-RPC 2.0 requests on stdin, writes one response per line on stdout, logs to stderr and exits when stdin closes.

## Protocol

- One JSON object per line, UTF-8, `\n` terminated. CRLF is tolerated on input.
- Requests carry `jsonrpc: "2.0"`, a numeric or string `id`, `method` and `params` (an object). A request without an `id` is a notification and gets no reply.
- Responses may arrive in any order; correlate on `id`. Nothing but responses is ever written to stdout.
- Errors use the standard codes (`-32700` parse, `-32600` invalid request, `-32601` method not found, `-32602` invalid params, `-32603` internal) plus two of our own: `-32000` the payload is not a Draughtsman document the method can use, `-32001` ELK failed on the graph. `error.data` carries detail when there is any.
- Binary Yjs payloads cross the boundary as base64 strings.

## Methods

| Method | Params | Result |
| --- | --- | --- |
| `ping` | none | `{ ok: true, version, node, pid }` |
| `layout.elk` | `document`, optional `options { algorithm, direction }` | `{ document, warnings[] }` |
| `route.document` | `document` (nodes already placed) | `{ routes: { [edgeId]: { status, points[], reason? } } }`, only for edges the editor would route itself |
| `sequence.scene` | `document` (its `type` must be `sequence`; no geometry needed) | `{ scene: { headers[], lifelines[], messages[], bars[], destroys[], fragments[], bounds } }`, the derived layout of a sequence diagram (a note is a `messages` row with a `note` box; a `fragment` node is a frame in `fragments`, never a header) |
| `yjs.fromJson` | `document` | `{ state, stateVector }` |
| `yjs.toJson` | `state` and/or `updates[]` (applied in order) | `{ document }` |
| `yjs.applyUpdates` | optional `state`, `updates[]` | `{ state, stateVector }` (the full state after the updates) |

`algorithm` is `layered` (default for flowchart and architecture), `stress` (default for freeform) or `tree` (ELK's mr.tree, opt-in); `direction` is `down` (default), `right`, `up` or `left` and applies to layered only. Values are lower-case, as the .NET client's enum converter writes them.

## Connector routing

`sequence.scene` bundles `web/src/model/sequenceScene.ts` (the function `indexDocument` places a sequence document with), so the server's `render_document`/PDF draw the picture the editor does (`docs/sequence-diagrams.md`); like the router, it imports only DOM-free code (`web/src/canvas/textEstimate.ts`, the width estimate split out of `text.ts`). `layout.elk` returns a `sequence` document unchanged, since its geometry is derived, never stored.

`route.document` does not copy the editor's router, it bundles it: `web/src/canvas/routeDocument.ts` (`routeDocumentEdges`) is the function the client PNG/SVG export calls, over the `routingSnapshot` and `routeOrthogonal` the canvas's routing worker runs, so the server's `render_document`/PDF, the exports and the editor draw the same paths for the same document. It routes the edges with `routing.mode: orthogonal` and no stored waypoints (a pinned connector draws its waypoints) between two placed nodes, so lay the document out first. `scripts/build.mjs` aliases `yjs` to this package's copy and `tsconfig.json` maps it the same way; the Dockerfile's sidecar stage copies `web/src` and the kind vocabularies so the import graph resolves there too.

## Layout behaviour

`layout.elk` recomputes the position of every node, so one call serves import (nodes with no geometry), "Tidy" and agent requests alike. Sizes given in the input are kept; a node with no geometry, or a zero-sized placeholder, gets a default size by shape (160×80 unless the shape has its own entry in `DEFAULT_SIZE`, `src/layout/elk.ts`). Containers are sized by ELK to fit their children plus room for a title. Geometry in the result is absolute, including inside containers, and rounded to whole canvas units. A node's `rotation` is preserved.

Edges: with `layered`, ELK routes orthogonally, edges attached to a declared port leave from that port, and bend points become `routing.waypoints`; an edge that comes back with no bends loses any waypoints it had, because they belonged to the old positions. With `stress`, edges are straight and never carry waypoints, and a second ELK pass (`sporeOverlap`) pushes overlapping nodes apart, which stress on its own does not do. With `tree`, ELK's mr.tree draws a parent above its children top-down and edges are straight; an `assistant` node is then placed beside its manager on an offset side branch (when that spot is clear), the org-chart convention.

Member order in the `layout.elk` result is not a contract: the .NET client reads the frame into the document model and re-serialises it, so only `yjs.toJson` carries the canonical order described below.

Things the sidecar cannot lay out are named in `warnings` rather than failing the call: an edge to a node that does not exist, a self-loop, an edge between a container and a node inside it, an edge across containers under `stress` or `tree`, a `parent` that does not exist (the node is placed at top level), and duplicate ids.

## Yjs document shape

Spec section 4 says "a `Y.Map` per node/edge". Precisely, the shape is (source: `src/yjs/shape.ts`):

```
doc.getMap('header')   id, schemaVersion, title, type, theme?          plain values
doc.getMap('nodes')    node id -> Y.Map of that node's JSON fields, plus order
doc.getMap('edges')    edge id -> Y.Map of that edge's JSON fields, plus order
doc.getMap('meta')     createdAt, updatedAt, createdBy, aiProvenance?
```

Inside an element map every value is plain JSON (geometry, style, props, ports and routing are objects and arrays, not nested Y types), so an edit replaces the whole field. `order` is the element's index in the JSON array and its render z-order; it exists only in the Yjs form. `yjs.toJson` emits members in the canonical order the C# serialiser uses, so a document serialises to the same bytes on either side; the .NET integration test checks that with the shared example. An element map missing a field the model requires (`kind`, `label`, `shape` on a node, `kind`, `from`, `to` on an edge) is an `invalidDocument` error rather than a document with substituted values; `geometry` is optional and stays absent until layout has placed the node. The map key is the element's id: an element that also carries an `id` field must repeat the key, and a value under `nodes` or `edges` that is not a `Y.Map` (an element written as plain JSON) is an `invalidDocument` error as well, rather than an element quietly missing from the materialised document.

The SPA must build its `Y.Doc` to this exact shape and pin the same `yjs` version as `package.json` here (currently 13.6.32), or server-side snapshots will not match what the browser shows.

## Build and test

Node 22.12 or later.

```sh
cd sidecar
npm ci
npm run build      # type-checks with tsc, then esbuild writes dist/sidecar.js (about 3.4 MiB, CommonJS, no dependencies)
npm test           # builds first, then Vitest: framing, layout of the shared example, Yjs round trip, and the real bundle over stdio
```

`dotnet build` does the same `npm ci` and bundle step through `src/Draughtsman.Sidecar/Draughtsman.Sidecar.csproj` and copies `dist/` into the output of every project that references the client as `sidecar/`, so a built server tree is runnable without a separate step. Pass `-p:BuildSidecar=false` to skip that when Node is not installed, and `-p:NodeExe=/path/to/node` or `-p:NpmCommand=...` when the right Node is not first on PATH.

## How the server finds it

`NodeSidecarClient` looks for the script at `sidecar/sidecar.js` next to the server binary, which is where `dotnet build` and `dotnet publish` put it; nowhere else. It looks for Node at `sidecar/node` (`node.exe` on Windows) next to the binary, the place a release archive puts the bundled runtime, and then on PATH. Both can be pinned in `draughtsman.yaml`:

```yaml
sidecar:
  nodePath: /usr/local/bin/node   # full path, or a name to look up on PATH
  scriptPath: ""                  # empty means sidecar/sidecar.js next to the binary
  layoutTimeoutSeconds: 30        # greater than zero; anything else logs a warning and uses this default
  yjsTimeoutSeconds: 10           # greater than zero; anything else logs a warning and uses this default
```

The process is started with the server and pinged before `/api/health` reports `ok`; without it the server reports `degraded` and keeps serving. If it exits it is restarted with exponential backoff (500 ms doubling to 30 s); a request that times out kills it, because a stuck layout would block every other request, and it is restarted after the backoff. Requests that arrive during a backoff fail fast with the recorded error instead of queueing behind the restart.

## Packaging

The release archive per platform carries a Node 22 runtime under `sidecar/` next to `sidecar.js`; nothing is fetched at run time. Node's single-executable feature was tried and works with this bundle but saves nothing; the numbers and the reasoning are in the addendum to ADR 0002.
