# Upgrade fixtures

Each directory here is the **data folder an earlier build of Draughtsman wrote**, populated through that build's own HTTP API with
realistic throwaway data, snapshotted, and committed. The upgrade tests start the CURRENT server on a copy of each one and read
everything back, so "an upgrade never wipes a customer's data" is something the build proves on every run, not something it
promises (`docs/upgrade-backup.md`, "The guarantee").

Before 0.1.0 there was no formal release, so history stood in for releases: each of those fixtures is a commit that changed the schema or the data folder. From 0.1.0 on, every release has a fixture labelled with its tag (`scripts/release.sh` refuses to cut one without it).

| Label | Commit | What it is the first of | Migrations applied | Key ring |
| --- | --- | --- | --- | --- |
| `first-push-9e22f56` | `9e22f56` "Record SQL import and DDL export in the spec amendments" | The first commit pushed to the remote: local sign-in, uploaded images, branding, admin AI settings, SQL import. No licence, no organisation themes, no sequence diagrams. | 4 of 8 (`Initial`, `Assets`, `AddAuth`, `AddAuditActorUserId`) | plain text (written before protection existed) |
| `keyring-5561661` | `5561661` "Protect the Data Protection key ring at rest and migrate existing rings" | Key ring protection, a signed licence file (which this build no longer reads), organisation themes, sequence diagrams. | 4 of 8 | protected by a key-encryption key |
| `security-events-fe5b237` | `fe5b237` "Add security event log, token expiry and read scope..." | The security event log, API token lifetimes and read scope (the migration that grandfathers old tokens), fly-through paths, the CLI and release archives. | 5 of 8 | plain text (`none`) |
| `etag-backup-257d224` | `257d224` "Add backup and restore verbs, schema-newer refusal, retention and a stored document ETag" | The stored document ETag, `backup`/`restore`, an older binary refusing a newer database. | 6 of 8 | protected |
| `bin-5a99dbf` | `5a99dbf` "Add the document bin: soft delete, restore, purge with retention, version history..." | The bin (soft delete), named snapshots. | 7 of 8 | plain text (`none`) |
| `password-reset-b84733a` | `b84733a` "Password recovery: reset links, outgoing email and admin email page" | The newest schema: password reset tokens. | 8 of 8 (nothing pending) | protected |
| `email-settings-2fe39d9` | `2fe39d9` "Note email settings in AGENTS.md" (the tip of `main` when this was written; the feature is `4d617c1`) | Outgoing email set in the browser: `email/settings.json` with a Data Protection-encrypted SMTP password, a new secret folder. The same schema as above, a new element of the data folder. | 8 of 8 (nothing pending) | protected |
| `v0.1.0` | `b399fa5` "Commercial licence terms, compose quick-start, 0.1.0 readiness (#1)" | The first release, and the first fixture written under the pinned Data Protection application name: its stored AI key and SMTP password open from any folder, so the manifest records `dataProtectionApplicationName` and the tests that exercise the path-bound fallback skip it. | 8 of 8 (nothing pending) | protected |

Every fixture has a `sqlite/` and a `postgres/` side. The SQLite side is the data folder as a tarball (`data-folder.tar.gz`, including
`data.db`); the Postgres side is the same folder without the database plus a plain-SQL dump (`dump.sql.gz`, one `INSERT` per row so a driver can
run it) made by `pg_dump` from a throwaway Postgres 17 container the old build ran against. `expected.json` is what that build served,
through its API, immediately before it was stopped. `manifest.json` names the build (commit, version, migrations applied), the content root the
build ran under (see below), the key-ring protection, and the **throwaway** secrets a test needs (the key-encryption key, the licence's public key).

The content root is a neutral path, `/opt/draughtsman-upgrade-fixture/<label>/`, not the folder the build really ran from: a stored secret is
bound to that string, so each fixture's stored AI key and SMTP password were decrypted with the ring and protected again under the neutral path
before publication (the sizes and checksums in `manifest.json` are the re-protected files). Fixtures made from now on record the real path of the
build, and a fixture meant for publication should be re-bound the same way. A fixture from a pinned build (`dataProtectionApplicationName` in its
manifest, `v0.1.0` onwards) binds nothing to the path, so only the recorded strings are changed: `contentRoot` and `keys/install-paths.json`, with
that file's size and checksum updated in `manifest.json` and `provider.json`.

## What is in each one

Made by `scripts/upgrade-fixture/populate.mjs`, through the real API, so only what the build under test could do is there (a feature the
build did not have is recorded under `features` in `expected.json` as skipped, with the reason):

- users with known passwords: an administrator, an Editor, and a deactivated Editor;
- API tokens: a plain one (the agent), one with a 90-day expiry, a read-only one, an Editor's, and a revoked one (builds before token
  lifetimes issue neither expiry nor scope, which is the case the migration grandfathers);
- documents: every example in that commit's `docs/examples` (including the 2,000-node benchmark and, where the build has the type,
  the sequence diagrams), plus generated ones: Unicode, quotes and emoji; empty; a 40-node chain; an uploaded image node; a fly-through path
  (where the schema allows one);
- versions: agent saves make checkpoint versions; a person's own edit; a named snapshot (where the build has them);
- a bin: one document deleted by an Editor and one by an administrator (where the build has a bin);
- branding (product name, logo, light logo, favicon), two organisation themes with one made the default, AI settings with a stored API key, outgoing email settings with a saved SMTP password (where the build has the editor);
- a licence file signed by a throwaway Ed25519 key (where the build read licences; `manifest.json` records the public half). The current build never reads it: the tests show a data folder that holds one still starts and leaves the file alone;
- security events and the document audit trail.

Everything is throwaway. Addresses end in `.invalid`, passwords, tokens and the AI key say THROWAWAY, the key-encryption key and the licence key
are generated for the fixture and protect and sign nothing real. Never put a real schema, key or password in a fixture.

## How the tests use them

- `tests/Draughtsman.Server.Tests/Upgrade/` starts the real server (`WebApplicationFactory<Program>`) on a copy of each fixture and checks
  documents (canonical JSON hash and printed DSL hash against `expected.json`), users and passwords, tokens (scope and expiry), versions, the bin,
  images, branding, themes, the AI settings, the security log and audit rows, that an old licence file is left alone, that no table loses rows, and that the instance can then
  write. SQLite always; Postgres when `DRAUGHTSMAN_TEST_POSTGRES` names a server (CI's service container, or the `docker run` in `docs/api.md`).
- The same directory also runs the migrator alone (the pre-migration copy is whole, on the old schema, and putting it back restores the old state byte
  for byte), `draughtsman config check`, the new binary's `backup` and `restore` over an old folder, the key ring across the upgrade, and the theme and branding file formats.
- `tests/Draughtsman.Storage.Tests/StorageMigratorSafetyTests.cs` runs a deliberately destructive migration over each fixture's database and shows it is
  undone; `MigrationSafetyTests` is the destructive-migration guard, whose allow-list entries must name a fixture here that predates the migration.
- `scripts/upgrade-fixture/verify.mjs` makes the API-level checks against any running binary: the proof scripts use it on an archive swap and a Docker volume.

### Intentional normalisations

`normalisations.json` is the complete list of differences an upgrade may make to a document as the API serves it; today it is empty. Two things are
compared with a stated exception instead: an organisation theme's `updatedAt` is its file's modification time, which a copy or restore of the data
folder does not keep, so it is left out of the comparison; and the stored AI key and SMTP password were bound by the BUILD THAT WROTE THEM to its install path (Data Protection's
content root), which is recorded in `manifest.json` and is never the path a test host runs from. The current server reads them anyway, through its legacy fallback once the old
path is named (`legacyInstallPaths`, which is how an operator names it) or when it is the host's own (`UpgradeDataProtectionPinTests`, `StoredSecretsTests`), re-protects them under
the pinned application name, and needs nothing at all after that; `UpgradeAssertions` therefore requires both to be readable.

## Making a fixture: the release rule

```sh
scripts/make-upgrade-fixture.sh v0.2.0                         # this checkout, both providers (Postgres needs docker)
scripts/make-upgrade-fixture.sh m3-bin --source /scratch/clone # an older commit checked out elsewhere (outside $HOME)
scripts/make-upgrade-fixture.sh v0.2.0 --exe ./draughtsman     # a release archive's executable instead of a build
```

It builds (or runs) the version, starts it on a scratch data folder and a free random port, fills it, checks the filled instance with `verify.mjs`,
stops it by its own pid, and writes the fixture. **A release is cut only after its version has one:** `scripts/release.sh` refuses to build a release whose
`<Version>` has no `tests/upgrade-fixtures/v<version>/` ending at the newest migration (`scripts/release.test.sh` pins both directions). Commit the
new directory with the release, so the next release is proven to open what this one writes. Total size matters (the whole set is a few MB; keep it under 20 MB): the
generator keeps documents modest and the tarballs are compressed.

The fixtures in this directory were made on 2026-10-01 from clones of the commits above checked out under a scratch folder outside `$HOME`, one
build at a time, with `--key-ring key` for the protected ones and `--key-ring none` for the others, by the version of the script committed with them.
