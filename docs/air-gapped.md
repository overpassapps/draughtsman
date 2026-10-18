# Air-gapped and offline install

For a site whose servers have no route to the internet, or whose security team wants to be able to prove they have none that matters.
Version 0.1.0. Checked against the source and run against real builds; what was not run is marked **unverified**.

**The short version.** A default Draughtsman makes **no outbound network connection**: no update check, telemetry, crash
reporting, web fonts, CDN or analytics, from the server or from the browser. Four things *can* call out (an AI model endpoint, a mail
server, a Postgres server and the optional PDF browser's one-time download); each is **off until an administrator turns it on**, and each
goes only where the administrator points it ([the table](#what-leaves-your-network-and-when)). Everything you install comes from files you
carry across: a release archive or an image tarball and, if you want PDFs made on the server, a browser (PDF from the editor needs none: it prints from the browser). Every release carries a
software bill of materials ([below](#the-software-bill-of-materials)), and every claim here can be repeated on your own machine
([Prove it](#prove-it-on-your-own-machine)).

## What to carry across

Fetch these on a connected machine, check them there, and move them on whatever media your site allows.

| File | What it is | Needed |
| --- | --- | --- |
| `draughtsman-<version>-<platform>.tar.gz` or `.zip` | The server, the web app, the Node runtime for layout and every guide: one folder, self-contained ([`install.md`](install.md)) | One of these **or** the image |
| `draughtsman-<version>-image.tar.gz` and its `.sha256` | The container image, made by `scripts/docker-export.sh` ([Docker](#docker-image-from-a-tarball-no-registry)) | One of these **or** the archive |
| `SHA256SUMS` | Checksums of the archives and of the SBOM | Yes: check what you carried |
| `draughtsman-<version>.cdx.json` | The software bill of materials (the same file is inside every archive as `SBOM.cdx.json`, and in the image at `/app/SBOM.cdx.json`) | For your security review |
| A browser on the server, for server-side PDF | A Chrome, Chromium or Edge, or Playwright's Chromium ([below](#pdf-export-offline)) | Optional: File > Export PDF… prints from the user's own browser with nothing installed |

Nothing else: no .NET runtime, no Node, no package manager, no container registry, no database server, no `npm`, no `apt`.

## Install

Pick one. All three start the same server and print the same one-time setup code; what differs is only how the files arrive.
The step-by-step guides are the per-platform pages; this section says what is specific to a host with no network.

### Linux or Windows: the archive

1. Check it: `sha256sum -c SHA256SUMS --ignore-missing` (Windows: `Get-FileHash` and compare with `SHA256SUMS`).
2. Unpack it and follow [Linux](install-linux.md) or [Windows](install-windows.md): foreground try-out, then `draughtsman service install`.
3. Open `http://localhost:5180` (or your reverse proxy's name) and enter the setup code ([`admin-guide.md`](admin-guide.md#first-run-and-the-setup-code)).

The archive downloads nothing at first run or later: the Node runtime that does layout is inside it, SQLite is inside the executable, and the
web app's fonts, scripts and styles are served from the same folder ([What the browser loads](#what-the-browser-loads)). The archives
are not yet code-signed, so on macOS the first run of a downloaded copy is stopped by Gatekeeper and on Windows by SmartScreen: clear the
quarantine flag or choose "Run anyway" as [`install-macos.md`](install-macos.md) and [`install-windows.md`](install-windows.md) describe. Neither
page was run on a host with no network at all (**unverified** there).

### Docker: image from a tarball, no registry

On a connected machine, build the image from the source, or receive one from Overpass, and pack it:

```sh
docker build -t draughtsman:0.1.0 .
scripts/docker-export.sh draughtsman:0.1.0 --out ./offline
```

`docker-export.sh` refuses an image that carries no software bill of materials, then writes `draughtsman-0.1.0-image.tar.gz` and its `.sha256`
(about 300 MB). On the air-gapped host:

```sh
sha256sum -c draughtsman-0.1.0-image.tar.gz.sha256        # shasum -a 256 -c on a Mac
docker load -i draughtsman-0.1.0-image.tar.gz
docker run -d --name draughtsman --restart unless-stopped \
  -p 127.0.0.1:5180:5180 -v draughtsman-data:/data draughtsman:0.1.0
docker logs draughtsman 2>&1 | grep "setup code"
```

No registry is involved at any point: `docker save` and `docker load` move the image as a file, and running it pulls nothing. The image
needs the internet only to be *built* (base images, Node and npm packages, a Debian package for Node); running it does not
([`install-docker.md`](install-docker.md)). Help > Documentation in the image opens the vendor's website, which an air-gapped browser cannot
reach, because the image carries no `docs/` folder: set `about.docsUrl` to your own intranet page, or `""` to hide the entry
([`admin-guide.md`](admin-guide.md#about-and-help)); the archive serves the guides from the server itself.

## PDF export, offline

**PDF needs nothing installed.** File > Export PDF… draws the diagram in the browser and opens the browser's own print dialog, where you choose
Save as PDF; like PNG and SVG export it happens entirely in the browser and sends nothing to the server ([`pdf-export.md`](pdf-export.md)).
A server-side PDF, for scripts, agents or scheduled exports, is the optional extra: the server then needs a browser, and **the server never downloads one**: the only
download is a command an administrator runs once, by hand, on a machine that has a connection; the server only prints that command
([`pdf-export.md`](pdf-export.md)). Three ways to get a browser onto an offline host:

1. **A browser already there.** Install Chrome, Chromium or Edge from your own package mirror or installer, set
   `export.chromium.path` to its executable and `export.chromium.enabled: true`, restart. Nothing is downloaded by anyone.
2. **Playwright's Chromium, installed elsewhere and carried across.** On a connected machine of the **same operating system and
   architecture**, run the installer with the driver in the archive's own `.playwright/` folder (no Node needed), pointing it at a folder you
   will carry:

   ```sh
   PLAYWRIGHT_BROWSERS_PATH="$(pwd)/ms-playwright" ./.playwright/node/linux-x64/node ./.playwright/package/cli.js install chromium
   ```

   Copy the `ms-playwright` folder (several hundred megabytes) to the offline host, set `PLAYWRIGHT_BROWSERS_PATH` to its location in the
   server's environment (a service's unit or `Environment` value), set `export.chromium.enabled: true` and restart. It must be the build's own
   pinned Playwright version (`1.63.0` in 0.1.0): another revision is reported as "no browser". The relocation and copy are standard Playwright
   behaviour; **unverified** end to end on a second host (the install, the PDF and the `PLAYWRIGHT_BROWSERS_PATH` path were each checked, see
   [`pdf-export.md`](pdf-export.md#what-was-verified-and-where)).
3. **Docker.** On a connected machine build the image with `docker build --build-arg INSTALL_CHROMIUM=true -t draughtsman:0.1.0-chromium .`
   (several hundred megabytes more), export it with `scripts/docker-export.sh`, and load it on the offline host. The first start then
   enables PDF for a data folder that has no `draughtsman.yaml` yet.

Either way, confirm it from **Admin > Status** or `GET /api/health`: `export.pdf` is `true` only after a page has really been printed.
The printed page is built with JavaScript off, offline and with every request aborted, so a PDF render does not reach the network either
([`data-flow.md`](data-flow.md)); that is from the code and the browser's flags, not from a packet capture of Chromium.

## Backups

Backups are local files. `draughtsman backup` writes one zip wherever you point it, with the database (taken consistently while the server
runs), uploads, settings and branding, and a manifest of checksums; the key ring and AI settings only with `--include-secrets`.
Copy it to your own backup system. `draughtsman restore` puts it back; Postgres is the `pg_dump` the command prints. Nothing is uploaded
anywhere, and no component of the product needs a cloud account to hold a backup. The full procedure, schedules and the restore drill are
in [`upgrade-backup.md`](upgrade-backup.md); in Docker:

```sh
docker exec draughtsman draughtsman backup --out /data/backups/     # then copy /data/backups/ off the volume
```

## Finding out what is wrong, offline

`draughtsman doctor` and `draughtsman support-bundle` (and the **Connection check** page in Admin) need no internet: the verbs read the data folder and
ask this machine's own loopback address whether the server answers, and nothing else; the page runs in your own browser against your own server. The
support bundle is a zip you carry across to us yourself, after you have read it ([`troubleshooting.md`](troubleshooting.md#troubleshooting-an-install)).

## Upgrades with no internet

An upgrade is a new file carried across, then the same procedure as anywhere ([`upgrade-backup.md`](upgrade-backup.md)):

1. On a connected machine fetch the new archive (or image), `SHA256SUMS` and the SBOM; carry them across and check the checksums.
2. **Back up**, then run `draughtsman config check` with the **new** program against your data folder. It starts nothing and changes
   nothing, and reports whether the new version would start and what it would migrate. In Docker, with no network, exactly as the offline host would:

   ```sh
   docker run --rm --network none -v draughtsman-data:/data draughtsman:0.2.0 config check
   ```
3. Stop the old one and replace the files in the same folder (archive), or run the new tag on the same volume (Docker). The database migrates
   itself from the files in the program: a SQLite database is copied first and put back if the upgrade fails, and an older program refuses a
   newer database instead of damaging it.

Nothing is fetched during an upgrade: migrations, the web app and the sidecar are inside the new files.

## What leaves your network, and when

| What | When | Where it goes | Off by default? | To keep it off |
| --- | --- | --- | --- | --- |
| **Nothing** | A default install, any time: sign-in, create, save, layout, export, health, backups | n/a | n/a | n/a |
| The browser's requests | Loading and using the app | **Only to your Draughtsman server.** No web fonts, CDN, analytics or error reporter; the response's `Content-Security-Policy` is `default-src 'self'` with `connect-src 'self'`, so a request to any other origin is blocked by the browser | Yes | Nothing to do |
| **AI model** | A person uses the AI panel, or an administrator presses Test connection | The endpoint the administrator typed: your own Ollama host, or a cloud provider **only if** you name one. The prompt and, when updating, the open document go there | **Yes**: no provider is set, the panel is hidden and the AI routes answer `404` | Leave `ai.provider` empty (or Reset on **Admin > AI**). Use a model on your own network |
| **Outgoing email (SMTP)** | A person asks for a password-reset link, or an administrator sends the test message | The mail server the administrator named | **Yes**: `email.provider` is empty, the sign-in page tells people to ask an administrator, and the emailed-link route answers `404` (an administrator makes reset links by hand) | Leave `email.provider` empty, or use `file` to write messages to a folder |
| **A web page's CSS, for a draft theme** | An administrator presses Fetch and read colours on Admin > Themes > From a website | The one address the administrator typed (https only; loopback, private and metadata addresses refused unless you list the host), and up to 8 stylesheets that page names. Nothing is stored from it | **Yes**: `themes.fetchFromUrl.enabled` is false, the address box is not shown and the route answers `403`. Pasting CSS needs no network | Leave `themes.fetchFromUrl.enabled` false |
| **Postgres** | Always, once chosen | The database server you run | **Yes**: SQLite in the data folder | Leave `storage.provider: sqlite` |
| **The PDF browser's download** | Once, when an administrator runs the install command | Playwright's download hosts, **from the machine running the command**, never from the server | **Yes**: PDF is off, and the server only prints the command | [PDF, offline](#pdf-export-offline) |
| Help > Documentation | A person clicks it | In the archive: the server's own `/docs/`. In the Docker image, which has no `docs/`: the vendor's website, opened by the person's browser in a new tab | Opens only on a click | `about.docsUrl: ""` hides it, or an intranet address |
| Support contact | A person clicks it | A `mailto:` link in the browser | On a click | Set `about.supportContact` |
| Your reverse proxy | Its own certificate renewal | Not Draughtsman: Caddy's automatic HTTPS and any ACME client call out | Your proxy's setting | Use a certificate from your own CA ([`deployment.md`](deployment.md)) |
| macOS `security` tool | Server start, key ring protection `auto` or `keychain` | The local Keychain only | macOS only | `keyRing.protection: key`, `certificate` or `none` |

Nothing else: no update check, usage telemetry or crash reporter. [`data-flow.md`](data-flow.md) is the longer inventory, with what each AI provider is sent. The **Node sidecar** (layout) is a
child process that talks to the server over a pipe and opens no socket; the web app and the sidecar bundle are scanned on every build for
any other host or network module ([How we check this](#how-we-check-this-on-every-build)).

### What the browser loads

The page and its scripts, styles and fonts (Inter and JetBrains Mono, bundled) are served by the server itself, all by relative URL.
The bundles do contain some `https://` strings: XML and SVG namespace names, JSON Schema identifiers, and the address inside React's
error text. They are names the code carries, never requested, and the list is pinned in `scripts/check-offline-bundles.mjs`: a new one
fails the build until a person reads it.

## Prove it on your own machine

Do not take the page's word. Each of these is a few minutes and needs nothing from us.

**1. Start it with no network at all.** In Docker, `--network none` leaves the container a loopback interface and nothing else. If it
works, it needs no network:

```sh
docker run -d --name draughtsman-offline --network none -v draughtsman-offline:/data draughtsman:0.1.0
docker exec draughtsman-offline cat /proc/net/dev                 # only `lo`
docker exec draughtsman-offline node -e 'fetch("http://127.0.0.1:5180/api/health").then(r => r.text()).then(console.log)'
docker logs draughtsman-offline 2>&1 | grep -iE "resolve|unreachable|name or service"     # nothing
docker rm -f draughtsman-offline && docker volume rm draughtsman-offline
```

The repository's `scripts/docker-offline-smoke.sh` does this and more: it drives first run, sign-in, a token, create, save, layout, the text
exports, the server's own SVG render over MCP and the optional components (which must answer "not here") inside such a container, and fails
if the container's log shows anything that tried to leave.

**2. Look at the sockets.** While people use it, the server holds one listening socket and loopback connections, nothing else:

```sh
lsof -nP -a -p <the server's pid> -i          # macOS and Linux: one LISTEN socket, and only loopback connections
ss -tnp | grep draughtsman                    # Linux: no established connection to another address
```

**3. Block it and see.** Put the host behind a default-deny egress firewall (or unplug it) and use it for a day. Nothing changes: that is
the point. Anything that does change, tell us.

**4. Read what the browser loads.** Open the developer tools, Network tab, and use the editor: every request is to your server.
`curl -sI http://localhost:5180/ | grep -i content-security-policy` shows the policy that makes it so. To list every address in the web app:
`grep -rhoE "https?://[A-Za-z0-9./_-]+" --include='*.js' --include='*.css' --include='*.html' wwwroot | sort -u` (in an archive) shows only the namespace and identifier strings above.

**5. Read the sidecar.** `grep -oE 'require\("[^"]+"\)' sidecar/sidecar.js | sort -u` lists the Node modules the layout process can load:
`node:crypto` and `node:readline`, nothing that can open a connection.

**6. Check the inventory.** Open `SBOM.cdx.json` and compare it with your own list of what may run on that host ([next](#the-software-bill-of-materials)).

## The software bill of materials

Every release carries a **CycloneDX 1.5** SBOM, as JSON, in three places: inside every release archive (`SBOM.cdx.json`), beside the
archives (`draughtsman-<version>.cdx.json`, listed in `SHA256SUMS`) and inside the Docker image (`/app/SBOM.cdx.json`). From an image:
`docker run --rm --entrypoint cat draughtsman:0.1.0 /app/SBOM.cdx.json`.

- **What is in it:** every NuGet package of the server (about 27, including transitive ones, with their SHA-512 and licence), every npm
  package that is bundled into the web app or the sidecar (about 17, including the few that the lock file marks as development tools but that
  the web build bundles, such as the schema validator), and the bundled Node.js runtime. Each component has a package URL (`purl`), version,
  licence and a `draughtsman:origin` property saying whether it came from the server, the web app or the sidecar.
- **What is not:** the .NET runtime that a self-contained archive carries (its notices are `DOTNET-THIRD-PARTY-NOTICES.txt` in each archive), the
  Debian base layers of the container image (use an image scanner for those), the optional Chromium, and a dependency *graph*: the list is flat.
- **Which tool:** the generator is the repository's own `scripts/generate-sbom.mjs`, which uses no package and no network: it reads the
  files `dotnet restore` and `npm ci` already write (`project.assets.json` and the two `package-lock.json` files), the same inputs the
  official CycloneDX tools read. We did **not** add the official `@cyclonedx/cyclonedx-npm` or the CycloneDX .NET tool: that would put a
  tool download into the release path, and an air-gapped build is a goal here. The result was validated once, on 2 October 2026, against
  CycloneDX's own `bom-1.5.schema.json` (valid; a copy with a wrong `bomFormat` and component `type` was rejected, so the check can fail).
  The output is sorted and carries no clock, so the same inputs give the same bytes, and the build date stamps `metadata.timestamp`.
- **It cannot be missing:** the release scripts refuse to finish without it, and the Docker build fails if it is hollow
  ([How we check this](#how-we-check-this-on-every-build)). Any CycloneDX 1.5 consumer reads it (Dependency-Track, `grype sbom:`, `trivy sbom`
  and the like: **unverified** here, we ran only the schema check).

## How we check this on every build

These run in the product's own test suites and release scripts, so a regression is a failing build, not a surprise:

| Check | What it proves | Where |
| --- | --- | --- |
| No outbound connections | A default server, listened to from inside the process on every DNS lookup, TCP connect, HTTP request and TLS handshake the .NET runtime announces, is driven through first run, sign-in, create, save, layout, text exports, an SVG render, PDF and AI routes, a password-reset request and an admin export, then shut down: nothing is aimed at any machine but this one | `tests/Draughtsman.Server.Tests/NoOutboundNetworkTests.cs` |
| The listener is live | The same listener hears real loopback traffic on each channel, flags every non-loopback name and address, and treats anything it cannot read as off the machine | same file |
| Each opt-in is off, and heard when on | AI, email, the Themes page's fetch-a-web-page, PDF and Postgres are off in a default install and silent; switched on, each is heard going where it was pointed (a remote AI endpoint is seen by the listener and refused before any packet leaves) | `OptInNetworkFeaturesTests.cs` |
| The network code is a closed list | The only files in `src/` that can open a connection, or start a child process, are the ones listed there and in `data-flow.md`; a new one fails until it is added deliberately | same file |
| No payment or telemetry library in the server | The server's dependency manifest holds no payment library and no telemetry or error-reporting library | same file |
| No external host in the bundles | The built web app loads nothing from, and names no address outside, a short reviewed list; the sidecar imports only `node:crypto` and `node:readline` | `scripts/check-offline-bundles.mjs` and its test, in `verify-local.sh`, CI, `publish.sh` and the Dockerfile |
| The SBOM ships | `publish.sh` and `release.sh` fail unless the SBOM is generated, valid and in every one of the six archives; the Docker build fails on a hollow one; `docker-export.sh` refuses an image without one | `scripts/sbom.test.sh`, `scripts/check-sbom.test.mjs` |
| A real run with no network | The release image does all of the above in a container with no network interface but loopback | `scripts/docker-offline-smoke.sh` (needs Docker; not part of `verify-local.sh`) |

## What was verified, and where

All on 2 October 2026, on an Apple-silicon Mac with Docker Desktop (server 20.10.14), from the commit that added this page.

- **The image, with no network.** The image was built from the repository (`docker build`, with the web-bundle, sidecar-bundle and SBOM checks
  running inside the build), then started with `--network none`: the container had no route and no address but loopback. `scripts/docker-offline-smoke.sh`
  took it through first run, sign-out and sign-in, an API token, create and save, layout by the Node sidecar, the Mermaid and DSL exports, the server's own
  SVG over MCP, PDF, AI and the emailed reset link (all three answered "not here"), `draughtsman backup` and `draughtsman config check`, with health
  still reporting SQLite, no AI and no PDF; the container's log held no resolver, connect or HTTP failure, and its one listening socket was `0.0.0.0:5180`.
  An older image (before this work) fails the same script, so it can fail.
- **The tarball round trip.** `scripts/docker-export.sh` wrote a 304 MB `.tar.gz` with its checksum; the checksum verified; with the
  image's tag removed, `docker load -i` brought it back and `docker run --network none ... --version` printed `Draughtsman 0.1.0 (build 2026-10-02)`.
  No registry was used.
- **A real server process, macOS.** The same workload run against a native build (`draughtsman serve`): `lsof -nP -a -p <pid> -i` afterwards showed one
  socket, `127.0.0.1:<port> (LISTEN)`, and the sidecar child process held none.
- **The guide's own commands.** In the image, the sidecar's `require(...)` list was exactly `node:crypto` and `node:readline`, and the web app's address
  list was the namespace and identifier strings described above.
- **The SBOM.** 45 components (27 NuGet, which is the count `dotnet list package --include-transitive` reports for the server, 17 npm, the Node runtime), and valid
  against CycloneDX's own `bom-1.5.schema.json` (a copy with a wrong `bomFormat` and component type was rejected by the same validator).
- **Not run:** the Linux and Windows x64 archives on a host with no network; the PDF browser copied to a second offline host; Chromium's sockets under
  capture; any SBOM consumer (Dependency-Track, `grype`, `trivy`).

## What this does not prove

- The in-process listener sees the server process. The sidecar and, when enabled, Chromium are other processes, covered by a static check of the
  sidecar bundle, the off-by-default PDF component and its flags, not by a packet capture.
- A default configuration. An administrator who sets an AI endpoint, an SMTP host or a Postgres connection has chosen to reach that host.
  The AI endpoint is restricted by provider ([`ai-endpoint-policy.md`](ai-endpoint-policy.md)): a keyed provider may reach only its own
  host or one the operator lists in `draughtsman.yaml`, and Ollama only this machine and private addresses. Within that, an
  administrator chooses the host. If that matters, leave AI off.
- Anything outside the product: the operating system's own update and telemetry services, your browser's extensions and sync, the Docker or
  .NET tooling you build with, and the reverse proxy in front.
- Third-party software we ship is listed, not audited by us. The SBOM is the list; scanning it for known vulnerabilities is yours to do.

## If something looks wrong

A connection from a default Draughtsman to anything but a client of yours is a bug, and we want to hear about it:
[`../SECURITY.md`](../SECURITY.md). Include what `lsof`/`ss` showed and the version from **About**.
