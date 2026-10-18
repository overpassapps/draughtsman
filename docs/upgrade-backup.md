# Backup, restore, upgrade and downgrade

This is the operator's page for getting data out of a Draughtsman instance, putting it back, and moving between versions.
The contract for each command and endpoint is in [`api.md`](api.md) (Storage, Backup and restore, Admin export); this
page is the procedure. Every command and output below was run against a real instance, except where a line says
**unverified**.

In a container, wherever a command below says `draughtsman ...`, run `docker exec <container> draughtsman ...` (the long form,
`dotnet /app/draughtsman.dll ...`, works too). Everything below assumes the data folder is where the server uses it: set `DRAUGHTSMAN_DATA=<folder>` (or `--DRAUGHTSMAN_DATA=<folder>`) for the command if it is not the
default.

## The guarantee

**Upgrading Draughtsman does not wipe or corrupt your data, and when an upgrade fails it is undone.** That sentence is backed by tests that run on
every build, and the places it stops being backed are listed below it. Nothing here is a promise made on trust.

What is **proven on every build** (`tests/upgrade-fixtures/README.md`):

- The repository holds the data folder of **seven earlier builds**: the first commit ever pushed (`9e22f56`) and six commits that changed the schema or the
  data folder (key ring protection and an early licence file; the security log and token lifetimes; the document ETag; the bin; password recovery; email settings saved in the browser). Each was
  made by running that build and filling it through its own API with users and passwords, API tokens (plain, expiring, read-only, revoked), documents of
  every type including a 2,000-node diagram and a fly-through path, versions and named snapshots, a bin, branding, organisation themes, AI settings with a
  stored API key and SMTP password, a licence file (a data folder from a build that read one still starts, and the file is left alone) and security events.
- On every test run the **current** server is started on a copy of each (SQLite always, Postgres when a database is available, as it is in CI) and every document
  is compared with what the old build served (canonical JSON hash and printed DSL hash; the list of differences an upgrade is allowed to make is
  `tests/upgrade-fixtures/normalisations.json`, and it is empty), every user signs in with the same password, every token still authenticates with the same scope and
  expiry (a token from before token lifetimes keeps working), versions, snapshots, the bin, images, branding, themes, the AI settings and the security
  log are intact, no table has fewer rows than before, and the instance can then write.
- The key ring an older build protected opens, and a plain one is protected in place without losing the stored AI key.
- The stored AI key and SMTP password an older build tied to its install folder are read from a **different** folder (the old one named, or found beside the new one), re-protected under the pinned
  application name with the earlier file kept whole in `backups/secrets-<time>/`, and then read after a **second** move to yet another folder with no fallback at all. A secret nothing can read is
  reported (which, why, what to do) and never changed. The same holds for a ring protected by a key-encryption key, the Keychain (through a stand-in for it, off macOS), a certificate or DPAPI (on the Windows CI leg only; the discriminator is not in the ring files, and
  the ring is byte for byte what it was), for a corrupted value, a ring with no secrets, a backup taken before the change and restored after it, and the Docker image's `/app`.
- The pre-upgrade copy is a whole database on the old schema, and putting it back as described under [Going back](#downgrade-going-back) restores the old data
  folder byte for byte. A migration that loses data on a real earlier build's database is undone automatically, again byte for byte.
- No migration can silently destroy data: a test fails on any drop, rename or narrowing, and on raw SQL that deletes, unless a written justification and a fixture that
  proves the data survives are on file ([What an upgrade may change](#what-an-upgrade-may-change)).

What was **run on real binaries** (not only in tests), on 2026-10-01 (`scripts/upgrade-path-proof.sh` repeats the first two):

- **An archive swap.** A release archive built from `257d224` was filled through its API (144 checks passed). Its program folder was then replaced **in place** by an archive built from this
  branch, `config check` run (exit `0`, `data.db` byte for byte unchanged), and the new version started: it took its safety copy (byte for byte the old database), applied the two pending migrations,
  passed its post-migration check, and served everything the old build had (143 checks, including the stored AI key). The old program was then put back: it **refused** the migrated database
  (exit code `3`) and wrote nothing (`data.db` unchanged byte for byte); after the safety copy was put back as documented, it started and served all 143 checks again.
- **A Docker volume across two images**, the same sequence on one `/data` volume: config check, upgrade, the old image refusing the volume (exit `3`, nothing written), the documented rollback, the old
  image serving everything again.
- **Stored secrets across a move, on release archives** (`scripts/dataprotection-pin-proof.sh`, 28 checks). An archive built before the pin was filled through its API (a stored AI key and a saved SMTP password among 153
  checks) and a backup of it taken with `--include-secrets`. An archive with the pin, unpacked in a **different folder beside the old one**: `config check` said both secrets could be read and would be
  re-protected, and changed nothing; the server started and passed 152 checks including both secrets, `ai/settings.json` was rewritten, the earlier `ai/` and `email/` files sat byte for byte in
  `backups/secrets-<time>/`, and `keys/install-paths.json` held the one folder it ran from (no secret in it). With the old program folder **deleted** and the new one **moved to a third folder**, nothing named, both
  secrets were `current`: nothing written, no second backup, `config check` agreeing. A pristine copy of the old data with the old folder **unknown** (deleted, nothing recording it) was reported by the admin pages and by
  `config check` (both secrets, why, what to do), the server still started and served the rest (150 checks), and both settings files were byte for byte untouched with no backup made; naming the deleted folder in
  `DRAUGHTSMAN_LEGACY_INSTALL_PATHS` then read both and re-protected them. The old binary's backup, restored by the new binary, behaved the same way. The **old program on the re-protected data** showed both secrets as needing to be
  entered again until the `backups/secrets-<time>/` copies were put back as under [Going back](#downgrade-going-back), after which it served all 152 checks again.
- **The same on a Docker volume** (`scripts/dataprotection-pin-proof-docker.sh`, 11 checks): the old image, then the new image on the same `/data` volume (the program is `/app/` in both, which is what was recorded): both secrets
  re-protected at the first start, `current` in a fresh container after it with nothing written, and the old image reading them again once the backup copies were put back.
- **Postgres.** A Postgres database written by `257d224` was migrated by this build (the notice above printed first), then rolled back from the `pg_dump` taken beforehand; `pg_restore --clean` left it half
  restored (hence the advice above), and emptying the schema and restoring into it brought `257d224` back with 132 checks passing.
- **Failures.** A data folder the account cannot write (the program had crashed with an unhandled exception; it now refuses, exit code `5`) and an upgrade whose migration deletes rows (in the tests,
  on each fixture: undone, the database byte for byte as it was).

What is **not** covered, so you can decide what to do about it:

- **Postgres has no automatic rollback.** The server cannot snapshot your database, so before every upgrade of a Postgres instance you take a `pg_dump`, and
  going back means restoring it. The migrations themselves are tested against real populated Postgres databases; the safety net around them is yours.
- **A secret an older build tied to its install folder, when the program moved at the same time as it was upgraded and nothing records where it was.** From this version on the stored AI key
  and the saved SMTP password are protected by the key ring alone, so a program at any path reads them (tested, below). A data folder an **earlier** build wrote has them bound to the folder
  that build ran from, and that folder is part of the encryption: it cannot be recovered from the data, only guessed or told. This version tries the folder it runs from, the folders it has
  recorded, the folders you name, the folder the data folder sits in, the folders beside this one, old log files and the recommended install locations, reads the secret, and
  re-protects it at the first start. If every guess fails (the old folder is gone and the program moved), nothing is changed or dropped: Admin > AI, Admin > Email and `config check` say which
  secret, why and what to do, and naming the old folder once (`keyRing.legacyInstallPaths`) brings it back. See [Where to put the new version](#where-to-put-the-new-version).
- **Browser sessions** are rows in the database and carry over, but a browser holding the cookie of a build whose cookie name differs (the session cookie became
  `__Host-ds_session` over HTTPS) signs in again. Harmless; not tested.
- **A data folder edited by hand**, a folder written by a build **before** the first pushed commit (none was ever released), **SQL Server** (not supported yet), and the
  service managers on Windows and Linux (**unverified**, as in [`install.md`](install.md)).
- **Databases that already fail SQLite's `integrity_check`.** The upgrade refuses to touch them (its copy would be as damaged): restore a backup first.
- A **customer's own edits to files the server owns** (the `data.db` itself, `ai/settings.json`, the key ring).

## The one rule

**Do not back up a running SQLite instance by copying `data.db`.** SQLite runs in write-ahead-log mode: the newest commits
sit in `data.db-wal` until a checkpoint folds them into `data.db`, and the editor saves every couple of seconds while anyone
is editing. A live instance's `data.db` can be 4 KB beside a 240 KB log, so:

- a copy of `data.db` **alone** is a consistent database of *before* every save still in the log: it opens, looks fine, and is
  missing recent work;
- a copy of the folder (`cp -r data/`) **spans** whatever happens while it runs: a checkpoint in the middle builds the main file
  from two moments, and the result can be a database that never existed. The bigger the database, the longer the window.

`BackupConsistencyTests` reproduces all of this: a copy of `data.db` alone that lacks a committed save, a copy across a checkpoint
that is torn, and a continuous-writer run where, on an ~80 MB database, 4 of 23 `cp` copies of the three files were torn or behind
the last commit (2 and 2) while all 23 `draughtsman backup` snapshots were whole and current. (On a small database the same run
finds no bad copy in a few seconds: the window is short, not absent.) A copy taken with the server **stopped** is fine; so is
`draughtsman backup`, which does not need the server stopped.

## What is in a backup

`draughtsman backup` writes one zip (`manifest.json`, `README.txt` and everything under `data/`):

| In the bundle | Not in it (by default) |
| --- | --- |
| The database: every diagram (including those in the bin), its versions and checkpoints, the document audit trail, users (with Argon2id password hashes), sessions, API token hashes, the security log | `keys/`: the key ring that encrypts the stored AI key |
| Uploaded images (`assets/`) | `ai/` and `email/`: the stored AI and email settings, including the encrypted API key and SMTP password |
| `draughtsman.yaml` | `backups/`: the key ring's own backups |
| Branding (`branding/`), organisation themes (`themes/`), a chrome theme file kept in the data folder | The server's log files (`logs/`) and old `pre-migration` and `pre-restore` copies |
| `manifest.json`: version, time, provider, the newest applied migration, row counts, and the size and SHA-256 of every file | |

Consequences, so nobody finds them out the hard way:

- **A restored instance has a new key ring and no AI key.** The administrator enters the API key again under **AI**.
  Sessions and API tokens live in the database and carry over. Add `--include-secrets` to keep the ring and the stored key;
  the file is then named `...-SENSITIVE.zip` and says so inside. Treat it like a password vault. A ring protected by DPAPI, the
  macOS Keychain or a key-encryption key opens only where that protection is available: keep the key-encryption key in your
  secret store, not in the bundle.
- **The bundle holds password hashes** (and, on SQLite, the whole database). It is written owner-only; keep it somewhere
  access-controlled and copy it off the machine.
- **The log files and the server's own process state are not in it.** There is nothing else to restore.
- **Postgres is a different procedure** (below): the bundle then holds only the data-folder files.

## Back up (SQLite, the default)

```sh
draughtsman backup --out /srv/backups/                 # draughtsman-backup-<time>.zip (+ .sha256) in that folder
draughtsman backup --out /srv/backups/nightly.zip      # an exact file name; never overwrites
docker exec draughtsman draughtsman backup --out /data/backups/   # in the image; the data folder is /data
```

Output, run against a server that was open and being used (verified):

```
Backup written: /srv/backups/draughtsman-backup-20260930235752.zip
  1 files, 0.0 MiB; 9 documents, 2 users, 0 assets; schema 20260930222757_AddDocumentEtag
  sha256 c6a07605b7a54ccbfe73e5242fe5895a414e3d67775c8e6f5ba8f28c062902cf (also in /srv/backups/draughtsman-backup-20260930235752.zip.sha256)
  The key ring (keys/) and the AI and email settings (ai/, email/) are not in it; add --include-secrets if they must survive a restore.
```

The server does not need to be stopped. Exit `0` done, `1` refused or failed (nothing changed), `2` usage. The files are
`0600`.

**In Docker, a backup written to `/data/backups/` is on the same volume as the data.** It protects against a mistake, not
against losing the volume or the machine. Mount a second folder (`-v /srv/backups:/backups`) and write there, or copy the
bundle out with `docker cp` straight afterwards. **On systemd** the data folder belongs to a dynamic user: run the command with
`sudo` (unverified).

Scheduling is yours (cron, a systemd timer, Task Scheduler, a CI job). Run the command, keep the `.sha256`, and copy both
off the machine:

```sh
# /etc/cron.d/draughtsman: nightly at 02:30, keep 14 days (unverified: cron was not run here)
30 2 * * *  root  DRAUGHTSMAN_DATA=/var/lib/draughtsman /opt/draughtsman/draughtsman backup --out /srv/backups/ && find /srv/backups -name 'draughtsman-backup-*' -mtime +14 -delete
```

Check a bundle without restoring it by comparing its checksum with the `.sha256` file:

```sh
cd /srv/backups && shasum -a 256 -c draughtsman-backup-20260930235752.zip.sha256     # sha256sum -c on Linux
```

**Test a restore** into a scratch folder at least once, before you need it.

## Restore

Stop the server, then:

```sh
draughtsman restore /srv/backups/draughtsman-backup-20260930235752.zip --DRAUGHTSMAN_DATA=/var/lib/draughtsman
```

```
Restored 9 documents, 2 users and 0 assets into /var/lib/draughtsman (backup made 2026-09-30 23:57:53Z by Draughtsman 0.1.0).
  The bundle has no key ring or AI settings: start the server, then re-enter the AI API key under Admin > AI if one was configured. Sign-in sessions and API tokens, which live in the database, carry over.
  Start the server against this folder. A backup from an older version is migrated on the first start, with its usual pre-migration copy.
```

It verifies everything before it writes anything (the bundle's checksum, the manifest, every file's size and SHA-256, that
no unlisted or unsafe path is in the zip, that this version knows the bundle's schema, and that the database passes SQLite's
integrity check and holds the manifest's document count). A bad bundle is refused and the data folder is left untouched.

- An **empty or new** data folder is the normal case. A folder `restore` creates is not made private (checked: `755`, the
  database `644`); the server tightens the files at its first start, and warns about the folder, so create the folder first
  with `mkdir -m 700` or `chmod 700` it.
- A folder that **is not empty** is refused. Output (verified): `Restore refused: The data folder /... is not empty (data.db).
  Nothing was changed. Restore into an empty folder (and point the server at it), or re-run with --overwrite to set the
  current database and files aside in a pre-restore-<time> folder first.` and exit `1`.
- `--overwrite` moves the current database (and any `-wal`/`-shm`/`-journal`), assets, branding, themes and settings
  into `<data>/pre-restore-<time>/` first, so nothing is lost; delete that folder when you are happy.
- A bundle from an **older** version restores fine; the first start migrates it (with the usual pre-migration copy).
- A bundle from a **newer** version is refused with the reason: restore it with that version or newer.
- `--sha256 <hex>` checks the bundle against a checksum you were given separately (the default is the `.sha256` file beside it).

In Docker, restore into an empty volume with a one-off container, then start the server on it (verified):

```sh
docker run --rm -v /srv/draughtsman-data:/data -v /srv/backups:/backups:ro draughtsman:0.1.0 restore /backups/draughtsman-backup-20260930235752.zip
```

Then start the server and check the diagrams and users. Sign-in sessions survive. If the AI key was configured, enter it
again.

## Postgres

Draughtsman does not back Postgres up for you, and `draughtsman backup` will not pretend to: it prints the command to run and
exits `1` (verified):

```
This instance stores its documents in Postgres, which Draughtsman cannot back up for you (a consistent dump needs pg_dump and your credentials).
No backup was made. Run this, with the password in PGPASSWORD or ~/.pgpass (it is not printed here):

pg_dump --format=custom --no-owner --host 127.0.0.1 --port 55499 --username drafter --dbname diagrams --file draughtsman-20261001001152.dump

Then copy the rest of the data folder (assets/, branding/, themes/, draughtsman.yaml; keys/ only if you accept a sensitive copy).
To restore into an empty schema (pg_restore --clean cannot drop a table a newer migration added):
  psql --host 127.0.0.1 --port 55499 --username drafter --dbname diagrams --command 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;'
  pg_restore --no-owner --host 127.0.0.1 --port 55499 --username drafter --dbname diagrams '<dump file>'
```

Run the `pg_dump` it prints (verified in a Postgres 17 container: a dump of a Draughtsman database restored with
`pg_restore --no-owner` into an empty database and all 6 migrations were present). The data folder
still holds what is not in the database: `assets/`, `branding/`, `themes/`, `draughtsman.yaml` (which contains the Postgres
password, so protect it). Copy those with the server stopped (or accept that an upload in progress may be
missing), and `keys/` only if you accept a sensitive copy.

**Take a snapshot before every upgrade.** When an upgrade has migrations to apply to an existing Postgres database, the server
logs a warning with the `pg_dump` command first; it cannot take the snapshot itself, and going back means restoring one.

## Before you upgrade

Do these four things, in this order, every time. They take a few minutes and they are the whole of the risk management; an upgrade you did not do them for is
one you cannot go back from.

1. **Read the release notes** ([`CHANGELOG.md`](../CHANGELOG.md)): the **Breaking changes** section (it says "None" when there are none) and, for a major
   release, the oldest version it can upgrade from. Skipping versions is allowed within a major version; across a major one, follow the notes.
2. **Back up.** SQLite: `draughtsman backup --out /srv/backups/` (the running server need not stop; [Back up](#back-up-sqlite-the-default)). Postgres: the
   `pg_dump` the server prints (`draughtsman backup` prints it), plus a copy of the rest of the data folder ([Postgres](#postgres)). A version older than the one
   that introduced the `backup` verb (`257d224`) cannot run it: stop it and copy the whole data folder, or run the NEW binary's `backup` against the stopped data
   folder before first starting the new version (checked: it reads an older schema without changing it).
3. **Run `draughtsman config check` with the NEW binary** against the same data folder and the same environment the service has. It starts nothing and changes
   nothing, and it tells you what the upgrade would do and whether the server would refuse:

   ```
   Database
     OK     /srv/draughtsman/data/data.db passes SQLite's integrity_check (9 tables, 13 documents, 3 users).
     INFO   2 of 8 migrations are pending and would be applied at start: 20260930233553_AddDocumentBin, 20261001092923_AddPasswordResetTokens.
     INFO   Before migrating, the server copies the database to data.db.pre-migration-<time> beside it, checks the copy opens and passes integrity_check, and keeps the newest 3 such copies. If a migration fails or the result loses rows, the copy is put back and the server refuses to start (exit code 4).
     OK     There is room for the copy: the database is 1 MiB and 12917 MiB is free.
   Result: this version would start on this data folder (0 warning(s)).
   ```

   (An excerpt of a real run of the new release archive against a data folder an older archive had filled.)
   Every line is `OK`, `INFO`, `WARN` or `REFUSE`; only `REFUSE` makes the command exit non-zero (`1`; `3` when the database is newer than this build). Warnings are things
   to know: an unknown setting, a key ring whose protection is not available in this shell. `--json` prints the
   same report for a script.
4. **Decide where the new version goes** ([Where to put the new version](#where-to-put-the-new-version)): the data folder outside the program folder; the program can be anywhere.

## Where to put the new version

- **Keep the data folder outside the program folder** (`DRAUGHTSMAN_DATA=/var/lib/draughtsman`, a Docker volume at `/data`). A release archive's default is `data/`
  beside the executable, which is fine until the day someone replaces the program folder wholesale and takes the data with it. If you kept the default, unpack the
  new archive **over** the old folder (the archive contains no `data/`), or move `data/` out first.
- **The program can be at any path.** The stored AI key and the saved SMTP password are protected by the key ring in the data folder alone (Data Protection's application name is the
  constant `Draughtsman`, not the install folder), so replacing the files in the same folder, unpacking a new version beside the old one, moving the program, or running an image whose
  program is always `/app` all leave them readable. A service's registration points at the executable's path, so a different folder still means running `service install` again
  ([`install.md`](install.md)). Browser sessions are not affected; a tab that was signed in before the first start of this version holds a CSRF token the new server cannot read once, and
  its first refused save carries a fresh one that the editor retries, so no edit is lost (a reload does the same).
- **Upgrading from a build older than this one.** Those builds bound the stored AI key and SMTP password to the folder the program ran from. Do nothing special if the new program runs
  from that same folder (the documented upgrade): the first start reads them and re-protects them, after copying `ai/settings.json` and `email/settings.json` to
  `backups/secrets-<time>/`, and the log and Admin > AI say so. If you unpack the new version **elsewhere** (a new folder, another disk), run
  `draughtsman config check` from it first: its **Stored secrets** section says, per secret, whether it can be read (`legacy`: readable and will be re-protected; `unreadable`: it cannot, and
  why). It looks, read-only, in the folder it runs from, the folders recorded in `keys/install-paths.json` (this version records where it ran, so a later move can still find it),
  `keyRing.legacyInstallPaths`, the folder the data folder sits in, and then, only if those fail, old log files (`Content root path:` lines), the folders next to the new program (a new version unpacked beside
  the old one finds it, including Windows' `draughtsman-<version>-win-x64` folders) and the recommended locations (`/opt/draughtsman`, `/usr/local/draughtsman`, `/app`, `C:\Program Files\Draughtsman`).
  If it still says `unreadable`, name the folder the old program ran from and restart (the setting is read once, at start; it can be left in place or removed afterwards):

  ```yaml
  keyRing:
    legacyInstallPaths:
      - /opt/draughtsman-0.1.0        # the folder the OLD program ran from, with or without the trailing slash
  ```

  or set `DRAUGHTSMAN_LEGACY_INSTALL_PATHS=/opt/draughtsman-0.1.0` (several paths separated like `PATH`: `:`, or `;` on Windows). On Windows either slash direction and either case of the drive letter will do (`C:/Draughtsman/` finds a secret written under `C:\Draughtsman\`); a short 8.3 name is a different string, so name the folder the way the old program saw it, as the `Content root path:` line in its log shows. Until it is read, nothing is changed: the settings file is left byte for byte as it
  was, the page names the secret, and entering it again in Admin > AI or Admin > Email is always possible and affects nothing else. Once re-protected the secret no longer depends on any
  folder, and the setting does nothing more. There is no way round naming the folder when the program moved, the old folder is gone and nothing logged it: the folder is part of the key derivation.
- **Give the new version the same key-ring protection.** If `keyRing.protection` is `key`, keep `DRAUGHTSMAN_KEY_ENCRYPTION_KEY` (or the key file) in the service's
  environment; with `certificate`, the certificate and its password variable. The server still starts without it, but the stored AI key is unreadable until it is back.
  `config check` warns when it is missing from the shell you ran it in.

## Patch updates and major upgrades

A **patch** (0.1.0 to 0.1.1) and a **major** upgrade are the same procedure; they differ in what the release notes may contain
([Versioning and release notes](#versioning-and-release-notes)): a patch only fixes and adds, and a major may remove what a deprecation left behind.

1. [Before you upgrade](#before-you-upgrade): notes, backup, `config check`, where it goes.
2. **Stop the old version.**
3. **Replace the program, not the data.**
   - **Release archive or service:** stop the service, unpack the new archive over (or in place of) the old program folder, leave the data folder alone, start the service.
     The executable keeps its path, so the service registration is unchanged.
   - **Docker:** build or load the new image under a new tag, stop and remove the old container, and run the new tag against the **same** volume or folder. With compose, change
     the `image:` line and `docker compose up -d`.
4. **Start the new version.** On SQLite it:
   1. copies `data.db` to `data.db.pre-migration-<time>` beside it (after folding the write-ahead log in), and proves the copy: the same size, it opens, it passes SQLite's
      `integrity_check`;
   2. applies the migrations, each in a transaction;
   3. checks the result: `integrity_check` must be `ok` and **no table may hold fewer rows than in the copy**;
   4. only then prunes older copies (it keeps the newest `retention.preMigrationCopies`, 3).

   ```
   Copied SQLite database to /var/lib/draughtsman/data.db.pre-migration-20261001125132 before migrating; the copy opens and passes integrity_check (9 tables)
   Applying 1 storage migration(s) to sqlite: 20261001092923_AddPasswordResetTokens
   Post-migration check passed: SQLite integrity_check is ok and no table has fewer rows than in /var/lib/draughtsman/data.db.pre-migration-20261001125132
   ```

   On Postgres it logs the notice below and goes ahead; there is no copy and no rollback.
5. **Check**: sign in, open a diagram, look at **Status** and the log for warnings (a setting the new version no longer reads is one).

Settings in `draughtsman.yaml` are not rewritten by an upgrade; a setting the new version adds takes its default.
`draughtsman.example.yaml` in the new archive lists everything: compare it with your file.

## What an upgrade does and undoes

**SQLite.** If a migration throws, or the checks in step 4.3 fail, the server **restores the copy over the database** (deleting the `-wal` and `-shm` files first, so the failed run's
log cannot be replayed onto it), verifies the restored file is byte for byte the copy, refuses to start, and exits with code `4`. The data folder is exactly as it was before
that start, plus the one copy the start took, which stays so the message can name it:

```
crit: ... The storage upgrade of /var/lib/draughtsman/data.db did not pass its checks: table documents had 11 rows before the migration and has 0 now. The pre-upgrade copy
/var/lib/draughtsman/data.db.pre-migration-20261001125132 was put back automatically, so data.db is exactly as it was before this start (the copy is kept beside it) and nothing was served.
Start the previous version again, and report this failure with the log.
```

(That wording is from the test that makes a migration delete rows, run over a real earlier build's database; no shipped migration has done it.) If even the restore cannot be proven, the message says **THE COPY COULD NOT BE PUT BACK** and gives the
steps to do it by hand ([Going back](#downgrade-going-back)).

If the copy **cannot be made** (the disk is full, the folder is not writable, or the live file already fails `integrity_check`), the upgrade is **refused before anything
changes**: exit code `5`, "Nothing was changed and nothing was served", and the way out. Free space or fix the permissions and start again. If you must upgrade anyway, take a backup
yourself and set `storage.allowMigrationWithoutBackup: true` in `draughtsman.yaml`: the server then logs "MIGRATING WITHOUT A BACKUP" as a warning and goes ahead with no
automatic way back. Switch it off again afterwards.

**Postgres.** An existing Postgres database about to be migrated gets this block in the log, as a warning, before anything runs (the password is never printed):

```
POSTGRES UPGRADE, NO AUTOMATIC ROLLBACK: applying 2 storage migration(s) to an existing Postgres database: 20260930233555_AddDocumentBin, 20261001092928_AddPasswordResetTokens.
Draughtsman cannot snapshot Postgres for you, and unlike SQLite it does not copy the database first, so if this is not what you want, stop the server NOW. Going back to the previous
version means restoring a snapshot taken before this start. If you have none, take one first:
  pg_dump --format=custom --no-owner --host db.internal --port 5432 --username drafter --dbname diagrams --file draughtsman-20261001124500.dump
and to go back: stop this version, empty the schema (pg_restore --clean cannot drop a table a newer migration added), then restore the dump into it:
  psql --host db.internal --port 5432 --username drafter --dbname diagrams --command 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;'
  pg_restore --no-owner --host db.internal --port 5432 --username drafter --dbname diagrams '<the dump file>'
(the password comes from PGPASSWORD or .pgpass; see docs/upgrade-backup.md, 'Postgres')
```

(From a run against a Postgres 17 container holding a database an older build wrote; the host name is changed.) `draughtsman config check` shows the same block for a Postgres instance with migrations pending. The migrations were applied to real populated Postgres databases made by the seven
earlier builds in the tests and lost nothing, but a failure on YOUR data is yours to undo with the dump.

## Settings

An old `draughtsman.yaml` keeps working. A setting a later release **renames** is read under its new name; one it **removes** is ignored; one it does not know is ignored; each
is a warning that names what to do (in the log at start, and in `config check`), never a failure:

```
draughtsman.yaml: 'server.prot' is not a setting this version reads, so it is ignored (did you mean 'server.port'?).
draughtsman.yaml: 'storage.connection' was renamed 'storage.connectionString' in 1.2.0; the old name is still read this time. Rename it in draughtsman.yaml.
```

No setting has been renamed or removed since the first pushed build (the first example above is a typo; the second shows the form a rename takes). A file that is not valid YAML
is the one thing that stops the server, with the line and column; `config check` finds it first. The code is `ConfigCompatibility` (a rename or a removal is made only by adding it to
its tables, which a test pins).

## Downgrade (going back)

**An older version refuses to open a newer database.** It logs a critical message naming the migrations it does not know,
changes nothing, serves nothing, and exits with code `3`. Verified with a database carrying a migration the binary does not
know (SQLite, and the same on Postgres), and on a real older release archive and Docker image of `257d224` over a database a newer build had migrated (exit code 3, the database byte for byte unchanged):

```
crit: draughtsman[0]
      This sqlite database was migrated by a newer version of Draughtsman than this one (it has 1 migration(s) this build does not know: 29991231000000_FromTheFuture). Running an older version against a newer database can fail at runtime or corrupt data, so it was not opened and nothing was changed. Either start the newer Draughtsman version again, or downgrade by restoring what was saved before the upgrade. No pre-migration copy was found beside the database; restore a backup made before the upgrade ('draughtsman restore <bundle>', see docs/upgrade-backup.md).
```

(Earlier builds named `docs/backup-restore.md` in that message, a pointer to this page.) `mcp --stdio`, `reset-admin` and `restore` refuse the same
way, and `restore` refuses a bundle from a newer version. **Builds before `257d224` do not have this refusal**: they would try to run on the newer schema. Never downgrade
across it without restoring the pre-upgrade copy or backup first.

To go back (verified end to end on release archives and on a Docker volume for SQLite, and on a Postgres database from a dump):

1. Stop the new version.
2. Restore what you took before the upgrade: for SQLite the pre-upgrade copy the server left (`ls data.db.pre-migration-*`, newest last):
   **delete `data.db-wal` and `data.db-shm` first**, then copy the copy over `data.db`. A write-ahead log left behind by the migrated database is replayed
   onto the restored file and silently returns the instance to the migrated schema. Or `draughtsman restore <bundle> --overwrite` (the old data goes to
   `pre-restore-<time>/`). For Postgres, **empty the schema and restore the dump into it** (`DROP SCHEMA public CASCADE; CREATE SCHEMA public;`, then `pg_restore --no-owner`).
   Do not use `pg_restore --clean`: it drops only what the dump holds, so a table the upgrade added (the password-reset table has a foreign key to `users`) blocks it and leaves a half-restored
   database (this was found by trying it; the commands above are what was run instead and brought the old build back with every check passing).
3. Put the older program back (at the same path as before) and start it. **If this version re-protected the AI key or the SMTP password** (the log said so, and `backups/secrets-<time>/` exists in the data
   folder), copy `ai/settings.json` and `email/settings.json` from that folder over the ones in the data folder first: the older program only reads them from the folder it ran from, which is
   what those copies are, and shows the rest as needing to be entered again (nothing is lost either way: this version never deletes the earlier file).

```sh
sudo systemctl stop draughtsman                          # unverified: systemd; the file steps below were run, on a release archive and a Docker volume
cd /var/lib/draughtsman && rm -f data.db-wal data.db-shm
cp data.db.pre-migration-20261001125132 data.db        # the copy is a whole, openable database on the old schema (the tests open it with the old schema check)
# put the older program back, then:
sudo systemctl start draughtsman
```

Anything saved after the upgrade is lost in a downgrade; there is no way to run an older version over newer data. The copy is byte for byte the database you had, so everything up to the
upgrade is back.

## What an upgrade may change

This is the rule that makes the guarantee hold for the next release, written for whoever changes the schema.

**A release only adds. What an older release no longer reads is dropped by a LATER release, never the same one** (expand, then contract). A migration may create a table, add a column that
is nullable or has a default, add an index, widen a column and back-fill. It may not drop a table or column, rename one, narrow or retype a column, make a nullable column NOT NULL, add a
NOT NULL column with no default (it fails on any table that has rows), or run SQL that deletes, truncates or drops. Three things enforce it:

1. **The destructive-migration guard** (`tests/Draughtsman.Storage.Tests/MigrationSafetyTests.cs`) reads every migration of both providers and fails on any of the above, naming the
   migration and the operation. The only way through is an entry in `MigrationSafety/allowed-destructive-migrations.json` with the exact operation, a written justification of why
   no data is lost (at least 60 characters, so "ok" does not pass) and the **fixture** (a directory of `tests/upgrade-fixtures` made by a release that predates the migration) whose
   upgrade test proves it. A stale entry (nothing it covers), a fixture that already contains the migration, or a table dropped on purpose that is not also in
   `StorageMigrator.IntentionallyDroppedTables` fails the suite too. The guard was shown to fail with a deliberately destructive migration, and to pass it once justified and proven.
2. **The upgrade fixtures** run every migration over real data from seven earlier builds, on both providers.
3. **The migrator's own check** (above) undoes a SQLite upgrade that nevertheless loses rows.

A contract release also carries a CHANGELOG entry under **Breaking changes** and names the oldest version it can upgrade from.

## Versioning and release notes

Draughtsman uses [semantic versioning](https://semver.org/): `MAJOR.MINOR.PATCH`, from `<Version>` in `Directory.Build.props`.

| | A patch (0.1.x) | A minor (0.x.0) | A major (x.0.0) |
| --- | --- | --- | --- |
| Fixes bugs | yes | yes | yes |
| Adds features, settings, API members | settings and members only if needed by a fix | yes | yes |
| Database migrations | only additive ones (new nullable or defaulted columns, tables, indexes) | additive; a feature may deprecate a column or setting | may drop or rename what an earlier minor deprecated, one release after it stopped being used |
| Document model and DSL | never changes what an existing document says | additive | may change, with a migration of old documents and a golden test |
| `draughtsman.yaml` | never renames or removes a setting | may deprecate (a warning) | may rename or remove (a warning, then removal in the next major) |
| REST API and MCP tools | never removes or changes the meaning of a member | additive | may break, listed in the notes |
| Upgrade steps | the usual steps, nothing else | the usual steps | the usual steps, plus whatever the notes say |

Every release's notes in [`CHANGELOG.md`](../CHANGELOG.md) have a **Breaking changes** section (or say "None"), and say whether the release has pending migrations, which settings it adds, deprecates or
removes, and, for a major, the oldest version it upgrades from. **A patch that needs more than the usual steps is a bug in the release process**: it should have been a minor.

## The release rule

A release is cut only after its version has an upgrade fixture. From the release's own checkout (or against its archive's executable):

```sh
scripts/make-upgrade-fixture.sh v0.2.0
git add tests/upgrade-fixtures/v0.2.0 && git commit -m "Upgrade fixture for v0.2.0"
```

It runs that build, fills it through its API and writes `tests/upgrade-fixtures/v0.2.0/` for SQLite and Postgres (Postgres needs docker). The suite then proves the next release opens it.
`scripts/release.sh` **refuses to build a release whose `<Version>` has no fixture**, or whose fixture does not end at the newest migration (a fixture made before a later migration was
added does not describe this release), is missing a provider's data, or is labelled for another version; `scripts/release.test.sh` pins each of those. The release checklist is:
notes written (with Breaking changes), `scripts/make-upgrade-fixture.sh v<version>` committed, `scripts/release.sh --build-date <date>`.
`scripts/upgrade-path-proof.sh archive --old <previous archive> --new <new archive>` repeats the in-place swap and the way back on the real archives before you publish them.

## The bin and your backups

A backup (`draughtsman backup`) is of the whole database, so diagrams in the bin are in it, still in the bin, with their
versions; `restore` brings them back the same way and checks the number of binned diagrams against the manifest
(`counts.binnedDocuments`; a bundle made before the bin existed has none and is migrated on first start, its diagrams live).
A backup made before a diagram was deleted still has it live; one made after has it in the bin (until the retention pass removes
it, after which no later backup has it). Either way, an accidental delete is recoverable from the bin for `retention.binDays`
days without needing a backup ([`admin-guide.md`](admin-guide.md#the-bin-and-version-history)).

## Export everything (leaving with your diagrams)

An administrator signed in in the browser can download `GET /api/admin/export`: every diagram as JSON and as DSL text, and every
uploaded image, in one zip with a manifest (verified: `documents/<id>.json`, `documents/<id>.dsl`, `manifest.json`). It is a
way out in open formats, not a backup: the current version of each diagram only, with no checkpoints, users or settings, and an
API token cannot call it. Each export is recorded in the security log. Diagrams in the bin (deleted, not yet purged) are **not**
in it unless you ask: `GET /api/admin/export?includeBin=true` adds them under `bin/` in the zip, with a `deletedAt` in the manifest.

```sh
# needs an administrator's browser-session cookie (a token is refused); there is no button for it yet
curl -s -b cookies.txt -o draughtsman-export.zip https://draughtsman.example.com/api/admin/export
```

## What the server keeps, and for how long

`retention:` in `draughtsman.yaml` (see [`draughtsman.example.yaml`](draughtsman.example.yaml)): `preMigrationCopies` (3),
`checkpointsPerDocument` (50; the snapshots an agent edit takes first; named versions are never touched),
`expiredSessionDays` (7), `binDays` (30; how long a deleted diagram stays in the bin before the daily pass removes it for good; zero or less keeps it until someone deletes it permanently). `assets.quotaBytes` (5 GiB) refuses an upload that would pass the limit with `507`. A daily pass
(first run two minutes after start) does the pruning; zero or less keeps everything.
