# A key ring and stored AI key written by main

`keys/` and `ai/settings.json` were written by an unmodified build of this repository's `main` (commit `05a1539`,
before key ring protection existed): the server was started on an empty data folder, an administrator was created
through `POST /api/auth/first-run`, and the AI key was saved through `PUT /api/admin/ai/key`. They are the on-disk
format every existing install has, so the migration tests run against real output rather than a reconstruction of it.

- The stored key is the synthetic `sk-fixture-main-secret-0123456789`.
- The server's content root when it wrote them was a build folder on the author's machine. The stored key was then
  decrypted and protected again, with the same ring, under the neutral path `/opt/draughtsman-upgrade-fixture/key-ring-from-main/`
  (with the trailing slash, as `ContentRootPath` has it), so the fixture names no real folder. Data Protection binds a
  payload to that exact string, so tests must use it as the application discriminator.
- The ring's key is a random value generated for this fixture only; nothing else was ever encrypted with it.
