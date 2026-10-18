# Installing from a release archive

A release archive is one folder: unpack it, run it. Nothing else has to be installed on the machine to run Draughtsman (no .NET, no
Node). PDF export from the editor needs nothing either: it prints from the user's own browser. Only server-side PDF (for scripts and
agents) also needs a browser on the server: a Chrome or Edge you already have, or a Chromium that the archive's own driver downloads
(no Node needed for that either); see [`pdf-export.md`](pdf-export.md).
This page is the archive's `INSTALL.md`. The step-by-step guides for each platform are
[Docker](install-docker.md), [Linux](install-linux.md), [macOS](install-macos.md) and [Windows](install-windows.md); putting
it behind TLS is [`deployment.md`](deployment.md); the other guides are listed in [`index.md`](index.md). (In an archive the guides are in the `docs/` folder next to this
page, and the links here point there; pages that are for people who build the product, such as the specification and the
ADRs, are not in it.)

```
draughtsman-<version>-<platform>/
  draughtsman[.exe]            the server (self-contained)
  wwwroot/                     the web app
  sidecar/                     layout and snapshots: sidecar.js and the Node 22 runtime it runs on (node, LICENSE-node.txt)
  .playwright/                 the PDF driver: a Node runtime and the playwright package, used only when PDF export is on
  draughtsman.example.yaml     every setting, annotated, at its default
  docs/                        the guides: install per platform, deployment, admin, backup and upgrade, troubleshooting (start at docs/index.md)
  SECURITY.md, SKILL.md, CHANGELOG.md
  THIRD-PARTY-NOTICES.txt      licences of everything that is not ours (also served at /THIRD-PARTY-NOTICES.txt)
  SBOM.cdx.json                the software bill of materials: a CycloneDX 1.5 list of the packages shipped
  DOTNET-THIRD-PARTY-NOTICES.txt
  BUILDINFO.txt                version, build date, platform
  INSTALL.md                   this page
```

(The folder also holds `appsettings.json` and a `*.staticwebassets.endpoints.json` file that the server needs; leave them where
they are. Leave `.playwright/` whole too: its Node file and its `package/` folder only work together, and the server reports a
broken one as a driver problem, not a browser problem. The Windows archives alone also carry `playwright.ps1`, Playwright's
installer script, which no other platform needs. They carry no `web.config`: Draughtsman is run as a service or in a console and an IIS site
proxies to it ([`deployment.md`](deployment.md#iis-with-application-request-routing-windows)), never hosts it, so that IIS leftover of the
build is left out (the module's native dll still travels inside the executable and unpacks with SQLite; it is inert). `.playwright/` is not in the way when PDF is off: it is not read.)

Platforms: `osx-arm64` (Apple silicon), `osx-x64` (Intel Mac), `linux-x64`, `linux-arm64` (`.tar.gz`), `win-x64` and `win-arm64` (`.zip`;
Windows on Arm should use `win-arm64`, which starts two to three times faster than the `win-x64` archive running under emulation; its PDF
driver is the x64 one, which Windows runs under emulation and which was verified to print).

## Check the download

`SHA256SUMS` lists the archives' checksums:

```sh
shasum -a 256 -c SHA256SUMS --ignore-missing        # macOS and Linux (sha256sum -c on Linux)
Get-FileHash draughtsman-0.1.0-win-x64.zip          # Windows: compare with SHA256SUMS
```

Archives are not yet code-signed or notarised, so macOS Gatekeeper and Windows SmartScreen will stop the first run of a
downloaded copy: on macOS run `xattr -dr com.apple.quarantine draughtsman-<version>-osx-arm64` once after unpacking; on
Windows choose "More info, Run anyway" (not offered where Windows 11's Smart App Control is switched on: see [`install-windows.md`](install-windows.md#1-unpack-and-allow-it-to-run)).

## Run it

```sh
tar -xzf draughtsman-0.1.0-linux-x64.tar.gz && cd draughtsman-0.1.0-linux-x64
./draughtsman                    # same as ./draughtsman serve
```

Open <http://localhost:5180> (run on a Mac from a different folder, the default start bound `127.0.0.1:5180`, served the web app
and answered `/api/health` with `{"status":"ok"}`). The first start prints a **one-time setup code** in the log; enter it on the first-run page to
create the administrator. `./draughtsman --version` and `./draughtsman --help` do not start anything.

- **Data folder**: `DRAUGHTSMAN_DATA` if set, otherwise `data/` beside the executable (not the current directory, so it does
  not matter where you run it from). The database, uploads, branding and the key ring live there. Back it
  up with `draughtsman backup`, not by copying `data.db` while the server runs; copying the folder with the server stopped is
  also complete.
- **Settings**: copy `draughtsman.example.yaml` into the data folder as `draughtsman.yaml` and change what you need.
- **Reaching it from other machines**: the default bind is `127.0.0.1`. Keep it, and put a reverse proxy such as Caddy or
  nginx on the same machine in front of it for TLS: the server itself speaks plain HTTP. Set `server.knownProxies`,
  `server.allowedHosts` and, once every request arrives over HTTPS, `server.forceHttps: true` ([`deployment.md`](deployment.md)).
  Setting `server.host: 0.0.0.0` exposes the plain-HTTP port itself.
- **An AI agent on the same machine**: `draughtsman mcp --stdio` serves the MCP tools over stdio (extra options are read as
  host configuration; nothing else is ever written to stdout).

### What the executable leaves on disk

The executable is a single file that unpacks its native libraries (SQLite, about 2 MB, and the ASP.NET Core Module's in-process dll, about 0.4 MB, which nothing here uses) on first start into a per-build folder:
`~/.net/draughtsman/<hash>/` on macOS and Linux, `%TEMP%\.net\draughtsman\<hash>\` on Windows (for the `LocalService` account of a
Windows service, `C:\Windows\ServiceProfiles\LocalService\AppData\Local\Temp\.net\draughtsman\<hash>\`; no setting is needed there). Nothing else is unpacked: the Node runtimes and the PDF driver are ordinary files in the archive folder. A new build
makes a new `<hash>` folder and the server never removes an old one, so after many upgrades the folders add up (about 2 MB each
now; builds before this note left about 137 MB each because the PDF driver's Node runtime was inside the executable). They are
only a cache: stop the server and delete `~/.net/draughtsman` to clear it, and the next start rebuilds it. To keep the unpacked
files inside the install instead of the account's home, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a folder that account can write
before the executable starts, for example `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/opt/draughtsman/.bundle ./draughtsman` (checked on macOS:
the library appeared under that folder and nothing was written to `$HOME`). The server cannot choose this for itself, because the
unpacking happens before any of its code runs; the services `service install` writes already set it (to a `.bundle` folder under the data folder on macOS; see `service install --dry-run` for the others), which
is why a service account with no home folder works.

## Run it as a service

```sh
sudo ./draughtsman service install --dry-run     # print what it would do
sudo ./draughtsman service install               # Linux: a systemd unit; macOS: a LaunchDaemon; Windows (elevated): a service
./draughtsman service status
sudo ./draughtsman service uninstall             # the data folder is never touched
```

| Platform | Service | Data folder | Runs as | Logs |
| --- | --- | --- | --- | --- |
| Linux | `/etc/systemd/system/draughtsman.service`, started and enabled | `/var/lib/draughtsman` | a systemd dynamic user | `journalctl -u draughtsman` |
| macOS | `/Library/LaunchDaemons/uk.co.overpass.draughtsman.plist` | `/Library/Application Support/Draughtsman` | root unless `--user` | `<data>/logs/draughtsman.log` |
| Windows | service `draughtsman` ("Draughtsman diagram server"), automatic start, restarts on failure | `%ProgramData%\Draughtsman`, private to SYSTEM, Administrators and the service | `LocalService` | the Application event log (source `draughtsman`); the setup code is in `setup-code.txt` there, via `draughtsman first-run-code` from an elevated prompt |

`--name`, `--data`, `--user` and `--exe` change those defaults (run `draughtsman --help`). The service always names its data
folder explicitly, so the working directory does not matter. Install from where the executable will stay: the service points
at that path. To upgrade: back up, run `draughtsman config check` with the new binary, stop the service, replace the archive's files **in the
same folder** (leave the data folder alone), start it again; the database migrates itself at start, and a SQLite database is copied to
`data.db.pre-migration-<stamp>` first and put back if the upgrade fails. See [`upgrade-backup.md`](upgrade-backup.md) for the checklist, why the same
folder is simplest (the service registration points at it; the stored AI key and SMTP password do not depend on it) and for going back.

On macOS, give the service a key-encryption key before installing it ([`install-macos.md`](install-macos.md#3-run-it-as-a-service)).

## Installing without internet access

The whole of this is in [`air-gapped.md`](air-gapped.md): what to carry across, PDF, backups, upgrades, what leaves your network
(by default, nothing) and how to prove it. In short, the archive is already offline: copy the `.tar.gz` or `.zip` and its `SHA256SUMS` line across on whatever media you have. The
Node runtime is inside it, and nothing is downloaded at run time. The one exception is the browser for server-side PDF, which is not in
the archive (PDF from the editor prints from the user's own browser and needs none): on an offline host point `export.chromium.path` at a Chrome, Chromium or Edge already installed, or download
Playwright's Chromium elsewhere and copy its `ms-playwright` folder across ([`pdf-export.md`](pdf-export.md)).

For the container image, build or pull it on a connected machine, then carry it across:

```sh
scripts/docker-export.sh draughtsman:0.1.0 --out ./offline     # docker save, compressed, with a checksum and the load steps
# on the offline host:
docker load -i draughtsman-0.1.0-image.tar.gz
docker run -d --name draughtsman -p 127.0.0.1:5180:5180 -v draughtsman-data:/data draughtsman:0.1.0
```

Building an image needs the internet (the base images, Node and npm packages); running one does not.

## Building the archives (maintainers)

```sh
scripts/publish.sh osx-arm64                     # one platform, for a try-out
scripts/release.sh --build-date 2026-10-14       # all six, with the release guards
```

`release.sh` refuses to build when the build date is not pinned, when the third-party notices are stale, or when the software bill of materials cannot be made; and
it fails after building unless the SBOM is beside the archives and inside every one of the six. `publish.sh` also fails when the web app or the
sidecar bundle could reach another host (`scripts/check-offline-bundles.mjs`). See the header of each script.
