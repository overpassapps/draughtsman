---
name: draughtsman
description: Read, create, amend, critique and render Draughtsman diagrams (flowchart, architecture, freeform and sequence documents) as a typed graph, not pixels - through the DSL text form and the MCP tools at /mcp. Use whenever asked to draw, diagram, review or amend a flowchart, architecture, sequence or other node-and-edge diagram, or when a Draughtsman document id, DSL file, API token or /mcp endpoint is mentioned.
---

# Draughtsman

Draughtsman's model is the truth; the picture is a render of it. A diagram is a typed graph (nodes, edges, a small
header) with two lossless forms: the DSL (text, what you write) and JSON (what `read_document` returns by default).
You edit the graph, never pixels. Everything below was checked against a running server (version 0.1.0).

## 1. Connect and authenticate

| Route | How |
| --- | --- |
| HTTP (normal) | `POST <base>/mcp`, streamable HTTP, JSON-RPC. Header `Authorization: Bearer dst_...` is required. Send `Accept: application/json, text/event-stream` and `Content-Type: application/json`. The server is stateless: no session id is issued and `initialize` is optional. Replies are SSE-framed (`event: message` then `data: {json-rpc}`). |
| Claude Code | `claude mcp add --transport http draughtsman <base>/mcp --header "Authorization: Bearer dst_..."` |
| stdio (same machine) | Run `draughtsman mcp --stdio`. No token: it never opens a port, and it reads the same data folder (`./data` or `DRAUGHTSMAN_DATA`). From source use `dotnet run --no-launch-profile --project src/Draughtsman.Server -- mcp --stdio`; plain `dotnet run` prints a launch-settings line to stdout and corrupts the JSON-RPC stream. |

**Getting a token.** A person mints it while signed in to the web app: **Tokens** in the top bar (`/admin/tokens`, any
signed-in user, not only admins). Choose a name, an expiry (7 days to 1 year; default 90, operator ceiling 365) and
optionally **Read-only**. The secret (`dst_` plus 43 characters) is shown once, with a ready-made `claude mcp add` line.
A token cannot create other tokens, and there is no OAuth, OIDC or `/.well-known/*` discovery: do not start an OAuth flow.

| Token | What it can do over `/mcp` |
| --- | --- |
| `full` (default) | All eight tools, as its owner. |
| `read` | `list_documents`, `read_document`, `render_document`, `diff_documents`, `critique_document`, `get_schema`. `create_document` and `patch_document` fail (below). Over REST it may only `GET`. |

**What you see when it goes wrong**

| Situation | Result |
| --- | --- |
| No token, unknown, revoked or expired token | HTTP `401`, `WWW-Authenticate: Bearer`, plain text: `Authentication required. Send an API token as 'Authorization: Bearer <token>' (create one in the web app under API tokens, or POST /api/tokens). This server does not support OAuth.` The server never says which of the three it was; ask the human for a fresh token. |
| Read token on `create_document` / `patch_document` | Tool result `isError: true`: `An error occurred invoking 'patch_document': This API token is read-only; it cannot create or change documents. Use a full-scope token.` (REST write: `403 Read-only token`.) |
| Over the rate limit | HTTP `429`, `Retry-After: 60`, problem+json `Too many requests ... try again in 60 seconds.` Default is 300 `/mcp` calls per user per minute (`limits.mcpPerUserPerMinute`, 0 = off), counting every call, `tools/list` included, across all of that user's tokens. Back off and retry; do not loop. |
| Missing or mistyped argument | `isError: true`, text like `read_document: missing required argument 'id' (a string). Required arguments: id.` or `list_documents: argument 'limit' must be a integer; got a string.` |
| Anything the tool itself rejects | `isError: true`, text `An error occurred invoking '<tool>': <reason>` (reasons are quoted throughout this file). A tool failure is a normal result with `isError`, not a JSON-RPC `error`. |

Tool output is in `result.content[0].text`: JSON text for most tools, raw text for `read_document`. Minimal calls:

```sh
curl -s -X POST http://127.0.0.1:5180/mcp \
  -H 'Authorization: Bearer dst_XXXX' -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"read_document","arguments":{"id":"doc_4347880f4b1c","format":"outline"}}}'
```

Swap `"method":"tools/list"` (no `params`) to list the tools and their input schemas.

**Who is recorded as the writer.** Over HTTP the writer is the token's owner (`user:<display name>`) and no AI
provenance is written. Pass `actor: "agent:<name>/<model>"` (for example `agent:claude-code/claude-sonnet-5`) on
`create_document` and `patch_document` to be recorded as an agent: the document gets a `meta.aiProvenance` entry (agent,
model, time, `created`/`updated`, every element id the call touched) and `createdBy` becomes the agent. Over HTTP a
`user:...` or malformed `actor` is refused (`actor must be 'agent:<name>/<model>' when calling over HTTP; ...`); it can
only annotate, never impersonate. Over stdio `actor` is self-reported and defaults to `agent:mcp-client/unknown`.
The `X-Draughtsman-Actor` header is **not read on `/mcp`**; the `actor` argument is the only label.
The `X-Draughtsman-Actor` header does the same for REST writes made with a token, but it is ignored on `/mcp`; use the argument.

## 2. The tools

All eight, with real argument names. `*` = required. Nulls are omitted from results.

| Tool | Arguments | Returns |
| --- | --- | --- |
| `list_documents` | `query` (title substring), `workspace` (only `default`), `offset` (>= 0, default 0), `limit` (1 to 200, default 50) | `{items:[{id,title,type,updatedAt}], total, offset, limit}`, newest updated first |
| `read_document` | `id*`, `format`: `json` (default) \| `dsl` \| `outline` \| `mermaid` | Text. `outline` is one line per element, cheap for prompts; a sequence outline numbers messages in time order. `dsl` and `json` carry everything (including a fly-through); `outline` and `mermaid` do not. |
| `create_document` | `title*`, `type*`, `content*`, `format*` (`dsl` \| `mermaid` \| `sql` \| `drawio` \| `visio` \| `json`), `layout` (bool, default false), `actor`, `page` (drawio and visio only, from 1) | `{id, warnings}` |
| `patch_document` | `id*`, `ops*` (array, or a string holding the array), `layout`: `none` (default) \| `affected` \| `all`, `actor` | `{document, checkpointVersionId, warnings}` (the whole patched document) |
| `render_document` | `id*`, `format`: `svg` (default) \| `pdf`, `scale` (> 0, svg only) | `{svg, warnings}`, or `{pdf, warnings}` with `pdf` as base64 |
| `diff_documents` | `id*`, `fromVersion*` (a version id or `current`), `toVersion` (default `current`) | `{fromVersion, toVersion, diff}` |
| `critique_document` | `id*` | `{findings:[{rule, severity, message, nodeIds, edgeIds}]}` |
| `get_schema` | `type` (`flowchart` \| `architecture` \| `freeform` \| `sequence`) | See section 8 |

Notes that bite:

- `create_document`: a `type` line inside `dsl` or `json` content that differs from the `type` argument is **refused**
  (the content was read against that type's kinds), and a non-empty `title` inside the content that differs from the
  `title` argument is replaced by the argument, with a warning saying so. Make the argument and the content agree.
- `layout: true` runs ELK before saving; without it, nodes with no `@ x,y` stay unplaced in the stored document
  (`render_document` lays out in memory only and does not save positions). A `sequence` document never needs layout.
- `create_document` with `format: "json"` takes the same body as `POST /api/documents`: `id`, `schemaVersion` and `meta`
  may be left out and are filled in (a body `id` is never kept; `meta.aiProvenance` is dropped), `title` and `type` may be
  left out and come from the arguments, and every node needs `shape` (section 4).
- `mermaid`: a flowchart source for `flowchart`/`architecture`, a `sequenceDiagram` for `sequence`, an `erDiagram` for
  `architecture`/`freeform`. A mismatch is refused (`A sequence document is made from a Mermaid 'sequenceDiagram'; this source is not one. ...`).
  Node kinds are guessed from Mermaid shapes and flagged in `warnings` (`kind-guessed`). `sql` takes `CREATE TABLE`
  DDL, always laid out, type `architecture` or `freeform`, one entity per table and one edge per foreign key.
- `drawio` is a draw.io (diagrams.net) file's XML, plain or compressed (a `.drawio` file as it is), type `flowchart`,
  `architecture` or `freeform`. Positions come with the file, so it is **not** laid out unless `layout: true`. Kinds are guessed
  from shapes and flagged in `warnings` (`kind-guessed`); a stencil, picture or loose connector end is a warning, not an error.
  A multi-page file imports page 1 unless `page` says otherwise, and a `page-selected` warning says so. Details: `docs/drawio-import.md`.
- `visio` is a Visio `.vsdx` drawing, **base64-encoded in `content`** (a `.vsdx` is a binary ZIP and an argument is text: `base64 -i file.vsdx`;
  up to 3,000,000 bytes before encoding). Same types and `page` as `drawio`, positions kept and not laid out unless `layout: true`.
  Kinds are guessed from the master's name and the outline (`kind-guessed`); stencil masters, custom outlines, pictures, shape data, themes,
  layers and flips are ONE `unsupported-syntax` warning each with a count, a connector with a loose end is `missing-reference`. An older
  `.vsd` or a ZIP that is not Visio is an error that says to save as `.vsdx`. A Lucidchart document comes in by exporting it to Visio first.
  Details: `docs/moving-from-lucidchart-visio-drawio.md`.
- `read_document format=mermaid` exports a `sequenceDiagram` for a sequence document, a `flowchart` otherwise.
- Unknown id: `No document with id 'doc_x'.` There is no delete tool and no rename tool over MCP.

## 3. Document types

| `type` | Node kinds (from `get_schema`) | Edge kinds | Geometry |
| --- | --- | --- | --- |
| `flowchart` | `terminal`, `process`, `decision`, `io`, `subprocess`, `note`, `container`, plus `pool`/`lane`, UML state and BPMN kinds (`bpmn-*`) | `flow`, `association`, `message-flow`, `bpmn-association`, `data-association` | Stored; optional on input |
| `architecture` | `service`, `database`, `queue`, `actor`, `external`, `container`, `note`, `text`, `image`, `sticky`, plus icon kinds (`load-balancer`, `api-gateway`, `cache`, `cdn`, `firewall`, `object-storage`, `function`, ...), org-chart, UML class and ER kinds | `flow`, `data`, `dependency`, `association`, `dotted-report` | Stored; optional on input |
| `freeform` | None: any kind word is accepted, shapes default to `rectangle` | Any | Stored; optional on input |
| `sequence` | `participant`, `actor`, `fragment` | `sync`, `async`, `return`, `note`, `interaction-use`, `create`, `destroy` | **None stored** (below) |

Use only kinds from `get_schema` for the type. An unknown kind is a `warnings` entry on DSL create, but `patch_document`
accepts it silently and `critique_document` does not flag it, so check against the vocabulary yourself.

**Ids.** `^[A-Za-z_][A-Za-z0-9_.-]*$`, 1 to 128 characters, stable for the life of the element, never regenerated. Nodes
and edges are separate namespaces, but a duplicate within one is refused (`Cannot add node 'n_q': a node with that id already exists.`).
Convention: `n_` nodes, `e_` edges, `c_` containers, `p_`/`m_` participants/messages. Avoid dots in ids if you use ports.
A bad id: `'1bad' is not a valid id; ids start with a letter or underscore and contain letters, digits, '_', '.' or '-'`.

**Sequence documents.** Participants are nodes, messages are edges, and **two orders are the whole meaning: the order of
`edges` is time (first edge = first message), the order of `nodes` is left to right.** Write no `@ x,y`, no `style`, no `routing`:
they are derived on every render. To insert a message mid-stream use `addEdge` with `before` or `after`; to move one use
`moveEdge`. There is no `moveNode`: participant order is `nodes[]` order, so add participants in the order you want them.
Message props: `activate=true` opens an activation bar on the receiver, `deactivate=true` closes the sender's; `in="<fragment id>"`
and `operand=<n>` put a message in a combined fragment. A fragment is a node of kind `fragment` with `operator` and `operands` props; membership
is written on each message, never on the fragment. `create`/`destroy` messages start or end a lifeline; a `note` edge is a row of text from `from` to `to`.

## 4. The DSL

```
draughtsman 1
type architecture
title "Order service"

node n_web service "Web app" @ 40,40 320x80
container c_vpc "VPC" @ 16,168 568x300 { n_api n_db }
node n_api service "Orders API" @ 40,200 320x80 ports=[in@0.5,0 db@0.25,1] owner="platform"
node n_db database "Postgres" @ 40,360 160x80

edge e_1 flow n_web -> n_api.in "HTTPS" routing=orthogonal
edge e_2 data n_api.db -> n_db "read/write"
```

Rules agents get wrong:

- First line is `draughtsman 1`; `type` is required; `id`, `title`, `meta`, geometry and style are optional.
- **Labels and string prop values are always double-quoted**: `node n_a process A` is an error
  (`expected the label in double quotes but found 'A'`). Booleans and numbers are bare (`activate=true`, `count=3`).
- Node form: `node <id> <kind> "<label>" [@ x,y [wxh]] [key=value ...]`. `@ x,y` alone gives 160x80. Without `@` the node is unplaced; use `layout: true`.
- Edge form: `edge <id> <kind> <from>[.port] -> <to>[.port] ["<label>"] [key=value ...]`; `routing=orthogonal|straight|curved`.
- **`shape` is inferred from `kind`** (service becomes `rounded`, database `cylinder`, ...). Write `shape=` only to override.
- Containers: `container <id> "<label>" @ ... { child ids }`, nestable. A node's parent can also be set with `parent=<id>`.
- Style is sparse: `style.fill=#fde68a`, `style.stroke=...`, `style.dash=dashed`. Reserved bare keys (`id kind label shape parent rotation ports routing waypoints style.*`) are not props; any other `key=value` is a free-form prop.
- `#` starts a comment after whitespace. Parse errors name line and column: `DSL parse error at line 3, column 18: ...`. Lint problems (unknown kind, an edge to an undeclared node) come back in `warnings` and do not stop the create.

**Sequence DSL** (no geometry, edge order is time):

```
draughtsman 1
type sequence
node p_user actor "Customer"
node p_web participant "Web app"
node p_api participant "Orders API"
edge m_1 sync p_user -> p_web "Place order"
edge m_2 sync p_web -> p_api "POST /orders" activate=true
edge m_3 return p_api -> p_web "201 Created" deactivate=true
```

**JSON and REST need `shape`; the DSL does not.** In JSON every node must carry `id`, `kind`, `label` **and `shape`** (edges:
`id`, `kind`, `from`, `to`). This applies to `create_document format=json`, `POST /api/documents`, and the `node` you give `addNode`:
leaving `shape` off is refused (`/nodes/0: Required properties ["shape"] are not present`), while the DSL form of the
same node is accepted and the server infers the shape. Use the kind's `defaultShape` from `get_schema` (participant
`rectangle`, actor `rounded`, fragment `rectangle`, service `rounded`, database `cylinder`). `POST /api/documents`
(REST, bearer token) and `create_document format=json` both fill in `id` and `meta` themselves. Valid `shape` values: `rectangle rounded diamond cylinder pill hexagon parallelogram text image`,
the ISO 5807 symbols listed by `get_schema`, and `custom:<lib>/<name>` library shapes (get_schema lists the libraries).

## 5. Amending: `patch_document`

`ops` is an array of addressable operations, or a string holding that JSON. Ops apply in order; **any unknown op,
missing target or schema violation fails the whole call and writes nothing.**

| Op | Fields | Notes |
| --- | --- | --- |
| `addNode` | `node` (full node, `shape` required) | Duplicate id refused. |
| `removeNode` | `id` | Edges that referenced it are **left dangling** and reported in `warnings`; remove them yourself. |
| `updateNode` | `id`, `set` | `set` may hold only `label`, `kind`, `shape`, `geometry` (all of x,y,w,h), `parent`, `ports`. Not `props` or `style`. |
| `addEdge` | `edge`, optional `before` or `after` (an edge id) | A missing endpoint node is accepted with a warning (`edge 'e_x' references a missing node ('n_ghost')`). |
| `moveEdge` | `id`, `before` or `after` | Reorders only. Exactly one of the two. |
| `removeEdge` | `id` | |
| `updateEdge` | `id`, `set` | `label`, `kind`, `from`, `to`, `routing` only. |
| `setProp` | `target` (`node`\|`edge`), `id`, `key`, `value` | `value` required. Use this for `activate`, `in`, `operand`, `columns`, ... |
| `removeProp` | `target`, `id`, `key` | Refused if the key is not set. |
| `restyle` | `target`, `id`, `style` | Merges sparse style fields; a field set to `null` is cleared. |
| `setFlythrough` | `flythrough` (object, or `null` to remove) | Section 9. Not listed in the tool's own description, but accepted. |

```json
[
  { "op": "updateNode", "id": "n_api", "set": { "label": "Orders API v2" } },
  { "op": "addNode", "node": { "id": "n_cache", "kind": "cache", "label": "Redis", "shape": "rounded" } },
  { "op": "addEdge", "edge": { "id": "e_3", "kind": "data", "from": { "node": "n_api" }, "to": { "node": "n_cache" } } },
  { "op": "restyle", "target": "node", "id": "n_cache", "style": { "fill": "#fde68a" } },
  { "op": "setProp", "target": "node", "id": "n_cache", "key": "owner", "value": "platform" }
]
```

Failures you will meet, verbatim after the `An error occurred invoking 'patch_document': ` prefix:

| Cause | Message |
| --- | --- |
| Unknown op | `Unknown patch op 'frobnicate'. Expected one of: addNode, removeNode, updateNode, addEdge, removeEdge, updateEdge, moveEdge, setProp, removeProp, restyle, setFlythrough.` |
| Missing target | `Cannot update node 'n_nope': no node with that id exists.` |
| Unknown document | `No document with id 'doc_nope'.` |
| `props`/`style` in `set` | `updateNode/updateEdge cannot set 'props' directly; use setProp/removeProp instead.` |
| Not an array | `ops must be a JSON array.` / `ops is not valid JSON: ...` |
| Bad `layout` | `layout must be 'none', 'affected' or 'all'; got 'some'.` |
| Missing `shape`, bad id, partial geometry | `The document is not valid: ...` followed by JSON-pointer paths such as `/nodes/3: Required properties ["shape"] are not present` |
| Writer clash | `Document '<id>' was changed by another writer while it was being patched; nothing was written. Read it again and re-send the ops.` (from the source; I could not provoke the race on the test server.) |

**`layout`.** `none` leaves positions alone. `affected` and `all` re-run ELK over the **whole** document (there is no
partial layout), so untouched nodes can move; `affected` only does so when some node has no geometry. Prefer `none` and give new
nodes a `geometry`, or accept a relayout.

**Version safety.** Inside one `patch_document` call the server reads the current document, applies the ops and writes
back only if nobody else wrote in between, so a concurrent edit is never overwritten. You carry no ETag over MCP, which also
means the ops are applied to whatever is current, not to the copy you last read: re-read before a patch that depends on it. For REST, every
document response has an `ETag` (`"dv1:..."`) and `PUT /api/documents/{id}` requires it back in `If-Match`: absent is `428 Base version required`,
stale is `412 Document has changed` (nothing written). On any refusal, read again and re-apply.

**Checkpoints.** Every successful `patch_document` first stores the pre-patch snapshot as a version and returns its id
(`checkpointVersionId`, even for an empty `ops`). Compare with `diff_documents(id, fromVersion=<that id>)`. List versions
with `GET /api/documents/{id}/versions`; to roll back, `GET /api/documents/{id}/versions/{vid}` and `PUT` its `document`
to `/api/documents/{id}` with the current `ETag` in `If-Match`. There is no MCP restore tool. `create_document` makes no checkpoint.

## 6. Render and critique

**`render_document`.** `svg` works everywhere and returns the markup plus `warnings` naming anything it could not draw
(for example `edge 'e_x' was not drawn: the document has no node with id 'n_q' or 'n_ghost'.`). `pdf` needs the operator's optional Chromium component; without it:
`render_document format 'pdf' needs the optional Chromium export component, which is not available. Enable it with export.chromium.enabled and export.chromium.path in draughtsman.yaml and install Chromium (...)`.
`png` is not available over MCP: `render_document format must be 'svg' or 'pdf'; got 'png'. ...`. `GET /api/health` reports
`export.pdf` true or false, so you can check first. The server renderer ignores the fly-through and draws with the default theme.
A sequence render needs the Node sidecar and refuses rather than guesses if it is down.

**`critique_document`.** Deterministic: the same document always gives the same findings. `severity` is `error` or `warning`; every id in `nodeIds`/`edgeIds` exists.

| Rule | Meaning |
| --- | --- |
| `dangling-edge` (error), `dangling-parent` (error), `non-container-parent` (error) | An endpoint or parent that is missing, or a parent that is not a container. Runs on every type. |
| `decision-single-branch`, `decision-branch-unlabelled`, `dead-end`, `unreachable-node`, `isolated-node`, `empty-container`, `database-unused` | Flowchart and architecture structure. A freeform document is checked only for dangling references (plus the two ER rules below). |
| `unknown-column`, `entity-without-primary-key` | ER entity shapes; every type. |
| `unbalanced-activation`, `empty-fragment`, `non-contiguous-fragment`, `unknown-fragment` | Sequence only (all warnings). |

Run it after a patch; fix `error` findings first with `removeEdge` or `updateEdge`.

## 7. Typical session

1. `list_documents(query)` to find the diagram, or `create_document` from DSL (or a Mermaid sketch) with `layout: true`.
2. `read_document(id, format: "outline")` for a cheap view; `dsl` before editing so you reuse real ids and kinds.
3. `get_schema(type)` before using a kind you have not seen.
4. `patch_document(id, ops, actor: "agent:<name>/<model>")`; keep `checkpointVersionId`.
5. `critique_document(id)`; `render_document(id)` to show a person.

## 8. `get_schema`

Without `type` it returns just `{documentSchema}`, the JSON Schema for a document (about 11 KB). With any `type` it adds, for
`flowchart`, `architecture` and `sequence`, `kindVocabulary` (`nodeKinds` and `edgeKinds` with descriptions, `defaultShape`,
and for sequence the props), and for every type including `freeform`: `genericIcons` (shapes `custom:generic/<name>`), `stateShapes`,
`bpmnShapes`, `umlShapes`, `erShapes` and `basicShapes` (`custom:basic/<name>`: ellipse, triangle, trapezoid, chevron, cross, arrows, note, callout, brace, cube, folder, component; placed on any kind, so the shape is always written). With a type it is 45 to 70 KB; call it once and keep what you need. An unknown type: `type must be 'flowchart', 'architecture', 'freeform' or 'sequence'; got 'bogus'.`

## 9. Fly-through (camera tour)

A document may carry an optional top-level `flythrough`: an ordered camera path the editor plays as a tour (Present mode, or an
exported HTML player). It is presentation only: no node or edge refers to it, it is ignored by layout, `render_document`,
critique and Mermaid, and a keyframe does not follow a node that later moves.

An agent **can** author one, two ways:

1. In DSL (`create_document`), after the edges. Each `keyframe` is a world rectangle in the same units as node geometry:

```
flythrough loop=true
keyframe k_1 @ -20,-20 400x300 caption="Overview"
keyframe k_2 @ 0,0 200x120 caption="Web app" duration=1600 hold=3000 easing=easeOut
```

2. With `patch_document`, which replaces the whole path (or `null` removes it); it is checkpointed like any patch:

```json
[{ "op": "setFlythrough", "flythrough": { "loop": false, "keyframes": [
  { "id": "k_1", "view": { "x": -20, "y": -20, "w": 400, "h": 300 }, "caption": "Overview" },
  { "id": "k_2", "view": { "x": 0, "y": 180, "w": 200, "h": 120 }, "caption": "API", "durationMs": 1200, "easing": "linear" } ] } }]
```

Fields: `id` unique within the tour; `view` (`x`,`y`, `w`>0, `h`>0); optional `caption`, `durationMs` (time to arrive, default 1500),
`holdMs` (dwell, default 2500), both 0 to 600000; `easing` one of `cut linear easeIn easeOut easeInOut`; tour-level `loop`.
To frame a node, read its `geometry` and pad it. An empty `keyframes`, a zero-area view or an unknown easing fails the whole patch
(`/flythrough/keyframes: Value should have at least 1 items`). In DSL the same mistakes are dropped with a warning or are syntax errors.
`diff_documents` reports a changed tour as `flythroughChange {from, to}`. `read_document format=dsl` or `json` shows it; `outline` does not.
Present mode and HTML export are editor features; an agent only writes the path. 3D and video export do not exist.

## 10. Do not assume

- No `png` render, no delete, rename, restore or `moveNode` tool over MCP.
- No OIDC, OAuth, SSO or per-workspace tokens; `workspace` is always `default`.
- A token cannot mint tokens, manage users or read admin pages.
- `get_schema` and `read_document format=dsl` are the sources of truth for kinds and syntax; this file is a summary.
  Full references in the repository: `docs/api.md` (`POST /mcp`, Auth), `docs/spec/dsl.md`, `docs/sequence-diagrams.md`, `docs/flythrough.md`.
