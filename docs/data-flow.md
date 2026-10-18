# Data flow: what Draughtsman stores and what leaves the machine

For a customer's security reviewer. Version 0.1.0. Checked against the source; the checks you can repeat are at the end.

**The short version.** A running server stores everything in one data folder (and, if you choose Postgres, one database you
run). It makes **no outbound network connection of its own** unless an administrator configures an AI provider, or points
the database at another host. There is no update check, telemetry or crash reporting. The browser talks only
to the server it was loaded from. How to carry an install across to a host with no network, and how to prove all of this on your own machine,
is [`air-gapped.md`](air-gapped.md).

## What is stored, and where

The data folder is `DRAUGHTSMAN_DATA` (`/data` in the Docker image; otherwise `data/` beside the executable). On Unix the
server sets its umask to 077, so every file it creates, the database included, is owner-only; on Windows the protection is
the folder's ACL. It never loosens an existing folder's mode, but it warns at startup when the folder is open to other users.

| Path | Contents |
| --- | --- |
| `draughtsman.yaml` | Settings. With Postgres it holds the connection string, **including the password, in plain text**: protect the file. It holds no other secret. |
| `data.db` (+ `-wal`, `-shm`) | The SQLite database (default). With `storage.provider: postgres` the same tables live in your Postgres instead. |
| `data.db.pre-migration-<stamp>` | A full copy of the database taken before an upgrade migrates it; the newest 3 are kept (`retention.preMigrationCopies`). |
| `assets/*` | Uploaded images, content-addressed (`assets/<hh>/<sha256><ext>`); metadata is in the database. |
| `branding/*`, `themes/*` | The admin's logo, favicon and product name; organisation and chrome themes. |
| `keys/*` | The ASP.NET Core Data Protection key ring, plus `ring-protection.json` (names the protection; holds no key). It encrypts the stored AI key and the anti-forgery tokens. |
| `ai/settings.json` | The admin's AI provider, model and endpoint in plain text; the API key only as a Data Protection payload. |
| `logs/*` | Only if `logging.file.enabled` is set (off by default): a rolling file, 14 days. Also the service log on macOS. |
| `backups/key-ring-<time>/` | Copies of `keys/` and `ai/settings.json`, made once before the server repairs or protects an existing key ring. Not made on a fresh install. |

`draughtsman backup` writes a zip wherever you point it. It holds the database (so password hashes), assets, config,
themes and branding; the key ring and AI settings only with `--include-secrets`.

**In the database** (tables `documents`, `document_updates`, `document_versions`, `audit`, `security_events`, `assets`,
`users`, `api_tokens`, `sessions`):

- *Documents:* the current diagram as JSON, the Yjs update log, and every saved version (automatic checkpoints before an
  agent edit, pruned to 50 per document; named versions are never pruned).
- *Deleted documents:* a delete is a soft delete: the row stays, with who deleted it and when, for 30 days (`retention.binDays`), then a daily pass removes it with its versions. They are in backups until then.
- *Users:* email, display name, role, an **Argon2id** password hash (19 MiB, 2 passes), created and last sign-in times.
- *Password-reset links:* for each link only a **SHA-256 hash** of its secret, the account, whether it was emailed or made by an administrator (and which), and when it was made, expires, was used or was ended. Never the secret. A backup leaves the table out; ended links are deleted a week after they end.
- *Sessions and API tokens:* only the SHA-256 of the secret. The raw value exists in the cookie, or is shown once when a
  token is minted. Sessions last 30 days and ended ones are deleted after 7.
- *Document audit:* who (name, and user id) did what to which document, when, and for an agent the provider and model.
- *Security events:* sign-ins (including failed ones), password and role changes, token creation, exports, recovery. Each row
  has the actor's and target's **email** and the **source IP address**, never a password, token or hash. **No retention job
  deletes them**: they are kept until you remove them.

## What leaves the machine

Every outbound call or network reach in the source, runtime first. Found by searching `src/`, `web/src`, `sidecar/src`,
`Dockerfile`, `docker/` and `scripts/` for HTTP clients, sockets, DNS, WebSockets, database drivers, process starts, URLs in
`web/index.html` and CSS, and any update or telemetry code. Anything not listed was not found.

| What | When | Triggered by | Data | On by default | Disable |
| --- | --- | --- | --- | --- | --- |
| HTTP request to the AI provider (`Draughtsman.Ai`, four classes; see the AI table) | Each AI call, and Test connection | A user in the AI panel; an admin | The prompt and document content below | **No**: no provider is set | Leave `ai.provider` empty, or **Reset** in `/admin/ai` |
| SMTP connection to the mail server you configured (`Draughtsman.Server.Email`) | A person asks for a password-reset link on the sign-in page, or an administrator presses "Send a test email" | A person (anonymously, by typing an address); an admin | One plain-text message: the reset link and its expiry, or a test line. To the account's own address, or the administrator's own | **No**: `email.provider` is empty | Leave `email.provider` empty (the sign-in page then says to ask an administrator), or use `file` to write messages to a folder instead |
| HTTPS request to a web page and up to 8 of its stylesheets (`Draughtsman.Server.Theming.Extraction.ThemePageFetcher`) | An administrator presses **Fetch and read colours** in Admin, Themes, From a website | An admin (cookie session only) | The request carries no cookie, credential or document; the response (the page's CSS, at most 2 MiB) is read for its colours and discarded: nothing from it is stored or logged but the host and the outcome, as a security event | **No**: `themes.fetchFromUrl.enabled` is false, the box is not shown and the route answers `403` | Leave it false. Pasting CSS never leaves the machine. Loopback, private, link-local and metadata addresses are refused unless `themes.fetchFromUrl.allowedHosts` lists the host |
| One anonymous `GET /api/health` to this machine's own loopback address (`Draughtsman.Server.Diagnostics.LoopbackProbe`, written over a plain socket) | `draughtsman doctor` and `draughtsman support-bundle` | The operator, at a command line | The status word of this server's own health document; the request carries no credential | Never run unprompted | Do not run the verbs. It can only reach `127.0.0.1` and `::1`: no name is looked up and no other host is ever named |
| Postgres connection (Npgsql) | While running | The server | Everything the database holds | **No**: SQLite is the default | Use `storage.provider: sqlite` |
| Node sidecar (child process, stdio) | Server start | The server | Documents to lay out, over a pipe | Yes, local only | Needed for layout; it opens no socket |
| Headless Chromium for PDF (child process) | A PDF export | A user | The rendered SVG | **No**: optional and off | Leave `export.chromium.enabled` false |
| macOS `security` tool (child process) | Start, key ring protection `auto` or `keychain` | The server | The key-ring key, local Keychain only | macOS only | `keyRing.protection: key`, `certificate` or `none` |
| `systemctl`, `launchctl`, `sc`, `chown` | `draughtsman service ...` | The operator | None | Never run unprompted | Do not run the verb |
| Help > Documentation | Unset (the default): the browser opens this server's own copy of the guides (`/docs/`, same origin, behind sign-in) in a new tab, or the vendor's `https://overpass.co.uk/draughtsman` when the install ships no `docs/` folder. Set: the browser opens `about.docsUrl` | A user clicking it | Nothing leaves the machine for the local copy. For an external address the tab is opened with `noopener,noreferrer`, so no Referer is sent | Yes | `about.docsUrl: ""` hides the entry; an internal address keeps it inside your network |
| Support contact on the About page | A `mailto:` link in the browser | A user clicking it | None | Yes | Set `about.supportContact` |
| Inbound only: the HTTP listener and `/mcp` | Always | Browsers, API tokens, agents | n/a | `127.0.0.1:5180` | `server.host`, `mcp.enabled: false` |

Nothing else: no update check, no telemetry or analytics, no
crash reporter, no email unless you configure `email:` (then only password-reset links and the test message), no DNS lookups except
those the AI, Postgres and SMTP clients make for their configured hosts.

**Chromium PDF.** It prints the server's own SVG with JavaScript off, offline, every request aborted in a route handler and
`default-src 'none'` on the page. Playwright 1.63.0's default switches include `--disable-background-networking` and
`--disable-component-update`. The browser gets an allow-listed environment, so it never sees the AI key. This is from the
code: we have not launched Chromium and watched its sockets.

**The browser.** The SPA makes only relative requests to its own server (no absolute URL appears in `web/src`; the fonts, Inter
and JetBrains Mono, are bundled, and `web/index.html` loads nothing external). The server also sends
`Content-Security-Policy: default-src 'self'; ...; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; ...;
frame-ancestors 'none'` on every response, so a theme file or document that named an outside host would be blocked. The
bundle contains URL strings (React's error page, JSON Schema `$id`s); none is ever requested.

## AI providers

An administrator picks one provider at `/admin/ai`, or in `draughtsman.yaml`. Until then the AI panel is hidden and nothing is
sent. All four send one non-streaming request per call; "From text" may make a second if the first reply did not parse.

| Provider | Request and default endpoint | Where the data goes |
| --- | --- | --- |
| `ollama` | `POST {endpoint}/api/chat`, default `http://localhost:11434` | To that Ollama host. A model whose name ends in `:cloud` is **forwarded by the Ollama daemon to Ollama's cloud**: it leaves your machine, and Draughtsman cannot tell. Use a local model name if that matters. Needs no key; if one is set it is sent as a bearer token |
| `openai` | `POST {endpoint}/chat/completions`, default `https://api.openai.com/v1`; `Authorization: Bearer` | OpenAI, or an OpenAI-compatible service whose host the operator has listed in `draughtsman.yaml` |
| `anthropic` | `POST {endpoint}/messages`, default `https://api.anthropic.com/v1`; `x-api-key`, `anthropic-version` | Anthropic, or a host the operator has listed in `draughtsman.yaml` |
| `azure-openai` | `POST {endpoint}/openai/deployments/{model}/chat/completions?api-version=...`; `api-key`. No default host | Your Azure resource (`*.openai.azure.com`, or a host the operator has listed) |

**What is sent.** *From text:* the user's description; the diagram type's kind vocabulary and the DSL grammar (both fixed text);
and, when updating an open document, **that document's whole content as DSL, including geometry, style and props**. Never
another document. Images are never sent. *To document:* only the outline: the document's title and type, each node's id,
kind, label and container, and each edge's id, kind, label and endpoints. Test connection sends a fixed one-line prompt
("Reply with the single word OK." and "ping"), no document. A reply that creates or updates a document is checked, then
written through the same checkpointed path an agent uses, with the provider and model recorded as its actor (`aiProvenance`).

**The key.** Write-only: no endpoint, log line or error returns it. Stored in `ai/settings.json` as a Data Protection payload
using the key ring in `keys/`. The ring is protected by DPAPI (Windows), the macOS Keychain, a key-encryption key from an
environment variable or file, or a certificate; **on Linux and Docker with none configured it is plain text and the key is
effectively readable by anyone who can read the data folder**. The server says so at startup and on the admin page. A key in
the environment variable `DRAUGHTSMAN_AI_KEY` (or the name `ai.apiKeyEnvironmentVariable` gives) wins over the stored one.

**Endpoint policy.** Where the endpoint may point is restricted ([`ai-endpoint-policy.md`](ai-endpoint-policy.md)). OpenAI, Anthropic
and Azure OpenAI may use only their own hosts (`api.openai.com`, `api.anthropic.com`, `*.openai.azure.com`) unless the operator lists
another under `ai.allowedEndpointHosts` in `draughtsman.yaml`, which the admin page cannot change. Ollama may use this machine and private
addresses. Link-local and cloud-metadata addresses are refused for every provider, whether written as an address or reached through a
host name. A key is never sent over plain `http` except to this machine, redirects are not followed, a change of host forgets the
stored key, and the environment key goes only to the host in `draughtsman.yaml` (or the provider's own). Each change of host and each
refused endpoint is on the security log. What remains: Test connection against Ollama can still probe private addresses, a proxy
resolves names itself, and a host the operator lists is trusted with the key. If those matter, leave AI off.

## Logs and personal data

Logs go to the console (so `docker logs`, journald or the service log) and, if enabled, `logs/`. The framework's request
lines are off by default (level Warning). Personal data that can appear:

- **Security log** (`Draughtsman.Server.Security`): user ids and the **source IP**, and, for a sign-in against an unknown
  account or one that was throttled, the **email address that was typed** (if it looks like one). The table holds the same
  plus the actor's and target's emails.
- **Startup:** the data folder path, and the first-run **setup code**, to the console only. The file logger redacts it, but a
  service manager that captures the console (journald, `docker logs`, macOS `logs/draughtsman.log`) keeps it. It is valid
  only until the first administrator exists.
- A request refused for its `Host` header is logged.
- The AI key, passwords, tokens and session values are never logged.

## Build time and install time (not run time)

None of this happens when the server runs. It matters if you build from source, build the image, or add PDF.

| What | Network host | Trigger |
| --- | --- | --- |
| `dotnet restore` (also `build`, `publish`, `test`) | NuGet (`nuget.org`; the repo has no `nuget.config`) | Any .NET build |
| `npm ci` in `web/` and `sidecar/`; `dotnet build` runs the sidecar one itself | `registry.npmjs.org` (all 361 locked packages resolve there) | Any build; skip the sidecar one with `-p:BuildSidecar=false` |
| Docker build | Base images from Docker Hub and `mcr.microsoft.com`; Debian mirrors through `apt-get`; `deb.nodesource.com` for Node 22 in the runtime image (not checksum-pinned by this repo); plus the two rows above | `docker build` |
| `--build-arg INSTALL_CHROMIUM=true`, or `npx playwright@1.63.0 install chromium` | Playwright's download hosts (`cdn.playwright.dev`, `playwright.download.prss.microsoft.com`), and `apt` for `--with-deps` | You choose to add PDF. Or set `export.chromium.path` to a Chrome you already have |
| `scripts/publish.sh` | `nodejs.org/dist/v22.23.3`, SHA-256 pinned in `scripts/node-runtime.txt` and refused on mismatch | Cutting a release archive |
| CI (`.github/workflows/ci.yml`) | GitHub Actions, NuGet, npm | Overpass's own builds |

A release archive is self-contained and includes the Node runtime, so installing one needs no network. The .NET SDK and npm
may have telemetry defaults of their own that this repository does not change; we have not checked them.

## Check it yourself

```sh
lsof -nP -a -p <server pid> -i                      # one LISTEN socket on the bound address, no established connections
lsof -nP -a -p <sidecar pid> -i                     # nothing
curl -sI http://localhost:5180/ | grep -i content-security-policy
grep -rnE "HttpClient|Process\.Start|UseNpgsql|Playwright\.CreateAsync" src --include='*.cs'
```

That is what a 0.1.0 debug build showed with no AI, no PDF and the default SQLite store. The grep lists the whole of the
network and process code: the four AI providers and their wiring, three process starts, the Postgres context and the PDF
renderer. For proof on your own deployment, run it behind a default-deny egress firewall and exercise it.

## Software bill of materials

`scripts/generate-sbom.mjs` writes a CycloneDX 1.5 JSON inventory: every NuGet package restored for the server, the
production npm packages of the web app and sidecar, and the bundled Node runtime. It needs a prior restore, adds no
dependency and makes no network call:

```sh
dotnet restore src/Draughtsman.Server/Draughtsman.Server.csproj
node scripts/generate-sbom.mjs --out artifacts/draughtsman-0.1.0.cdx.json
node scripts/check-sbom.mjs artifacts/draughtsman-0.1.0.cdx.json --version 0.1.0 --repo .   # not hollow: every declared package is in it
node --test scripts/generate-sbom.test.mjs scripts/check-sbom.test.mjs                      # structure, unique purls, byte-identical across runs
```

Output is sorted and carries a timestamp only when `--timestamp <ISO date>` is passed (the release scripts pass the build date), so the
same inputs give the same file. It is a flat list (no dependency graph) and omits the .NET runtime, the container's Debian base and the
optional Chromium. **Every release carries it:** `scripts/publish.sh` writes it beside the archives (`draughtsman-<version>.cdx.json`, listed in
`SHA256SUMS`) and into each archive as `SBOM.cdx.json`, `scripts/release.sh` fails unless all six archives hold the same valid file, the
Dockerfile writes `/app/SBOM.cdx.json` and fails the build if it is hollow, and `scripts/docker-export.sh` refuses an image without one
([`air-gapped.md`](air-gapped.md#the-software-bill-of-materials)).
