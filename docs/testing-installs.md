# Testing installs: the install matrix

This page is for the people who build and release Draughtsman, not for customers. It is not shipped in the release archives.

The automated tests prove the product's code. They cannot see the layer between the product and a person's browser: a CDN that weakens a header, a
proxy that drops one, a `Secure` cookie that a browser refuses over plain http, a data folder the server's account cannot write, an archive that
needs something a clean machine does not have. Three of those reached the first real install. The install matrix is the rig that looks there:

**one scripted first journey, run against every cell of a small matrix of installs, with a pass/fail table at the end.**

```sh
scripts/install-matrix.sh                         # every automated cell, one at a time (needs Docker, Node 22, curl, openssl)
scripts/install-matrix.sh --cells docker-direct,docker-caddy
scripts/install-matrix.sh --list                  # the cells, the deliberate-breakage proofs and the manual cells
```

It is **not** part of `scripts/verify-local.sh` or CI: it needs Docker, builds an image and one or two release archives, and takes about fifteen
minutes the first time. The pure parts of the rig (the report formatter, the version comparison, the redaction, the expectation rules, the cookie
jar, the proxy rules, the zip reader) have unit tests that need no Docker, and `verify-local.sh` runs those:

```sh
node --test scripts/install-matrix/test/*.test.mjs
```

## What one run does

1. Builds the image once from the current tree as `draughtsman:matrix` (`docker build`, the way `scripts/docker-smoke.sh` does), and a release
   archive with `scripts/publish.sh` into a scratch folder only when a cell needs one.
2. For each cell, one at a time: brings the install up (its own Docker network, volume and containers, all named `dmx-<run>-...`), runs
   `journey.mjs` against it, reads the server log for what the cell expects of it, and tears the cell down.
3. Prints a table, writes `artifacts/install-matrix/<timestamp>/report.md` (git-ignored: every cell's steps, statuses and evidence), and exits
   non-zero if any cell failed unexpectedly. An `EXPECTED-FAIL` is not a failure; a `MANUAL` cell is listed and never counted.

Options: `--cells a,b` a subset; `--keep` leave the containers, networks and volumes for inspection (remove them later with `--clean-stale`);
`--skip-build` reuse an existing `draughtsman:matrix` image (and do not remove it afterwards); `--break NAME` a deliberate-breakage proof (below).

### What it touches, and what it will not

Everything it creates is written to a registry as it is made, and only that is removed: containers, networks and volumes named `dmx-<run>-*`,
the image tag `draughtsman:matrix` (only if this run built it), its temp folders (`dmx-run.*` under `$TMPDIR`) and the process ids it started
(each checked to still be the program it started before it is stopped; never by name). It cleans up on success, on failure and on Ctrl-C. It never
touches any other container, volume, network or image on the machine. The base images it pulls (`nginx:alpine`, `caddy:2`, `ubuntu:24.04`) are
shared, so they stay in the local image cache; they are named in one place, the `images` block of `cells.json`.
`scripts/install-matrix.sh --clean-stale` removes leftovers of an interrupted or `--keep` run: containers, networks and volumes whose names begin
`dmx-`, and nothing else.

## The journey

`scripts/install-matrix/journey.mjs` is the test body every cell runs. It takes the address a browser would use and, optionally, the container name:

```sh
node scripts/install-matrix/journey.mjs --base-url https://localhost:8443 --setup-code-from docker-logs:NAME --container NAME
```

It makes up its own email and a random 24-character password (it prints `password: generated`, never the password), and works through these
steps, recording **PASS**, **FAIL** or **EXPECTED-FAIL** for each with the exact HTTP status and, on a failure, the response body and the relevant
response headers (never a cookie or a token):

| Step | What it checks |
| --- | --- |
| a | `GET /api/auth/status` says first run is required |
| b | `POST /api/auth/first-run` with the setup code from the log gives `201` |
| c | sign in gives `200` **and the session works**: the next `GET /api/auth/status` shows the user. A `Secure` cookie that a browser would refuse over plain http returns `200` and then no session; this step reports it as `signed in but session not kept`, names the scheme and points at `server.forceHttps` |
| d | create a document |
| e | read it, and take its version from both `X-Document-Version` and the `ETag`; report whether the ETag arrived weakened (`W/`) or missing |
| f | save (`PUT` with `If-Match`) once with the ETag exactly as received and once with `X-Document-Version` |
| g | rename (`PATCH`) |
| h | a stale save is refused with `412` |
| i | name a version, change the document, restore the version |
| j | exports: SVG (the server's render, over MCP `render_document`, through the sidecar), DSL and Mermaid text; PDF only if `GET /api/health` says `export.pdf` is true. PNG is drawn in the browser only, so a scripted journey cannot exercise it (the report says so) |
| k | `GET /api/admin/selfcheck` and the browser round trip, printing each row's state (`proxy`, `secure`, `host`, `data`, `disk`, `build`, `cookie`, `etag`, `encoding`, `clock`). A row that fails fails the step unless the cell declared it |
| l | with `--container`: `draughtsman doctor` exits 0 or 1; `support-bundle` writes a zip whose entries are listed (and checked for a database or key) and is then deleted; `reset-admin <email> --generate` gives a password that signs in; `docker restart`, wait for health, sign in again |
| m | sign out, then sign in again |

The client behaves like a **strict browser**: its cookie jar drops a `Secure` cookie (and any `__Host-` cookie) set over plain http, even on
`127.0.0.1`. (Chrome makes an exception for localhost; Safari and every other address do not, and a first install is rarely on localhost.)
When step c finds the session not kept, the journey carries on with a lenient jar (like `curl`) so the rest of the journey still says something
about the server; step c stays failed.

### Expectations

A cell may declare that a step is *expected* to fail, with the reason: `--expect c="session not kept: forceHttps is on over http"`. The step then
reads `EXPECTED-FAIL`. **A step declared expected-to-fail that passes is itself a failure (`UNEXPECTED-PASS`)**, so a stale declaration cannot
hide a fix. `--expect-selfcheck secure=fail` declares a Connection check row's status; every other row that fails is a failure. The journey exits
0 only if every step passed or is a declared `EXPECTED-FAIL`.

For an instance that already has users (the upgrade cells) use `--sign-in-as EMAIL --password-env VAR`: step b is not applicable and the
journey signs in as that administrator. For the real server, see the manual cells below.

### Evidence is redacted when it is captured

`lib/redact.mjs` keeps evidence to a fixed list of response headers (no `Set-Cookie`, `Cookie` or `Authorization`), describes each `Set-Cookie` by
name and attributes only (`__Host-ds_session (Path=/, Secure, SameSite=lax, HttpOnly)`), and scrubs tokens (`dst_`, `dpr_`), session and
anti-forgery values, the setup code and every exact secret the run holds from every body and log line before it can reach a report.

## The cells

Each cell is an entry of `scripts/install-matrix/cells.json`. What each proves:

| Cell | What it proves |
| --- | --- |
| `docker-direct` | The release image on a fresh volume, port on `127.0.0.1`, plain http, `forceHttps` off: the baseline. |
| `docker-forcehttps-http` | `forceHttps` on but reached over plain http: sign-in answers `200` and **no session is kept** (the sign-in button stays on "Signing in..."). The journey must report step c as an expected `session not kept`, and the Connection check's `secure` row must be a `fail` that says why. |
| `docker-nginx-good` | The documented nginx setup: `nginx:alpine` in front on a Docker network, gzip on for JSON, `Host`, `X-Forwarded-For` and `X-Forwarded-Proto` set, TLS with a certificate made at run time, `forceHttps` on, `server.knownProxies` set to the network's own range (read from `docker network inspect`). The `proxy` and `secure` rows must be `pass` and the log must not draw the untrusted-proxy warning. nginx weakens the ETag when it gzips, so this cell also exercises issue 63. |
| `docker-nginx-noproto` | The same nginx with **no `X-Forwarded-Proto` and no `knownProxies`**. With `forceHttps` on the Secure cookie still works over https, so the journey passes; what must be caught is the lockout hazard: the `proxy` row must be a `warn`, and the log must carry the "Forwarding headers ... arrived from ... not listed in server.knownProxies" line. |
| `docker-caddy` | `caddy:2` with `encode gzip` and automatic internal TLS, `forceHttps` on, `knownProxies` set. |
| `docker-cloudflare-like` | `cfproxy.mjs`, a small Node proxy (no dependency) standing in for Cloudflare: TLS, gzips JSON, **rewrites the strong ETag to `W/"..."`**, adds `CF-Connecting-IP` and `X-Forwarded-Proto: https`. The journey must pass (issue 63) and step e must report the ETag as weakened. |
| `docker-cloudflare-noetag` | The same proxy removing the ETag entirely. The journey must still pass, saving with `X-Document-Version`, and step e must report the ETag as missing. |
| `archive-macos` | The real `osx-arm64` release archive, run on this Mac with a bare `PATH` (`env -i`) and a fresh data folder, direct. Nothing but the archive is installed. |
| `archive-linux-clean` | The Linux release archive for Docker's architecture (`scripts/publish.sh`), run in a bare `ubuntu:24.04` container (no .NET, no Node) as the stock non-root `ubuntu` user, data folder on a bind mount owned by that user, direct. |
| `upgrade-newest` | The newest data folder under `tests/upgrade-fixtures/` (written by an earlier build, through its own API) opened by the **current** image. `scripts/upgrade-fixture/verify.mjs` checks every user, token, document, version and setting reads back, then the whole journey runs as the fixture's administrator. |
| `upgrade-oldest` | The same from the oldest fixture, so the migrations really run (the pre-migration copy is made and the data survives). |

The fixtures are real data folders, so an old build is not needed: `scripts/make-upgrade-fixture.sh` made them (see `tests/upgrade-fixtures/README.md`),
and the existing .NET upgrade tests (`tests/Draughtsman.Server.Tests/Upgrade/`) cover the same ground in process. The matrix adds the part those cannot: the
**release image** on the old volume, signing in over HTTP the way a person does.

### Manual cells

| Cell | Why it is manual and how to run it |
| --- | --- |
| `windows-archive` | Needs the Windows VM, which uses about 13 GB, so a person starts it. On the VM unpack the `win-x64` or `win-arm64` archive and run it (`docs/install-windows.md`), then from the Mac: `scripts/install-matrix.sh --vm windows-vm --vm-port 5180 --setup-code-file ./setup-code.txt`. `--vm HOST` reads the VM's address from `ssh -G HOST`, runs only the journey from the Mac (no `--container`: the verbs run on the VM) and judges the same steps. |
| `dogfood-cloudflare` | The real server behind nginx and Cloudflare. The journey run against its https address is the acceptance test of the connection-layer fixes. On a fresh instance: `node scripts/install-matrix/journey.mjs --base-url https://<host> --setup-code-from file:./code.txt`. On one that already has an administrator: `DMX_PASSWORD='...' node scripts/install-matrix/journey.mjs --base-url https://<host> --sign-in-as you@example.com --password-env DMX_PASSWORD` (the password is read from the environment, never from the command line). It creates documents named `Install matrix journey`; delete them afterwards. |

## Deliberate-breakage proofs

A test that cannot fail proves nothing. `--break NAME` breaks one thing in the cell that defines it and runs that cell; the journey must fail at a
**named step with a message that says what is wrong**:

| Command | The break | What the journey says |
| --- | --- | --- |
| `scripts/install-matrix.sh --break proxy-strip-versions` | the Cloudflare-like proxy removes the ETag **and** `X-Document-Version` | step e fails (`no version header arrived (ETag: missing; X-Document-Version: missing): a save has nothing to quote in If-Match`), then f, g and i for the same reason |
| `scripts/install-matrix.sh --break data-root` | `data.db` is owned by root inside the image user's folder (what a restore run as root leaves behind) | the server refuses to start (exit code 5, the product's own `Draughtsman cannot start: it cannot write to /data/data.db ... chown -R 10001 /data`); the cell reports the container's exit state and that line, and step a fails with `ECONNREFUSED` |
| `scripts/install-matrix.sh --break nginx-no-proto` | nginx stops sending `X-Forwarded-Proto` while `forceHttps` is off (the common nginx setup that relies on the header) | step k fails: `secure: expected pass, got warn (Secure cookies and sign-in)` |
| `scripts/install-matrix.sh --break nginx-untrusted-proxy` | `server.knownProxies` is not set, so the proxy's headers are ignored and every visitor shares one address for the sign-in throttle | step k fails on the `proxy` row, and the "log is free of the untrusted-proxy warning" check fails |

Note what the first nginx break does **not** do: with `forceHttps` **on** (the good cell as shipped), removing `X-Forwarded-Proto` changes nothing a person can see, because `forceHttps`
makes the cookie `Secure` whatever the proxy says. The proof therefore also turns `forceHttps` off.

The observed output of each is in the "First run of the matrix" section below.

## Adding a cell

1. Add an object to `cells` in `scripts/install-matrix/cells.json`:

   | Field | Meaning |
   | --- | --- |
   | `name`, `description` | the cell's name (also its folder in the report) and what it proves |
   | `kind` | `docker` (the image, optionally behind a proxy), `archive-host`, `archive-container` or `upgrade` |
   | `proxy` | for `docker`: `{ "type": "none" }`, `{ "type": "nginx", "forwardProto": true|false }`, `{ "type": "caddy" }` or `{ "type": "cfproxy", "mode": "weak|noetag|strip-both|pass" }` |
   | `yaml` | the `draughtsman.yaml` seeded into the data volume; `@SUBNET@` becomes the Docker network's range |
   | `journey.expect` | `["c=reason", ...]`: steps expected to fail |
   | `journey.expectSelfcheck` | `["secure=fail", ...]`: Connection check rows and their declared status |
   | `logExpect`, `logAbsent` | `[{ "pattern": "regex", "why": "..." }]`: lines the server log must (or must not) contain |
   | `expectNote` | `{ "step": "e", "pattern": "regex", "why": "..." }`: a note a step must carry (so a cell meant to reproduce a CDN's behaviour fails when it stops) |
   | `breaks` | named deliberate breakages: `{ "NAME": { "description": "...", "override": { ...fields to replace... } } }` |

2. If it needs a new kind of proxy or machine, add a `start_proxy` branch or a `cell_<kind>` function to `matrix.sh`; the rest is declarative.
3. Run it alone with `scripts/install-matrix.sh --cells <name>`, read its section of the report, and write the expectations from what the
   product *should* do, not from whatever it did.

## What the matrix cannot see

A real browser (Safari, a password manager, an extension), the real Cloudflare (the stand-in reproduces one behaviour, not the service), Windows
and Linux hosts other than the ones above, `systemd` and `launchd` services, a real DNS and certificate, PNG export (client-side only) and PDF
(only when the optional Chromium component is on). Where one of those matters, it is a manual cell or a note in the report.

## First run of the matrix

The first full run (2026-10-06, image built from the tree, `osx-arm64` and `linux-arm64` archives built by `scripts/publish.sh`, Docker Desktop on an Apple-silicon Mac)
gave 10 PASS, 1 EXPECTED-FAIL and 2 MANUAL. What each cell showed beyond its result:

- **`docker-forcehttps-http`**: step c is the expected `signed in but session not kept` (the server set `__Host-ds_session`, the antiforgery cookie and `XSRF-TOKEN`, all `Secure`, over plain http), and the Connection check's `secure` row was `fail`. Every other step passed once the journey carried on with a lenient jar.
- **`docker-nginx-good`**: nginx weakened the ETag when it gzipped (`ETag: weak`), so this cell reproduces issue 63 on a real nginx; the saves still worked. The `proxy` row said the proxy at the network address is trusted and the scheme is https.
- **`docker-nginx-noproto`**: it did **not** produce a session failure. With `forceHttps` on the cookie is `Secure` whatever the proxy sends, so sign-in held. What it did catch is the lockout hazard: the `proxy` row was a `warn` ("Proxy headers arrived from ... not listed in `server.knownProxies`, so the server IGNORES them. Every visitor looks like ... to the sign-in throttle"), `doctor` warned that `forceHttps` is on with `knownProxies` empty, and the log carried the `Forwarding headers (X-Forwarded-For and the like) arrived from ...` line.
- **`docker-caddy`**: Caddy left the responses uncompressed and the ETag strong. That is consistent with the server's `Cache-Control: no-transform` on every `/api` response, which Caddy's `encode` honours (nginx's gzip and the Cloudflare stand-in do not); it is an inference, not something the run proved. The cell still proves the documented TLS, `knownProxies` and forwarded-header setup end to end.
- **`docker-cloudflare-like`** and **`-noetag`**: the ETag arrived `W/"..."` (or not at all), the journey passed, and the Connection check's `etag` and `encoding` rows were `warn`, which is the product doing its job.
- **`archive-macos`**: `draughtsman --version` under `env -i PATH=/usr/bin:/bin` printed `Draughtsman 0.1.0 (build 2026-10-06)`; the whole journey passed. Step l (the verbs) is not applicable: it needs `--container`.
- **`archive-linux-clean`**: the Linux archive started and served in a bare `ubuntu:24.04` container as `uid=1000(ubuntu)`, with no `dotnet` and no `node` on the PATH, and the data folder on a bind mount. One thing to know when you read `doctor` here: it reads the listening address from the environment and `draughtsman.yaml`, not from a `--urls=` argument of the running process, so a server started with `--urls` inside a container draws `FAIL The server listens only on loopback`; start it with `ASPNETCORE_URLS`, as the image does. (The matrix does, and a first attempt that used `--urls` showed it.)
- **`upgrade-newest`** and **`upgrade-oldest`**: `scripts/upgrade-fixture/verify.mjs` read back 152 and 86 checks with none failing, and the oldest run logged `Copied SQLite database to /data/data.db.pre-migration-... before migrating`.

The three deliberate breakages (above) were run the same day and failed at the named steps with the messages in the table. One more thing the run taught: `doctor`'s exit code 2 inside a container is a real signal worth reading, so the journey prints the failing finding and the line that explains it.
