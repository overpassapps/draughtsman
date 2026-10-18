# ADR 0002: CRDT materialisation and layout engine on the server

**Status:** Accepted, 2026-09-20

## Context

The spec makes two things non-negotiable that both need a JavaScript runtime on the server:

1. **Yjs from day one** (section 3). The persisted form of a document is a Yjs document; the JSON snapshot alongside it is regenerated on save and feeds search, export and the MCP read path (section 4). The server therefore has to *materialise* Yjs state: apply a stream of binary updates to a document and read the result out as the JSON model. There is no mature Yjs implementation for .NET.
2. **ELK auto-layout** (sections 5, 6 and 9). `create_document` via MCP or REST accepts DSL, Mermaid or JSON with no geometry and must return a laid-out document with no browser in the loop. ELK is a Java library; the JavaScript build (`elkjs`) is the only maintained non-JVM form. There is no .NET port.

The maintainer asked for this to be settled in M0 with a recorded decision rather than discovered in M1.

## Decision

**A bundled Node sidecar**, living in `sidecar/`, written in TypeScript and bundled with esbuild to a single JavaScript file, that `Draughtsman.Server` spawns as a child process and talks to over **stdio using JSON-RPC 2.0** with newline-delimited JSON frames (one JSON object per line on stdin and stdout; stderr is captured to the server log).

### Methods

| Method | Params | Result |
| --- | --- | --- |
| `yjs.applyUpdates` | `docId?: string`, `state?: base64`, `updates: base64[]` | `state: base64` (full encoded state after applying updates in order) |
| `yjs.encodeState` | `state: base64` | `{ state: base64, stateVector: base64 }` |
| `yjs.toJson` | `state: base64` | the JSON document model (schema in `docs/schema/document.schema.json`) |
| `layout.elk` | `document` (JSON model), `algorithm: "layered" \| "stress"`, `options?: object` | the same document with `geometry` filled on every node and `waypoints` on edges where ELK produced them |
| `ping` | none | `{ ok: true, version: string }` |

All binary payloads cross the boundary as base64 strings. Requests carry a JSON-RPC `id` and are correlated on the .NET side by that id; the sidecar may answer out of order.

### Process model

- One sidecar process per server process, started at server startup and health-checked with `ping` before the server reports healthy.
- If the sidecar exits, the server restarts it with exponential backoff (starting at 500 ms, capped at 30 s) and fails any in-flight requests with a retryable error. A sidecar that cannot be started at all makes `/api/health` report degraded, not down: editing continues; snapshots and server-side layout fail fast with a retryable error until it returns.
- Per-request timeout (default 30 s for layout, 10 s for Yjs calls), configurable in `draughtsman.yaml` under `sidecar`.

### Keeping the sidecar off the hot path

The server treats Yjs updates as **opaque bytes** on the editing path: the WebSocket hub relays them between clients and appends them to `document_updates` without decoding. The sidecar is called only to:

- materialise a snapshot, batched and debounced (after N updates or T seconds since the last snapshot, both configurable, and always before a named version or an agent write checkpoint);
- serve the MCP read path when the stored snapshot is behind the update log;
- run layout on import, on "Tidy", and on agent request.

So sidecar latency never sits between a keystroke and another editor's screen.

### Version pinning

`web/` and `sidecar/` must pin the **same** `yjs` major version, and in practice the identical version. The Yjs update format is stable across versions, but keeping them identical removes a whole class of "works in the browser, snapshot differs on the server" bugs.

## Alternatives considered

**(a) Native y-crdt bindings for .NET (YDotNet).** The community `YDotNet` package wraps `yrs` (the Rust Yjs port) via FFI and ships native binaries per platform; Squidex uses it in production. This is genuinely viable and is recorded here as **the fallback** if sidecar snapshot latency or throughput ever becomes a measured problem. Rejected for M0 because ELK still needs a JavaScript runtime, so a Node process is required regardless; adding a native-FFI dependency on top creates a second per-platform packaging and debugging risk axis across five RIDs for no reduction in moving parts.

**(b) Layout only in the browser.** Rejected: `create_document` from an agent with missing geometry is an MVP acceptance test (M1) and must work with no browser session open. It would also make the MCP `render_document` output depend on a client having visited the document.

**(c) A pure-JSON model without Yjs** (server-authoritative JSON plus an operation log, CRDT added later). Rejected: spec section 3 lists the Yjs document model as non-negotiable precisely because retrofitting collaboration is misery, and the editor's undo/redo (`Y.UndoManager`) and the sync path are designed around it. Choosing this would trade a known M0 cost for an unknown M4 rewrite.

**(d) Embedding a JavaScript engine in-process** (Jint or ClearScript V8). Rejected: Jint is an interpreter and is too slow for `elkjs` on graphs of a few hundred nodes; ClearScript ships native V8 binaries per platform, so it inherits the FFI packaging problem of (a) while losing the crash isolation a separate process gives.

**(e) Ycs, the C# port of Yjs.** Rejected: incomplete, unmaintained for years, no support for the v2 update format, and no one to fix it but us.

## Consequences

- **Packaging.** The self-contained release must carry a JavaScript runtime. Two options: bundle a Node 22 runtime per RID next to the sidecar bundle (roughly 50 MB per artefact), or compile the sidecar with Node's Single Executable Application (SEA) feature per RID. Decision: the sidecar task evaluates SEA; the default until then is a Node 22 runtime under `sidecar/` inside the release archive. Docker images layer the official Node binary into the image. Nothing is downloaded at runtime, so air-gapped installs are unaffected either way.
- **Security posture.** The sidecar is a local child process on stdio; it opens no ports and receives only what the server sends it. This keeps the "no phone-home" promise intact and is easy to explain to an auditor.
- **Repo shape.** `sidecar/` is a separate Node project from `web/`. They will duplicate the document-model TypeScript types at first. If that duplication hurts, lift shared types into a `packages/model` workspace; noted as tech debt, deliberately not done in M0.
- **Testing.** The sidecar gets its own unit tests (Vitest) and a .NET integration test that spawns the real bundled sidecar and round-trips a document through `yjs.applyUpdates` then `yjs.toJson`. CI runs that on all three OS legs, which is also how we find path and process-spawning bugs early.
- **M0 scope.** Only `sidecar/package.json` and `sidecar/README.md` exist in the skeleton. The implementation is the sidecar task.

## Addendum: what the sidecar task found (2026-09-21)

Recorded after the implementation in `sidecar/` and `src/Draughtsman.Sidecar`; the decision above stands. The method table in `sidecar/README.md` is the maintained one.

**Protocol as built.** Same framing as decided; `ping` is the health method. `yjs.fromJson` (document to state) was added because the server needs to create a Yjs document from JSON on `create_document`. `yjs.toJson` accepts `updates` alongside `state` so the MCP read path can materialise a stored state plus the tail of the update log in one call. `layout.elk` returns `{ document, warnings }` rather than the bare document: things the layout could not do (edges to missing nodes, self-loops, container-to-child edges) are reported instead of failing, which is what `create_document`'s `warnings[]` needs. `algorithm` lives inside `options` with the other layout options rather than at the top level as the table above shows; the .NET client sends `options.algorithm` and the sidecar accepts only that.

**Layout engine behaviour worth knowing.** ELK `layered` honours fixed port positions, so edges leave from the declared port and bend points map straight onto `routing.waypoints`. ELK `stress` rejects fixed ports ("Port side is undefined"), routes no edges, has no notion of edges across hierarchy levels, and lets boxes overlap; the sidecar therefore drops ports and cross-container edges for stress and runs ELK's `sporeOverlap` algorithm as a second pass over the positioned graph. Layout always recomputes every position; "pinned nodes" is the phase 2 layout-constraints item and not attempted here.

**Process supervision as built.** One process, started with the host and pinged before health reports `ok`. Exit is noticed by the stdout pump and the exit watcher; in-flight requests fail with a retryable `SidecarUnavailableException`, and a restart is scheduled after the backoff (500 ms doubling to 30 s) without waiting for traffic. A request that exceeds its timeout kills the process, because the sidecar is single-threaded and a stuck layout blocks every other request; the watcher restarts it after the backoff, and requests in the meantime fail fast rather than queueing behind the restart. Layout and Yjs timeouts are configurable under `sidecar` in `draughtsman.yaml` as decided, plus `nodePath` and `scriptPath`.

**Packaging numbers, measured on macOS arm64 with Node 22.19.0.**

| Artefact | Size |
| --- | --- |
| `dist/sidecar.js` (esbuild, CommonJS, elkjs 0.12.0 bundled in-process, yjs 13.6.32) | 3.4 MiB |
| Node 22.19.0 binary alone | 110 MB (35 MB gzipped) |
| Node single executable (SEA) with the bundle injected | 113 MB |

**Single executable application (SEA) evaluated.** It works with this bundle: the bundle is CommonJS and requires nothing but Node built-ins, which is exactly what SEA supports. The steps are `node --experimental-sea-config`, copying the Node binary, injecting the blob with `postject`, and re-signing (`codesign` on macOS, `signtool` on Windows), all doable on any host per target platform. It saves nothing: the executable is the Node runtime plus the 3.4 MiB blob, and the feature is still marked experimental in Node 22 (`--experimental-sea-config`). The only gain is one file instead of a runtime folder plus a script. **Decision:** keep the default from this ADR, a Node 22 runtime under `sidecar/` next to `sidecar.js` in each release archive (Docker images layer the official Node binary), and revisit SEA at release packaging time (M3) only if the two-file layout proves awkward to sign or install. The client already looks for `sidecar/node` next to the server binary before PATH, so either packaging drops in without a code change.

**Runtime size is the cost.** The ADR's estimate of roughly 50 MB per artefact was optimistic: an uncompressed Node 22 runtime is about 110 MB, 35 MB in the compressed archive. That is the price of the install promise and is accepted; if it ever matters, the fallback remains YDotNet for Yjs plus a smaller JavaScript engine for ELK alone, and nothing in the client contract precludes it.

**Build integration.** `src/Draughtsman.Sidecar/Draughtsman.Sidecar.csproj` runs `npm ci` (incremental on `node_modules/.package-lock.json`) and the bundle script (incremental on the TypeScript sources) as part of `dotnet build`, calling `node scripts/build.mjs` directly rather than `npm run build` so the build does not depend on npm's PATH rewriting, and copies `dist/` into the output of every referencing project as `sidecar/`. This is what lets the .NET integration tests spawn the real bundle on all three CI legs.
