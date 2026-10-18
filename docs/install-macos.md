# Install on macOS (release archive and launchd)

Archives are published for `osx-arm64` (Apple silicon) and `osx-x64` (Intel). An archive is self-contained: the server, the
web app and an official Node 22 for the layout sidecar are inside it. You do not install .NET or Node.

**What was run for this page** (macOS, Apple silicon): the `osx-arm64` archive built from the repository by
`scripts/publish.sh`, its checksum checked, unpacked, started with an empty environment (`env -i PATH=/usr/bin:/bin`) from a
different working directory, and used: `/api/health` answered `{"status":"ok"}`, `/` served the web app, the sidecar ran on the
bundled Node. `service install --dry-run --platform launchd` was run. **Not run: installing the LaunchDaemon.** It needs `sudo`
and would change the machine, so `launchctl` below is **unverified**. `osx-x64` was built (its layout checked, not run).
Re-checked on 1 October 2026 against a fresh `osx-arm64` build: a plain `./draughtsman` from a different folder (no `--urls`)
listened on `127.0.0.1:5180`, `http://localhost:5180/` served the web app and `/api/health` answered `{"status":"ok"}`; and a PDF
was produced from the unpacked archive (see [6. PDF export](#6-pdf-export-optional)).

## 1. Unpack and allow it to run

```sh
shasum -a 256 -c SHA256SUMS --ignore-missing
tar -xzf draughtsman-0.1.0-osx-arm64.tar.gz
xattr -dr com.apple.quarantine draughtsman-0.1.0-osx-arm64
./draughtsman-0.1.0-osx-arm64/draughtsman --version        # Draughtsman 0.1.0 (build <date>)
```

The archives are **not code-signed or notarised yet**, so Gatekeeper stops the first run of a downloaded copy ("cannot be
opened because the developer cannot be verified"). `xattr -dr com.apple.quarantine <folder>` removes the quarantine flag
(the command ran; the Gatekeeper prompt itself needs a downloaded copy and was not seen). Move the folder where it will
stay, for example `/usr/local/draughtsman`: the service points at the executable's path.

## 2. Try it in the foreground

```sh
./draughtsman                                        # data folder: data/ beside the executable
```

Open <http://localhost:5180>; the setup code is printed in the terminal. Stop with Ctrl+C. Output on the first start includes
`Key ring protection: key-encryption key in the macOS Keychain (item "Draughtsman data key ring", account <id>)`: the server
keeps the key that protects the stored AI key in **your login keychain**, one item per data folder. Deleting the data folder
leaves the item behind (`security delete-generic-password -s "Draughtsman data key ring" -a <id>` removes it).
The stored AI key and SMTP password follow that Keychain item and the data folder, **not the folder the program is in**, so moving the program (or unpacking a new version elsewhere)
does not touch them. Upgrading from a build older than this one, run `./draughtsman config check` from the new folder first: its **Stored secrets** section says whether a secret that build
tied to its install folder can still be read (it looks beside the new folder and in `/usr/local/draughtsman`) and, if not, which folder to name in `keyRing.legacyInstallPaths`
([`upgrade-backup.md`](upgrade-backup.md#where-to-put-the-new-version)).

## 3. Run it as a service

```sh
sudo ./draughtsman service install --dry-run         # prints the plist and the launchctl commands; changes nothing
sudo ./draughtsman service install
./draughtsman service status
```

Dry run (verified): it creates `/Library/Application Support/Draughtsman` (with `logs/` and `.bundle/`), writes
`/Library/LaunchDaemons/uk.co.overpass.draughtsman.plist`, then runs `launchctl bootstrap system ...` and `launchctl enable
system/uk.co.overpass.draughtsman`. The plist runs `<executable> serve` with `DRAUGHTSMAN_DATA=/Library/Application
Support/Draughtsman` and `DOTNET_BUNDLE_EXTRACT_BASE_DIR` set, `RunAtLoad` and `KeepAlive` on, and sends standard output and
error to `<data>/logs/draughtsman.log`. Without `sudo` it stops with `Access to the path '/Library/Application
Support/Draughtsman' is denied. Run this as root, for example: sudo draughtsman service install` (verified). `service status`
on a machine where it is not installed prints `Could not find service "uk.co.overpass.draughtsman" in domain for system` and
exits `1` (verified).

- **The service runs as root** unless you pass `--user <account>`. Prefer a dedicated account that owns the data folder.
- **The setup code** is in `<data>/logs/draughtsman.log`: `grep "setup code" "/Library/Application
  Support/Draughtsman/logs/draughtsman.log"`. That file is the service's standard output; Draughtsman does not rotate it, so
  rotate it with `newsyslog` or truncate it yourself. (The `logging.file` setting writes separate, rotated files under
  `<data>/logs/` as well.)
- **Settings** go in `/Library/Application Support/Draughtsman/draughtsman.yaml`; restart with `sudo launchctl kickstart -k
  system/uk.co.overpass.draughtsman` (unverified).
- **Give the service a key-encryption key before you start it.** Under `auto`, the server uses the Keychain for the key ring when
  nothing else is configured. A daemon has no login keychain. With none, the server gives the Keychain 20 seconds, then
  **stops with a message** (`Draughtsman cannot start: The macOS Keychain cannot be used to protect the key ring ...`, exit
  status 1) that names the two safe choices: a key-encryption key, or `keyRing.protection: none` as a deliberate opt-out. It
  never carries on with the ring silently unprotected. (Earlier builds blocked for ever here and printed nothing.) Set a
  key-encryption key up front, before `service install`:

  ```sh
  sudo mkdir -p "/Library/Application Support/Draughtsman" "/Library/Application Support/Draughtsman-keys"
  sudo sh -c 'openssl rand -base64 32 > "/Library/Application Support/Draughtsman-keys/kek"'
  sudo chmod 700 "/Library/Application Support/Draughtsman-keys" && sudo chmod 600 "/Library/Application Support/Draughtsman-keys/kek"
  sudo sh -c 'printf "keyRing:\n  protection: key\n  keyFile: /Library/Application Support/Draughtsman-keys/kek\n" > "/Library/Application Support/Draughtsman/draughtsman.yaml"'
  ```

  Then `sudo ./draughtsman service install`. The key file must be outside the data folder (the server refuses one inside it),
  and backed up separately: without it a restored data folder cannot open its stored AI key. (Checked with an archive started
  under `HOME=/tmp/nonexistent`: with `protection: key` and a `keyFile` it started and logged `key-encryption key from the file`.)
  To run without protection instead, set `keyRing.protection: none` (or `DRAUGHTSMAN_KEY_RING_PROTECTION=none`) knowing the key
  ring then sits as plain text beside the data.
- **Remove**: `sudo ./draughtsman service uninstall`; the data folder is never touched.

## 4. Reach it

The service binds `127.0.0.1:5180`. For other machines put a reverse proxy in front ([`deployment.md`](deployment.md); Caddy is
available from Homebrew) and open only its port. macOS's application firewall may ask whether the unsigned executable may accept
incoming connections the first time something other than loopback listens (unverified).

## 5. Back up, upgrade

```sh
sudo DRAUGHTSMAN_DATA="/Library/Application Support/Draughtsman" /usr/local/draughtsman/draughtsman backup --out /Volumes/Backup/
```

To upgrade: back up, `sudo launchctl bootout system/uk.co.overpass.draughtsman` (unverified), unpack the new archive over the old
folder (remove the quarantine flag again), `sudo launchctl bootstrap system /Library/LaunchDaemons/uk.co.overpass.draughtsman.plist`.
Run `draughtsman config check` with the new binary first, and unpack into the same folder. The database migrates itself and a failed upgrade is undone
([`upgrade-backup.md`](upgrade-backup.md)).

## 6. PDF export (optional)

PDF from the editor needs nothing on the server: it prints from the user's own browser. Server-side PDF (scripts and agents) is off until you give the server a browser; the archive already carries the driver it needs. Set
`export.chromium.enabled: true` in `data/draughtsman.yaml` and either `export.chromium.path: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"`
or install Playwright's Chromium with the shipped driver, run from the archive folder as the account that runs the server:
`./.playwright/node/darwin-arm64/node ./.playwright/package/cli.js install chromium` (`darwin-x64` on an Intel Mac). No Node is needed. Restart,
and the Export PDF dialog offers the server's file as its primary button. Both routes were run on the `osx-arm64` archive with an empty environment and produced a PDF
(`200`, `%PDF-1.4`; details and the three failure states in [`pdf-export.md`](pdf-export.md)). A service runs as another account whose
browser cache is not yours: point `export.chromium.path` at the system Chrome (no cache involved), or install the browser as that account.

## If it does not start

[`troubleshooting.md`](troubleshooting.md). Check `<data>/logs/draughtsman.log` first.
