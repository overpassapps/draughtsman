# Install on Linux (release archive and systemd)

Archives are published for `linux-x64` and `linux-arm64`. An archive is self-contained: the server, the web app and an
official Node 22 for the layout sidecar are inside it. You do not install .NET or Node.

**What was run for this page.** The `linux-arm64` archive, built from the repository, was unpacked and run in a bare
`ubuntu:22.04` container (arm64) with nothing installed first: it started, applied its migrations, printed the setup code, and
answered `{"status":"ok"}` on `/api/health` and `200` on `/`. `service install --dry-run` was run for the systemd unit.
**Not run: the service itself.** No machine with systemd was available, so `service install`, `systemctl` and `journalctl` below
are **unverified**. `linux-x64` was built and its layout checked (`.playwright/` whole, Node 755) but not run (same code and build; a different runtime pack and Node download).
Re-checked on 1 October 2026 with a fresh `linux-arm64` archive in the same kind of bare `ubuntu:22.04` (arm64) container, archive mounted
read-only: after `apt-get install curl ca-certificates` and the browser install below, PDF export produced a PDF (`200`, `%PDF-1.4`,
17,984 bytes) with no Node on the host. An
Alpine (musl) host was not tried; use a glibc distribution.

## 1. Unpack

```sh
sha256sum -c SHA256SUMS --ignore-missing                 # the archive's checksum
sudo mkdir -p /opt/draughtsman
sudo tar -xzf draughtsman-0.1.0-linux-x64.tar.gz -C /opt/draughtsman --strip-components=1
/opt/draughtsman/draughtsman --version                   # Draughtsman 0.1.0 (build <date>)
```

Install from where the executable will stay: the service points at that path. The archive's layout is in
[`install.md`](install.md).

## 2. Try it in the foreground

```sh
DRAUGHTSMAN_DATA=/tmp/ds-try /opt/draughtsman/draughtsman
```

It binds `127.0.0.1:5180`, creates the data folder owner-only, applies migrations, and prints the setup code (checked in the
container: the log lines `Draughtsman 0.1.0; data folder ...; storage sqlite`, `Applying 6 storage migration(s)`, then the setup-code
banner, then `Serving web build from ...`). Open <http://localhost:5180>. Stop it with Ctrl+C. Without `DRAUGHTSMAN_DATA` the data
folder is `data/` beside the executable, whatever the working directory is.

## 3. Install the service

Look first:

```sh
sudo /opt/draughtsman/draughtsman service install --dry-run
```

It prints, and changes nothing (verified on the `systemd` plan; the unit is generated, not a template you copy):

```
# write /etc/systemd/system/draughtsman.service
[Unit]
Description=Draughtsman diagram server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart="/opt/draughtsman/draughtsman" serve
WorkingDirectory=/opt/draughtsman
Environment="DRAUGHTSMAN_DATA=/var/lib/draughtsman"
Environment="DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/draughtsman"
Restart=on-failure
RestartSec=5
DynamicUser=yes
StateDirectory=draughtsman
CacheDirectory=draughtsman
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
# run systemctl daemon-reload
# run systemctl enable --now draughtsman.service
```

Then, for real:

```sh
sudo /opt/draughtsman/draughtsman service install
sudo /opt/draughtsman/draughtsman service status
sudo journalctl -u draughtsman -f                         # the log, and the first-run setup code
```

- **Account.** By default the service runs as a systemd *dynamic user*, so no account is created, and owns
  `/var/lib/draughtsman` (the data folder) and `/var/cache/draughtsman`. To use a real account add `--user <account>`; the
  account must exist and own the data folder (`--data <folder>` changes the folder; the unit then lists it in
  `ReadWritePaths`, which `ProtectSystem=strict` needs).
- **`DOTNET_BUNDLE_EXTRACT_BASE_DIR`** is set because the single-file executable unpacks a few native libraries at start and a
  dynamic user has no home folder. Checked on macOS: started with `HOME` pointing at a folder that does not exist, the same
  executable stops with `Failure processing application bundle ... DOTNET_BUNDLE_EXTRACT_BASE_DIR is not set`. If you start
  the executable under your own supervisor instead of `service install`, set that variable (or a writable `HOME`).
- **Setup code.** `journalctl -u draughtsman | grep "setup code"`. The first-run page names the journal when it detects it is
  running under systemd.
- **Settings** go in `/var/lib/draughtsman/draughtsman.yaml` (root can edit it; keep it `0600` and owned by the service's user,
  or the server warns and tightens it); `sudo systemctl restart draughtsman` after editing.
- **Admin commands** (`backup`, `restore`, `reset-admin`) read the same data folder, so run them with `sudo`, and set
  `DRAUGHTSMAN_DATA=/var/lib/draughtsman`: `sudo DRAUGHTSMAN_DATA=/var/lib/draughtsman /opt/draughtsman/draughtsman reset-admin
  admin@example.com --generate`. Run with the service running or cleanly stopped; check `ls -l /var/lib/draughtsman/` afterwards
  shows nothing owned by root that the service needs to write.
- **SELinux** (RHEL, Fedora): an executable under `/opt` that systemd starts may need the `bin_t` label. Not tried; if the unit
  fails with `Permission denied` on the executable and `ausearch -m avc` shows a denial, that is the cause.
- **Not under systemd** (a container orchestrator, runit, supervisord): run `/opt/draughtsman/draughtsman serve` with
  `DRAUGHTSMAN_DATA` and `DOTNET_BUNDLE_EXTRACT_BASE_DIR` set, restart on failure, and send it `SIGTERM` to stop.

## 4. Reach it

The service binds `127.0.0.1:5180`. Put Caddy or nginx on the same machine in front of it, list the proxy and your host name in
`draughtsman.yaml`, and open only 443 ([`deployment.md`](deployment.md)). To reach it without a proxy for a test, set
`server.host: 0.0.0.0` and expect the server to log that it is not loopback.

## 5. Back up, upgrade, remove

```sh
sudo DRAUGHTSMAN_DATA=/var/lib/draughtsman /opt/draughtsman/draughtsman backup --out /srv/backups/
```

To upgrade: back up, check the new binary against the data (`sudo -u <service account> /opt/draughtsman-new/draughtsman config check --DRAUGHTSMAN_DATA=/var/lib/draughtsman`),
`sudo systemctl stop draughtsman`, unpack the new archive over `/opt/draughtsman` (the same folder: the unit points at it; the stored AI key and SMTP password would survive another folder
too), `sudo systemctl start draughtsman`. The database migrates itself and copies SQLite first, and puts the copy back if the upgrade fails ([`upgrade-backup.md`](upgrade-backup.md)). To remove:
`sudo /opt/draughtsman/draughtsman service uninstall`; the data folder is never touched.

## 6. PDF export (optional)

PDF from the editor (File > Export PDF…) needs nothing on the server: it prints from the user's own browser. Server-side PDF, for scripts and agents, needs a browser, which the archive does not carry; the driver it needs is in `.playwright/`. Either point
`export.chromium.path` at a Chromium or Chrome you installed, or install Playwright's, as the account that runs the server, with
the archive's own driver (no Node needed) and the system libraries Chromium wants:

```sh
sudo /opt/draughtsman/.playwright/node/linux-x64/node /opt/draughtsman/.playwright/package/cli.js install --with-deps chromium
```

(`linux-arm64` in the folder name on an Arm host. `--with-deps` runs `apt-get`, so it is for Debian and Ubuntu; elsewhere install
the libraries by hand. Without `sudo`, drop `--with-deps`. The browser lands in the installing account's `~/.cache/ms-playwright`; a
service account with another home needs `PLAYWRIGHT_BROWSERS_PATH` set to a shared folder, in the unit file too, for both the install
and the server.) Then set `export.chromium.enabled: true` in `draughtsman.yaml` and restart. In a container Chromium may refuse to start
its sandbox; the server then runs it without one and logs why ([`pdf-export.md`](pdf-export.md)). Verified as described at the top of this page
(`linux-arm64`, Ubuntu 22.04, run as root in a container); a systemd service and `linux-x64` were not run.

## If it does not start

[`troubleshooting.md`](troubleshooting.md). First: `journalctl -u draughtsman -n 100 --no-pager`; exit code `3` means the database
is newer than this version.
