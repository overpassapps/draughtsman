# Troubleshooting

Symptom, cause, fix. The messages quoted are what a real instance printed when the fault was produced for this page, unless
a line says **unverified**. Start with the log (where it is depends on the install: [`admin-guide.md`](admin-guide.md#logs))
and `GET /api/health`, then find the symptom.

```sh
curl -s http://127.0.0.1:5180/api/health        # {"status":"ok"} ; "degraded" means the layout service is down
draughtsman --version                           # which build is this
```

## Troubleshooting an install

When sign-in does not stick, a save fails with `400`, everyone seems to share one lock-out, or you simply cannot tell what your server is doing,
work through these three, in this order. Each one works without the internet, and none needs you to read a log.

**1. Open Admin > Status > Connection check** (`/admin/connection`, administrators only; [`admin-guide.md`](admin-guide.md#connection-check)).
It tests the path between your browser and the server and says pass, warning or fail for each of these, with what it saw and the exact change
to make for nginx, Caddy, Cloudflare and `draughtsman.yaml`:

- **Reverse proxy and client address:** the direct peer, whether it is listed in `server.knownProxies`, the raw `X-Forwarded-Proto`, `X-Forwarded-For`, `X-Forwarded-Host` and `CF-Connecting-IP` it sent, the scheme the server believes, and the address the sign-in lock-out will count you as.
- **Secure cookies and sign-in:** `server.forceHttps` against the address in your browser. Over plain `http://` with `forceHttps` on, the browser throws the session cookie away: the sign-in form can sit on "Signing in…" for ever.
- **Host header and allowed hosts:** the `Host` the proxy sends, against `server.allowedHosts`.
- **Session cookie round trip, ETag and compression** (run by the page, in your browser): a test cookie set like the session cookie, and whether it comes back; whether an `ETag` survives the proxy (a CDN that weakens it to `W/"..."`, or strips it, is what made saves and renames fail with `400 Invalid If-Match header` on builds before the weak-tag fix, issue 63; this version accepts a weakened tag and the editor also sends the version in `X-Document-Version`, so saving needs no fix, but a script quoting a stripped `ETag` still has nothing to quote); whether anything compressed the response on the way (harmless; both are warnings, never failures).
- **Server clock and your clock:** a difference over two minutes is a warning; a server clock 30 days or more behind the browser's makes the session cookie expired as it is set, so no sign-in can stick.
- **Data folder and database, free disk space, this server:** whether the server's account can write the folder and the database file, how much room is left, and the version, build date, platform, and whether it is in a container.

Nothing secret is shown, and the page makes no call outside your own server. The same findings are summarised on **Admin > Status** (the peer address and a count of what passed, warned and failed).

**2. Run `draughtsman doctor`.** It checks what needs no network and no sign-in, and works **with the server stopped**, which is when you need it:

```sh
docker exec <container> draughtsman doctor        # Docker: as the image's own user (uid 10001), which is what the server is
draughtsman doctor                                # an archive install, as the account the service runs as (sudo -u draughtsman draughtsman doctor)
draughtsman doctor --verbose                      # also the raw facts behind each line
draughtsman doctor --json                         # the same findings as data
```

It reports the settings and database (migrations, integrity: what `config check` says), who owns the data folder and what mode it has **against the account
the server runs as** (it finds a running server's account on Linux, and assumes the image's uid `10001` in a container), whether the folder and database are writable
and the disk has room, the listening address and whether anything answers on it, `server.forceHttps` and `server.knownProxies` (including the proxy headers the running
server has seen from an address it does not trust), the key ring, the clock, and the account in use. It changes nothing except one empty file
it creates and deletes in the data folder. Exit `0` no problems, `1` warnings, `2` failures, `64` a bad command line.

Do not run it with `docker exec -u 0` for a permissions question: root can open any file, so a database the image's own user cannot write would look fine.
It says so when it is run as root, and compares the owners with uid `10001` instead.

**What it looks like.** This is the output in a real container (the published image, `docker exec <container> draughtsman doctor`) after a restore left `data.db` owned by root while the server runs as the image's own
user, uid `10001`, trimmed to the findings that matter (the lines in between are the rest of the report, all `OK`):

```text
$ docker exec draughtsman draughtsman doctor
Draughtsman doctor: version 0.1.0 (build 2026-10-06), Ubuntu 24.04.5 LTS Arm64
Data folder: /data
...
Data folder and database
  FAIL  The database (/data/data.db) cannot be written (UnauthorizedAccessException: Access to the
        path '/data/data.db' is denied.); that was tried as the account running this command. If
        that is the server's account, the server cannot save anything: /api/health reports degraded
        and a correct password is answered with an error instead of a session. This usually follows
        a restore or copy done as another account (root).
        /data/data.db is owned by uid 0 but this process runs as uid 10001.
        Fix (command):
          # As root, hand the database and its folder back to the server's account:
          # chown -R <server account> /data

...
Data folder ownership and permissions
  FAIL  Files the server needs are owned by the wrong account: data.db is owned by uid 0. The server
        (running as uid 10001) cannot write them, so it cannot save anything: /api/health reports
        degraded and a correct password is answered with an error instead of a session, while the
        page still loads. This usually follows a restore, a copy or a docker cp done as root.
        rw------- root (uid 0)                   4 KiB  data.db
        rw------- draughtsman (uid 10001)       32 KiB  data.db-shm
        rw------- draughtsman (uid 10001)    293.7 KiB  data.db-wal
        rwx------ draughtsman (uid 10001)          0 B  keys/
        Fix (command):
          # As root, hand everything back to the server's account:
          # chown -R 10001 /data
          # Docker: docker exec -u 0 <container> chown -R 10001:10001 /data

...
Result: 3 failure(s) and 0 warning(s). Exit code 2.
```

The same container, run as root with `docker exec -u 0`, says so and still compares the owners with uid `10001`; and it **does not open the database** while `data.db` is root's but its `-wal` and `-shm` files are the server's
(SQLite, run as root, would hand those two files to `data.db`'s owner, which would take them from the running server): it warns instead, and the ownership finding still names the fix. A healthy install exits `0`;
`server.forceHttps: true` with no `server.knownProxies` is one warning and exit `1`.


**3. Send us a support bundle.** One zip, which you read before you send it:

```sh
docker exec <container> draughtsman support-bundle --output /data/support-bundle.zip
docker cp <container>:/data/support-bundle.zip .
draughtsman support-bundle --output support-bundle.zip           # an archive install
```

On a read-only container `docker cp` cannot read a file from `/tmp` (a tmpfs there), so write the bundle to the data folder, as above, never to `/tmp`, or stream it out without `docker cp`:

```sh
docker exec <container> cat /data/support-bundle.zip > support-bundle.zip
```

It works with the server stopped, and never overwrites a file. Every file in it is plain text, and its `README.txt` lists exactly what is in it and what is not:

| File | What it holds |
| --- | --- |
| `system.txt` | Version, build date, platform, whether it is a container, the account, the names (not values) of the `DRAUGHTSMAN_*`, `ASPNETCORE_*` and `DOTNET_*` environment variables. |
| `doctor.txt` | The `doctor --verbose` output above. |
| `settings-effective.txt` | Every setting as this version reads it, with the **value shown only for settings on an allow-list of ones that cannot hold a secret** (ports, switches, hosts, paths, names of environment variables). Every other setting shows `(set)` or `(not set)`. Not `draughtsman.yaml` itself. |
| `health.txt` | Whether the server answers on this machine and its status word. |
| `log-tail.txt` | The last 2,000 lines (`--lines <n>`) of the log **file**, redacted. See below if you run without one. |
| `security-events.txt` | Counts of security events by type for 7 days, and the last 50, with each email cut to its first character and domain and each address cut to its /24 (IPv4) or /64 (IPv6). |
| `proxy-headers.txt` | Which proxy addresses sent `X-Forwarded-*` headers since the server started, how many requests, and whether the address was trusted. No header's content. |
| `data-folder-listing.txt` | Names, sizes, modes and owners in the data folder. No file's content; folders of assets, keys and mail are a count. |
| `migrations.txt` | The database migrations: applied, pending, or unknown to this build. |

It never contains the database (so no document, diagram or user), a password hash, a session cookie, an API token, a password-reset link, the key ring, a stored AI
key or SMTP password, the setup code, a Postgres connection string, or the contents of any file in the data folder. Redaction takes out an email's name,
everything after a word like `password`, `token`, `secret`, `api key`, `session`, `cookie` or `authorization`, `Bearer` credentials, a URL's user and password, the shapes
of API keys, tokens and JWTs, long random-looking strings and hex strings, and a document's title or an API token's name where the product itself logged one; and every secret the command can read on
the machine (an environment variable named for one, a password or key file the settings point at, the setup code) is removed from every file **by value**. That catches what it knows to look for: a free-form
log line could still hold a secret in a form no rule recognises, which is why you read `log-tail.txt` first.

**No log file?** The server writes one only when `logging.file.enabled: true` is set in `draughtsman.yaml` (it is off by default, and in Docker the log is the container's own). Without it,
`log-tail.txt` says so; send the container's log yourself: `docker logs <container> 2>&1 | tail -n 2000 > server.log`. To keep a log in the data folder from now on, set
`logging.file.enabled: true` and restart ([`admin-guide.md`](admin-guide.md#logs)).

### What the server logs about a refused save

A save, rename or restore refused over its version tag (`If-Match`) writes **one Warning**, with the route (never the document's id or the tag), the status, the reason and whether the tag was weak, at most once per
route per minute so a client in a retry loop cannot fill the log:

```text
warn: Draughtsman.Server.VersionTag[0]
      Document write refused: PUT /api/documents/{id} answered 412 (stale version); the If-Match tag was not weak. The document changed after the client read it (another editor or an agent
      saved first), so the client has to reload and merge. Not a fault unless it repeats for one editor.
```

The reasons are `missing If-Match` (428), `list of tags`, `wildcard` and `malformed tag` (400) and `stale version` (412: another writer saved first, not a fault unless it repeats for one editor).
A weakened tag (`W/"..."`, what a CDN makes of the server's strong one when it compresses a response) is **accepted**, because it names the same version; the server notes that once an hour, at Information, so the cause is on record without a refused save:

```text
info: Draughtsman.Server.VersionTag[0]
      accepted a weakened version tag from 198.51.100.7; a proxy or CDN is rewriting ETag (logged once an hour per address).
```

If a proxy sends `X-Forwarded-*`, `CF-Connecting-IP` or similar from an address that is not in `server.knownProxies`, the server logs one Warning an hour saying so and naming the address to add (`Forwarding headers (X-Forwarded-For and the like) arrived from ...`).
A sign-in that fails, is throttled or succeeds already writes a security event that says why ([`api.md`](api.md#security-events)); look for `sign_in.failed` (its detail says wrong password or unknown account) and `sign_in.throttled`.

## It will not start

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Failed to bind to address http://127.0.0.1:5180: address already in use.` | Another process (often a second copy of Draughtsman) holds the port. | Stop it, or change `server.port`, or pass `--urls=http://127.0.0.1:5181`. `lsof -nP -iTCP:5180 -sTCP:LISTEN` (Linux and macOS) or `Get-NetTCPConnection -LocalPort 5180` (Windows) names the owner. Note `ASPNETCORE_URLS` beats `server.port`. |
| `Draughtsman cannot start: it cannot write to the data folder /data, running as draughtsman (uid 10001). ...` and exit code `5` (older builds: an unhandled exception, `SQLite Error 14: 'unable to open database file'`, or `UnauthorizedAccessException` with a stack trace) | The account running the server cannot write the data folder, or `data.db` (or the `-wal` and `-shm` files SQLite keeps beside it) inside it, usually because a restore or copy was made as root or a Docker bind mount is owned by root. Nothing was changed and nothing is served. | Do what the line says: give the account the folder and everything in it (`chown -R 10001 <folder>` for the Docker image, whose uid is `10001`; the service account on Windows), mode `700`, and start again ([`install-docker.md`](install-docker.md#if-the-data-folder-is-not-writable)). |
| `Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:1 ... Connection refused` and the process exits | The Postgres connection string in `draughtsman.yaml` points at nothing. | Fix host, port and credentials; Postgres must be reachable from the server and accept the role; add `SSL Mode=Require` if the server demands TLS. Test with `psql` from the same machine. |
| `crit: ... This sqlite database was migrated by a newer version of Draughtsman than this one ...` and exit code `3` | You started an older version on a database a newer one already upgraded. | Start the newer version again, or restore the backup taken before the upgrade ([`upgrade-backup.md`](upgrade-backup.md#downgrade-going-back)). The older version changed nothing. |
| `crit: ... did not pass its checks: table documents had N rows before the migration ...` and exit code `4` | An upgrade's migration failed or lost rows, and the server **put the pre-upgrade copy back** (the message says so and names it). | The data is exactly as it was before that start. Start the previous version again, and report the failure with the log ([`upgrade-backup.md`](upgrade-backup.md#what-an-upgrade-does-and-undoes)). |
| `crit: ... was not started: the safety copy it takes first could not be made` and exit code `5` | The disk is full, the data folder is not writable, or `data.db` already fails SQLite's `integrity_check`, so the upgrade stopped before changing anything. | Free space or fix the permissions and start again; restore a backup if the file is damaged. To go ahead without a copy, take your own backup and set `storage.allowMigrationWithoutBackup: true` ([`upgrade-backup.md`](upgrade-backup.md#what-an-upgrade-does-and-undoes)). |
| `draughtsman config check` ends `would NOT start` | A `REFUSE` line says why: a database a newer version migrated (exit `3`), a damaged database, an unreadable `draughtsman.yaml`, a Postgres database that cannot be reached, no room for the safety copy. | Fix what the line says; the command changed nothing ([`upgrade-backup.md`](upgrade-backup.md#before-you-upgrade)). |
| `draughtsman.yaml: '...' is not a setting this version reads` in the log | A mistyped setting, one from a newer version, or one this version removed. It is ignored; the server started. | Fix or delete the line (the warning names the nearest real setting, or what replaced it). |
| After an upgrade, Admin > AI (or Admin > Email) says the stored key (or SMTP password) "cannot be read" | The page gives the reason. Most often the secret was written by a build older than this one, which tied it to the folder the program ran from, and the program has since moved to a folder nothing remembers (a program moved **and** upgraded in one step). This version no longer ties secrets to a folder, so it only happens once, on that first start. Otherwise the key ring's protection (the key-encryption key, the Keychain item) is not available, or the value is damaged. | Name the folder the OLD program ran from in `keyRing.legacyInstallPaths` (or `DRAUGHTSMAN_LEGACY_INSTALL_PATHS`) and restart: the secret is read and re-protected, and `draughtsman config check` shows `Stored secrets`. Or restore the key-encryption key. Or enter the secret again; it works and affects nothing else. Nothing was changed or dropped meanwhile ([`upgrade-backup.md`](upgrade-backup.md#where-to-put-the-new-version)). |
| `Failure processing application bundle. ... DOTNET_BUNDLE_EXTRACT_BASE_DIR is not set, and a read-write cache directory couldn't be created.` | The single-file executable unpacks native libraries at start and its account has no writable home folder (a service, `env -i`). | Set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a writable folder, or give the account a home. `service install` sets it on Linux and macOS. |
| macOS service exits at once with `Draughtsman cannot start: The macOS Keychain cannot be used to protect the key ring` (after a 20 second wait) | The account running the server has no login keychain (a LaunchDaemon, a missing or locked home folder). The server refuses to continue with an unprotected ring unless you choose that. | Give the service a key-encryption key ([`install-macos.md`](install-macos.md#3-run-it-as-a-service)): `keyRing.protection: key` and `keyRing.keyFile` (or `DRAUGHTSMAN_KEY_ENCRYPTION_KEY`). Or opt out deliberately with `keyRing.protection: none`. |
| "Cannot be opened because the developer cannot be verified" (macOS) or "Windows protected your PC" | The archives are not code-signed or notarised yet. | macOS: `xattr -dr com.apple.quarantine <folder>`. Windows: More info, Run anyway. |
| Docker: `exec: "draughtsman": executable file not found in $PATH` | The image predates the `draughtsman` wrapper on the path. | Pull a current image, or `docker exec <container> dotnet /app/draughtsman.dll <command>`. |
| Log line `The data folder ... is open to other users (mode 755); chmod 700 it unless that is intended.` | The folder (often a named Docker volume made by an older image, or one `restore` created) is group or world readable. | `chmod 700` it. Never `chmod 777`. |
| Log line `Key ring protection: The key ring is not protected ...` | Linux or Docker with no key-encryption key. | Set `DRAUGHTSMAN_KEY_ENCRYPTION_KEY` or `keyRing.keyFile` ([`admin-guide.md`](admin-guide.md#the-api-key-and-the-key-ring)). Harmless if you never store an AI key. |
| Log line `server.knownProxies entry proxy.internal (a hostname ...) was ignored` | Host names are not understood there. | List the proxy's IP address or a CIDR range. |
| Log line (at most once an hour) `Forwarding headers (X-Forwarded-For and the like) arrived from 172.17.0.1, which is not listed in server.knownProxies ...` | A reverse proxy is forwarding, but the server does not trust the address it connects from, so every visitor shares that address and one throttle budget. Behind Docker the peer is the bridge gateway, `172.17.0.1`, not `127.0.0.1`. | List the address the line names in `server.knownProxies` and restart ([`deployment.md`](deployment.md#serverknownproxies), [`install-docker.md`](install-docker.md#nginx-or-another-proxy-on-the-docker-host)). |

## I can reach the server but not use it

| Symptom | Cause | Fix |
| --- | --- | --- |
| Browser: `Bad Request - Invalid Hostname` (HTTP 400), but `/api/health` works on `localhost` | The Host header is not allowed. A loopback server answers only to `localhost`; a proxy forwards the public name. | Name it in `server.allowedHosts`, or list the proxy in `server.knownProxies` ([`deployment.md`](deployment.md#serverallowedhosts)). |
| `GET /` answers `401` as plain text, and the log says `No web build found; serving the API only` | The web app is not next to the executable: you are running from a build tree without `npm run build` in `web/`. | Use a release archive or image, or build `web/`. |
| The page says `Signed in, but your browser did not keep the session. If this address starts with http:// while forceHttps is on, use https://.` (older builds: the button stays on `Signing in…` for ever, or you are signed out at once; cookies never stick). The server's log shows `sign_in.succeeded` | The server accepted the password and issued the session cookie, but the browser threw it away. `server.forceHttps: true` makes the cookie `Secure`, and a browser discards a `Secure` cookie from an insecure page (Chrome is lenient on `localhost`, Safari is not), so this happens when you browse over plain `http://` (or `http://<ip>:5180`), or the proxy loses `https`, or the server's clock is more than 30 days behind the browser's. The password is right. | Browse over `https://` (the same address with `https`). Or turn `forceHttps` off until TLS works. Check the proxy sends `X-Forwarded-Proto` and is listed in `server.knownProxies`. After a first run, the page that follows is the sign-in page carrying the same message: the administrator exists, sign in over `https://`. |
| `503 This instance cannot save changes` on sign-in (the right password; a wrong one is still `401`), or on any request that writes; unless the cause is a full disk, `/api/health` says `"status": "degraded"` and, signed in as an administrator, `"storageWritable": false` | The database opens and reads but cannot be written: `data.db` or its `-wal`/`-shm` files owned by another account (a restore or `docker cp` made as root), a read-only mount, a full disk, or a Postgres role or replica that may not write. The log says which, once, with the account and the data folder. | `chown -R <uid> <data folder>` (Docker image: `10001`), free disk space or remount read-write, then restart the server (a connection opened while the file was read-only stays read-only until then) ([`install-docker.md`](install-docker.md#if-the-data-folder-is-not-writable)). |
| Sign-in answers `429 Too many failed attempts from this account or network address; try again in 15 minutes.` even with the right password | Five failures for the account from that address, or twenty from the address, in 15 minutes. Verified: five wrong passwords, then the right one, still `429`. Behind an unlisted proxy everybody shares one address. | Wait 15 minutes or restart the server (the counters are in memory). Fix `server.knownProxies` so each person has an address (the log line above says when that is the case). |
| `403 CSRF validation failed - Missing or invalid X-XSRF-TOKEN header.` | A script using a **browser session** cookie did a `POST`, `PUT`, `PATCH` or `DELETE` without the CSRF header. | Use an API token (`Authorization: Bearer dst_...`), which needs no header. |
| `401` with a token that worked | The token expired (90 days by default) or was revoked, or its owner's password was reset (that revokes tokens), or the account was deactivated. | Create a new token under **Tokens** ([`admin-guide.md`](admin-guide.md#api-tokens)). The `401` never says which. |
| `403 Read-only token` | The token is read-only and the request writes. | Create a full-access token. |
| `413` from a proxy when saving or uploading | The proxy's body limit (nginx: 1 MB) is below the server's (10 MiB). | `client_max_body_size 12m;` ([`deployment.md`](deployment.md#what-a-proxy-must-do)). Draughtsman's own `413` is a body over `limits.maxRequestBodyBytes` or `limits.maxDocumentBytes`. |
| `504` or a dropped connection when generating a diagram from text | The proxy gave up waiting for the model (nginx 60 s, IIS ARR 30 s). | Raise the proxy's timeout to at least 330 seconds. |
| A document card on the Documents page shows the diagram type's symbol, or a plain grey diagram, instead of a picture | The thumbnail request failed or was over a limit (the symbol, and the next visit tries again), or the diagram is too large to draw (the grey placeholder; over 400 nodes and edges it is plain boxes, over 6,000 a placeholder), or the layout engine is not running (the grey placeholder, until it is). | `GET /api/documents/<id>/thumbnail` says which: the `X-Draughtsman-Thumbnail` header is `full`, `simplified`, `empty`, `too-large` or `unavailable` ([`api.md`](api.md#get-apidocumentsidthumbnail)); [Status](admin-guide.md#status) shows the sidecar. |
| `429` with a `Retry-After` header on PDF, layout, import, AI, MCP or thumbnail calls | A `limits` rate or concurrency cap. | Slow the caller, or raise `limits.*` in `draughtsman.yaml` ([`draughtsman.example.yaml`](draughtsman.example.yaml)). |
| Everyone shows the same IP in the security log | The proxy is not in `server.knownProxies`, or is not sending `X-Forwarded-For`. | [`deployment.md`](deployment.md#serverknownproxies). |
| First-run page: `Invalid setup code` (403) | Wrong code, or the server restarted and issued a new one. | Read the newest `First-run setup code:` in the log ([`admin-guide.md`](admin-guide.md#first-run-and-the-setup-code)). On a **Windows service** it is not in any log: run `draughtsman first-run-code --DRAUGHTSMAN_DATA "C:\ProgramData\Draughtsman"` from an elevated prompt. |
| Windows service: no setup code anywhere, the Application log has only an Event ID 0 error saying the source `draughtsman` was not found | An older build did not register the log source and wrote the code nowhere a person could read it. | Install a current build and run `service install` again (it registers the source and keeps the code in `setup-code.txt`, readable by Administrators only), or `New-EventLog -LogName Application -Source draughtsman` and restart. |
| Windows: `draughtsman cannot start: Windows DPAPI cannot be used to protect the key ring` (console and Application log), or sign-in answered `500` on an older build | The logon running the server has no DPAPI master key: an SSH public-key session, a scheduled task run without a stored password, `runas /netonly`. A service as `LocalService` and a signed-in console user have one. | Give it a key-encryption key (`DRAUGHTSMAN_KEY_ENCRYPTION_KEY`, or `keyRing.protection: key` and `keyRing.keyFile`), or opt out deliberately with `keyRing.protection: none`. |
| Windows: `service install` says the executable "is inside the user profile folder", or an older build created the service and then `sc start` failed with `Access is denied` | `LocalService` cannot read `C:\Users\<you>`. | Unpack to `C:\Program Files\Draughtsman` (or any folder outside `C:\Users`) and run `service install` from there. An older build left a stopped service behind: `draughtsman.exe service uninstall` first. |
| Windows: log warning `The data folder ... is open to other users on this machine (BUILTIN\Users can read or change it)` | The folder was made by hand under `C:\ProgramData`, which gives every local user read and create rights, or an old `service install` only added to them. | Run the `icacls` command the warning prints, or `service uninstall` then `service install` (it cuts inheritance and leaves SYSTEM, Administrators and the service account). |
| First-run is gone, and nobody remembers the administrator's password | First run closes for good once one account exists. | `draughtsman reset-admin <email> --generate` on the host ([`admin-guide.md`](admin-guide.md#lost-administrator-password)). |
| Saving or renaming in the editor fails with `400 Invalid If-Match header` ("must be a strong entity tag") while opening and creating work, only behind a CDN, WAF or gateway | The intermediary compressed the response and weakened its `ETag` (`W/"dv1:…"`); builds before the fix refused a weak tag. | **Upgrade.** A current build accepts a weakened tag and also sends the version as `X-Document-Version`, so no CDN setting is needed. Switching compression off for the hostname is not a fix (it did not keep the strong `ETag` in our one test behind Cloudflare Free); see [`deployment.md`](deployment.md#behind-a-cdn-or-waf). |
| `412 Document has changed` or `428 Base version required` when saving through the API | A save must say which version it was made from (`If-Match`); another writer saved in between. | Read the document again, take its `ETag`, redo the change on it ([`api.md`](api.md#base-versions)). In the editor, a change to something you did not touch is taken in and saved automatically; a change to the same thing you changed raises the "Out of date" banner: choose Reload theirs, Keep mine or Save mine as a new document. After Reload theirs your version is kept in the browser for the rest of that visit and for a week, but it is not offered again on later opens. |
| A deleted diagram is missing | Deleting moves it to the bin; it is not in the list. | **Bin** on the home page, then Restore ([`admin-guide.md`](admin-guide.md#the-bin-and-version-history)). It stays `retention.binDays` days. |
| A colleague deleted or overwrote my diagram | Every Editor can change any diagram (one-organisation trust). | Restore from the bin, or from **History** (every agent edit and every restore stores a checkpoint). |

## Something is degraded

| Symptom | Cause | Fix |
| --- | --- | --- |
| `/api/health` says `{"status":"degraded"}`; **Status** shows "Layout service: Not running"; layout answers `503 The layout engine is not available.` | The Node sidecar did not start. The log says why, for example `Sidecar unavailable: sidecar.nodePath '/nonexistent/node' was not found. Give the full path to a Node 22 executable or leave it empty to use PATH.` Editing and saving still work; layout, Tidy, SQL import, AI generation and exported connector routes do not. | A release archive and the Docker image carry Node: do not set `sidecar.nodePath` unless you mean to. Running from source needs Node 22 on `PATH`. The server restarts the sidecar with backoff; restart the server once the cause is fixed. |
| `/api/health` says `{"status":"degraded"}` and **Status** and the sidecar are fine; signed in as an administrator, health shows `"storageWritable": false` | The database refuses writes outright: `data.db` or its `-wal`/`-shm` files owned by another account, a read-only mount, a Postgres role or replica that may not write. (A full disk does not show here; it shows as the `503` above.) | `chown -R <uid> <data folder>`, free space or remount read-write, then restart ([`install-docker.md`](install-docker.md#if-the-data-folder-is-not-writable)). |
| `GET .../pdf` answers `501 PDF export is not available` (the editor's File > Export PDF… still works: it prints from the browser, and an administrator sees a note about the server component): `server-side PDF export is enabled, but no Chromium browser was found for Playwright 1.63.0. Install it on the server with npx playwright@1.63.0 install chromium ... or set export.chromium.path ...` (`export.pdfProblem` is `browser`) | PDF needs a headless Chromium or Chrome, which is not bundled; the driver is fine. | Set `export.chromium.path` to a Chrome, Chromium or Edge executable, or install Playwright's browser with the **pinned** command the message prints (an unpinned one installs the wrong revision); in a release archive the shipped driver does it with no Node on the host: `./.playwright/node/<platform>/node ./.playwright/package/cli.js install chromium` ([`pdf-export.md`](pdf-export.md)). In Docker build with `INSTALL_CHROMIUM=true`. It is rechecked within 30 seconds, no restart needed. |
| The same 501, but the message says `Playwright's driver is not usable on this server: ... is missing` (or `is not executable`, or `could not be started`), `export.pdfProblem` is `driver`, and the dialog says "its driver is not usable" | The `.playwright` folder beside the executable (Node runtime plus the playwright package) is missing, incomplete, or its Node file will not run, so no browser was ever tried. A damaged or partly copied install, or an archive unpacked by a tool that lost file modes (the server repairs the execute bit itself when it may write the file). **Installing Chromium will not help.** | Unpack the release archive again over the install, keeping `.playwright/` whole, and restart. On macOS remove the quarantine flag from a downloaded copy (`xattr -dr com.apple.quarantine <folder>`). |
| Disk space under `~/.net/draughtsman/` grows by a folder per upgrade | The single-file executable unpacks one SQLite library (about 2 MB; about 137 MB in builds before the PDF driver was moved out of the executable) per distinct build and never cleans up. | Stop the server, delete `~/.net/draughtsman`, start it again. To keep it inside the install, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` before the executable starts ([`install.md`](install.md#what-the-executable-leaves-on-disk)). |
| `504 PDF export timed out`, or health says `PDF export did not finish within 60 seconds, so the browser it was using was stopped` | The browser started but never printed (found with Microsoft Edge under a Windows service). The server stopped it and its driver by process id and nothing else; no process is left behind. | Use Playwright's Chromium instead of Edge ([`install-windows.md`](install-windows.md#3-install-the-service)), or raise `export.chromium.timeoutSeconds` if the diagram is very large. |
| **Status** shows PDF export as unavailable with a reason while `export.chromium.enabled: true` | It reports the same launch probe `/api/health` uses (earlier builds showed the configuration only, and said OK with no browser installed). The reason says whether the driver or the browser is the matter. | Follow the reason; see the two rows above. |
| No AI panel in the editor | No provider is configured: the panel is hidden, not broken. | **AI** in the admin pages. |
| AI **Test connection**: `Could not reach 127.0.0.1:1. Check the endpoint, and that the server is running and reachable from this machine.` | Wrong endpoint, model server down, or a firewall. In Docker `localhost` is the container. | Use `http://host.docker.internal:11434` for a host Ollama ([`admin-guide.md`](admin-guide.md#local-models)). |
| AI **Test connection**: `No API key is set. Paste one here, or set the environment variable DRAUGHTSMAN_AI_KEY.` | A keyed provider with no key. | Paste a key, or set the variable. |
| After a restore, the AI page says the stored key must be entered again | The key ring was not restored (not in a backup unless `--include-secrets`) or its protection is missing. A **move** of the program no longer does this: the stored key and SMTP password do not depend on the folder the program runs from. | Enter the key again; it works. Restoring the original protection makes the old key readable again. A backup taken by a build older than this one and restored with `--include-secrets` onto a program at another path needs the old folder named once, as above. |
| Logs are huge | Framework logging at `Information`. | `logging.frameworkLevel: Warning` (the default) and read [`admin-guide.md`](admin-guide.md#logs). |

## Forgotten passwords and email

| Symptom | Cause | Fix |
| --- | --- | --- |
| The sign-in page has no **Forgot password?**, only "Ask your administrator for a reset link" | Emailed links are not on: `email.provider` is not set (or is broken), `server.publicUrl` is not set, or `auth.passwordReset.selfService` is `false`. | Open **Admin, Email**: it lists what is missing. Meanwhile an administrator can create a link under **Users, Create reset link**. |
| **Forgot password?** says a link was sent but nothing arrives | The answer is the same for every address on purpose, so it does not tell you. The address may have no active account; the mail server may have refused it; at most three emails go to one address in 15 minutes. | Look in the **Security** log: *Password reset requested*, then *Reset email sent* (it was handed to the mail server) or *Reset email could not be sent* (with the reason). Check the spam folder. |
| **Send a test email**: *Could not connect to host:587: ConnectionRefused* | Wrong host or port, or a firewall. | `email.smtp.host` and `email.smtp.port`; test from the server with `nc -vz host 587`. |
| *The secure connection (STARTTLS) to … failed: The remote certificate is invalid* | The mail server's certificate is not trusted by this host (self-signed, an unknown issuer, or a name that does not match the host you configured). | Use the name on the certificate for `email.smtp.host`, or install the issuing certificate on this host. There is no setting to skip the check. |
| *did not accept the user name or password* | Wrong user or password, or the server needs an app-specific password. | Check `email.smtp.user` and where the password is read from (shown on the Email page). |
| *The mail server … requires STARTTLS first, and email.smtp.tls is not "starttls"* | `tls: none` against a server that insists on encryption. | `email.smtp.tls: starttls`, port 587. |
| Email page: *email.smtp.tls is "none" but email.smtp.user is set* | Draughtsman will not send a password to another machine unencrypted. | Use `starttls`, or drop the user name for a relay that needs none. |
| A link says *This reset link is not valid. It may have expired or already been used.* | It was used, it expired (60 minutes for an emailed link, 24 hours for an administrator's), a newer link replaced it, the person's password was changed another way, the account was deactivated, or it was copied with a character missing. A mail client that wraps a long line can break a link. | Ask for a new one. The **Security** log's *Reset link refused* row says which class it was. |
| A link opens a page that says "This page opens from a reset link" | The link's `#token=…` part did not arrive (the address was cut short), or the page was reloaded after it had read the link (it removes the secret from the address bar on purpose). | Open the link again from the email or message. |
| Reset page or sign-in says `Too many attempts` | More than ten requests for links from one network address in 15 minutes (three for one email), or twenty failed uses of a link. Counters are in memory. | Wait 15 minutes, use another network, or restart the server. Nothing was locked on any account. |

## An AI agent cannot connect (MCP)

The symptom in Claude Code is usually `draughtsman: failed` with a message such as `JSON Parse error: Unrecognized token ' '` or
`Dynamic Client Registration rejected (HTTP 401)`. Work out which transport you configured (`claude mcp list`, or the `mcpServers`
entry in `~/.claude.json`).

| Configuration | Symptom | Cause | Fix |
| --- | --- | --- | --- |
| HTTP (`"type": "http"`, URL ending `/mcp`) with **no** `Authorization` header | `Dynamic Client Registration rejected (HTTP 401)` or `401` | Every route including `/mcp` needs an API token. Claude Code prefixes the server's hint with its own OAuth wording, which is not a sign that OAuth is expected: this server does not support it. | `claude mcp add --transport http draughtsman https://host/mcp --header "Authorization: Bearer dst_..."`. Create the token under **Tokens**. The `401` carries `WWW-Authenticate: Bearer` and `Authentication required. Send an API token as 'Authorization: Bearer <token>' ...` (verified). |
| HTTP, token given | `401` later | The token expired, was revoked, or its owner's password was reset. | New token. |
| HTTP, read-only token | Reads work, `create_document` and `patch_document` refuse | The token is read-only. | Full-access token. |
| HTTP | Connection refused, wrong port, TLS error | URL, port or scheme wrong; or the server is on a different port from `5180`; or a proxy certificate the client does not trust. | `curl -s https://host/api/health` first. |
| HTTP | Hangs or is cut after 30 or 60 seconds | A proxy timeout or response buffering. | Raise the timeout and turn buffering off for `/mcp` ([`deployment.md`](deployment.md#nginx)). |
| HTTP | `/mcp` is `404` | `mcp.enabled: false` in `draughtsman.yaml`. | Set it to `true` and restart. |
| HTTP | `429` | More than `limits.mcpPerUserPerMinute` (300) calls a minute for that user. | Slow the agent or raise the limit. |
| stdio via `dotnet run` | `JSON Parse error: Unrecognized token 'U'` (or similar) at start-up | Plain `dotnet run` prints `Using launch settings from ...` on standard output before the server starts, which corrupts the JSON-RPC stream. Verified: that line is the first thing printed. | Use `dotnet run --no-launch-profile --project src/Draughtsman.Server -- mcp --stdio`, or better the published binary: `draughtsman mcp --stdio` (verified: only the JSON reply on stdout). |
| stdio | Starts but sees no diagrams | A different data folder from the web server's. | Pass the same `DRAUGHTSMAN_DATA` (`claude mcp add draughtsman -e DRAUGHTSMAN_DATA=/path/to/data -- /path/to/draughtsman mcp --stdio`). |
| Either | Errors from a tool call | The tool says what is wrong and how to correct it (missing argument, unknown op, unknown id). | [`SKILL.md`](../SKILL.md) lists every tool's arguments. |
| An old trial binary | Errors that the current docs do not match | An older build than the documentation. | `draughtsman --version`; use the current build. |

`draughtsman mcp --stdio` needs no token: it never opens a port, and it reads the data folder directly, so it is as trusted as
anyone who can run it on that machine.

## Backup and restore

| Symptom | Fix |
| --- | --- |
| `Restore refused: The data folder ... is not empty (data.db). Nothing was changed.` | Restore into an empty folder, or add `--overwrite` (the old files go to `pre-restore-<time>/`). |
| `draughtsman backup` on Postgres prints a `pg_dump` command and exits `1` | That is the supported procedure: run the command ([`upgrade-backup.md`](upgrade-backup.md#postgres)). |
| A restored instance has no AI key and new sign-in sessions are fine | Expected: the key ring is not in a backup by default. Enter the key again. |

## Still stuck

Collect: the output of `draughtsman --version`, `GET /api/health` (as an administrator, for the detail), the last 100 log lines
(the log never contains passwords, tokens or API keys), and your `draughtsman.yaml` with the connection string and any secrets
removed. Send them to the support address on the **About** page.

Testers and release builders: the steps above (sign in, save, rename, the Connection check, `doctor`) are also run as one scripted journey against every supported install layout
(Docker with and without a proxy, the release archives, an upgrade) by the install matrix, [`testing-installs.md`](testing-installs.md).
