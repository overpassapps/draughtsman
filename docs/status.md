# Status and feature detail

This page holds the long-form status of Draughtsman 0.1.0: what is built, how each part is configured, and the
developer workflow. The [README](../README.md) is the short version; [`index.md`](index.md) lists the guides for installing and
running it. Draughtsman is free software under the GNU AGPL-3.0-only; there is no licence file, key or evaluation mode, and
every feature described here is in the open build.

## Status

Version 0.1.0, not yet released. The editor's acceptance checklist is complete; the product shell (sign-in, administration, branding and themes, AI settings, backup and restore, a recycle bin and version history) and the shape library are built. See [`CHANGELOG.md`](../CHANGELOG.md) for what 0.1.0 contains and its known limitations. Draughtsman is built for agent-native diagramming, with a typed graph model and a lossless text form (DSL).

- **Foundations**: DSL parser and printer with round-trip fuzz tests, Mermaid, SQL and DSL import, SQLite/Postgres storage behind `IStore` with the document REST API, and the ELK layout and Yjs sidecar.
- **Editor**: a document home screen (a grid of diagram thumbnails, or a table) with create, rename, duplicate, search, sort and delete (deleted documents go to a bin for 30 days), a version history panel (named versions, compare, restore) and a critique panel; a styling panel; curved connectors with segment dragging; connector ends including ER and UML notation; connectors that attach to a table's column rows; and pen and touch for connecting and dragging. Sequence diagrams are edited on the canvas by message order.
- **Shape library**: flowchart, generic tech icons, org chart, UML state, UML class, BPMN, swimlanes and database tables (see [Diagram types](#diagram-types)).
- **Export**: PNG and SVG are client-side and draw the connectors as the editor routes them; PDF prints from the browser by default, with an optional server-side Chromium route (see [PDF export](#pdf-export-optional-chromium-component)). See [Import and export](#import-and-export).
- **Fly-through**: a camera path over a diagram, saved in the document (keyframes you capture from the canvas, or generate from its containers). Present plays it full screen with captions, play/pause, stepping, a scrubber and clicker keys, and Export fly-through writes one self-contained HTML file that plays anywhere (see [`docs/flythrough.md`](flythrough.md)).
- **Product shell**: local sign-in, API tokens, an admin branding page, admin AI settings and runtime chrome themes (see [Product shell](#product-shell)). A multi-stage [Docker image](#docker) is provided for self-hosted deployment.
- **Agents and AI**: a full set of MCP tools is available for agents: `list_documents`, `read_document`, `create_document`, `patch_document`, `render_document`, `diff_documents`, `critique_document` and `get_schema` (see [`docs/api.md`](api.md)). "Bring-your-own AI" is supported via an Ollama, OpenAI-compatible, Anthropic or Azure OpenAI provider, including **Generate from text** and **Diagram to document** (see [Bring your own AI](#bring-your-own-ai)).
- **Not built yet**: OIDC, multiple workspaces, a Viewer role or per-document permissions (every signed-in person can read every diagram, and an Editor can change any of them), and a published container image.

## Diagram types

A document has one of four types; the type picks the kind vocabulary (`get_schema` returns it).

- **Flowchart**: processes, decisions, terminators and the wider ISO 5807 symbol set, plus swimlane pools and lanes and the BPMN shapes ([`docs/bpmn.md`](bpmn.md) lists what is shipped and omitted).
- **Architecture**: services, stores, queues, actors and external systems, with the generic tech icons (load balancer, API gateway, cache, CDN, firewall, VPC boundaries and more), org-chart boxes, UML class and interface shapes ([`docs/uml-class-compartments.md`](uml-class-compartments.md)) and database tables ([`docs/er-entities.md`](er-entities.md)).
- **Freeform**: no vocabulary, so every shape family is available from the palette: presentation shapes, flowchart symbols, tech icons, org chart, UML state shapes, UML class, BPMN, swimlanes and ER tables.
- **Sequence**: participants are nodes and messages are edges, and the order of the edges is time, so no position is stored and an agent writes messages in the order they happen ([`docs/sequence-diagrams.md`](sequence-diagrams.md)).

A database table is one node whose rows are its columns; a foreign-key connector attaches from one column's row to another's.

## Import and export

| Format | In | Out |
| --- | --- | --- |
| DSL (the lossless text form) | `POST /api/import/dsl` | `POST /api/export/dsl` |
| JSON (the document model) | `POST /api/documents`, `PUT /api/documents/{id}` | `GET /api/documents/{id}` |
| Mermaid | `POST /api/import/mermaid`: flowchart, graph and `sequenceDiagram` | `POST /api/export/mermaid` (a `sequenceDiagram` or a `flowchart`), MCP `read_document format=mermaid`, File menu Export Mermaid (saves a `.mmd` file, listing anything it simplified) and Copy as Mermaid (the clipboard) |
| SQL DDL | `POST /api/import/sql` (T-SQL, PostgreSQL, MySQL, SQLite) | `POST /api/export/sql` |
| draw.io (diagrams.net) | `POST /api/import/drawio` (plain or compressed, one page of a multi-page file; positions kept, nothing laid out; [`docs/drawio-import.md`](drawio-import.md)) | |
| Visio (`.vsdx`), and Lucidchart via its export to Visio | `POST /api/import/visio` (one page of a multi-page file; groups, connectors and masters; positions kept, nothing laid out; [`docs/moving-from-lucidchart-visio-drawio.md`](moving-from-lucidchart-visio-drawio.md)) | |
| PNG, SVG | | File menu, client-side |
| PDF | File menu: Export PDF… opens the browser's print dialog (Save as PDF) | `GET /api/documents/{id}/pdf`, with the optional Chromium component |

The editor's File menu has matching Import Mermaid, Import draw.io, Import Visio and Import SQL dialogs. Importers are stateless: nothing is saved, and a document with no geometry goes through `POST /api/layout` before it is stored. Sequence diagrams need no layout. The MCP `create_document` tool takes `dsl`, `mermaid`, `sql`, `drawio`, `visio` (the `.vsdx` file base64-encoded) or `json`. Every endpoint is in [`docs/api.md`](api.md).

### Quickstart: import over the API

With the server running and an API token (see [Sign-in](#sign-in)), a Mermaid `sequenceDiagram` becomes a `sequence` document:

```sh
curl -s -X POST 'http://localhost:5180/api/import/mermaid?title=Checkout' \
  -H "Authorization: Bearer $DRAUGHTSMAN_TOKEN" -H 'Content-Type: text/plain' \
  --data-binary $'sequenceDiagram\n  participant U as Customer\n  participant W as Web app\n  U->>W: Place order\n  W-->>U: 201 Created'
```

and SQL DDL becomes entity tables joined by a foreign-key connector:

```sh
curl -s -X POST 'http://localhost:5180/api/import/sql?dialect=postgres&title=Shop' \
  -H "Authorization: Bearer $DRAUGHTSMAN_TOKEN" -H 'Content-Type: text/plain' \
  --data-binary $'CREATE TABLE customers (id int PRIMARY KEY, name text NOT NULL);\nCREATE TABLE orders (id int PRIMARY KEY, customer_id int REFERENCES customers (id));'
```

Each answers `{ "document": …, "warnings": […] }`. Post the SQL document to `POST /api/layout` to place the tables before saving it.

## Product shell

- **Sign-in**: local accounts, cookie sessions, an admin users page and per-user API tokens, protecting every route including MCP ([Sign-in](#sign-in)).
- **Branding**: an admin uploads a product name, logo and favicon at `/admin/branding`, with no restart ([Configuration](#configuration)).
- **AI settings**: an admin picks and tests the model provider at `/admin/ai` ([Admin AI settings](#admin-ai-settings-no-file-or-environment-access-needed)).
- **Themes**: the chrome theme is a YAML file loaded at runtime, and the diagram themes are compiled from `themes/` ([`themes/README.md`](../themes/README.md)).

## Layout

```
Draughtsman.sln
src/Draughtsman.Core/      document model records, kind vocabularies (Schemas/*.json), IStore contract, DSL parser and printer (Text/), text importers (Import/)
src/Draughtsman.Sidecar/   ISidecarClient: spawns, supervises and talks to the Node sidecar (ADR 0002)
src/Draughtsman.Storage/   EF Core IStore for SQLite and Postgres, with a migration set per provider
src/Draughtsman.Mcp/       the MCP tools over IStore and ISidecarClient, transport-agnostic (docs/api.md)
src/Draughtsman.Server/    ASP.NET Core minimal API (document REST contract in docs/api.md) and the MCP transports; serves web/dist when built
tests/                     xUnit tests for Core, Sidecar, Storage, Mcp and Server
web/                       Vite + React 18 + TypeScript SPA (web/src/model/types.ts mirrors the C# model)
sidecar/                   Node sidecar: ELK layout and Yjs materialisation over stdio JSON-RPC (sidecar/README.md)
docs/                      spec, ADRs, schema, examples, checklists, HTTP API reference
themes/                    chrome and diagram theme token files
design/atlas/              per-screen design notes; one is required before a screen is built
Dockerfile, docker/        multi-stage image build; see the Docker section below
```

## Build and run

Prerequisites: .NET SDK 10.0.100 or later 10.0.x (pinned in `global.json`) and Node 22.12 or later. Linux, macOS and Windows are all supported for development.

```sh
# Backend (dotnet build also runs npm ci and bundles the sidecar into the server output)
dotnet build
dotnet test

# Sidecar on its own
cd sidecar
npm ci
npm test

# Frontend
cd web
npm ci
npm run lint
npm run build      # writes web/dist, which the server serves when present
npm test
```

Run the server from the repository root:

```sh
dotnet run --project src/Draughtsman.Server
# http://localhost:5180/api/health  ->  {"status":"ok"}   (version, storage and sidecar detail need an administrator's session)
# the server listens on 127.0.0.1 only; reaching it from other machines is docs/deployment.md
```

`status` is `degraded` when the sidecar is not running; the server keeps serving and restarts it with backoff. Lay out a document server-side with `POST /api/layout` (`?algorithm=layered|stress&direction=down|right|up|left`): a document whose nodes have no geometry goes in, `{ document, warnings }` comes out with geometry on every node. The REST surface is described in `docs/api.md`.

The first run creates the data folder and its SQLite database, then serves the document API described in
[`docs/api.md`](api.md). Every route requires a signed-in session or an API token except health, the chrome
theme and the sign-in/first-run routes themselves (see [Sign-in](#sign-in)); that applies to the MCP tools at `/mcp`
as much as to `/api`. The server still binds loopback by default regardless.

The same binary is also the MCP server for an agent on this machine, reading the same data folder instead of binding
any port:

```sh
dotnet run --no-launch-profile --project src/Draughtsman.Server -- mcp --stdio
# or, from a published build: draughtsman mcp --stdio
```

`--no-launch-profile` is required for stdio: without it `dotnet run` prints `Using launch settings from ...` to stdout
before the server starts, and a client parsing stdout as JSON-RPC fails on that line.

For frontend development run the Vite dev server alongside; it proxies `/api` to port 5180:

```sh
cd web && npm run dev
# http://localhost:5173
# http://localhost:5173/example  ->  the shared example document on the canvas
# http://localhost:5173/try      ->  paste DSL or Mermaid and run it through import, layout and save
```

### Install from a release archive

`scripts/publish.sh <osx-arm64|osx-x64|linux-x64|linux-arm64|win-x64|win-arm64|all>` builds a folder you unpack and run: the
self-contained `draughtsman` executable with the web app, the sidecar, an official Node 22 (checksum-pinned in
`scripts/node-runtime.txt`), `draughtsman.example.yaml` and `THIRD-PARTY-NOTICES.txt`. `draughtsman --help` lists the
commands (`serve`, `mcp --stdio`, `service install|uninstall|status`, `--version`). `scripts/release.sh` adds the release
guards (build date pinned, checksums, SBOM); `scripts/docker-export.sh` packs an image for an air-gapped host.
Installing, running as a service and the offline path are in [`docs/install.md`](install.md) and the per-platform guides [`docs/install-linux.md`](install-linux.md), [`docs/install-macos.md`](install-macos.md) and [`docs/install-windows.md`](install-windows.md).

### Configuration

[`docs/draughtsman.example.yaml`](draughtsman.example.yaml) lists every setting at its default, annotated.

The server reads `draughtsman.yaml` from the data folder, which is `DRAUGHTSMAN_DATA` if set, otherwise `data/` beside the executable of a release archive (or `./data` when run from a build tree). Every key has a default, so an absent or empty file is a valid install. Keys and defaults are listed in [`src/Draughtsman.Server/Configuration/DraughtsmanConfig.cs`](../src/Draughtsman.Server/Configuration/DraughtsmanConfig.cs); the `storage` section, which picks SQLite or Postgres, is explained in [`docs/api.md`](api.md).

The `theme` section selects the app shell's chrome theme. The server compiles it to CSS custom properties at startup and serves them (with the product name, logo, favicon and mode) at `/api/theme`, so the shell re-skins without rebuilding `web/`. To run the fictional client theme shipped as the second-theme proof:

```yaml
theme:
  chrome: chrome-example-client.yaml   # relative to <data folder>/themes/, then <data folder>, then the shipped themes
```

A missing or invalid theme file logs a warning and falls back to the shipped Overpass theme. See [`themes/README.md`](../themes/README.md) and the theme endpoints in [`docs/api.md`](api.md).

An admin can also change the product name and upload a logo, an optional light-on-dark logo and a favicon from the web admin page **`/admin/branding`** (or the `/api/admin/branding` endpoints), without touching the server's files or restarting. The uploads are stored in the instance data folder's `branding/` directory and are layered over the chrome theme; removing one, or **Reset all branding**, returns to the theme's own values. An uploaded SVG is validated against a strict allowlist (no script, event handlers, external references or `foreignObject`) and served as an image under a restrictive policy. See [`docs/api.md`](api.md#branding).

### Sign-in

Local accounts, sessions and API tokens (build-spec-v0.1.md section 7); the full HTTP contract is in
[`docs/api.md`](api.md#auth). OIDC and workspaces beyond `default` are not built yet.

**First run**: with no users at all, every page redirects to "Create the first administrator". The server logs a one-time setup code at startup; enter it on that page (or send it as `setupCode` to `POST /api/auth/first-run`). Once that account
exists, first-run is closed for good — there is no way back into it, for anyone.

**Users** (admin only, `/admin/users` in the web app): list, add a local user, change role (Admin or Editor), reset
a user's password to one the admin types, create a single-use **reset link** the person uses to choose their own, and deactivate. Deactivating a user (or changing their password) revokes
every session they currently hold, immediately — not just future sign-ins. An admin cannot deactivate or demote
their own account.

**API tokens** (`/admin/tokens`, or the equivalent page for any signed-in user — every user manages their own):
create, name and revoke a token; an admin may additionally revoke anyone's. The raw secret (`dst_…`) is shown
exactly once, at creation. A token expires (90 days unless you choose otherwise, never more than
`auth.tokens.maxLifetimeDays`, default 365) and can be **read-only**; the tokens page shows when each expires and was last used. Use it with MCP over HTTP:

```sh
claude mcp add --transport http draughtsman http://localhost:5180/mcp --header "Authorization: Bearer dst_…"
```

Without the header `/mcp` answers `401` with `WWW-Authenticate: Bearer`, and Claude Code reports the server's hint
("Authentication required. Send an API token as 'Authorization: Bearer <token>' ... This server does not support
OAuth.") - it prefixes that with "Dynamic Client Registration rejected (HTTP 401)", which is its own OAuth fallback
wording, not a sign that OAuth is expected. The fix is always the token.

`draughtsman mcp --stdio` needs none of this: it never binds a port, so it stays inside the trust boundary of the
machine and data folder it runs on, the same as before authentication existed.

**Passwords** are Argon2id (19 MiB, 2 iterations, 1 thread — the OWASP Password Storage Cheat Sheet's baseline),
minimum 12 characters. Sessions are an HttpOnly, `SameSite=Lax` cookie, server-side revocable, lasting 30 days with
no "keep me signed in" option. Failed sign-ins are throttled in memory, per account and per caller IP.

Every user can change their own password at **Account** (your name in the top bar): it needs the current password and
signs you out everywhere else. An admin reads the **Security** log (`/admin/security`): sign-ins and failures, throttle
trips, password and role changes, tokens created and revoked, with who, which account and from which IP, and never a
secret. A person who forgets their password gets a reset link from an administrator (**Users, Create reset link**, no email
needed) or, when outgoing email is set up (`email:` and `server.publicUrl` in `draughtsman.yaml`), asks for one with **Forgot
password?** on the sign-in page ([`docs/admin-guide.md`](admin-guide.md#forgotten-passwords)). A lost *administrator* password
with nobody left to create a link is recovered on the host, not over HTTP (`draughtsman reset-admin <email>`;
[`docs/api.md`](api.md#lost-admin-recovery) has the options and its trust model).

### Connect Claude Code

**For stdio, agents must run the published binary or `dotnet run --no-launch-profile`.** Plain `dotnet run` still prints one non-JSON line (`Using launch settings from ...`) to stdout, which breaks the JSON-RPC stream.

Both forms below were run against a real instance with Claude Code 2.1.286 (`claude mcp list` showed `✔ Connected`). Replace the port, token and paths with yours.

HTTP, for a running server (mint the token under **API tokens** in the web app, or `POST /api/tokens`):

```sh
claude mcp add --transport http draughtsman http://127.0.0.1:61862/mcp --header "Authorization: Bearer dst_…"
```

Stdio, for the same machine and data folder, with no token (keep `--no-launch-profile`, or stdout is polluted):

```sh
claude mcp add draughtsman -e DRAUGHTSMAN_DATA=/path/to/data -- dotnet run --no-launch-profile --project /path/to/draughtsman/src/Draughtsman.Server -- mcp --stdio
```

From a published build, use its binary in place of the `dotnet run ...` part: `-- /path/to/draughtsman mcp --stdio`.

### Bring your own AI

AI features (build-spec-v0.1.md section 7/9) are off unless `ai` in `draughtsman.yaml` names a supported provider — an unset or unrecognised provider hides the web AI panel entirely and `GET /api/health` reports `ai.enabled: false`, never a broken feature. Today `ollama`, `openai` (or any OpenAI-compatible endpoint), `anthropic` and `azure-openai` are implemented; they all sit behind the same `IModelProvider` interface without changing a caller. An API key, where a provider needs one, is never written to `draughtsman.yaml`: it is read from the environment variable `ai.apiKeyEnvironmentVariable` names, or pasted into the admin AI settings (below).

To point Draughtsman at a local Ollama (an air-gapped site loses nothing but quality). From a **Docker** container `localhost` is the container: use `http://host.docker.internal:11434` ([`docs/admin-guide.md`](admin-guide.md#local-models)):

```yaml
ai:
  provider: ollama
  model: llama3.1                    # any model `ollama list` shows; a :cloud suffix routes to Ollama's cloud, not local inference
  endpoint: "http://localhost:11434" # optional; this is the default
  think: false                       # whether to use hidden reasoning traces; off by default for speed
  timeoutSeconds: 300                # optional; Ollama defaults to 300s if unset
```

To point Draughtsman at OpenAI, or any endpoint that speaks the OpenAI Chat Completions API (Azure OpenAI's compatibility surface, vLLM, LiteLLM, OpenRouter, …), put the key in an environment variable and name that variable in the config — the key itself never enters `draughtsman.yaml`:

```yaml
ai:
  provider: openai
  model: gpt-4o-mini                 # any chat model the endpoint serves
  endpoint: "https://api.openai.com/v1" # optional; this is the default
  apiKeyEnvironmentVariable: OPENAI_API_KEY # optional; defaults to DRAUGHTSMAN_AI_KEY
  timeoutSeconds: 300                # optional; OpenAI defaults to 300s if unset
```

```console
export OPENAI_API_KEY=sk-...         # the one place the key lives
```

To point Draughtsman at Anthropic's Messages API:

```yaml
ai:
  provider: anthropic
  model: claude-sonnet-5-5           # any model the Anthropic API serves (model names are retired over time: use a current one)
  endpoint: "https://api.anthropic.com/v1" # optional; this is the default
  apiKeyEnvironmentVariable: ANTHROPIC_API_KEY # optional; defaults to DRAUGHTSMAN_AI_KEY
  timeoutSeconds: 300                # optional; Anthropic defaults to 300s if unset
```

```console
export ANTHROPIC_API_KEY=sk-ant-...  # the one place the key lives
```

To point Draughtsman at an Azure OpenAI deployment, give the resource's own endpoint, put the **deployment name** in `ai.model`, and optionally pin the REST API version with `ai.apiVersion` (defaults to `2024-10-21`):

```yaml
ai:
  provider: azure-openai
  model: my-deployment               # the Azure OpenAI deployment name, not the underlying model name
  endpoint: "https://my-resource.openai.azure.com" # required: the resource's endpoint
  apiKeyEnvironmentVariable: AZURE_OPENAI_API_KEY # optional; defaults to DRAUGHTSMAN_AI_KEY
  apiVersion: "2024-10-21"           # optional; this is the default
  timeoutSeconds: 300                # optional; Azure OpenAI defaults to 300s if unset
```

```console
export AZURE_OPENAI_API_KEY=...      # the one place the key lives
```

A missing key, or an endpoint that cannot be reached, surfaces as the same clear `ModelProviderException` the Ollama provider raises — the AI panel reports it rather than failing silently.

#### Admin AI settings (no file or environment access needed)

A signed-in **administrator** can also configure AI from the web app at **AI** in the top bar (`/admin/ai`): pick the provider, endpoint and model, paste an API key, and press **Test connection**. It applies at once, with no restart, and `draughtsman.yaml` stays the fallback: **Reset** returns to the file's values. Only a cookie-signed-in admin can use it; an editor or an API token (even an admin's) cannot, and neither can an agent.

- **The key is write-only.** The page shows only that a key is set (and lets you replace or clear it); no endpoint, log line or error message ever returns it. It is stored encrypted with ASP.NET Core Data Protection in `<data>/ai/settings.json`, using the key ring kept in `<data>/keys/`. The ring is itself protected at rest (next section), so a copy of the data folder alone does not give the key up; while the ring is unprotected the page says so.
- **The environment variable still wins.** If the variable named by `ai.apiKeyEnvironmentVariable` (default `DRAUGHTSMAN_AI_KEY`) is set, it is the key in use and the page says so; the stored key is used when the variable is absent.
- **Local models need no key.** An Ollama instance keeps working with none, and nothing leaves your network.
- The endpoint must be a plain `http` or `https` URL with no credentials in it. Test connection makes one short request and reports the result in plain English (unreachable, key rejected, wrong model, and so on) without echoing the key.

With that set, the editor's AI panel offers two tabs:

- **From text** (`POST /api/ai/generate-from-text`): a prompt and a diagram type become a laid-out draft, written through the same checkpointed path an MCP agent uses. The prompt, and, when updating an open document, that document's whole current content as DSL, are sent to the model.
- **To document** (`POST /api/ai/diagram-to-document`): the diagram becomes a design doc, runbook or plain explanation, generated from its outline form only (ids, kinds and labels — never geometry, style or images) and citing its elements by node id. It reads and writes nothing to the document, so there is no checkpoint; the Markdown is shown with Copy and Download.

Both endpoints live in [`docs/api.md`](api.md).

#### Protecting the key ring

The stored AI key is encrypted by a key ring in `<data>/keys/`. If that ring sits there as plain text, the encryption protects nothing against anyone who can read the data folder: a copy, a backup, a mounted volume. So the ring is protected at rest, by a key that is **not** in the data folder:

| Where | Default protection (`keyRing.protection: auto`) |
| --- | --- |
| Windows | DPAPI, scoped to the account the server runs as. |
| macOS | The login Keychain (item `Draughtsman data key ring`, one account per data folder). |
| Linux, Docker | A **key-encryption key** you supply from outside the data folder: `DRAUGHTSMAN_KEY_ENCRYPTION_KEY`, or a file named by `keyRing.keyFile`. With neither, the ring stays **unprotected**; the server logs that at startup and the admin AI page shows a warning. |
| Any | A PKCS#12 certificate named by `keyRing.certificate.path`, if you prefer one; it wins over everything else under `auto`. |

```yaml
keyRing:
  protection: auto                        # auto | dpapi | keychain | key | certificate | none
  keyEnvironmentVariable: DRAUGHTSMAN_KEY_ENCRYPTION_KEY
  keyFile: ""                             # e.g. /run/secrets/draughtsman-kek (a Docker secret); must be outside the data folder
  certificate:
    path: ""                              # a .pfx with an RSA private key
    passwordEnvironmentVariable: DRAUGHTSMAN_KEY_CERTIFICATE_PASSWORD
```

A key-encryption key is 32 or more random bytes as base64. Generate one, keep it somewhere that is backed up separately from the data folder, and hand it to the container:

```sh
openssl rand -base64 32 > draughtsman-kek.txt && chmod 600 draughtsman-kek.txt
docker run -d --name draughtsman -p 127.0.0.1:5180:5180 \
  -v "$(pwd)/draughtsman-data:/data" \
  -e DRAUGHTSMAN_KEY_ENCRYPTION_KEY="$(cat draughtsman-kek.txt)" \
  draughtsman:local
# or as a file: mount it read-only (readable by the image's uid 10001) and set keyRing.keyFile: /run/secrets/kek in draughtsman.yaml
```

The server says which protection is active on every start (`Key ring protection: ...` in the log), and `DRAUGHTSMAN_KEY_RING_PROTECTION` overrides `keyRing.protection` for a container that cannot edit the file.

**An existing install migrates itself.** The first start with a protection available copies `keys/` and `ai/settings.json` to `<data>/backups/key-ring-<UTC time>/`, protects each key **in place** (the keys themselves do not change, so the stored AI key needs no re-encryption), and only swaps the protected files in after reading the result back through the real key ring and checking that the stored AI key decrypts to the same value. If any step fails the ring is left as it was, or put back from the backup, and the server starts anyway, with the reason in the log and on the admin AI page. Nothing deletes the backup for you, and it still holds the old plain-text ring, so once Test connection shows the key works, delete the folder.

**If the protection goes missing** (the variable removed, the Keychain item or certificate gone, the ring opened on another machine) the server still starts. Keys the lost protection covered cannot be read, the admin AI page says the stored key has to be entered again, and entering it again works: the ring makes a new protected key for it. Nothing is discarded; restoring the original key-encryption key makes the old keys, and the old stored key, readable again. A ring file that is not valid XML is moved aside as `key-….xml.damaged` (and is in the backup), because one such file stops the whole ring loading.

**The stored AI key and the saved SMTP password are tied to the ring, not to where the program is installed.** Moving the program, replacing it or unpacking a new version elsewhere leaves them readable. A data folder an earlier build wrote has them bound to the folder that build ran from: the first start of this one reads them (from the folder it runs from, the folders it recorded, `keyRing.legacyInstallPaths` or `DRAUGHTSMAN_LEGACY_INSTALL_PATHS`, the folder the data folder sits in, then old logs, neighbouring folders and the recommended install locations) and re-protects them, keeping the earlier file in `<data>/backups/secrets-<time>/`; one that cannot be read is named on Admin > AI, Admin > Email and in `draughtsman config check`, never changed, and brought back by naming the old folder once ([`docs/upgrade-backup.md`](upgrade-backup.md#where-to-put-the-new-version)).

What this does not do: protect against someone who can run as the server's own account (DPAPI and the Keychain both trust that account), or against anyone who has both the data folder and the key-encryption key. Changing protection later (say from a key to a certificate) protects only keys created afterwards; existing ones stay under the protection they were written with. See [`docs/api.md`](api.md#ai-settings) for the `keyRing` field the admin endpoint returns.

### PDF export (optional Chromium component)

The editor's File > Export PDF… needs no component: it draws the themed SVG on a page of the chosen size and opens the browser's print dialog, where you choose Save as PDF ([`docs/pdf-export.md`](pdf-export.md)). Server-side PDF is an optional, separately installed component (build-spec-v0.1.md section 7), for headless, scripted or scheduled PDFs and for agents. It renders a document's diagram as a single-page PDF through headless Chromium, and is used by `GET /api/documents/{id}/pdf`, the `render_document` MCP tool's `pdf` format, and, when it is working, the primary button of the editor's Export PDF dialog. Without it, nothing else changes: PNG/SVG stay client-side, the health endpoint reports `export.pdf: false`, and the PDF endpoint answers 501 naming how to enable it.

Install Chromium with the Playwright version this build of the server uses (an unpinned CLI installs a different browser revision and the server reports PDF as absent), then enable it in `draughtsman.yaml`:

```console
npx playwright@1.63.0 install chromium
```

A release archive needs no Node for this: it ships Playwright's driver in `.playwright/` beside the executable, and `./.playwright/node/<platform>/node ./.playwright/package/cli.js install chromium` does the same install (or set `export.chromium.path` to a Chrome you already have). The server finds that driver itself and reports a missing or broken one as a driver problem (`export.pdfProblem: "driver"`), not as a missing browser. The server prints this exact command in `GET /api/health` (`export.pdfHint`), in the editor and in the 501 answer, so it always matches the running version. [`docs/pdf-export.md`](pdf-export.md) has the rest: how availability is decided, the page options, and the page options.

```yaml
export:
  chromium:
    enabled: true      # false by default: the component is absent until turned on
    path: ""           # path to a Chromium executable; empty means Playwright's installed browser location
```

With `enabled: false` (the default) or a browser that cannot be launched, the component is treated as absent: health reports `export.pdf: false` only until a launch has succeeded.


## Docker

A multi-stage [`Dockerfile`](../Dockerfile) builds the sidecar and web app with their own Node toolchains, publishes the
server framework-dependent (no `-r`), and runs it as a non-root user on a small ASP.NET Core runtime image with
Node 22 added for the sidecar child process. The data folder is meant to be volume-mounted at `/data`
(`DRAUGHTSMAN_DATA`), and the container binds every interface (`ASPNETCORE_URLS=http://0.0.0.0:5180`) so a published
port actually works — outside Docker the loopback-by-default bind (`server.host` in `draughtsman.yaml`) is untouched.

Every route requires sign-in (see [Sign-in](#sign-in) above), but publishing further than localhost is still a
choice worth making deliberately rather than by default — start with localhost-only:

```sh
docker build -t draughtsman:local .

mkdir -p ./draughtsman-data && sudo chown -R 10001:10001 ./draughtsman-data && sudo chmod 700 ./draughtsman-data   # Linux; see below
docker run -d --name draughtsman \
  -p 127.0.0.1:5180:5180 \
  -v "$(pwd)/draughtsman-data:/data" \
  draughtsman:local
# http://localhost:5180/api/health
```

The container's fixed uid is `10001`; a bind-mounted host folder on Linux must be owned by it (`chown -R 10001:10001`,
then `chmod 700`) since Docker never rewrites a bind mount's ownership. Do not `chmod 777` it: the data folder holds every
document, the password and token hashes, the key ring and the stored AI key, and the server creates everything in it
readable by its own account only (files `0600`, folders `0700`), which a world-writable folder undermines - it warns at
start-up when the folder is open to other users. Docker Desktop on macOS and Windows maps ownership for you, so there the
`chown` is unnecessary. Alternatively use a named volume (`-v draughtsman-data:/data`), which Docker creates with the right
owner. On Windows hosts running the server directly there are no file modes: keep the data folder under an account-private
location (for example the service account's profile) and do not grant other users access to it.

`scripts/docker-smoke.sh` builds the image and checks it end to end on a random port with a scratch data folder: health,
non-root uid, first-run sign-in, creating a document, sidecar layout, and the document surviving a container restart
(`SKIP_BUILD=1` reuses an existing `draughtsman:smoke`). It is not part of `scripts/verify-local.sh`; a cheaper guard
that runs there, `DockerfileBuildContextTests`, fails when a file a project embeds is not copied into the publish stage.

### Optional Chromium component for PDF

Off by default, same as a non-Docker install (see above). Build with `--build-arg INSTALL_CHROMIUM=true` to install
Playwright's Chromium into the image; [`docker/entrypoint.sh`](../docker/entrypoint.sh) then seeds
`export.chromium.enabled: true` into the data folder's `draughtsman.yaml` the first time it finds none there — it
never overwrites a config already saved by an earlier run:

```sh
docker build --build-arg INSTALL_CHROMIUM=true -t draughtsman:local-chromium .
```

### Publishing an image (not done)

No image is published, and no registry namespace has been chosen. Building only this machine's architecture is enough for local
use; a release image for both `linux/amd64` and `linux/arm64` comes from one buildx invocation against whichever registry you use:

```sh
docker buildx build --platform linux/amd64,linux/arm64 -t <registry>/draughtsman:<ver> --push .
```

Step-by-step Docker use, compose with a TLS proxy, and moving an image to an air-gapped host are in
[`docs/install-docker.md`](install-docker.md) and [`docs/deployment.md`](deployment.md).

## Continuous integration

Run [`scripts/verify-local.sh`](../scripts/verify-local.sh) before landing any change — it runs the full macOS CI leg (backend build/test/format, then web and sidecar lint/build/test) on a Mac. [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) builds and tests on Ubuntu and Windows only, on pushes to `main` and `workflow_dispatch` (not on every PR); the `release-gate` job is green only when every leg is green, and stays the required check. 

## Conventions

- British English in documentation and product copy.
- No Windows-only APIs (ADR 0001). `System.Drawing`, the registry and Windows auth assumptions are out.
- The document model lives in several places that must change together; [`AGENTS.md`](../AGENTS.md) lists them. Tests on both sides validate the shared example against the schema.
- UI work needs its design atlas entry first (see `design/atlas/README.md`).
- Warnings are errors on the .NET side; ESLint runs with type-checked strict rules on the web side.
