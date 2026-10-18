# HTTP API

The server is an ASP.NET Core minimal API on `http://localhost:5180` by default (`draughtsman.yaml` in the data folder changes host and port). Everything is JSON, under `/api`. Every route requires a signed-in session or a valid API token except `GET /api/health`, the chrome theme routes, and the sign-in/first-run routes themselves - see [Auth](#auth). All JSON uses the document model from `docs/schema/document.schema.json` with camelCase names and nulls omitted. Errors are RFC 9457 problem details (`title`, `status`, `detail`).

The server exposes the document store under `/api`. The SPA, the MCP layer and scripts all use this contract; the
model is the truth and the picture is a render, so every body is the document JSON defined by
`docs/schema/document.schema.json`. The implementation is `src/Draughtsman.Server/Endpoints/DocumentEndpoints.cs`
and its tests in `tests/Draughtsman.Server.Tests/DocumentEndpointTests.cs` are the executable form of this page.

## Conventions

- Request and response bodies are JSON (`application/json`, UTF-8). Documents serialise exactly as
  `DocumentJson` does: camelCase, enums as camelCase strings, nulls omitted, timestamps as UTC with three
  fractional digits and a `Z` suffix.
- Errors are [Problem+JSON](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`) with `status`,
  `title` and `detail`. Schema violations add an `errors` array of `"<json pointer>: <message>"` lines.
- A write the database refuses (a read-only `data.db`, a full disk, a Postgres role or replica that may not write) is `503` with the title
  `This instance cannot save changes` and a `detail` that says so. It names neither the data folder nor the database's own error; both are in the
  server log, with the account the server runs as and the fix (`chown -R <uid> <data folder>`), and `GET /api/health` reports `degraded` (except for a full disk, which it cannot see).
  Reads, and a wrong password (`401`), are unaffected, so the first sign of a database that cannot be written is a **correct** sign-in answering `503`.
- Ids match `^[A-Za-z_][A-Za-z0-9_.-]*$` (1 to 128 characters). Server-assigned document ids look like
  `doc_<12 hex>`, version ids like `v_<21 hex>` and sort chronologically.
- Every response carrying a document carries its version as an `ETag` (`"dv1:<32 hex>"`) and, unquoted, as
  `X-Document-Version`, and `PUT` requires that version back in `If-Match`. See [Base versions](#base-versions).
- A signed-in session (browser cookie) or a valid API token (`Authorization: Bearer <token>`) is required, on every
  route except the ones [Auth](#auth) names. The actor recorded in audit and `aiProvenance` comes from that
  authenticated identity, never from a header; see [`X-Draughtsman-Actor`](#x-draughtsman-actor) for the one thing
  that header still does. The server still binds `127.0.0.1` by default; `server.host` in `draughtsman.yaml` widens
  it, and every route above stays behind sign-in and the per-account/IP throttle either way.

### Limits and headers

A body over its limit is refused in plain words that name the limit a person was told, not the byte count of the request
with its framing: for a branding image `That file is too large: a branding image can be at most 512 KiB.`, for an upload
`That file is too large: an image can be at most …`, and elsewhere `That request is too large: this part of Draughtsman accepts at most … at a time.`

Every route has a request body limit, enforced before the handler reads a byte (a declared `Content-Length` over it, or a chunked body that passes it, is `413` with a problem body): 16 KiB for `/api/auth/*`, 256 KiB for `/api/ai/*`, about 3 MB for `/api/import/*` (the importers' own cap is 1,000,000 characters), `assets.maxSizeBytes` plus multipart framing for uploads, `limits.maxDocumentBytes` (10 MiB) for documents, layout, exports and `/mcp`, and `limits.maxRequestBodyBytes` (1 MiB) for anything else. PDF, layout, import, AI, MCP and thumbnail calls are also rate limited per signed-in user per minute, and PDF, layout and AI calls capped in flight across the instance (thumbnails are drawn a few at a time, and a request over that waits rather than being refused); a call over a limit is `429` with `Retry-After`. All of these are `limits` in `draughtsman.yaml` (0 turns a per-minute limit off). A Mermaid or SQL source the importer takes too long to read (a single step passing the 5 s regex match timeout: a source shaped to make an expression backtrack, or, rarely, a host too busy to run the thread) is `422` with the message "took too long to read; try again, or split the source into smaller parts".

Every response carries `Content-Security-Policy` (with `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, `X-Frame-Options: DENY` and `Permissions-Policy`; every `/api` response that does not choose its own `Cache-Control` carries `no-store, no-transform` (`no-transform` asks an intermediary to pass the payload through as sent; it is only a request, some intermediaries, Cloudflare among them, ignore it, and a gateway may compress or re-tag the response anyway, see [Base versions](#base-versions)). The server answers only to the Host headers `server.allowedHosts` names (by default `localhost` names while it listens on loopback with no proxy, anything otherwise); another Host is `400`.

### Base versions

Draughtsman has two writers by design: a person drafting in the browser and an agent amending the same document.
A save that simply replaced the stored snapshot would silently discard whatever landed between the caller's read and
its write, so a save has to say which version of the document it was made from, and one made from a version that is
no longer current is refused.

- Every response that carries a document carries that document's version as an `ETag`: `GET /api/documents/{id}` (and
  its `304`), `POST /api/documents`, `PUT` and `PATCH /api/documents/{id}`, a duplicate and a version restore. The server
  issues it strong. The tag is a fingerprint of the stored snapshot, opaque and
  server-issued; no client computes one. The store keeps it in its own column (`documents.etag`), computed once when
  the document is written, so a conditional read or the check of a save is one indexed read and never parses the
  snapshot. A document saved before that column existed gets its tag the first time its version is asked for.
- The same response carries the same version a second time, unquoted, as `X-Document-Version: dv1:<32 hex>`. A CDN,
  WAF or gateway may compress a response and in doing so weaken its `ETag` (`W/"dv1:…"`) or drop it, and has no reason
  to touch a header it does not know, so a client that has to survive one reads `X-Document-Version` first and falls
  back to the `ETag`; the browser editor does. API clients can keep using `ETag` and `If-Match` exactly as before.
  No CDN or proxy setting is needed for a save to work: a weakened tag is accepted (next point) and the editor needs
  only this header.
  (A thumbnail's `ETag` is a different thing, a render's, and carries no `X-Document-Version`.)
- `PUT /api/documents/{id}` requires `If-Match` with the `ETag` of the read (or write) the save was composed from.
  Exactly one entity tag is required; a list is refused with a detail explaining that one base version is required.
  The tag may be quoted weakly (`W/"dv1:…"`): the version is an opaque identifier the server issued, not a byte-exact
  validator, so a weak tag names the same version as its strong form, and is checked against the stored version the
  same way (stale is still `412`). This is what keeps a save working through an intermediary that weakened the `ETag`
  on its way to the client.
- The rule is the same for both writers. An agent write is the same `PUT`, so it carries a base version too: a guard
  that held only the browser would leave exactly the hole it exists to close.

| Case | Status | Title |
| --- | --- | --- |
| `If-Match` absent | `428` | `Base version required` |
| `If-Match` is a list, `*`, or not an entity tag | `400` | `Invalid If-Match header` |
| The document has been written to since that version | `412` | `Document has changed` |

A `412` body names both versions, so a caller can tell a stale save from a bad request:

```json
{
  "title": "Document has changed", "status": 412,
  "detail": "This save was made from version \"dv1:1f0c…\", but document 'doc_9f1c2b3a4d5e' is now at \"dv1:8ab3…\"; it was written to in between. Nothing was written. GET /api/documents/doc_9f1c2b3a4d5e returns the current version.",
  "expectedVersion": "\"dv1:8ab3…\"", "providedVersion": "\"dv1:1f0c…\""
}
```

A refused save writes nothing at all: no snapshot, no checkpoint version, no audit row. The one exception is an
agent save that loses a race with a simultaneous write, where the checkpoint was already taken: it stays as one
more restore point of a snapshot that really existed. What to do about a refusal is the caller's: the server takes
no position on merging, reloading or retrying.

The browser's answer (`web/src/editor/useDocumentEditor.ts`, `design/atlas/editor-safety.md`): it autosaves, so a
refusal is a stale autosave. It stops saving, keeps the user's document as a local copy in the browser, and asks
the user to reload the server's version, keep the user's (a `PUT` quoting the server's *current* `ETag`, sent only
after an explicit confirmation) or save the user's as a new document (`POST /api/documents` without `id` or `meta`).

`*` is refused rather than honoured: it is a save that names no base version, which is the case the rule exists to
refuse. A weak tag is not: it names a version (see above). `POST` is unaffected — a create that collides is already
`409`. A restore (`POST /api/documents/{id}/versions/{versionId}/restore`) and a rename follow the same rules as `PUT`.

### `X-Draughtsman-Actor`

The actor recorded in audit and `aiProvenance` is always the authenticated identity - a signed-in session's user, or
an API token's owner - never this header. On a **token**-authenticated request only, the header may additionally
annotate which agent is acting through that token, in the wire form `agent:<name>/<model>` (the same form `version.actor`
and audit rows use elsewhere):

| Header | On a session (cookie) | On a token (bearer) |
| --- | --- | --- |
| absent | Actor is the signed-in user | Actor is the token's owner |
| `agent:<name>/<model>` | Ignored outright, not validated | Actor is `agent:<name>/<model>`, annotating the owner's token call |
| anything else (`user:...`, malformed, oversized) | Ignored outright, not validated | `400 Invalid actor header` |

It can never make a request act as a different user: on a session it is inert, and on a token it only narrows *which
agent*, never *whose* token. This table is for the REST routes: `/mcp` does not read the header at all (see [`POST /mcp`](#post-mcp)). Malformed or oversized (over 512 characters, or a name/model over 256) is `400 Invalid
actor header` on a token request only. MCP's own `actor` tool argument is a separate, pre-existing mechanism
(`src/Draughtsman.Mcp/Tools/DraughtsmanTools.cs`), unaffected by this header and by local sign-in.

## Auth

Local accounts, sessions and API tokens (build-spec-v0.1.md section 7). Implementation is
`src/Draughtsman.Server/Auth/`, tests are `tests/Draughtsman.Server.Tests/AuthEndpointTests.cs`,
`UserEndpointTests.cs`, `TokenEndpointTests.cs`, `CsrfMiddlewareTests.cs` and `ProtectedRoutesTests.cs` (the sweep
asserting every route requires sign-in except the documented exceptions). OIDC and workspaces are out of scope for this MVP
(spec section 7 and the login design, `design/atlas/login-admin.md`); password recovery is [below](#password-reset).

**Passwords** are Argon2id (`Draughtsman.Server.Auth.PasswordHasher`): 19 MiB memory, 2 iterations, 1 thread, a
16-byte salt and a 32-byte key, the OWASP Password Storage Cheat Sheet's Argon2id baseline. Minimum length 12.
Comparison is constant-time. No password or token is ever logged.

**Sessions** are an HttpOnly, `SameSite=Lax` cookie (`ds_session`; `Secure` when served over HTTPS), server-side and
revocable - sign-out and an admin deactivating a user both end it immediately, checked on every request rather than
trusting a signed ticket. They last 30 days from sign-in; there is no "keep me signed in" option (design/atlas/login-admin.md).

**CSRF**: every state-changing (`POST`/`PUT`/`PATCH`/`DELETE`) *cookie*-authenticated request needs the antiforgery
request token echoed back in an `X-XSRF-TOKEN` header; the value is readable from the non-HttpOnly `XSRF-TOKEN`
cookie the server (re)issues on sign-in, first-run and sign-out. A missing or wrong value is `403 CSRF validation
failed`. A bearer-token-authenticated request never needs this header - a token cannot be replayed by a browser the
way an ambient cookie can.

**Throttling**: only *failed* sign-ins are throttled, in memory, in a rolling 15-minute window, in two independent
buckets. Per account (the email and caller IP pair): five failures, then `429 Too many attempts` for that account from
that IP, even with the correct password. Per caller IP across all accounts: twenty failures, then `429` for every
sign-in from that IP. The IP budget is higher because an office NAT, or any reverse proxy not listed in
`server.knownProxies`, presents many people as one address (the server logs one warning an hour when forwarding headers arrive from a peer it does not trust, naming the
peer and `server.knownProxies`, never a header value). A correct sign-in is never charged: it clears its
account bucket and refunds its own single IP charge, but it does not reset failures already counted against the IP, so
interleaving a known-good login cannot extend a spray. This resets on restart; it is not persisted.

**First run**: with no users at all, `firstRunRequired` is `true` and only `POST /api/auth/first-run` can create an
account (an admin). It also needs a one-time **setup code**: the server prints `First-run setup code: <code>` to its
log at startup (the console, or `docker logs` in a container) whenever no user exists yet. A **Windows service** has only the
Application event log, which every local user can read, so it writes the code to `setup-code.txt` in the data folder instead (readable by
Administrators and SYSTEM only, deleted when the first administrator exists) and logs only where that is; `draughtsman first-run-code` prints
it. The code lives only in memory (and that file), is valid until first-run succeeds, and a restart before then issues a fresh one and invalidates the old. A
missing or wrong code is `403 Invalid setup code`. Surrounding whitespace and letter case in the code are ignored (it is copied out of a terminal, and the page enables its button on the trimmed value).
Only a **wrong** code is charged against the throttle, per IP (five in 15 minutes, then `429` even for the right code): a right code that arrives with something else wrong, such as a password that is too short, is refunded. Once any user exists, first-run is closed for good
(`409`), for every future caller regardless of who completed it.

| Method and path | Purpose | Success |
| --- | --- | --- |
| `GET /api/auth/status` | Whether first-run is needed, and the caller's own user if signed in; while first-run is open, `firstRun: { dataFolder, host }` | `200` |
| `POST /api/auth/first-run` | Create the first administrator (needs the setup code from the server log) and sign them in | `201` user, sets session cookie |
| `POST /api/auth/login` | Sign in; generic failure either way (never "user not found") | `200` user, sets session cookie |
| `POST /api/auth/logout` | Revoke the current session | `204` |
| `POST /api/auth/password` | Change your own password (current one required); ends your other sessions (cookie session only) | `204` |
| `POST /api/auth/password-reset/request` | "Forgot password?": ask for a reset link by email (anonymous; only when [email is set up](#email-settings)) | `202` `{ message }`, the same for every address |
| `POST /api/auth/password-reset/complete` | Spend a reset link and set the password: `{ token, newPassword }` (anonymous; the link is the credential) | `204` |
| `POST /api/users/{id}/password-reset-link` | Create a single-use reset link for another person (admin, cookie session only) | `201` `{ token, link?, expiresAt, expiresInHours, userEmail }` |
| `GET /api/admin/email` | What outgoing email is set to, and which fields `draughtsman.yaml` locks (admin, cookie session only) | `200` |
| `PUT /api/admin/email` | Save the outgoing email settings; applies at once, no restart (admin, cookie session only; [rules](#email-settings)) | `200` the settings |
| `DELETE /api/admin/email` | Forget the saved settings and password, back to `draughtsman.yaml` alone (admin, cookie session only) | `200` the settings |
| `POST /api/admin/email/test` | Send a test email to the signed-in administrator, of the saved settings or of `{ values }` without saving them (admin, cookie session only) | `200` `{ ok, message }` |
| `GET /api/users` | List users (admin only) | `200` array |
| `POST /api/users` | Create a local user (admin only) | `201` user |
| `PATCH /api/users/{id}` | Change role, reset password, or (de)activate (admin only) | `200` user |
| `GET /api/tokens` | List the caller's own API tokens | `200` array |
| `POST /api/tokens` | Create a token for the caller: `{ name, expiresInDays?, scope? }` (cookie session only) | `201` `{ id, name, createdAt, expiresAt, scope, token }` |
| `DELETE /api/tokens/{id}` | Revoke a token (owner, or any admin on a cookie session) | `204` |
| `GET /api/admin/tokens` | Every user's API tokens with their owner, newest first (admin, cookie session only) | `200` array |
| `GET /api/admin/security-events` | The security event log, newest first, filterable (admin, cookie session only) | `200` page |
| `GET /api/admin/audit?documentId=` | One document's audit rows, newest first (admin, cookie session only) | `200` page |

All request and response bodies are JSON. `GET /api/auth/status`:

```json
{ "firstRunRequired": false, "user": { "id": "usr_…", "email": "eric@example.com", "displayName": "Eric", "role": "Admin", "createdAt": "…", "lastSignInAt": "…", "active": true },
  "passwordReset": { "selfService": true } }
```

`passwordReset.selfService` is whether "Forgot password?" is offered (emailed links are set up, `server.publicUrl` is set and
`auth.passwordReset.selfService` is not `false`); it is the only thing about the mail configuration an anonymous caller is told.

While no administrator exists, and only then, the body also carries `"firstRun": { "dataFolder": "/data", "host": "container" }` so the first-run page can name the folder and open the right "where is the setup code" hint. `host` is `console`, `container`, `systemd`, `launchd` or `windowsService`, guessed from the environment; it never changes behaviour. The setup code itself is never in a response. After the first administrator exists `firstRun` is absent.

`POST /api/auth/first-run` takes `{ email, displayName, password, setupCode }`; `POST /api/users` takes
`{ email, displayName, password, role }` (`Admin` or `Editor`). For both, a password under 12 characters is `400`, a duplicate email is `409`.
`PATCH /api/users/{id}` takes any subset of `{ role, password, active }` and applies only what is present; changing
`password` revokes every existing session and API token for that account. An admin cannot deactivate or demote their own account
through this endpoint (`400`) - a self-service way to lock the only signed-in admin out is not a feature. `POST
/api/tokens` takes `{ name }` and returns the raw secret (`dst_<43 chars>`) exactly once, in this response, never
again; `GET /api/tokens` and every other read return only `{ id, name, createdAt, lastUsedAt, revoked }`.

```bash
# First run: setupCode is the one printed in the server log at startup (placeholder shown)
curl -s -c cookies.txt -X POST http://localhost:5180/api/auth/first-run \
  -H 'Content-Type: application/json' \
  -d '{ "email": "eric@example.com", "displayName": "Eric", "password": "a-long-enough-password", "setupCode": "ABCD234567" }'

# Create a token, then use it (no cookie, no CSRF header needed)
curl -s -b cookies.txt -X POST http://localhost:5180/api/tokens \
  -H 'Content-Type: application/json' -H "X-XSRF-TOKEN: $(grep XSRF-TOKEN cookies.txt | cut -f7)" \
  -d '{ "name": "CI token" }'
curl -s -H 'Authorization: Bearer dst_…' http://localhost:5180/api/documents
```

### Changing your own password

`POST /api/auth/password` takes `{ currentPassword, newPassword }` and answers `204`. It is for a signed-in
browser session only (a token, even knowing the password, gets `403`; CSRF applies as for every cookie request).
The current password is required and checked with the same Argon2id verification as sign-in; a wrong one is a
generic `400 The current password is incorrect.`, and guessing it is throttled per user with the sign-in budget
(five wrong guesses, then `429` for the rest of the 15-minute window, even for the right password; a correct
change refunds its own charge). A new password under 12 characters, or one equal to the current, is `400`. On
success every *other* session of that user is revoked and the one making the request stays signed in; their API
tokens are left alone (revoke them on the token page; an admin's reset below is the one that revokes tokens too).
Recorded as `password.changed` (or `password.change_failed`).

### Password reset

Two ways to a new password for someone who has lost theirs, sharing one token mechanism and one page (`/reset-password`).
Implementation is `Auth/PasswordReset/` and `Email/`; tests are `AdminResetLinkTests`, `PasswordResetEmailTests`,
`PasswordResetThrottleTests`, `PasswordResetLeakTests`, `PasswordResetStorageTests`, `SmtpEmailSenderTests`, `EmailEndpointTests`
and the `PasswordResetStoreContractTests` provider suite. Design: `design/atlas/password-recovery.md`.

**The token.** A link's secret is 32 bytes from the system CSPRNG, base64url under a `dpr_` prefix (47 characters). Only its
SHA-256 is stored (table `password_reset_tokens`: the hash, the account, `purpose` `email` or `admin`, created, expiry, used and
invalidated times, and for an administrator's link who created it). The secret is shown once, in the response that creates it or
in the email; it is never logged, never in a security event, an audit row, a backup or the whole-instance export. A link is live
while it is unused, not invalidated and unexpired; using it is one atomic conditional update inside a serializable transaction
(two concurrent uses: exactly one wins), which also writes the password, revokes **every session** of the account and ends its
other live links. The account's API tokens are **not** revoked (see the admin guide for why). Expiry: 60 minutes for an emailed
link, 24 hours for an administrator's (`auth.passwordReset.emailedLinkMinutes` 5 to 1440, `adminLinkHours` 1 to 168; values outside
the range are pulled back to it). A link ends when: it is used; a newer **administrator** link is created for the same person; any
other link for them is used; their password is changed by anyone (an administrator through `PATCH /api/users/{id}`, themselves,
or `reset-admin`); the account is deactivated; or an email it was made for could not be sent.

**Where the secret travels.** In the URL **fragment**: `https://host/reset-password#token=dpr_…` (built from `server.publicUrl` when
set, else by the administrator's page from its own address). A browser does not send the fragment, so the secret is in no access
log, proxy log or `Referer`, and a mail scanner that fetches the link cannot spend it. The page reads it, removes it from the
address bar (`history.replaceState`) and POSTs it in a JSON body. The server never reads a token from a query string, a path or a
header (tested). `Referrer-Policy: same-origin`, the CSP (`default-src 'self'`, `connect-src 'self'`, `frame-ancestors 'none'`) and
`Cache-Control: no-store` on `/api` apply as on every response.

**`POST /api/users/{id}/password-reset-link`** (admin on a browser session; CSRF applies; a token of any scope, an Editor or an
anonymous caller is refused `403`/`401`). Creates the link and ends the person's other live links. `201`
`{ "token": "dpr_…", "link": "https://…/reset-password#token=dpr_…" | absent, "expiresAt": "…", "expiresInHours": 24, "userEmail": "…" }`.
`404` for an unknown id, `409` for a deactivated account. Records `password_reset.link_issued` (actor, target, expiry, how many
earlier links it ended; never the link). The response is `Cache-Control: no-store`.

**`POST /api/auth/password-reset/request`** `{ email }` (anonymous; an API token is refused `403`). `404` unless emailed links are
available. `400` for a body with no plausible address (a function of the input only). Otherwise **always** `202`
`{ "message": "If an account with that address can be reset, we have emailed it a link. The email can take a few minutes to arrive." }`,
for an address with an account, without one, a deactivated one or one typed in capitals. The handler does only what does not depend
on the account (throttle, one `password_reset.requested` event holding the address as typed and no account id, enqueue) and
answers; finding the account, making the link and talking to the mail server happen afterwards in a background worker
(`PasswordResetMailWorker`), so the response and its time are the same either way (a test gives the mail server 700 ms and compares
the medians). The worker sends nothing for an unknown address, a deactivated account or an address that is not a plain `name@host`;
it never ends a link, revokes a session or changes a password; it creates a link only if fewer than three emailed links were
created for that account in the last 15 minutes. Because the earlier links stay live, anyone typing a stranger's address can only
cause up to three emails to that inbox per 15 minutes, never replace or use up the link already there. A send that fails ends the
link made for it and records `password_reset.email_failed` with the reason.
Throttles (in memory, charged on every request since success cannot be told from failure): ten requests per network address, and three
for the same email from the same address, in 15 minutes, then `429`; a trip is recorded once per window as `sign_in.throttled`.

**`POST /api/auth/password-reset/complete`** `{ token, newPassword }` (anonymous; an API token is refused `403`). The password is
checked first (under 12 characters: `400 Password must be at least 12 characters.`, the link untouched, whatever the link is). Then
any link that is unknown, malformed, used, expired, ended or whose account is deactivated gets one answer:
`400` `{ "title": "Link not valid", "detail": "This reset link is not valid. It may have expired or already been used. Ask for a new one." }`.
Success is `204`; **it does not sign anyone in**. Lookup is by SHA-256 of the secret, compared again in constant time. A
Unicode password and one of several thousand characters work; the body limit for `/api/auth` (16 KiB) applies (`413`). Throttles:
twenty failed attempts per network address in 15 minutes (`429`; a success refunds its own charge, a short password is not
charged), and a budget per (account, address) for repeating a **dead** link of that account (further tries are refused as above and
logged once); a live link is never subject to the per-account budget, so repeating an old link cannot keep its owner out.

Security events (all strings, never a secret): `password_reset.requested`, `password_reset.link_issued`, `password_reset.email_sent`,
`password_reset.email_failed`, `password_reset.link_used`, `password_reset.link_refused` (`detail` is the class: `link not recognised`,
`link already used`, `link expired`, `link ended by a newer link, a password change or a deactivation`, `account deactivated`),
`password_reset.completed` (sessions signed out, links ended, "API tokens left as they were"; `method` is `reset_link`),
`email.test_sent` and `email.test_failed`, and `email.settings_changed` and `email.settings_reset`. `X-Draughtsman-Actor` is not read by any of these endpoints.

The table is part of the database snapshot a **backup** takes, but the snapshot's rows are deleted (with `secure_delete` and a
`VACUUM`) before it is zipped, so a backup carries no link and a restore cannot revive one; the live database is untouched. The
whole-instance export holds documents and assets only. The daily maintenance pass deletes ended links older than
`retention.expiredSessionDays` (7). Postgres: use `pg_dump`, which does include the table; its rows are hashes of links that
expire within a day.

### Email settings

Outgoing email is set at **Admin, Email**, or in `draughtsman.yaml` (`email:` and `server.publicUrl`), or both. Every route below is
`CookieAdminOnly`: an administrator on a browser session. An Editor gets `403`, an API token gets `403` whatever its owner's role (a
token must not be able to repoint the instance's mail), and an anonymous caller `401`. Writes carry the CSRF header.

**Precedence.** A value `draughtsman.yaml` sets **wins**: its field is reported in `locked` with the reason and cannot be changed
through the API. A key left at its shipped default (empty text, port `587`, `starttls`) counts as not set, so it locks nothing. The
SMTP password follows the same rule: a value from the environment variable or the password file named in the yaml wins and locks
`password`; otherwise the saved, encrypted one is used. A saved value for a field the yaml later takes over stays on disk underneath and
applies again if the yaml key is removed. With `email.provider: file` in the yaml, `host`, `port`, `tls`, `user` and `password` are
locked too (no mail server is used); `from`, `replyTo` and `publicUrl` stay editable.

`GET /api/admin/email` returns what the two add up to:

```json
{ "configured": true, "provider": "smtp", "from": "Draughtsman <noreply@example.com>", "replyTo": "...", "host": "smtp.example.com",
  "port": 587, "tls": "starttls", "user": "mailer", "passwordSet": true, "passwordKind": "saved", "passwordUnreadable": false,
  "passwordSource": "saved on this server (encrypted)", "outboxFolder": "...", "publicUrl": "https://draughtsman.example.com",
  "locked": { "host": "Set in draughtsman.yaml. Edit that file to change it." }, "hasSaved": true,
  "selfServiceEnabled": true, "selfServiceAvailable": true, "emailedLinkMinutes": 60, "adminLinkHours": 24, "problems": [], "keyRing": { } }
```

Absent members are omitted. `passwordKind` is `environment`, `file` (both lock the field), `saved` or `none`. **The password is never in any
response**, not in any call below either: `passwordSet` says whether one is available and `passwordSource` names where it comes from.
`passwordUnreadable` is true when a saved password cannot be decrypted (the ring changed, or an earlier build tied it to an install folder nothing names; `problems` carries the reason and what to do, as `keyRing.storedSecrets` does): name the old folder or enter it again. `keyRing` is the same
state the AI settings report, so the page can warn while the ring that encrypts the saved password is not protected at rest.

`PUT /api/admin/email` validates, stores and **applies at once, with no restart** (the sender reads the current settings on every
message), and answers the new state as above. Body (every member optional; a blank text field clears its saved value):

```json
{ "from": "...", "replyTo": "...", "host": "smtp.example.com", "port": 587, "tls": "starttls", "user": "mailer",
  "publicUrl": "https://draughtsman.example.com", "passwordAction": "keep", "password": "only with passwordAction replace" }
```

- `passwordAction` is `keep` (the default, and what leaving it out means: an edit elsewhere never loses the password), `replace`
  (needs `password`) or `clear`. A password with no user name to go with it is refused, and clearing the user name drops a saved password.
- A **locked** field may be sent back as the page showed it (it is ignored); a different value is refused with `400` naming the field.
  `replace` or `clear` on a locked password is refused the same way.
- Email is saved whole or not at all: when any mail server field is filled in, the result must have what a send needs (host, From, a
  password if there is a user name, a valid port and encryption), or the whole save is refused and nothing changes. Leaving every mail
  server field blank, with only the public address, saves just that and leaves email off.
- `400` is Problem+JSON with `detail` (the sentences, joined) and `errors: [{ field, message }]`. A message names the field and never
  repeats the rejected value. What is refused: a **host** that is not a plain host name or address (no URL, `user@`, path, query, port,
  whitespace, brackets or non-ASCII; at most 253 characters); a **From or Reply-to** that is not one address (`name@host` or `Name
  <name@host>`, no comma or semicolon, at most 320 characters); a line break or any control character in the **From, Reply-to, user
  name, password or public address** (this is what stops a forged header or an injected SMTP command); a user name over 256 or a
  password over 512 characters; a **port** outside 1 to 65535; **encryption** other than `starttls` or `none` (implicit TLS on port 465 is
  not supported by the framework's mail client: use the provider's 587 option); `none` with a user name for a server on another machine
  (a password is never sent unencrypted to another machine); a **public address** that is not an `http` or `https` address without user
  name, query or fragment (at most 2048 characters). `500` means the settings file could not be written; nothing changed.
- The change is recorded as `email.settings_changed` (below).

`DELETE /api/admin/email` forgets every saved field and the saved password and applies at once: only the yaml remains. Recorded as
`email.settings_reset`.

`POST /api/admin/email/test` sends one message to the **signed-in administrator's own address** (a `to` in the body is ignored: it cannot
be used to mail anyone else) and answers `200` `{ ok, message }`. With no body it tests what is **saved**. With `{ "values": { ...the
PUT body... } }` it tests those values **without storing anything** (a typed password is used for that call only; `keep` uses the saved
one). The values are validated exactly as for `PUT` (`400` with the same shape, not counted against the limit). `ok: false` carries a
short class of failure ("Could not connect to host:port", "did not accept the user name or password", "STARTTLS ...", "refused the
recipient address", "did not answer within N seconds") and **never the mail server's own reply text**, which is text an arbitrary host
chose. At most five tests per administrator per 15 minutes, saved and unsaved together (`429`).

**What an administrator can do with this, said plainly.** Setting a mail server lets an administrator make this server open a connection
to any host and port they enter. That is what an outgoing-mail setting is, and is why only an administrator on a browser session may set
it, the test mail goes only to that administrator, and what comes back is a class of failure rather than the other end's words. It does
not make Draughtsman a relay: nothing a user types is ever the recipient of a message. Provider notes, STARTTLS behaviour and the
options are in [`deployment.md`](deployment.md#outgoing-email).

**Where it is stored.** `<data>/email/settings.json` (owner-only): the fields as text, and the password only as an ASP.NET Core Data
Protection payload under the key ring in `keys/`, so a lost key ring makes it unreadable (`passwordUnreadable`) rather than recoverable.
It is a secret folder for backups: left out of one unless `--include-secrets`, like `ai/`.

### API tokens

A token is always minted for the signed-in caller themselves, through a browser session: there is no way to create
one for another user, and a token can never mint a token or reach `/api/users` (security review F3). Every token
created now **expires** and may be **read-only**:

- `expiresInDays` (a whole number, default `auth.tokens.defaultLifetimeDays` = 90) may not exceed
  `auth.tokens.maxLifetimeDays` (default 365); more, zero or negative is `400`. Both are operator settings in
  `draughtsman.yaml`, read at startup (an out-of-range configured value is clamped to 1 to 3650 days, and the default
  is pulled down to the ceiling, so a typo can neither disable expiry nor mint a dead token):
  ```yaml
  auth:
    tokens:
      defaultLifetimeDays: 90
      maxLifetimeDays: 365
  ```
  An expired token is refused on its next request with the same `401` as an unknown or revoked one (the response
  never says which). `expiresAt` is omitted, and `expired` is false, for a token created before expiry existed.
- `scope` is `full` (the owner's whole role: the default, and how every token behaved before scopes) or `read`. A
  read token may make `GET`, `HEAD` and `OPTIONS` requests and nothing else: any other method is `403 Read-only
  token` before authorization or the handler runs, for every route (one middleware, `TokenScopeMiddleware`, swept
  over every registered route by `TokenExpiryAndScopeTests`). Over `/mcp`, the one POST surface, a read token can
  list, read, render, diff, critique and fetch the schema, but `create_document` and `patch_document` refuse it.
- `lastUsedAt` is stamped on every authenticated request and listed; it is `null` until first use.
- **An administrator sees every token.** `GET /api/admin/tokens` (admin **and** a browser session: a token, even an admin's,
  gets `403`; `/api/tokens` stays the caller's own list for everyone, with no `?all`) returns each token as the own-token
  list does plus `ownerId`, `ownerEmail` and `ownerName`. It never carries the secret or its hash. Revoking from it is the
  ordinary `DELETE /api/tokens/{id}`, which records a `token.revoked` security event naming the administrator as the actor
  and the owner as the target. The Tokens page shows it to administrators as "Everyone's tokens".
- **Existing tokens are grandfathered.** The migration that added these columns (`AddSecurityEventsAndTokenExpiry`)
  leaves every existing token with no expiry and `full` scope, rather than giving it a date: a date chosen by a
  migration would break an agent's configuration with no warning at an arbitrary moment, and this product has had no
  production release, so the few tokens that exist are the operator's own. The token page flags them ("No expiry")
  so their owners can replace them; a revoked token stays revoked.

### Security events

Every authentication and administration event writes a row to the instance-level `security_events` table (the
document `audit` table is per document and stays as it was) and a structured log line under the
`Draughtsman.Server.Security` logger. A row carries the **type**, the time, the **actor** (user id and email as they
were then), the **target** account, the **source IP** (after `server.knownProxies` forwarding), how the caller was
authenticated (`method`: `session`, `token`, `password` for a sign-in, `host` for the shell recovery,
`anonymous`) and a short `detail`. It never holds a password, token, session value or hash; the free text is stripped
of control characters and capped so it cannot forge a log line; an attempted sign-in email is stored only if it
looks like one (people paste passwords into that box).

| Type | When | Notes |
| --- | --- | --- |
| `sign_in.succeeded` / `sign_in.failed` | Every sign-in attempt | A failure's `detail` says `wrong password`, `unknown account` or `inactive account`; the response to the caller is identical for all three. |
| `sign_in.throttled` | The failed-attempt throttle refused a request | Once per bucket per 15-minute window, not once per refused request. Also first-run setup-code and password-change guessing. |
| `sign_out` | Sign-out | |
| `first_run.completed` / `first_run.failed` | First administrator created / wrong setup code | |
| `user.created`, `user.role_changed`, `user.deactivated`, `user.reactivated` | Admin account changes | Only recorded when something actually changed. |
| `password.reset` | An admin set another account's password | Its sessions and tokens were revoked. |
| `password.changed` / `password.change_failed` | Self-service change | |
| `password_reset.requested` | Someone used "Forgot password?" | Recorded for every address alike; holds the address as typed, never an account id. |
| `password_reset.link_issued` | An administrator created a [reset link](#password-reset) | Actor, target and expiry; never the link. |
| `password_reset.email_sent` / `password_reset.email_failed` | A reset email was handed to the mail server / could not be | The failure reason is in `detail`. |
| `password_reset.link_used` / `password_reset.completed` | A reset link was spent / the password changed with it | `method` is `reset_link`. |
| `password_reset.link_refused` | A link was refused | `detail` is the reason class only. |
| `email.test_sent` / `email.test_failed` | An administrator pressed one of the test buttons | `detail` says whether the saved settings or unsaved form values were used; a failure is a class of reason, never the server's reply. |
| `email.settings_changed` / `email.settings_reset` | An administrator saved / removed the [email settings](#email-settings) | `detail` names what changed ("mail server, password replaced"), never a value. |
| `ai.endpoint_host_changed` | An administrator changed the host the [AI endpoint](#ai-settings) points at (directly, by switching provider, or by resetting to `draughtsman.yaml`) | `detail` names the host and port before and after (`none` when AI had no endpoint) and says whether the stored key was cleared because of it. Hosts are not secrets; the key never appears. |
| `ai.endpoint_refused` | An administrator tried to save an AI endpoint the [endpoint policy](#ai-settings) refuses | Logged at warning level. `detail` names the host that was refused and the rule in fixed words. Nothing was changed. |
| `theme.url_fetch` | An administrator asked the server to fetch a page for [a draft theme](#extract-a-draft-from-css-or-a-web-address) | `detail` is `host=<host> outcome=<ok, or why it was refused>`; never the path, the query or any of the page. |
| `token.created` / `token.revoked` | API token lifecycle | `detail` has the token's id and name, never the secret. |
| `admin.recovered` | The host-shell `reset-admin` ran | `method` is `host`; the actor names the OS user. |
| `data.exported` | An admin downloaded [the whole-instance export](#admin-export) | `detail` has the document and asset counts, and how many came from the bin when it was asked for. |
| `document.deleted` | A document was moved to [the bin](#bin) | The target label is the document's title; `detail` starts with its id. |
| `document.restored` | A document was taken out of the bin | Same shape. |
| `document.purged` | A binned document was removed for good, by a person (`DELETE /api/bin/{id}`) or by the retention pass | For the retention pass the actor is `retention policy`, `method` is `host`, and `detail` says how long it had been in the bin. |

`GET /api/admin/security-events` (admin **and** a browser session: a token, even an admin's, gets `403`, and nothing
in the API writes or deletes an event) returns `{ items, total, offset, limit }`, newest first. Query parameters,
all optional and combined with AND: `type` (one id or a comma-separated list), `user` (a user id, matched as the
actor or the target), `ip`, `since` (inclusive) and `until` (exclusive) as ISO 8601, `offset` (default 0) and `limit`
(1 to 200, default 50); anything unparsable is `400 Invalid query parameter`.

```json
{ "items": [ { "id": 41, "at": "2026-09-30T10:02:11.000Z", "type": "user.role_changed", "actorUserId": "usr_…",
    "actorLabel": "eric@example.com", "targetUserId": "usr_…", "targetLabel": "jane@example.com",
    "sourceIp": "203.0.113.9", "method": "session", "detail": "Editor to Admin" } ],
  "total": 1, "offset": 0, "limit": 50 }
```

`GET /api/admin/audit?documentId=doc_…` is the read side of the per-document audit trail (below), under the same
access rule, newest first, paged the same way (`offset`, `limit`); it survives the document's deletion. Events are
kept indefinitely: there is no retention job yet (the throttle bounds how fast failed sign-ins can add rows).

### Lost-admin recovery

If **no** administrator can sign in, a [reset link](#password-reset) cannot help (it needs one to create it, or working email), and
there is deliberately no HTTP way back in. The recovery is on the host, and unchanged by password reset: `draughtsman reset-admin <email> [--generate] [--promote]`
(`Auth/Recovery/ResetAdminCommand.cs` over `AdminRecoveryService`). It reads the same `draughtsman.yaml` and data
folder as the server (`DRAUGHTSMAN_DATA`), sets a new Argon2id password for that administrator, **reactivates** the
account if it was deactivated, and **revokes every session and API token** it held; a running server honours that on
its next request. The password is never an argument (shell history, process list): `--generate` prints a random one
once, else it comes from `DRAUGHTSMAN_NEW_PASSWORD`, piped standard input, or a hidden prompt. An Editor is refused
unless `--promote` is given, so a mistyped address cannot hand out admin rights. Exit `0` done, `1` refused, `2`
usage. It writes an `admin.recovered` security event naming the OS user that ran it.

**Trust model.** The credential is shell access to the host as a user who can read and write the data folder (and,
on Postgres, the connection string in `draughtsman.yaml`). Whoever has that can already read every password hash,
session and document and could edit the database directly, so this adds no capability; it makes the recovery
supported, safe and recorded instead of a hand-edited `users` table. It is the same boundary as
`draughtsman mcp --stdio`. There is no HTTP endpoint for it and no "recovery code": anything reachable over the
network would be a second way in for an attacker.

**Invocation.** It is a verb of the same binary (`Cli/ServerCli.cs`, listed in `draughtsman --help`): it never starts the web
host, and `--DRAUGHTSMAN_DATA=<folder>` picks the data folder as for `serve` and `mcp --stdio`. `ResetAdminTests` runs it through that dispatcher.

`/mcp` (below) requires the same bearer token over HTTP; `draughtsman mcp --stdio` is unaffected, since it never
binds HTTP at all and stays inside the trust boundary of the machine and data folder it runs on.

## Endpoints

| Method and path | Purpose | Success |
| --- | --- | --- |
| `GET /api/documents` | List documents (search, sort and type filter); newest updated first by default; never the bin | `200` page |
| `POST /api/documents` | Create a document | `201` document, `Location` header |
| `GET /api/documents/{id}` | Read the current snapshot; `If-None-Match` makes it conditional | `200` document, or `304` |
| `PUT /api/documents/{id}` | Replace the snapshot; requires `If-Match` | `200` document |
| `DELETE /api/documents/{id}` | Move the document to the bin (recoverable; see [Bin](#bin)) | `204` |
| `POST /api/documents/{id}/duplicate` | Store a copy under a new id | `201` document, `Location` header |
| `PATCH /api/documents/{id}` | Rename the document; requires `If-Match` | `200` document |
| `GET /api/documents/{id}/versions` | List named snapshots, newest first | `200` page |
| `GET /api/documents/{id}/versions/{vid}` | Read one snapshot | `200` version with `document` |
| `POST /api/documents/{id}/versions` | Name a snapshot of the document as stored now | `201` version summary |
| `POST /api/documents/{id}/versions/{vid}/restore` | Put the document back to a version (checkpoints first); requires `If-Match` | `200` document |
| `GET /api/documents/{id}/diff?from=&to=` | Structured diff between two revisions (`diff_documents`'s shape) | `200` |
| `GET /api/documents/{id}/critique` | The structural critique of the stored document (`critique_document`'s shape) | `200` |
| `POST /api/critique` | The same, for a document in the body (what is on the canvas, saved or not) | `200` |
| `GET /api/bin` | The bin this caller may see; see [Bin](#bin) | `200` |
| `POST /api/bin/{id}/restore` | Take a document out of the bin | `200` |
| `DELETE /api/bin/{id}` | Remove a binned document for good (browser session only) | `204` |
| `GET /api/documents/{id}/pdf` | Render a single-page PDF (optional Chromium component) | `200` PDF, `501`, `504` or `429` |
| `GET /api/documents/{id}/thumbnail` | A document drawn small, as an SVG that fits a 320 by 200 box; see [Thumbnail](#get-apidocumentsidthumbnail) | `200` SVG, `304` or `404` |
| `GET /api/health` | Liveness, version, storage provider and export capability | `200` |
| `GET /api/theme` | The configured chrome theme: product name, mode, logo/favicon paths | `200` |
| `GET /api/theme/chrome.css` | Chrome theme tokens as CSS custom properties | `200` text/css |
| `GET /api/theme/logo` | The theme's logo asset | `200` image, or `404` |
| `GET /api/theme/logo-light` | The light-on-dark logo asset (falls back to the logo) | `200` image, or `404` |
| `GET /api/theme/favicon` | The theme's favicon asset | `200` image, or `404` |
| `GET /api/admin/branding` | The effective branding and which slots are overridden (admin only) | `200` |
| `PUT /api/admin/branding` | Set the product name; blank resets it (admin only) | `200` |
| `POST /api/admin/branding/{slot}` | Upload `logo`, `logo-light` or `favicon` (admin only) | `200` |
| `DELETE /api/admin/branding/{slot}` | Remove one override (admin only) | `200` |
| `POST /api/admin/branding/reset` | Remove every override (admin only) | `200` |
| `POST /api/assets` | Upload an image for an image node | `201` asset |
| `GET /api/assets/{id}` | Serve an uploaded image | `200` image, or `404` |
| `POST /api/import/mermaid` | Convert a Mermaid block into a document (nothing is saved) | `200` document and warnings |
| `POST /api/import/drawio` | Read a draw.io (diagrams.net) file into a placed document (nothing is saved) | `200` document, warnings and pages |
| `POST /api/import/visio` | Read a Visio (`.vsdx`) file into a placed document (nothing is saved) | `200` document, warnings and pages |
| `POST /api/export/dsl` | Print an unsaved document as canonical DSL (nothing is saved) | `200` text |
| `POST /api/import/dsl` | Parse DSL text into a document (nothing is saved) | `200` document and warnings |
| `POST /api/import/sql` | Read CREATE TABLE DDL into entity tables and relationships (nothing is saved) | `200` document and warnings |
| `POST /api/export/sql` | Print an unsaved entity diagram as CREATE TABLE DDL (nothing is saved) | `200` text |
| `POST /api/export/mermaid` | Print an unsaved document as Mermaid text (nothing is saved) | `200` text |

### `GET /api/documents`

Query parameters:

| Name | Default | Notes |
| --- | --- | --- |
| `q` | none | Case-insensitive substring match on the title, Unicode included. `%` and `_` are literal. |
| `offset` | `0` | Rows to skip. |
| `limit` | `50` | Rows to return, 1 to 200. |
| `sort` | `modified` | `modified`, `created`, `title` or `type`. Ties always break on id, so paging never reshuffles. |
| `dir` | the column's natural one | `asc` or `desc`, as the reader sees it: `asc` is A to Z or oldest first. Natural is `desc` for `modified` and `created`, `asc` for `title` and `type`. |
| `type` | none | Only `flowchart`, `architecture`, `freeform` or `sequence` documents. |

A document in the [bin](#bin) is never listed, searched or counted. Response:

```json
{
  "items": [
    { "id": "doc_9f1c2b3a4d5e", "title": "Order service", "type": "architecture", "workspace": "default",
      "createdAt": "2026-09-21T09:30:15.123Z", "updatedAt": "2026-09-21T10:02:44.001Z" }
  ],
  "total": 1,
  "offset": 0,
  "limit": 50
}
```

`total` is the count of matching documents, not the page size. Only the `default` workspace exists until workspaces
land. An out-of-range `offset` or `limit` is `400 Invalid query parameter`.

### `POST /api/documents`

Body: a document. The server fills in what a client (or an agent over MCP) does not need to know:

| Member | When absent |
| --- | --- |
| `id` | A new `doc_` id is assigned. |
| `schemaVersion` | The current version (`1`). |
| `meta` | Created with `createdAt` and `updatedAt` set to now and `createdBy` set to the actor. |
| `meta.createdAt`, `meta.updatedAt`, `meta.createdBy` | Filled individually the same way. |

The completed body is validated against the schema; anything else is `400 Document is not valid` with `errors`. That
includes the optional `flythrough` member (a camera path, `docs/flythrough.md`): it is stored and served back with the
document, and a path the schema refuses is a `400` naming the pointer (`/flythrough/keyframes/0/view/w`).
`errors` is a list of `location: message` lines, the location a JSON pointer into the body (`(document)` for the body itself):
`/nodes/0/id: The string value is not a match for the indicated regular expression`. An object or array that has a failing
member also says which members failed (`/nodes/0: Some properties did not match the required schema: ["id"]`), and the lines
come outermost first. The schema is checked in-house (`SchemaValidator`, `docs/adr/0004-document-schema-validator.md`); a number
too large to compare or text that is not valid Unicode (a lone surrogate) is an ordinary line, never a `500`.
A body that is not a JSON object is `400 Malformed body`. An id already in use is `409 Document already exists`.

**What a body needs** (QA-09). The server fills `id`, `schemaVersion` and `meta`, but not the parts of a node or an edge, so a
node needs all of `id`, `kind`, `label` **and `shape`**, and an edge needs `id`, `kind`, `from` and `to`, each an **object** naming
the node (`{ "node": "a" }`, optionally with `"port"`), not a bare id. `shape` is the part people miss: the DSL and the Mermaid, SQL
and DSL importers work it out from the kind, but a hand-written JSON node without one is refused:

```json
{ "title": "No shape", "type": "architecture", "nodes": [ { "id": "a", "kind": "service", "label": "A" } ], "edges": [] }
```

answers `400 Document is not valid` with `"/nodes/0: Required properties [\"shape\"] are not present"`. A shape is one of the names in
`docs/schema/document.schema.json` (`rectangle`, `rounded`, `diamond`, `cylinder`, ..., or `custom:<library>/<name>`; `rect` is not
one). The smallest valid body:

```json
{ "title": "Minimal", "type": "architecture",
  "nodes": [ { "id": "a", "kind": "service", "label": "A", "shape": "rectangle" },
             { "id": "b", "kind": "store", "label": "B", "shape": "cylinder" } ],
  "edges": [ { "id": "e1", "kind": "flow", "from": { "node": "a" }, "to": { "node": "b" } } ] }
```

Easier: send DSL text to `POST /api/import/dsl` (below), which infers shapes, and post the `document` it returns.

On success the server records an audit row with action `created` and returns `201` with the stored document, its
version as an `ETag` and `Location: /api/documents/{id}`. No version row is written: the document itself is the
baseline. The response contains the snapshot committed by this request and its matching tag, even if another
write or delete lands before the response.

```bash
curl -s -X POST http://localhost:5180/api/documents \
  -H 'Content-Type: application/json' -H 'Authorization: Bearer dst_…' \
  -H 'X-Draughtsman-Actor: agent:claude-code/claude-fable-5-1' \
  -d '{ "title": "Order service", "type": "architecture", "nodes": [], "edges": [] }'
```

### `GET /api/documents/{id}`

Returns the stored snapshot with its version as an `ETag` (and as `X-Document-Version`), or `404 Document not found`.
That `ETag` is what a later `PUT` of this document has to quote in `If-Match`.

With `If-None-Match` carrying a version the caller already has, the read is conditional: `304 Not Modified` with the
same `ETag` and no body while that is still the stored version, the whole document with its new `ETag` once any writer
has saved over it. Any tag in the list matching counts, and `*` always matches. The `304` is answered from the stored
version tag alone, without loading the snapshot, so the one-second poll of every open editor costs one narrow read.

This is how an open editor sees another writer's change (M2 checklist item 30): it polls this endpoint once a second
with its current base version, and merges a changed document into the open Y.Doc under a non-local origin
(`web/src/model/remoteMerge.ts`), so the change appears without a reload, is never undone by the local user's undo, and
leaves their unsaved edits in place; the next save quotes the merged-in version. Polling rather than a push channel is
deliberate: `patch_document` over stdio runs in a separate process against the same store, so only the stored
version sees every writer. The Yjs WebSocket hub in the build spec replaces this when realtime collaboration lands.

### `PUT /api/documents/{id}`

`If-Match` must carry the base version this save was made from; see [Base versions](#base-versions). It is checked
before anything is written or checkpointed, and again inside the `UPDATE` itself, so two saves from the same base
version cannot both land.

```bash
# Read, keeping the version; then save from it. A save from a version that has moved on is 412, not a silent overwrite.
version=$(curl -sD - -o diagram.json -H 'Authorization: Bearer dst_…' http://localhost:5180/api/documents/doc_9f1c2b3a4d5e | awk -F': ' '/^[Ee][Tt]ag/ { print $2 }' | tr -d '\r')
curl -s -X PUT http://localhost:5180/api/documents/doc_9f1c2b3a4d5e \
  -H 'Content-Type: application/json' -H "If-Match: $version" -H 'Authorization: Bearer dst_…' \
  -H 'X-Draughtsman-Actor: agent:claude-code/claude-fable-5-1' \
  --data-binary @diagram.json
```

Body: the whole document. `id` may be omitted; if present it must equal the route id (`400 Document id mismatch`).
`meta.updatedAt` is always set to the server clock, because the snapshot is regenerated on every save and listings
order by it. `meta.createdAt` and `meta.createdBy` default to the stored values when omitted. Validation is as for
`POST`. Unknown ids are `404`.

When the actor is an agent, the server first takes a version named `checkpoint` of the snapshot being replaced, with
`reason` `Before replace by <actor>`, so an agent edit can always be diffed against, or rolled back to, what came
before. A write by a user actor takes no checkpoint; `document_versions` holds named snapshots and pre-agent-edit
checkpoints only. The server then records an audit row with action `replaced` and returns `200` with the stored
document committed by this request and its matching `ETag`, even if another write or delete lands before the
response. The checkpoint, the replace and the audit row are separate store calls, not one transaction; a delete
that prevents the checkpoint or replacement is reported as `404 Document not found`. A caller saving repeatedly
need not read the document back between saves.

### `DELETE /api/documents/{id}`

Moves the document to [the bin](#bin) (OP-02); nothing is destroyed. It keeps its snapshot, update log and versions but
every read, listing and write treats it as gone (`404`) until it is restored, and its id stays taken (`POST /api/documents`
with that id is `409`, and the message says it may be in the bin). An audit row with action `deleted` and a `document.deleted`
security event are recorded. Any signed-in writer may do this, an Editor included: it is recoverable, and the bin entry
remembers who did it. `204` on success, `404` when there was no live document with that id (a second delete included).
The permanent step is `DELETE /api/bin/{id}`.

### `POST /api/documents/{id}/duplicate`

Stores a copy under a new id: the same type, nodes, edges and theme, the title `"<title> (copy)"` (a copy of a copy keeps the
single suffix) or the `title` of the optional body `{ "title": "…" }`, a fresh `meta` with the caller as creator, and an
empty history (the original's versions stay the original's). Images are shared by reference. The copy is audited as `created`,
so its creator is the caller. `201` with the document, its `ETag` and `Location`; `404` for an unknown or binned document;
`400` for a `title` that is blank or not a string.

### `PATCH /api/documents/{id}`

Renames a document, changing only its title and restamping `meta.updatedAt` from the server clock; the graph, type
and creation metadata are untouched. Like `PUT`, it is a guarded write: `If-Match` must carry the base version the
rename was composed from, and a stale one is refused with `412 Document has changed` before anything is written. The
snapshot is regenerated through the model, so the version tag moves on and the next save must quote the new `ETag`.

Body: `{ "title": "<new title>" }`. A missing or blank `title`, or a body that is not a JSON object, is
`400 Malformed body`. On success the server records an audit row with action `renamed` and returns `200` with the
stored document and its new `ETag`. Unknown ids are `404`.

### `GET /api/documents/{id}/versions`

`404` for an unknown document, otherwise a page of version summaries, newest first. Paging parameters mirror
`GET /api/documents`:

| Name | Default | Notes |
| --- | --- | --- |
| `offset` | `0` | Rows to skip. |
| `limit` | `50` | Rows to return, 1 to 200. |

Response:

```json
{
  "items": [
    { "id": "v_8dd7e2a1c4b0f5e2a9c1b", "documentId": "doc_9f1c2b3a4d5e", "name": "checkpoint",
      "createdAt": "2026-09-21T10:02:44.001Z", "actor": "agent:claude-code/claude-fable-5-1",
      "reason": "Before replace by agent:claude-code/claude-fable-5-1", "size": 2410 }
  ],
  "total": 1,
  "offset": 0,
  "limit": 50
}
```

`size` (in each item, characters of the stored snapshot) is what the editor's History panel shows. `total` is the count of versions, not the page size. A version summary carries no `document`; read the snapshot with
`GET /api/documents/{id}/versions/{vid}`. `offset` must be an integer from 0 to 2147483647; a valid offset at or beyond
`total` returns an empty `items` array with the total unchanged. Non-integer or out-of-range `offset` or `limit`
values are `400 Invalid query parameter`; omitted or blank values use the defaults.

### `GET /api/documents/{id}/versions/{vid}`

The version summary plus the snapshot under `document`. `404 Version not found` when either id is unknown, or the document is in the bin.

## History, compare and restore

What the editor's History and Review panels use (PR-08, `design/atlas/version-history.md`, `critique-panel.md`). None of it is new
logic: compare is `DocumentDiff` and critique is `CritiqueAnalyser`, the code behind the `diff_documents` and `critique_document`
MCP tools, returned in the same shapes.

### `POST /api/documents/{id}/versions`

Body `{ "name": "Before the rework", "reason": "optional note" }`. Names a snapshot of the document **as stored now** (a client
with unsaved edits saves first). The name is trimmed, 1 to 256 characters, and may not be `checkpoint`, which is the reserved
name of the automatic snapshots retention prunes; a named version is never pruned. `201` with the version summary
(`actor` is the caller), `400` for a bad name, `404` for an unknown or binned document. Audit action `snapshotted`.

### `POST /api/documents/{id}/versions/{vid}/restore`

Puts the document back to a version: its title, nodes, edges and theme become the version's, while its id and creation
header stay and `meta.updatedAt` is the server's clock. It is a guarded write like `PUT`: `If-Match` must carry the version
the caller is looking at (`428` without it, `412` when stale, both before anything is written or checkpointed). It first stores
the document as it is as a `checkpoint` whose `reason` is `Before restoring "<name>" (<time>)`, so a restore is itself undone by
restoring that checkpoint from the same list. `200` with the restored document and its new `ETag`; `404` for an unknown version
or a binned document. Audit action `version_restored`. The editor adopts the result as its saved document; like any
server-side change it is not an entry in the browser's local undo stack.

### `GET /api/documents/{id}/diff`

`from` (required) and `to` (default `current`) are each a version id or the word `current` for the stored document.
Response: `{ "fromVersion", "toVersion", "diff": { addedNodes, removedNodes, changedNodes, addedEdges, removedEdges, changedEdges,
movedNodes, movedEdges, documentChanges, volatileChanges } }`, identical to the `diff_documents` tool: elements matched by id,
`changed*` entries listing the top-level fields that differ (and `props`, a per-key list of what changed in the element's
props). `documentChanges` lists the header fields that are part of the diagram (`title`, `type`, `schemaVersion`, `id` and
each `theme.<token>`), and `volatileChanges` the ones every save rewrites (`meta.createdAt`, `meta.updatedAt`, `meta.createdBy`,
`meta.aiProvenance`), kept apart so that no caller counts a save as an edit. `400` without `from`, `404` for an unknown document or revision, `422` when a revision has two
elements with one id (a diff matches by id).

### `GET /api/documents/{id}/critique`, `POST /api/critique`

`{ "findings": [ { "rule", "severity": "error"|"warning", "message", "nodeIds", "edgeIds" } ] }`, the `critique_document` result:
deterministic, errors then warnings then rule id, every evidence id an element that exists. The `GET` judges the stored
document; the `POST` takes a document body (validated against the document schema, `400` otherwise) and judges it as sent, which is
what the editor uses so that unsaved edits are included. Nothing is stored either way.

## Bin

Deleting is recoverable (OP-02). `DELETE /api/documents/{id}` moves the document to the bin; it stays there, with its versions,
for `retention.binDays` days (30 by default, `0` or less keeps it until someone deletes it permanently), and the daily
maintenance pass then removes it for good, recording each removal (audit action `purged` and a `document.purged` security event
from `retention policy`). Restoring brings it back exactly as it was, down to its `ETag`.

**Who can do what** (the Editor rule; it uses the signed-in user's own id and role, never the actor header):

| Caller | Sees in the bin | Can restore | Can delete permanently |
| --- | --- | --- | --- |
| Admin | everything | everything | everything |
| Editor | entries they created, or deleted | those entries | only documents they created |
| API token | what its owner may see | what its owner may restore | never (browser session only) |
| Read-scoped token | the same list (`GET`) | no (`403`) | no (`403`) |

An Editor can already change any document (the organisation-wide trust model), so an Editor may also delete a colleague's; the
entry then appears in both people's bins and either can restore it. An entry a caller may not see is `404`, not `403`, so the bin
does not reveal what others deleted. `DELETE /api/bin/{id}` needs a cookie session because nothing can undo it, and a caller who
may restore but not purge (an Editor who deleted a colleague's document) gets `403`.

### `GET /api/bin`

`offset` and `limit` as for the document list. Newest deletion first:

```json
{ "items": [ { "id": "doc_…", "title": "Order service", "type": "architecture", "createdAt": "…", "updatedAt": "…",
    "deletedAt": "2026-10-01T09:00:00.000Z", "deletedBy": "Eric", "purgesAt": "2026-10-31T09:00:00.000Z", "canPurge": true } ],
  "total": 1, "offset": 0, "limit": 50, "retentionDays": 30 }
```

`purgesAt` is absent when `retention.binDays` is `0` or less. `canPurge` says whether this caller may remove the entry for good.

### `POST /api/bin/{id}/restore`

`200 { id, title, type }` and the document is live again; `404` when it is not in the bin or the caller may not see it. An audit row
`restored` and a `document.restored` security event are recorded.

### `DELETE /api/bin/{id}`

The explicit second step. `204`, and the document, its update log and its versions are gone; audit rows stay (`purged`) and a
`document.purged` security event is recorded. `404` when it is not in the bin (a live document is never touched by this
route, and a document restored a moment earlier is not destroyed: the check and the delete are one guarded statement), `403` for
a caller who may not purge it, and `403` for an API token whatever its owner's role.

### `GET /api/health`

Anonymous, because a load balancer or monitor probes it, so what it says depends on who asks. A caller with no session gets the status and nothing else:

```json
{ "status": "ok" }
```

A signed-in user also gets what the editor gates its menus on:

```json
{ "status": "ok", "export": { "pdf": false }, "ai": { "enabled": false } }
```

An administrator (a session, or a token its owner minted) gets everything:

```json
{
  "status": "ok",
  "version": "0.1.0",
  "storage": "sqlite",
  "storageWritable": true,
  "sidecar": { "status": "running", "version": "0.1.0", "node": "v22.19.0", "restarts": 0 },
  "export": { "pdf": false, "pdfHint": "server-side PDF export needs the optional Chromium component. Install the browser with `npx playwright@1.63.0 install chromium`, then set export.chromium.enabled to true ..." },
  "ai": { "enabled": false, "provider": null }
}
```

Fields the caller may not see are omitted, not null (`ai.provider` is omitted for a non-admin and `null` for an admin while AI is off).

- `status` is `ok` when every component is up and `degraded` when the sidecar is not, or when the database opens but refuses writes outright (a read-only `data.db` or mount, a Postgres role or replica that may not write); the server keeps serving, reads included, either way. A disk that fills is **not** seen by this check (a write that matches no row needs no space): it shows as `503 This instance cannot save changes` on the first request that writes, and in the log. It is answered the same way to an anonymous caller. The write check is a no-op `UPDATE` asked of the database at most every five seconds (`GET /api/health` never turns into a stream of write transactions). Administrators also get `storageWritable` (`false` while it cannot be written) and, then, `storageProblem`: the database's own words, which name no path. The cause and the fix are in the server log, once when it starts and once when it recovers. A `data.db` or data folder the account cannot write at **start** is refused before serving, with one line on standard error and exit code `5` ([troubleshooting](troubleshooting.md)).
- `sidecar.status` is `running`, `starting`, `unavailable` or `stopped`. Why it is unavailable is written to the server log, not returned here.
- `export.pdf` is true only when a page has actually been printed by the browser (a tiny one, through the same driver, browser and print path a real render uses, under a time limit of `export.chromium.timeoutSeconds`, capped at 30 s for this check), never on configuration alone or on a launch alone: the server probes lazily on a signed-in health request (so this call can take a few seconds the first time it is enabled; an anonymous request never starts a browser) and caches the answer, five minutes after a success and thirty seconds after a failure (ten seconds when the probe or a render only ran out of its time limit, which says the host was slow just then, not that the browser is absent); one probe runs at a time. While it is false, `export.pdfHint` and `export.pdfProblem` (administrators only) say what is wrong: `pdfProblem` is `disabled` (not switched on), `driver` (Playwright's driver, the `.playwright` folder beside the executable, is missing, incomplete or will not run; installing a browser does not help, and the hint says so) or `browser` (the driver is fine but no Chromium or Chrome could be launched or it never printed within the limit; the hint carries the exact Playwright command pinned to the version this build uses, or the timeout advice). A render that overruns the limit has its browser and driver stopped by process id and answers `504 PDF export timed out`. See [PDF export](pdf-export.md).
- `ai.enabled` is true only once the effective AI settings - `ai` in `draughtsman.yaml`, or an admin's overrides ([AI settings](#ai-settings)) - name a supported provider (`ollama`, `openai`, `anthropic` or `azure-openai`) with a model; `ai.provider` (administrators only) is `null` while off. It follows an admin's change on the next request, with no restart. The web AI panel hides itself entirely rather than showing a disabled state.

### `GET /api/theme`

The chrome theme (spec section 8) as the SPA's runtime configuration, so an instance admin can re-skin the shell by editing a YAML file and restarting, without rebuilding `web/`.

```json
{
  "name": "Overpass",
  "productName": "Draughtsman",
  "logo": "/api/theme/logo",
  "logoLight": "/api/theme/logo-light",
  "logoCompact": "/api/theme/logo-compact",
  "logoCompactLight": "/api/theme/logo-compact-light",
  "favicon": "/api/theme/favicon",
  "mode": "dark"
}
```

- The theme comes from the file named by `theme.chrome` in `draughtsman.yaml` (default `chrome.yaml`). A relative path resolves against the data folder's `themes/` directory first, then the data folder itself, then the themes shipped in the server binary (the Overpass default and the fictional `chrome-example-client.yaml`). The shipped Overpass theme is the fallback when the configured file is missing or invalid: a bad theme logs a warning and never stops the server starting. An admin's branding overrides ([Branding](#branding)) layer over the file, so the file stays the fallback and a reset returns to it.
- `mode` is `dark`, `light` or `system`; the CSS honours all three. `logo`, `logoLight` and `favicon` are endpoint paths, or `null` when neither the theme nor an override names an asset that resolved. The shipped default theme names none of them, so on an instance where nobody has set a brand every one is `null` and the web app draws the Draughtsman mark and favicon it carries itself (a static file each, `/favicon.svg`, `/favicon-16.png`, `/favicon-32.png`, `/apple-touch-icon.png` and `/brand/*.svg`, served anonymously with a day's `Cache-Control`); the mark is never drawn once `logo` is set or `productName` is anything but `Draughtsman`. `logoLight` falls back to `logo` when no light-on-dark variant exists, so it is non-null exactly when `logo` is; a client draws `logoLight` on a dark surface and `logo` on a light one. `logoCompact` and `logoCompactLight` are optional header-only marks (`GET /api/theme/logo-compact`, `GET /api/theme/logo-compact-light`): `null` when the theme names none, and also `null` whenever an admin has uploaded a `logo` or `logo-light`, so an uploaded logo is drawn in the app header as it is. The app header draws the compact mark for the active surface (`logoCompactLight` on dark, `logoCompact` on light, the former falling back to the latter) and otherwise the normal logo; the login, first-run and About pages always use `logo`/`logoLight`.
- `GET /api/theme/chrome.css` returns the same tokens as CSS custom properties (`--chrome-*`), kebab-cased from the YAML (`surfaceRaised` becomes `--chrome-surface-raised`), with `:root`, `:root[data-theme-mode="dark"]`, `:root[data-theme-mode="light"]` and, for `system`, a `prefers-color-scheme` rule. `GET /api/theme/logo`, `GET /api/theme/logo-light` and `GET /api/theme/favicon` serve the named assets. The colours themselves are not editable at runtime - only the product name and the three images are (see [Branding](#branding)). See `themes/README.md`.

## Branding

An instance admin can change the product name and upload a logo, an optional light-on-dark logo and a favicon, from the web admin page `/admin/branding` or these endpoints (M3). The files are stored under the instance data folder in `branding/` (a `branding.json` naming them, plus the images) and applied on the next request, with no restart. The chrome theme file is the fallback; `DELETE` of a slot, or `POST /api/admin/branding/reset`, returns everything to the theme's own values. The product name and the served images are public through `GET /api/theme`, because the sign-in page needs them before anyone has signed in.

Every admin route requires a **cookie-authenticated admin** (`CookieAdminOnly`): an Editor or an anonymous caller is refused, and so is an API token even when its owner is an admin - changing instance-wide branding is not something an agent's token may do, matching the Users and Tokens pages. A cookie write needs the usual CSRF header. Each change is logged with the acting admin's user id; the document-scoped `audit` table has no row for instance-level changes (see [Audit](#audit)).

The image is decided by its content, never by its file name or declared type:

- `logo` and `logo-light` accept a **PNG or SVG**; `favicon` accepts a **PNG, SVG or ICO**.
- A file over 512 KiB is `413`. A logo over 4096 px on a side, or a favicon over 256 px, is `400`. A malformed header is `400`; a file that is not an accepted type is `415`.
- An **SVG** is validated against a strict allowlist before it is stored: `<script>`, `<foreignObject>`, event-handler attributes (`on*`), external references (an `href` that is not `#…`) and DTDs/processing instructions are refused, and a `<style>` block or attribute carrying `javascript:`, an `expression(`, `@import` or an external `url(...)` is refused. It is then served as an image with `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox` and `X-Content-Type-Options: nosniff` as well, and is never inlined into the page. Validation and refusal are in `src/Draughtsman.Core/Media/SvgSanitiser.cs`; the type/size/dimension rules are in `src/Draughtsman.Core/Media/BrandingImages.cs`.

```sh
# Upload a logo as the admin (cookie session; include the CSRF header the SPA would)
curl -s -b cookies.txt -X POST http://localhost:5180/api/admin/branding/logo \
  -H "X-XSRF-TOKEN: $(grep XSRF-TOKEN cookies.txt | cut -f7)" -F 'file=@logo.svg'
curl -s -b cookies.txt -X PUT http://localhost:5180/api/admin/branding \
  -H 'Content-Type: application/json' -H "X-XSRF-TOKEN: $(grep XSRF-TOKEN cookies.txt | cut -f7)" \
  -d '{ "productName": "Acme Diagrams" }'
```

Every branding route answers with the same body: the effective `productName`, the `themeName` and `themeProductName`, the `mode`, the three asset paths, and an `overridden` map saying which slots the admin set (so a UI can offer a per-slot reset).

```json
{
  "themeName": "Overpass", "themeProductName": "Draughtsman", "productName": "Acme Diagrams", "mode": "dark",
  "logo": "/api/theme/logo", "logoLight": "/api/theme/logo-light", "favicon": "/api/theme/favicon",
  "overridden": { "productName": true, "logo": true, "logoLight": false, "favicon": false }
}
```

### `POST /api/assets`

Uploads an image and returns the asset an image node references from its `props.assetId`. The body is
`multipart/form-data` carrying one file; the file's content, not its name or its declared type, decides the type. PNG,
JPEG, GIF, WebP and SVG are accepted; anything else is `415`, and a file larger than `assets.maxSizeBytes` (10 MiB by
default) is `413`. An upload that would take all stored images past the instance quota, `assets.quotaBytes` (5 GiB by
default, zero or less for none), is `507 Asset quota reached`; bytes already stored never count twice, so re-uploading
an existing image still succeeds at the limit. The bytes are stored content-addressed by their SHA-256 under the data folder's `assets/`
directory, with a row in the `assets` table holding id, hash, path, content type, size and creation time; uploading
the same bytes twice returns the same id rather than a second copy. The response is `201` with
`{ "id", "contentType", "size" }`. An SVG is accepted as an image and is never parsed into this origin's DOM.

### `GET /api/assets/{id}`

Serves an uploaded image with its stored content type, so the canvas can draw it through `<image href>` and an export
can inline it. The response is cacheable forever (`Cache-Control: private, max-age=31536000, immutable`), because an
id's bytes never change; an unknown id is `404`. An SVG is served with
`Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox` and `X-Content-Type-Options: nosniff`
in addition, so script in it stays inert; JPEG and the raster types are not given the SVG policy. The bytes live on
disk, never in the database, so a backup of the data folder carries them.

```sh
id=$(curl -s -X POST http://localhost:5180/api/assets -F 'file=@logo.png' | jq -r .id)
curl -s -o logo.png "http://localhost:5180/api/assets/$id"
```

### `POST /api/import/mermaid`

Converts a Mermaid `flowchart` / `graph` block, or a `sequenceDiagram`, into a document. Stateless: nothing is saved; the caller decides whether to store the result. This is how agents arrive with a diagram they already know how to write.

The header picks the document: a `sequenceDiagram` becomes a `sequence` document (see [Mermaid sequenceDiagram](#mermaid-sequencediagram) below), an `erDiagram` an `architecture` document of entities (see [Mermaid erDiagram](#mermaid-erdiagram) below), anything else the flowchart importer described here.

**Request**

- Body: the Mermaid source as `text/plain`. A pasted ```` ```mermaid ```` fenced block and YAML front matter (`title:`) are accepted.
- `?type=flowchart` (default) or `?type=architecture` selects the kind vocabulary that Mermaid shapes are mapped into; `?type=sequence` is for a `sequenceDiagram` and is the default when the header is one. `?type=architecture` (the default for an `erDiagram`, as for the SQL import) or `?type=freeform` holds an `erDiagram`'s entities; `freeform` has no vocabulary for anything else and is rejected. A `type` that disagrees with the source (a `sequenceDiagram` with `flowchart`, `architecture` or `freeform`, an `erDiagram` with `flowchart` or `sequence`, or a flowchart with `sequence`) is a `400` naming the mismatch: the importer never relabels a document.
- `?title=` sets the document title; otherwise the front-matter title, then `Imported diagram`.

**Response** `200 OK`

```json
{
  "document": { "id": "doc_…", "schemaVersion": 1, "title": "…", "type": "flowchart", "nodes": [], "edges": [], "meta": {} },
  "warnings": [
    { "code": "kind-guessed", "message": "\"Sign in\" is drawn as a circle, so it was given the kind 'terminal'; change it if that is not what it is.", "line": 2, "elementId": "n_A" }
  ],
  "direction": "LR"
}
```

- No node has `geometry`; it is absent until layout has placed the node. Import does not call the sidecar, so it answers whether or not the layout engine is up; to place the nodes, post the returned document to `POST /api/layout`. Deciding when that happens belongs to the caller: the SPA, the MCP tools and the importer flow each trigger it. `direction` is the header's `TB` / `LR` / `RL` / `BT` hint for that layout (`TD` is normalised to `TB`), or absent when the source gave none; a `direction` statement in the body (Mermaid's per-subgraph hint) is dropped, because the model has nowhere to keep it.
- Ids are stable and derived from the source: `n_<mermaidId>` for nodes, `c_<subgraphId>` for subgraphs (containers), `e_1`, `e_2`… for links in source order. Ids are sanitised to the schema pattern and de-duplicated, with an `id-renamed` warning when that changes anything.
- Subgraph membership follows Mermaid: the first block to close that mentions a node claims it, so a node named in both a block and a block nested inside it belongs to the nested one, and a node named in two sequential blocks belongs to the first. `flowchart-elk` headers are accepted as `flowchart`.
- Warning `line` numbers count from the first line of the pasted text, fence and front matter included.
- Warnings never fail the import. Codes:

| Code | Meaning |
| --- | --- |
| `kind-guessed` | The kind was inferred from a shape that could mean several things (a circle, a hexagon, a flag). A plain box, a rounded box and a bare id are the type's ordinary box and are not flagged. The message quotes the node's label and names the shape. `elementId` names the node; the UI should ask for confirmation. |
| `unsupported-syntax` | `classDef`, `class`, `style`, `linkStyle`, `click`, `accTitle`, `accDescr` were skipped (reported once per statement type). |
| `parse-error` | A line could not be read and was skipped. |
| `unsupported-diagram` | The header names another diagram type (`classDiagram`, `stateDiagram`, …). |
| `missing-header` | No `flowchart` / `graph` header; `flowchart TB` was assumed. |
| `id-renamed` | A source id was changed to satisfy the id pattern or avoid a collision. |
| `style-approximated` | An arrowhead the style model cannot express (`--x`) was replaced by the nearest. |
| `empty-diagram` | The source produced no nodes. |

**Errors**

- `400` empty body, unknown `type`, `type=freeform` on anything but an `erDiagram`, or a `type` that does not match the source (problem details body).
- `413` source over 1,000,000 characters; a `Content-Length` over that is rejected before the body is read.
- `422` the source is a Mermaid diagram type other than flowchart/graph/sequenceDiagram; the body still has the (empty) document and the `unsupported-diagram` warning.

#### Mermaid erDiagram

An `erDiagram` header (after an optional code fence, front matter, blanks and `%%` comments) makes the response a document of ER entities ([docs/er-entities.md](er-entities.md)): `architecture` by default, as for the SQL import, or `freeform` with `?type=freeform`. It is the reader for what `POST /api/export/mermaid` writes for an entity diagram. Like `POST /api/import/sql` it is a tolerant reader, and like the other Mermaid importers it places nothing: no node has `geometry`, so the caller lays it out (the SPA and MCP `create_document` do).

| Mermaid | Becomes |
| --- | --- |
| `CUSTOMER { … }` | node `t_CUSTOMER`, kind `entity`, shape `custom:er/entity`, label `CUSTOMER` |
| `p["Person"] { … }` or `p[Person]` | node `t_p` labelled `Person`; relationships still name it `p` |
| `int id PK`, `int owner_id FK`, `int k PK, FK` | one `props.columns` line each, in the column format: `PK id int`, `FK owner_id int`, `PK FK k int`. Mermaid has no nullability, so no `null` / `not null` is written; a type may carry a size (`varchar(10)`) or brackets (`string[]`) |
| `A \|\|--o{ B : places` | edge `e_n`, kind `association`, from `t_A` to `t_B`, label `places` (quoted or bare; `#quot;` is a double quote; `""` or none is no label), `routing` orthogonal |
| the left token | `arrowStart`: `\|\|` `oneAndOnlyOne`, `\|o` `zeroOrOne`, `}o` `zeroOrMany`, `}\|` `oneOrMany` |
| the right token | `arrowEnd`: `\|\|` `oneAndOnlyOne`, `o\|` `zeroOrOne`, `o{` `zeroOrMany`, `\|{` `oneOrMany` |
| `--` identifying, `..` non-identifying | solid, or `dash: dashed` |
| an entity named only in a relationship or on a line of its own | an entity with no columns |
| `title` in front matter | the document title (`?title=` wins); `direction LR` fills the response's `direction` |

- A relationship joins table to table: Mermaid has no column-level relationships, so no edge carries a column port (unlike the SQL import).
- Entities are in the order of first mention. Ids are `t_` plus the name with anything outside letters, digits and `_` replaced by `_`, de-duplicated with a numeric suffix.
- Warnings never fail the import, one per construct: `unsupported-syntax` for a `UK` marker or an attribute comment (the column is imported without it) and for `classDef`, `class`, `style`, `click`, `accTitle`, `accDescr`, `link`, `callback`; `parse-error` for a line or attribute it cannot read (skipped, later lines still import) and for a block never closed; `duplicate-table` when an entity block is repeated (the first is kept); `missing-header`; `empty-diagram`.
- The word forms of cardinality (`one or zero`, `only one`, …) and Mermaid's `to` syntax are not read; such a line is a `parse-error`.
- Export then import is stable: an exported two-table document imports to the same tables, columns (minus nullability), ends, dashes and labels, and exporting that again reproduces the text.
- MCP `create_document` with `format: "mermaid"` takes an `erDiagram` for type `architecture` or `freeform` and always lays it out; any other type is refused with an error naming the mismatch.

#### Mermaid sequenceDiagram

A `sequenceDiagram` header (after an optional code fence, front matter, blanks and `%%` comments) makes the response a `sequence` document, laid out by nothing: its nodes carry no `geometry` and its messages no `routing`, because a sequence's picture is derived from the order of its nodes and edges (`docs/sequence-diagrams.md`), so there is no `POST /api/layout` step to run and no `direction`. The importer is a tolerant reader, like the SQL one: what it cannot carry is a warning and the messages around and inside it still import, in order.

| Mermaid | Becomes |
| --- | --- |
| `participant A as Alice` | node `p_A`, kind `participant`, label `Alice` (no `as`: the label is the name) |
| `actor U as User` | node `p_U`, kind `actor` |
| `A->>B: text` | edge `m_n`, kind `sync` |
| `A-->>B: text` | kind `return` |
| `A-)B: text`, `A--)B: text` | kind `async` (`--)` also `dash: dashed`) |
| `A->B`, `A-->B` | kind `sync` with `arrowEnd: none` (`-->` also `dash: dashed`) |
| `A<<->>B`, `A<<-->>B` | kind `sync` / `return` with an arrowhead at the sender as well |
| `A->>+B`, `B-->>-A` | `props.activate: true` on the receiver's message, `props.deactivate: true` on the sender's |
| `activate B` / `deactivate B` | the same props, when the message just before it goes to (comes from) that participant; otherwise a warning |
| `A->>A: think` | a self message (`from` = `to`) |
| `alt guard` … `else guard` … `end` | a `fragment` node (`f_1`, `f_2`… in the order it first holds a row, operator `alt`, one operand per branch, each guard stored bare and drawn `[guard]`); each row inside carries `props.in` (the fragment id) and `props.operand` (the branch, omitted for 0) |
| `opt`, `loop`, `break` | a `fragment` with that operator and one operand |
| `par guard` … `and guard`, `critical guard` … `option guard` | a `fragment` (operator `par` or `critical`) with one operand per branch |
| a block inside a block | a fragment whose `parent` is the outer fragment and whose `props.operand` is the outer branch it sits in |
| `Note over A,B: text`, `Note over A: text` | an edge `n_n`, kind `note`, `from` the first and `to` the last participant named, label the text |
| `Note left of A: text`, `Note right of A: text` | a `note` over `A` (one `unsupported-syntax` warning: the model has no side) |
| `create participant B as Bob` then `A->>B: text` | the participant, and the message is kind `create` |
| `destroy B` then `A-xB: text` (or any arrow to `B`) | the message is kind `destroy` (its `-x` cross is not warned about) |
| `title Text` (or front matter) | the document title (`?title=` wins) |

Participant ids are `p_<mermaidId>` (sanitised to the id pattern and de-duplicated, with an `id-renamed` warning when that changes anything), messages are `m_1`, `m_2`… in source order (the order of `edges[]` is time), and a participant only mentioned in a message is created where it first appears (node order is left to right). A label is one line: `<br/>` becomes a space. Only the style that differs from the kind's default is written, so a plain arrow carries none.

A fragment is created the first time a row lands inside its block, so a block that holds no message is left out with an `unsupported-syntax` warning on its opener, and a block still open at the end of the source is closed there with a `parse-error` warning. Each of these is one `unsupported-syntax` warning with its `line`, and is flattened: the `rect`/`box` blocks (the warning is on the opener; `end` is silent), a `create` whose next message does not go to the participant, a `destroy` that is not followed by a message to it (the model ends the lifeline of a message's receiver, so a destroyed sender is warned), `autonumber`, `link`/`links`/`properties`/`details`, a participant's `@{ … }` type config, and the lost-message cross `-x`/`--x` (imported as a plain `sync` message, except on the message that destroys). A line that is none of these is a `parse-error` warning and is skipped.

**Shape to kind mapping**

| Mermaid | Shape written | flowchart kind | architecture kind |
| --- | --- | --- | --- |
| `id` or `[text]` | rectangle | process (guessed) | service (guessed) |
| `(text)` | rounded | process (guessed) | service (guessed) |
| `([text])` | pill | terminal | actor (guessed) |
| `[[text]]` | rectangle | subprocess | external (guessed) |
| `[(text)]` | cylinder | io (guessed) | database |
| `((text))`, `(((text)))` | pill | terminal (guessed) | actor (guessed) |
| `>text]` | rectangle | note (guessed) | note (guessed) |
| `{text}` | diamond | decision | service (guessed) |
| `{{text}}` | hexagon | process (guessed) | queue (guessed) |
| `[/text/]`, `[\text\]`, trapezoids | parallelogram | io | queue (guessed) |
| `subgraph … end` | rectangle | container | container |

**Link mapping**

| Mermaid | Edge kind | Style written |
| --- | --- | --- |
| `-->`, `-- text -->`, `-->\|text\|` | flow (architecture: `data` when either end is a database) | none |
| `-.->`, `-. text .->` | flow (architecture: dependency) | `dash: dashed` |
| `==>`, `== text ==>` | flow | `strokeWidth: 3` |
| `---`, `-.-`, `===` (no head) | association | none, `dash: dashed`, `strokeWidth: 3` respectively |
| `<-->`, `--o` | as above | `arrowStart` / `arrowEnd` |
| `--x` | as above | none; the cross becomes the default arrowhead with a `style-approximated` warning |

Labels: quotes are stripped, `<br>` becomes a newline, `#quot;` / `#35;` style entities are decoded, and a quoted label may span lines. `A & B --> C & D` fans out to four edges. Class annotations (`:::name`) are dropped.

Example:

```sh
curl -s -X POST 'http://localhost:5180/api/import/mermaid?type=architecture&title=Orders' \
  -H 'Content-Type: text/plain' \
  --data-binary $'graph LR\n  web[Web app] --> api[Orders API] --> db[(Postgres)]'
```

### `POST /api/import/drawio`

Reads a draw.io (diagrams.net) file into a document: the shapes, containers and connectors of one page, with the labels, sizes and positions the file gave them. Stateless like the other importers: nothing is saved. Unlike them it needs no `POST /api/layout` afterwards. A draw.io file already carries every position, so the document comes back placed, and laying it out again would undo the work of whoever drew it. The mapping rule by rule, the limits and the security notes are in [drawio-import.md](drawio-import.md); the editor's File menu and the home screen have a matching Import draw.io dialog.

**Request**

- Body: the file's XML, either as the request body (`text/plain`, `application/xml`, `text/xml`, `application/vnd.jgraph.mxfile` or `application/octet-stream`) or as one file of a `multipart/form-data` form (the first file; its field name does not matter). All three spellings of a diagram are read: an `<mxfile>` of pages (each page plain or compressed, which is how draw.io saves by default), a bare `<mxGraphModel>` (draw.io's Extras, Edit Diagram), and the bare compressed text a `<diagram>` holds. Up to 1,000,000 characters.
- `?type=flowchart` (default), `architecture` or `freeform` selects the kind vocabulary the shapes are mapped into. A drawing has no type of its own, so the choice decides what an ellipse or a cylinder means: in a flowchart an ellipse is a terminator and a cylinder is stored data, in an architecture an ellipse is a service and a cylinder is a database. `freeform` has no vocabulary, guesses nothing and keeps every shape as a plain `shape`. `sequence` is `400`: a sequence document is made from a Mermaid `sequenceDiagram`.
- `?page=` is the page of a multi-page file to import, counted from 1 (default 1). Only that page is read, and only that page is decompressed.
- `?title=` sets the document title; otherwise the page's name (unless it is draw.io's own `Page-1`), then `Imported diagram`.

**Response** `200 OK`

```json
{
  "document": { "id": "doc_…", "schemaVersion": 1, "title": "Order flow", "type": "flowchart", "nodes": [], "edges": [], "meta": { "createdBy": "drawio-import" } },
  "warnings": [
    { "code": "kind-guessed", "message": "\"Start\" is drawn as an ellipse, so it was given the kind 'terminal'; change it if that is not what it is.", "elementId": "n_start" },
    { "code": "page-selected", "message": "Page 1 (\"Overview\") of 3 was imported; the other 2 pages were not. Choose another page to import it instead." }
  ],
  "pages": [
    { "index": 1, "name": "Overview", "selected": true },
    { "index": 2, "name": "Details", "selected": false },
    { "index": 3, "name": "Scratch", "selected": false }
  ]
}
```

- Every node with a position in the file has `geometry`, absolute (a shape inside a container is relative to it in draw.io; the importer adds the container's origin), so the caller does not lay it out. A shape drawn as an icon (a stick figure, a cloud) is grown to at least 96 × 96 about its centre so its label has room. A shape the file gave no position, or one so far from the origin no canvas could hold it, has none and a warning says so.
- `pages` lists every page of the file in order, `selected` marking the one that was imported; a file that is one bare diagram lists one unnamed page. A file with several pages also gets a `page-selected` warning, first in the list. It is additive: the other importers' responses have no `pages`.
- Ids are stable and derived from draw.io's own: `n_<id>` for shapes, `c_<id>` for containers, `e_<id>` for connectors, sanitised to the schema pattern and de-duplicated with an `id-renamed` warning when that changes anything.
- A container is any swimlane, group or `container=1` shape, and any shape that has other shapes inside it. Its children's `parent` is its id, and it is written before them so it is drawn underneath.
- Warnings never fail the import, and one is written per element (at most 500; a last one says how many more there were). Codes:

| Code | Meaning |
| --- | --- |
| `kind-guessed` | The kind was a judgement (an ellipse, a hexagon, a cloud, a stick figure, a plain shape that holds other shapes). A plain box, a diamond, a cylinder and ISO flowchart symbols are not flagged. `elementId` names the node; the UI should ask for confirmation. |
| `unsupported-syntax` | Something has no equivalent and was left out or drawn as a plain box: a stencil or other unknown `shape=` (the message names it), a picture, a second label on a connector, a link that is not a web or mail address, a label cut at 10,000 characters, a cycle of parents, a duplicate id, hidden shapes (counted in one warning), several layers. |
| `missing-reference` | A connector with a loose end, or attached to a cell that is not in the file, to another connector, or to a hidden shape, was left out; or a shape names a parent that is not in the file and sits at the top level. |
| `style-approximated` | A colour that is not hex (`lightblue`, `rgb(…)`), a font name that is not plain, or an arrowhead with no equivalent (`box`, `halfCircle`) was dropped or replaced by the nearest. |
| `page-selected` | The file has several pages and one was imported. |
| `id-renamed` | An id was changed to satisfy the id pattern or avoid a collision. |
| `empty-diagram` | The file or page has no shapes. |

**Errors**

- `400` empty body, no file in a multipart body, unknown `type` (`sequence` included), a `page` that is not a number from 1, or a `page` the file does not have (the message says how many it has).
- `413` source over 1,000,000 characters; a `Content-Length` over three times that is rejected before the body is read.
- `422` the source itself is refused: it is not a draw.io file, or it has a `DOCTYPE` (which draw.io never writes, and which is how XML smuggles in entity bombs and reads of other files), or it is past a limit (a compressed page that inflates past 16 MiB, nesting deeper than 32 levels, more than 150,000 elements, 10,000 shapes and connectors, 500 pages, 1,000 attributes on one element, or 8 million characters of XML). The body is problem details with the reason in `detail`.

### `POST /api/import/visio`

Reads a Visio (`.vsdx`) drawing into a document: the shapes, groups and connectors of one page, with the text, sizes and positions the file gave them. It is the draw.io import's twin and is stateless the same way: nothing is saved, and the document comes back placed, so it needs no `POST /api/layout` afterwards. A Lucidchart document comes in by exporting it to Visio first. The mapping rule by rule, the limits, the security notes and the Lucidchart route are in [moving-from-lucidchart-visio-drawio.md](moving-from-lucidchart-visio-drawio.md); the editor's File menu and the home screen have a matching Import Visio dialog.

**Request**

- Body: the file's bytes, either as the request body (`application/octet-stream`, `application/vnd.ms-visio.drawing`, `application/vnd.ms-visio.drawing.macroenabled.12`, `application/vnd.visio`, `application/zip` or `application/x-zip-compressed`) or as one file of a `multipart/form-data` form (the first file; its field name does not matter). A `.vsdx` is a ZIP package, so it is binary, not text. Up to 3,000,000 bytes.
- `?type=flowchart` (default), `architecture` or `freeform` selects the kind vocabulary the shapes are mapped into, exactly as for draw.io: the type decides what an ellipse or a cylinder means, and `freeform` has no vocabulary, guesses nothing and keeps every shape as a plain `shape`. `sequence` is `400`: a sequence document is made from a Mermaid `sequenceDiagram`.
- `?page=` is the page of a multi-page file to import, counted from 1 (default 1). Only that page, and only the masters its shapes use, are read.
- `?title=` sets the document title; otherwise the page's name (unless it is Visio's own `Page-1`), then `Imported diagram`.

**Response** `200 OK`: the same `{ document, warnings, pages }` envelope as `POST /api/import/drawio`, with `meta.createdBy` `visio-import`.

- Every node has `geometry`, absolute: Visio's inches from the page's bottom-left, y up, become canvas units (100 to the inch) from the top-left, y down, to two decimals; a shape inside a group is placed through the group's own position and turn, however deep. A shape turned in Visio carries `rotation` (degrees clockwise about its centre). A shape placed too far from the origin for any canvas has none and a warning says so.
- `pages` lists every page of the file in order, `selected` marking the one that was imported. A file with several pages also gets a `page-selected` warning, first in the list.
- Ids are derived from Visio's own shape ids: `n_<id>` for shapes, `c_<id>` for containers, `e_<id>` for connectors, sanitised to the schema pattern and de-duplicated with an `id-renamed` warning when that changes anything.
- A container is a Visio group with imported shapes in it, a shape from a swimlane or container master, or a shape Visio marks as a container. A group that is an instance of a master nothing here knows (a stencil shape) is **one** plain box standing for the whole shape, not a container of its parts, and a connector glued to a part of it reaches the box.
- Warnings never fail the import. A loss is one warning per kind with a count (a master with no equivalent, a custom outline, pictures, shape data, themes, flips, layers, shapes with nothing to draw, connectors whose arrowhead the theme chooses); only a kind that was a judgement, and a connector left out, are one warning per element. At most 500 are listed; a last one says how many more there were. Codes:

| Code | Meaning |
| --- | --- |
| `kind-guessed` | The kind was a judgement (an ellipse, a hexagon, a cloud, a stick figure). A plain box, a diamond, a cylinder, a group and ISO flowchart symbols named by their master are not flagged. `elementId` names the node. |
| `unsupported-syntax` | Something has no equivalent and was left out or drawn as a plain box, said once per kind with a count (see the page above), or a label cut at 10,000 characters, a repeated shape id, a shape too far from the origin. |
| `missing-reference` | A connector with a loose end, or glued to a shape the page does not have, to another connector, or to a hidden shape, was left out. |
| `style-approximated` | An arrowhead number past Visio's table (1 to 45) was replaced by a plain arrowhead. |
| `page-selected` | The file has several pages and one was imported. |
| `id-renamed` | An id was changed to satisfy the id pattern or avoid a collision. |
| `empty-diagram` | The page has no shapes, or the file has no pages (a stencil). |

**Errors**

- `400` empty body, no file in a multipart body, unknown `type` (`sequence` included), a `page` that is not a number from 1, or a `page` the file does not have (the message says how many it has).
- `413` a file over 3,000,000 bytes; a `Content-Length` over that is rejected before the body is read, and a body past the route class's own limit is stopped by the request-size middleware.
- `422` the source itself is refused: it is not a `.vsdx` (text, an older binary `.vsd` or a password-protected file, whose message says to save it as `.vsdx`, or a ZIP that is not a Visio drawing), or it is damaged or cut short, or it is a hostile package: an entry name that could write outside its folder, two entries of one name, an encrypted entry, a size header that lies or claims more than a part may be (64 MiB for one part, 256 MiB for all, 128 MiB read), a relationship that climbs out of the package, a `DOCTYPE` in any part (which Visio never writes, and which is how XML smuggles in entity bombs and reads of other files), or past a counted limit (5,000 entries, 2,000,000 XML elements, 64 levels of nesting, 100 attributes on one element, 10,000 shapes, 500 pages, 5,000 masters, 50,000 connections). The body is problem details with the reason in `detail`.

```sh
curl -s -X POST 'http://localhost:5180/api/import/visio?type=architecture&title=Network' \
  -H "Authorization: Bearer $DRAUGHTSMAN_TOKEN" -H 'Content-Type: application/octet-stream' \
  --data-binary @network.vsdx
```

### `POST /api/export/dsl`

Accepts a complete document as `application/json` and returns canonical DSL as `text/plain`, using `Dsl.Print`.
Stateless: this prints the browser’s current unsaved model without saving or laying it out. Invalid JSON returns
400; a document that fails the shared schema or cannot be printed returns 422 Problem Details.
The DSL’s documented empty-collection normalisation and dotted endpoint ambiguity still apply (docs/spec/dsl.md).

### `POST /api/import/dsl`

Parses DSL text (`docs/spec/dsl.md`) into a document with `Dsl.Parse`. Stateless: nothing is saved. The browser has
no DSL parser of its own, so this is how the SPA (and anything else without `Draughtsman.Core.Text`) turns pasted
DSL into a document; an agent that already links the library calls `Dsl.Parse` directly instead.

**Request**: the DSL source as `text/plain`.

**Response** `200 OK`: the same `{ document, warnings }` envelope as `/api/import/mermaid`. `warnings` carries the
parser's lint warnings (an unknown kind, a reference to an undeclared node or port, an edge end on a table column that the
table does not have, or a value outside what the
schema admits that was replaced by the field's default) with code `dsl-lint` and `line` set; there is no
`elementId`. The document always satisfies `docs/schema/document.schema.json`, so it can be posted to
`POST /api/documents` as it stands. Geometry is present only for nodes written with `@ x,y` in the source; post the
document to `POST /api/layout` to place the rest.

**Query** `newId=true` (optional): give the document a fresh `doc_` id, as the Mermaid and SQL imports always do.
Without it, a source with no `id` line takes an id slugged from its title (`doc_order_service`), which is what lets an
exported document import back unchanged but collides with an existing document of the same title when both are created
(`409 Document already exists`, whose detail names this option). Pass it when the text is meant to become a new document.

**Errors**

- `400` empty body (problem details body).
- `413` source over 1,000,000 characters; a `Content-Length` over that is rejected before the body is read.
- `422` a syntax error. `detail` is the parser's message without the position prefix; `line` and `column` (1-based)
  are extension members on the problem body, so a caller can compose `line {line}, column {column}: {detail}`
  itself or point at the source directly.
  A number the grammar admits but that is out of range for a double (`1e400`) is one of these when it is written
  where the parser stores a double, such as geometry; prop values are JSON and keep their raw text, so they are not.
  `docs/spec/dsl.md` lists the fields.

```sh
curl -s -X POST http://localhost:5180/api/import/dsl -H 'Content-Type: text/plain' --data-binary @docs/examples/order-service.dsl
```

## Themes

An organisation admin creates themes from the web page `/admin/themes` or these endpoints, and chooses the **default** every signed-in user receives. A theme has two halves: the **chrome** (the app's colours, corner radius and fonts, in a dark and a light set) and a **diagram** override (a starting diagram theme plus palette, background, grid and font changes). Themes are plain files in the data folder, one `themes/org/<id>.yaml` each plus `themes/org/_settings.json` naming the default, so backup is still copying a folder, and they apply on the next request with no restart. The product name, logo and favicon are not part of a theme; they stay [branding](#branding).

**Authorisation.** Every `/api/admin/themes` route requires a **cookie-authenticated admin** (`CookieAdminOnly`): an Editor or an anonymous caller is refused, and so is an API token even when its owner is an admin. `X-Draughtsman-Actor` only annotates and never authorises. A cookie write needs the usual CSRF header. `GET /api/themes` is open to any signed-in caller, token included. Each change is logged (`Draughtsman.Server.Themes`) with the acting admin's user id, never the values.

**Validation.** The server accepts only values that cannot carry markup or a second CSS declaration, and refuses the rest with `422` and every problem at once (`problems: [{ field, message }]`; nothing is written):

- a colour is hex, `#rgb` or `#rrggbb` (stored as lower-case `#rrggbb`); `rgb()`, names, `url(...)` and anything else are refused;
- a font is a comma-separated list of family names, each bare or in one matched pair of quotes, letters, digits, spaces, hyphens and underscores only, at most 120 characters; there is no free-form CSS;
- corner radius is 0 to 24; `mode` is `dark`, `light` or `system`; the diagram `base` is `overpass`, `neutral` or `mono`; a palette has at most 32 entries named with letters and digits;
- both colour sets must give all nine chrome tokens: `brand`, `brandText`, `surface`, `surfaceRaised`, `border`, `text`, `textMuted`, `accent`, `danger`;
- a name is 1 to 60 characters and unique (case-insensitively; a clash is `409`); an organisation has at most 50 themes.

`ChromeThemeCss` additionally drops any value that could end its declaration, open a comment or load a resource, so a hand-edited theme file cannot inject style either.

WCAG AA contrast is **not** checked by the server: the page shows the token compiler's checks (`web/src/theme/themeContrast.ts`) as warnings and never blocks a save.

| Route | Purpose |
| --- | --- |
| `GET /api/admin/themes` | `{ defaultId, themes: [theme], bases }`. `defaultId` is absent while the shipped Overpass theme is the default. |
| `POST /api/admin/themes` | `{ name, baseId? }` creates a theme: `baseId` absent or `overpass` copies the shipped default, otherwise a copy of that theme. `201` with the theme. |
| `GET` / `PUT` / `DELETE /api/admin/themes/{id}` | Read, replace (the full theme as returned by GET) or delete. The default cannot be deleted (`409`). |
| `POST /api/admin/themes/{id}/duplicate` | A copy named "<name> copy". |
| `PUT /api/admin/themes/default` | `{ id }` makes a theme the default; `{ id: null }` returns to the shipped Overpass default. |
| `GET /api/admin/themes/{id}/export` | The theme file, `application/x-yaml`, as an attachment. |
| `POST /api/admin/themes/import` | `{ yaml }` adds a theme from the text of a theme file (at most 64 KiB); a name in use is numbered ("Acme 2"). |
| `GET /api/admin/themes/extract/options` | `{ url }`: true only when the operator set `themes.fetchFromUrl.enabled`; the page shows its web address box only then. |
| `POST /api/admin/themes/extract` | A draft theme from a site's colours; saves nothing. See [Extract a draft from CSS or a web address](#extract-a-draft-from-css-or-a-web-address). |
| `GET /api/themes` | Any signed-in caller: `{ defaultId, themes: [{ id, name, diagram }] }`, the diagram half only. The editor's Theme menu lists them and opens in the default. |

The chrome half of the default is what the anonymous `GET /api/theme/chrome.css` serves (and `name` and `mode` in `GET /api/theme`), so the sign-in page wears it too; the diagram half is applied by the SPA over the compiled diagram theme (`web/src/theme/orgTheme.ts`). Export always uses the document's diagram theme, never the chrome.

**One format.** The theme file is `themes/chrome.yaml`'s shape (`name`, `productName`, `mode`, `colors` for the mode's set, a `light:` or `dark:` companion block, `radius`, `font`) plus a `diagram:` block (`base`, `palette`, `canvasBackground`, `exportBackground`, `gridColor`, `fonts.label`, `fonts.mono`). The chrome loader ignores keys it does not know, so an exported file also works as a `theme.chrome` file, and the same file imported into the page brings the diagram block back. `productName` is written only because the chrome format requires it; an import ignores it.

```json
{ "id": "acme-corp", "name": "Acme Corp", "mode": "dark",
  "chrome": { "dark": { "brand": "#c0392b", "brandText": "#ffffff", "surface": "#0a0e1a", "surfaceRaised": "#151e33", "border": "#202a42", "text": "#f2f2f2", "textMuted": "#9aa4b8", "accent": "#e04832", "danger": "#ff4d4f" },
              "light": { "...": "the same nine tokens" }, "radius": 6, "fonts": { "ui": "Inter, system-ui, sans-serif", "mono": "JetBrains Mono, monospace" } },
  "diagram": { "base": "overpass", "palette": { "red": "#00aa55" }, "canvasBackground": "#0b1f2a", "fonts": {} },
  "updatedAt": "2026-09-30T10:00:00.000Z", "isDefault": true }
```

As everywhere, null members are omitted from responses.

### Extract a draft from CSS or a web address

`POST /api/admin/themes/extract` (cookie admin only, like every other route in this section) reads the colours of a site into a **draft** theme. Nothing is saved and nothing is stored: the response is the draft and the reasons for it, and the draft becomes a theme only through the create and update routes above (the page creates a theme from the Overpass default, then saves the edited draft over it). The request body is exactly one of:

| Body | Behaviour |
| --- | --- |
| `{ "css": "..." }` | Pure computation over the text sent: a stylesheet, or a whole page's HTML (its `<style>` blocks and `<meta name="theme-color">` are read). No network, so it works on an air-gapped install and whatever `themes.fetchFromUrl` says. Body limit 4 MiB (`413` beyond it); the reader looks at the first 2 Mi characters. |
| `{ "url": "https://..." }` | The server fetches the page and up to 8 of its linked stylesheets. Refused with `403` (`reason: "disabled"`) unless the operator set `themes.fetchFromUrl.enabled: true` (off by default). Rules below. |

Neither or both is `400`. Success is `200`:

```json
{ "draft": { "id": "theme-from-pasted-css", "name": "Theme from pasted CSS", "mode": "dark",
             "chrome": { "dark": { "brand": "#ff6a3d", "brandText": "#0f172a", "surface": "#0b1020", "surfaceRaised": "#121a30", "border": "#263150", "text": "#e6e9f2", "textMuted": "#98a2bd", "accent": "#ff6a3d", "danger": "#ff5c5c" },
                         "light": { "...": "the same nine tokens" }, "radius": 6, "fonts": { "ui": "Inter, system-ui, sans-serif", "mono": "JetBrains Mono, monospace" } },
             "diagram": { "base": "overpass", "palette": { "red": "#ff521e", "redDeep": "#d63200" }, "fonts": {} },
             "updatedAt": "1970-01-01T00:00:00.000Z", "isDefault": false },
  "reasons": ["The site is dark (its page background or text says so), so the theme's mode is dark.",
              "Dark surface #0b1020: the page background.",
              "Dark brand #ff6a3d: a button background.",
              "Light colours derived from the dark ones: the site gave only a dark scheme.", "..."],
  "adjustments": ["light textMuted #777270 became #726d6b: it read at 4.2:1 against the surface and the raised surface, below the 4.5:1 WCAG AA asks for.",
                  "light accent #ff6a3d became #e03400: it read at 2.8:1 against the surface, below the 4.5:1 WCAG AA asks for."],
  "source": "css" }
```

(The response above is the real one for the dark-site stylesheet in `tests/Draughtsman.Server.Tests/ThemeExtraction/ThemeFixtures.cs`, shortened with `...`; the draft's `id` and `updatedAt` are placeholders, since nothing is saved.)

`reasons` is "how this was chosen": one line per colour (where it came from: a custom property name such as `--brand`, the page background, a button background, the theme-color meta tag) and the diagram choice. `adjustments` lists every colour the extractor moved to pass the chrome contrast checks the Themes page shows, with the from and to values. Both modes are always filled: the one the site gave is used as written (a dark scheme under `prefers-color-scheme: dark`, `.dark` or `[data-theme=dark]` is found, not derived), the other is derived from it and says so. The extractor is deterministic (the same input always gives the same draft), reads hex, `rgb()`, `hsl()`, named colours and `H S% L%` custom properties, ignores `transparent`, `inherit`, a mostly transparent colour and a gradient's middle stops, never reads `@font-face` or `@keyframes`, and carries nothing of the source but hex colours into the draft (no font, image, selector or URL). Fonts and corner radius are left at the Overpass defaults. A source with no readable colour is `422`.

**Fetch rules (`url`).** Each is enforced by the server and pinned by a test (`ThemePageFetcherTests`):

- **https only.** Plain http only for `localhost` or a loopback address, which is blocked anyway unless listed in `allowedHosts`. A user name or password in the address is refused, and none is ever sent; no cookies are sent or kept.
- **Address rules.** The host is resolved and **every** address it resolves to is checked; loopback, private (10/8, 172.16/12, 192.168/16), link-local (including the cloud metadata address 169.254.169.254), shared (100.64/10), unique-local IPv6 (including `fd00:ec2::254`), multicast, reserved and documentation ranges are refused, in IPv4 and IPv6 and in the forms that carry an IPv4 address inside an IPv6 one (`::ffff:a.b.c.d`, NAT64, 6to4, Teredo). Decimal, octal and hex spellings of an address and bracketed `[::1]` are read as the address they are; a name ending in digits or `0x…` is refused. A host listed in `themes.fetchFromUrl.allowedHosts` (an exact host name or address) skips the address rule. The check is made in the connect step, on the address about to be connected, so a name that resolves differently a moment later cannot slip past it.
- **Redirects** are followed by hand, at most 3, only to the same host and never from https to http; any other redirect is refused.
- **Limits.** At most 8 `<link rel="stylesheet">` files (print-only sheets and `@import` rules are not followed); 2 MiB across the page and every sheet, counted after decompression; 15 seconds in all; the page must be `text/html` and each sheet `text/css`. A sheet that cannot be read (blocked, wrong type, over what is left) is left out and counted in the first line of `reasons`; the page itself being refused is an error.
- **Errors** are fixed text that never echoes an address, a status line or any of the page: `reason` is one of `disabled` (`403`), `invalid-url`, `scheme-refused`, `blocked-address`, `redirect-refused`, `too-large`, `not-css` (`422`), `unreachable` (`502`), `timed-out` (`504`).
- **Logging.** Every `url` request, refused or not, is a [security event](#security-events) `theme.url_fetch` whose `detail` is `host=<host> outcome=<ok|reason|no-colours>`: the host only, never the path, the query, the credentials or any of the page.

**What `reasons` can reveal.** Only what an administrator who asked for that address could see anyway: hex colours found in the CSS, the names of custom properties (letters, digits, hyphens and underscores, at most 40 characters) and fixed phrases for where a colour was used. Not selectors, values other than the colours, comments, URLs or any other text. Pointed at an internal site the operator has allow-listed, the response does tell its administrator which colours and custom property names that site uses.

## AI settings

An instance admin can choose the AI provider, endpoint and model, paste an API key and test the connection, from the web admin page `/admin/ai` or these endpoints (M3), instead of editing `draughtsman.yaml` and the server's environment. It applies on the next request, with no restart. `draughtsman.yaml`'s `ai` section stays the fallback and a `POST /api/admin/ai/reset` returns to it.

Access is exactly [Branding](#branding)'s: every route requires a **cookie-authenticated admin** (`CookieAdminOnly`). An Editor and an anonymous caller are refused, and so is an API token even when its owner is an admin - an agent's token must not be able to point the instance at another provider or replace its key. A cookie write needs the usual CSRF header. Each change is logged, with the acting admin's user id and *what* changed but never a value, under the `Draughtsman.Server.Ai.Settings` logger category (the document-scoped `audit` table has no row for it; see [Audit](#audit)). A change of the endpoint's host, and a refused endpoint, are also [security events](#security-events) (`ai.endpoint_host_changed`, `ai.endpoint_refused`). The AI endpoints themselves (`generate-from-text`, `diagram-to-document`) behave as before for everyone who may call them.

| Route | Effect |
| --- | --- |
| `GET /api/admin/ai` | The settings (below). |
| `PUT /api/admin/ai` | Sets the overrides `{ provider, model, endpoint, apiVersion }`. All four are replaced at once; a blank or absent field clears that override, so it falls back to the file. An endpoint the [endpoint policy](ai-endpoint-policy.md) refuses is `400` and nothing is saved. **A change of the endpoint's host or port clears the stored key.** |
| `PUT /api/admin/ai/key` | `{ "apiKey": "…" }` stores the key encrypted, replacing any earlier one. `400` while the endpoint in effect is plain `http` to a host that is not this machine (a key is never sent there). |
| `DELETE /api/admin/ai/key` | Forgets the stored key. |
| `POST /api/admin/ai/reset` | Drops every override **and the stored key** (it was meant for the provider that is no longer in use), returning to the file. A change of host that results is logged like any other. |
| `POST /api/admin/ai/test` | Makes one minimal request with the settings in effect and returns `{ "ok": true/false, "message": "…" }` (always `200`). |

Validation (`400` Problem+JSON that never echoes the value): `provider` is one of `ollama`, `openai`, `anthropic`, `azure-openai`; `endpoint` is an absolute `http` or `https` URL with no credentials, query or fragment (any other scheme is refused; put the key in the key field, not the URL); `apiVersion` is letters, digits, `.`, `-` and `_`; a key is one token of at most 512 characters with no whitespace or control characters.

**Endpoint policy** ([`ai-endpoint-policy.md`](ai-endpoint-policy.md), AU-11, option A). Where the endpoint may point is restricted, for `draughtsman.yaml`, the admin page and every connection alike:

- A keyed provider (`openai`, `anthropic`, `azure-openai`) may use only its published host (`api.openai.com`, `api.anthropic.com`, `*.openai.azure.com`) unless the operator lists the host under **`ai.allowedEndpointHosts`** in `draughtsman.yaml` (a host name, an IP address, or `*.example.com` for the subdomains; the file only, never this API). `ollama` may use loopback and private addresses; a public host needs listing too.
- Link-local and cloud-metadata addresses (`169.254.169.254` and the rest) are refused for every provider, listed or not, as a literal (including `::ffff:169.254.169.254`, the decimal and hex IPv4 spellings and bracketed hosts) and as the address a name resolves to. A name is resolved when it is connected to, every answer is judged, and the connection is made to an address that was judged.
- `http://` with a key is refused unless the host is loopback (for Ollama, whose key is optional, no key is stored for or sent to a plain-http host that is not this machine).
- Redirects are not followed; a redirect is reported by Test connection as an answer.
- A change of the endpoint's host or port forgets the stored key, which has to be entered again. A key entered before any endpoint existed stays only when the endpoint then chosen is the provider's own default.
- The environment key is sent only to the host in `draughtsman.yaml`, or the provider's default when the file names none.

A refusal is `400` Problem+JSON titled `Endpoint not allowed` (or `Key not accepted`), with a fixed `detail` that never repeats the host. From `draughtsman.yaml` it leaves AI off and `problem` says why. An upgrade that finds a stored or file endpoint the policy refuses behaves the same way: AI off until the host is listed.

**Layering.** Each override wins over the file. The file's `model`, `endpoint` and `apiVersion` only apply while the effective provider is the file's own, so switching provider in the UI never inherits, say, an Ollama endpoint for Anthropic; an unset endpoint then takes the provider's default (Azure OpenAI has none and must be given one). `timeoutSeconds`, `think` and `apiKeyEnvironmentVariable` always come from the file. Overrides are stored in `<data>/ai/settings.json`.

**The key is write-only.** No endpoint, log line, audit line or error message returns it or any part of it. It is stored only as an ASP.NET Core Data Protection payload in `<data>/ai/settings.json` (owner-read/write on Unix), using the key ring persisted in `<data>/keys/`. The ring is protected at rest by something that is not in the data folder (DPAPI, the macOS Keychain, a key-encryption key from the environment or a file, or a certificate; see [Key ring protection](#key-ring-protection)), so a leaked settings file, a backup or a copy of the whole data folder does not give the key up. It does **not** protect against someone who can run as the server's own account, or who has the data folder and the protection's key together. While the ring is unprotected (a Linux or Docker host with no key-encryption key configured, or protection switched off) the endpoint says so in `keyRing` and the page shows a warning. If the ring is lost or its protection is missing, the stored key reads as unset (`key.storedUnreadable`) and the admin pastes it again. Provider error text is filtered too: a provider's rejection of a key is reported by status only (some providers echo the key back masked), and any other provider body has an echoed key redacted before it is kept.

**Precedence.** The environment variable named by `ai.apiKeyEnvironmentVariable` (default `DRAUGHTSMAN_AI_KEY`) still works and **wins over the stored key**, so a deployment secret cannot be silently replaced from the browser. `key.source` says which is in effect. The environment key is sent **only to the host in `draughtsman.yaml`** (or the provider's default when the file names none): for any other host it is held back and `key.withheld` says so. **Ollama needs no key**, so a fully local instance keeps working with none set; one behind a reverse proxy may still store a key, sent as a bearer token.

```json
{
  "enabled": true,
  "problem": null,
  "effective": { "provider": "openai", "model": "gpt-4o-mini", "endpoint": "https://api.openai.com/v1", "apiVersion": null },
  "file": { "provider": "ollama", "model": "llama3.1", "endpoint": "http://localhost:11434/", "apiVersion": null },
  "overrides": { "provider": "openai", "model": "gpt-4o-mini", "endpoint": null, "apiVersion": null },
  "key": { "source": "stored", "environmentVariable": "DRAUGHTSMAN_AI_KEY", "environmentSet": false, "storedSet": true, "storedUnreadable": false, "required": true },
  "providers": ["ollama", "openai", "anthropic", "azure-openai"],
  "keyRing": {
    "state": "protected",
    "provider": "key",
    "message": "The key ring is protected by key-encryption key from the environment variable DRAUGHTSMAN_KEY_ENCRYPTION_KEY.",
    "unreadableKeys": 0,
    "backup": "backups/key-ring-20260930T202036Z",
    "migration": "migrated"
  }
}
```

`effective` is what is in use, `file` what `draughtsman.yaml` alone gives, and `overrides` what the admin has set (null = not overridden). `problem` says why AI is off when `enabled` is false (including a refusal by the [endpoint policy](ai-endpoint-policy.md), in fixed words that never repeat the host). `key.source` is `environment`, `stored` or `none` and is the key that would really be sent to this endpoint; the rest is presence only, never a value. `key.withheld` (added with the endpoint policy; omitted while nothing is held back) says in plain English why a key that exists is not being sent: the environment key to a host other than the file's, or any key over plain `http` to a host that is not this machine.

`keyRing` (added with key ring protection; absent only from an older server) describes how the ring that encrypts the stored key is protected at rest. It is identifiers, plain English, a count and a folder name, never a key:

| Field | Meaning |
| --- | --- |
| `state` | `protected`, or `unprotected` when any key is stored as plain text. The page warns on `unprotected`. |
| `provider` | `dpapi`, `keychain`, `key`, `certificate`, or `none`. |
| `reason` | Why it is unprotected: `no-provider` (nothing configured, the Linux default), `disabled` (`keyRing.protection: none`), `unavailable` (configured but unusable, e.g. a missing key file) or `migration-failed` (protection is set up but the existing ring could not be protected this start). Absent when protected. |
| `message` | What this means and what to set, in English. |
| `unreadableKeys` | Keys in the ring that cannot be decrypted with the protection available now (a lost or changed key-encryption key). Whatever they protected, the stored key included, has to be entered again. |
| `backup` | The newest `backups/key-ring-<UTC time>` folder, relative to the data folder, while it exists. It holds the **old plain-text ring**; the page asks the admin to delete it once the key is confirmed working. Looked up on each request, so deleting it clears the note without a restart. |
| `migration` | What this start did to an existing ring: `none`, `migrated`, `failed` or `rolled-back`. |
| `storedSecrets` | Each secret kept under the ring that is **stored** (the AI API key in `ai/settings.json`, the saved SMTP password in `email/settings.json`), checked at start: `{ name, file, state, message }`. `state` is `current` (protected by the key ring alone), `reprotected` (an earlier build tied it to the install folder and this start moved it; `message` names the `backups/secrets-<time>` copy of the earlier file), `legacy` (readable under an old install folder but not moved this start; it still works) or `unreadable` (`message` says which secret, why and what to do). Never a value. Absent from a server that predates it. |

## Key ring protection

The Data Protection key ring in `<data>/keys/` is what encrypts the stored AI key, and the antiforgery tokens. Out of the box the framework stores it as plain text beside `ai/settings.json`, so the encryption would protect nothing against anyone who can read the data folder. The server therefore protects the ring at rest with a key kept elsewhere, chosen by `keyRing` in `draughtsman.yaml` (README, "Protecting the key ring", has the table, the Docker example and the YAML):

- **Protections.** `dpapi` (Windows, the account the server runs as), `keychain` (macOS login Keychain; the key-encryption key is generated on first use and its item is named by an account id in `keys/ring-protection.json`, so the ring follows the machine, not the folder's path), `key` (a key-encryption key of at least 32 random bytes, base64, from `keyRing.keyEnvironmentVariable` or `keyRing.keyFile`; a file inside the data folder is refused, symbolic links included), `certificate` (a `.pfx` with an RSA private key; its password from `keyRing.certificate.passwordEnvironmentVariable`). The key-encryption key wraps each key's secret with AES-256-GCM under a key derived with HKDF; DPAPI and the certificate use the framework's own encryptors. `auto` picks certificate, then key, then the OS store; Linux with none of them is `unprotected` with `reason: no-provider`. `DRAUGHTSMAN_KEY_RING_PROTECTION` overrides `keyRing.protection`.
- **Startup.** Before the host is built, the server resolves the protection, then (only if an existing ring has plain-text keys) migrates it, then wires the ring in. Nothing in this can stop the server starting: every failure leaves the ring as it was, is logged (`Draughtsman.Server.KeyRing`, never a secret) and shows on the admin AI page. The log always has one line, `Key ring protection: ...`, saying which protection is active or that there is none.
- **Migration.** (1) Copy `keys/` and `ai/settings.json` to `<data>/backups/key-ring-<UTC time>/` (owner-only on Unix, a SHA-256 manifest, a README saying how to go back), verifying each copy against its source; a ring that cannot be backed up is not changed, and an identical backup already on disk is reused rather than copied again. (2) Read the ring as it is: which keys load, and whether the stored key decrypts. (3) Write encrypted versions of the plain-text key files into `keys.migrating-*` beside `keys/`; nothing in `keys/` is touched. (4) Read that copy back through the real framework with the new protection: every key that loaded before must load, the stored key must decrypt to the same value, and no key's secret may appear in any file. (5) Only then rename the encrypted files over the plain ones, and verify again on `keys/`; if that fails, put the backed-up originals back. The keys are not regenerated, so every payload already protected by them stays valid; the stored AI key is never decrypted and re-encrypted by the migration, only read to check it still opens.
- **The backup is not deleted by the server.** It is the way back if the protection is wrong, and it still holds the old plain-text ring, so `keyRing.backup` and the log keep reporting it until an operator removes it.
- **Damaged ring files.** The framework lets one malformed `keys/*.xml` fail the whole ring (no new key, nothing decrypts, every antiforgery token fails), so such a file is moved aside as `<name>.damaged` after the backup is taken, never edited or deleted. `keyRing.message` names how many there are; one may still hold a plain-text key.
- **Lost protection.** With protection configured but unusable (variable unset, file missing, Keychain item gone, wrong password) the server starts with `provider: none` and `reason: unavailable`; keys the lost protection covered count in `unreadableKeys`, and the stored AI key reads as `key.storedUnreadable`. Nothing is rewritten or removed, so restoring the original protection makes them readable again. A Keychain that cannot be read (locked, no GUI session) is unavailable, not a reason to create a new key-encryption key; only a missing item creates one.
- **Logs and audit.** Startup lines carry paths, variable names, counts and exception type names, never a key, a key-encryption key or a password; the AI settings and key ring changes write no `audit` rows (the table is document-scoped, see [Audit](#audit)).

**Stored secrets do not depend on where the program is installed.** Data Protection's application name is the constant `Draughtsman` (`KeyRing/DataProtectionIdentity.cs`), so the stored AI key and the saved SMTP password are bound to the ring alone, and so are the antiforgery tokens: a program moved, replaced or unpacked elsewhere reads all of them. Builds before that name was pinned used the framework's default, the **content root**, so every secret they wrote is bound to the folder they ran from. This build reads such a secret through a **read-only fallback** (`KeyRing/LegacySecretReader.cs`: a private Data Protection stack over the same ring, no key generation, one protector per candidate path) trying, in order, the content root this process runs from (with and without its trailing separator), the content roots recorded in `keys/install-paths.json` (written at every start, one entry per distinct path, never a secret), `keyRing.legacyInstallPaths` and `DRAUGHTSMAN_LEGACY_INSTALL_PATHS`, the folder the data folder sits in, and only if all fail, `Content root path:` lines in `<data>/logs/`, the folders beside this install folder and the recommended install locations. A wrong guess costs one failed decryption (the payload is authenticated against the path). **Re-protection** (`KeyRing/StoredSecrets.cs`, once at start, after the schema is accepted): for each secret that opened only under an old path, (1) copy its settings file byte for byte to `<data>/backups/secrets-<UTC time>/` and verify the copy, (2) protect the plain text under the current name and replace only that one value in the file (everything else in it, unknown members included, is kept) through a temporary file and one rename, (3) read the file back with the fallback switched off and compare, and (4) if anything threw or did not match, put the copy back. The earlier ciphertext is never deleted. A secret nothing opens is reported (`storedSecrets`, the log, `config check`) with the reason (the key it was written under is not in the ring or the ring's protection cannot open it; the install path is not known; the value is damaged) and is **never** altered. Nothing here can stop the server starting. A tab signed in before the first start of such a build holds an antiforgery pair this build cannot read once: the `403` that refuses its first mutating request carries a fresh pair (`X-Draughtsman-Csrf: reissued`, only to a caller the session cookie authenticated) and the editor retries its save once with it.

**Test connection** sends one short prompt to the provider (30 s limit) and reports in plain English: connected, no key set (or why the environment key is not sent to this host), could not reach the host, refused by the endpoint policy (a name that resolves to a blocked address, an unlisted public Ollama host), key rejected, a redirect that was not followed, `404` (check the endpoint and model), rate limited, a provider server error, or a reply in an unexpected format. The message is built from the outcome, never from the provider's own text, so it cannot carry the key.

## About and status

`GET /api/about` is the About screen's data, for **any signed-in caller** (session or token; the global fallback policy, so anonymous is `401`). The body is the same for an Editor and an Administrator and holds nothing host-specific: no data folder and no file paths.

```json
{ "version": "0.1.0", "buildDate": "2026-09-30", "supportContact": "draughtsman@overpass.co.uk", "docsUrl": "/docs/", "noticesUrl": "/THIRD-PARTY-NOTICES.txt" }
```

`supportContact` and `docsUrl` come from the `about` section of `draughtsman.yaml`; `docsUrl` is what Help > Documentation opens: the configured `about.docsUrl` when it is an absolute http(s) URL; **`/docs/` while `about.docsUrl` is unset and this install ships its guides** (a release archive does), the vendor's `https://overpass.co.uk/draughtsman` when it is unset and none are shipped; omitted when `about.docsUrl` is set to an empty string `""` or to anything that is not an http(s) address. `noticesUrl` is where an instance serves its bundled third-party licences (the release archive and image carry the file in the web root).

`GET /api/admin/status` is the admin status page's data: the components whose failure the product otherwise survives quietly. **Cookie-authenticated admin only** (`CookieAdminOnly`); an Editor, an anonymous caller and a token are refused. Read-only, and never a connection string, key or token.

```json
{
  "version": "0.1.0", "buildDate": "2026-09-30",
  "dataFolder": "/data", "host": "container", "logHint": "The container's output: docker logs <container name>.",
  "storage": { "provider": "sqlite", "file": "/data/data.db" },
  "sidecar": { "status": "running", "version": "0.1.0", "node": "v22.19.0", "restarts": 0, "nodePath": "/usr/bin/node", "scriptPath": "/app/sidecar/sidecar.js" },
  "pdf": { "enabled": false, "available": false },
  "ai": { "enabled": false },
  "connection": { "peer": "172.17.0.1", "peerTrusted": false, "forwardedHeadersSeen": true, "scheme": "http", "throttleAddress": "172.17.0.1", "checks": { "pass": 4, "warn": 2, "fail": 0 }, "problems": ["Reverse proxy and client address", "Host header and allowed hosts"] }
}
```

`connection` is the [Connection check](#connection-check) in a line: the direct peer address of this request, whether the server trusts it as a proxy (`server.knownProxies`), whether the request carried proxy headers, the scheme the server believes, the address the sign-in throttle counts it as, and how many of the server-side findings passed, warned and failed, with the titles of the ones that did not pass. `sidecar.status` is `running`, `starting`, `unavailable` or `stopped`; `sidecar.error` carries the supervisor's own message when it is not running. `pdf.enabled` is `export.chromium.enabled`; `pdf.available` is whether a browser launch has just worked (the same probe `/api/health`'s `export.pdf` reports from, never configuration alone), and `pdf.hint` says how to fix it while it is not and `pdf.problem` (`disabled`, `driver` or `browser`, as on [health](#get-apihealth)) says which part. `storage.file` is present for SQLite only. Null fields are omitted.

### Connection check

Four routes, all **cookie-authenticated administrator only** (`CookieAdminOnly`: an Editor, an anonymous caller and a token, whatever its owner's role, are refused), all read-only,
and none shows a secret. They are what the admin **Connection check** page (`/admin/connection`) runs; [`troubleshooting.md`](troubleshooting.md#troubleshooting-an-install) is the guide to reading it.

`GET /api/admin/selfcheck?pageScheme=http|https` is what only the server can see of the request. `pageScheme` is the scheme of the address in the caller's browser, the one fact the server cannot see for itself (leave it out and the Secure-cookie finding judges the server's side alone).

```json
{
  "generatedAt": "2026-10-06T11:17:10.012Z",
  "summary": { "pass": 4, "warn": 2, "fail": 0 },
  "items": [
    {
      "id": "proxy",
      "title": "Reverse proxy and client address",
      "status": "warn",
      "summary": "Proxy headers arrived from 192.168.1.176, which is not listed in server.knownProxies, so the server IGNORES them. Every visitor looks like 192.168.1.176 to the sign-in throttle ...",
      "detail": ["If 192.168.1.176 is your reverse proxy, add it to server.knownProxies and restart.", "..."],
      "facts": {
        "Direct peer address": "192.168.1.176", "Peer trusted (server.knownProxies)": "no", "server.knownProxies": "(empty: only a proxy on this machine is trusted)",
        "Scheme of the connection itself": "http", "X-Forwarded-Proto": "https", "X-Forwarded-For": "127.0.0.1", "X-Forwarded-Host": "draughtsman.example.com",
        "CF-Connecting-IP": "(not sent)", "Scheme the server believes": "http", "Client address the sign-in throttle uses": "192.168.1.176"
      },
      "fix": {
        "nginx": "location / {\n    proxy_pass http://127.0.0.1:5180;          # your Draughtsman address\n    proxy_set_header Host $host;\n ...",
        "caddy": "Caddy's reverse_proxy already sends X-Forwarded-For, X-Forwarded-Proto and X-Forwarded-Host. ...",
        "cloudflare": "Nothing to change in Cloudflare for this. ...",
        "yaml": "server:\n  knownProxies:\n    - \"192.168.1.176\""
      }
    }
  ]
}
```

`status` is `pass`, `warn`, `fail` or `info`; `detail`, `facts` and `fix` are omitted when empty, and `fix` holds only the places where the change lives (`nginx`, `caddy`, `cloudflare`, `yaml`). The `id`s are `proxy`
(the peer, whether it is trusted, the raw `X-Forwarded-Proto`, `X-Forwarded-For`, `X-Forwarded-Host` and `CF-Connecting-IP` it sent, the scheme the server believes and the client address the throttle will use; a warning when the headers
come from a peer that is not in `server.knownProxies`, when `CF-Connecting-IP` names a different visitor than the throttle uses, or when `X-Forwarded-For` lists several addresses), `secure` (`server.forceHttps` against `pageScheme`: a **fail** for `forceHttps` over a page that is plain `http://`,
since the browser drops the Secure cookie), `host` (the `Host` header against `server.allowedHosts`; a fail if it would be refused, a warning if the proxy says visitors asked for a different name), `data` (the data folder and the database
file writable by the server's account, with their owners; a fail when not), `disk` (a warning under 1 GiB free, or under 5 GiB and a tenth of the volume; a fail under 100 MiB) and `build` (version, build date, platform, runtime, the account, container or not).

`GET /api/admin/selfcheck/echo?step=1|2` is the half the **browser** runs, because only a browser can see what a proxy did to a response. Step 1 sets a test cookie (`ds_selfcheck`, or `__Host-ds_selfcheck` when the connection is https) with the attributes the session cookie has (`HttpOnly`, `SameSite=Lax`, `Path=/`,
`Secure` when the server believes https, `Expires` 30 days on); step 2 reads it, reports whether it came back, and clears it. Both answer with the `Cache-Control: no-store, no-transform` every `/api` response gets, a strong `ETag` and the same tag in the body, and are **never compressed by the server itself**, so a `Content-Encoding` the browser sees on them was added in transit:

```text
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Cache-Control: no-store, no-transform
ETag: "sc-02df901c8084c6f8"
Set-Cookie: ds_selfcheck=2351ac6fc1ebb1bfa13cac3a4eb5afa0; expires=Thu, 05 Nov 2026 11:17:10 GMT; path=/; samesite=lax; httponly

{"step":1,"tag":"\"sc-42fc38efda915849\"","cookieReceived":false,"cookieWasSecure":false,"scheme":"http","serverTime":"2026-10-06T11:17:10.284Z"}
```

`POST /api/admin/selfcheck/roundtrip` takes what the browser saw and returns the same shape as `selfcheck` with four findings, so every rule and every word of fix text is in one place:

```json
{ "pageScheme": "https",
  "first":  { "status": 200, "etagHeader": "W/\"sc-aaaa\"", "contentEncoding": null, "bodyTag": "\"sc-aaaa\"", "cookieReceived": false, "cookieWasSecure": true, "serverTime": "2026-10-06T11:00:00.000Z" },
  "second": { "status": 200, "etagHeader": "W/\"sc-bbbb\"", "contentEncoding": "gzip", "bodyTag": "\"sc-bbbb\"", "cookieReceived": true, "cookieWasSecure": true, "serverTime": "2026-10-06T11:00:01.000Z" },
  "clientTimeMs": 1791284400000 }
```

The findings: `cookie` (pass when step 2 got the cookie back; a **fail** when it did not, naming a Secure cookie on a plain-http page, a server clock 30 days or more behind the browser's, or a proxy that removes `Set-Cookie`), `etag` (a **warning** when the `ETag` header the browser saw is weak (`W/`), missing or different from the tag in the body: what made saves and renames fail with `400 Invalid If-Match header` before the weak-tag fix, and what leaves a script nothing to quote in `If-Match` if it is stripped),
`encoding` (a warning when a `content-encoding` other than `identity` arrived that this server did not add) and `clock` (a warning when the clocks differ by more than two minutes; a fail when the server is 30 days or more behind the browser, since the session cookie is then expired as it is set). A first call that was not answered `200`, or no answer at all, is one `roundtrip` failure instead. Text from the browser is cleaned of control characters and capped before it is shown. Every request body is limited like any other POST.

## Documentation

`GET /docs/` and `GET /docs/{path}` serve the guides a release archive carries (its `docs/` folder), read-only, for a site whose browsers cannot reach the vendor's website: Help > Documentation opens `/docs/` while `about.docsUrl` is unset ([About and status](#about-and-status)). **Signed-in callers only** (session or token; an anonymous request is `401`, as for every route that is not on the short anonymous list). `/docs` redirects to `/docs/`, which is `index.md`. Only `.md`, `.json`, `.dsl`, `.grammar`, `.yaml` and `.txt` files are served, always as `text/plain; charset=utf-8` with `X-Content-Type-Options: nosniff`, so a page can never run as HTML in the app's origin; Markdown is shown as text, not rendered. Anything that resolves outside the folder (a `..` segment, a backslash, a drive letter, an absolute path, a symbolic link pointing elsewhere), any other file type and any missing file is `404`. An install with no `docs/` folder (a build tree, the Docker image) answers `404` for every path.

## POST /api/ai/generate-from-text

"Generate from text" (build-spec-v0.1.md section 9, MVP AI feature 1; design/atlas/ai-panel.md): a description
becomes Draughtsman DSL via the configured `IModelProvider` (`ai` in `draughtsman.yaml`, or the admin's [AI settings](#ai-settings)), parsed with `Dsl.Parse` (one retry with the parser's error fed back to the model if the first
attempt does not parse), then written through the same `create_document`/`patch_document` path an MCP agent
uses, so the write is checkpointed and its `meta.aiProvenance` names the provider and model. `404` while
`GET /api/health`'s `ai.enabled` is false — the same answer a route that does not exist would give, so a client
cannot distinguish "off" from "not built here", matching the panel being hidden entirely rather than shown
disabled.

Without `documentId`, this creates a brand-new document: the model writes it from scratch and it is laid out
through the sidecar (every node is new, so every position is). With `documentId`, this updates that document: the
model is shown its exact current DSL and asked to reply with the complete updated document, so it can restructure
the diagram (group existing nodes into a new container, relabel, reconnect, remove), not just append to it; the
reply is diffed against the document it was built from and applied as addressable `patch_document` ops
(`addNode`/`removeNode`/`updateNode`/`addEdge`/`removeEdge`/`updateEdge`/`restyle`/`setProp`/`removeProp`), so an
id the reply left alone keeps its stored geometry, style and props exactly as they were. Before the diff, three
geometry adjustments run without a layout pass (which would recompute every node's position and move things the
reply never touched): a new container with no position of its own is fitted around its (now reparented) children's
existing boxes; a brand-new node the reply left positionless is placed in free space beside its placed neighbours,
or beside the diagram's bounding box when unconnected, so it does not land on top of existing shapes; and a
container that gained a child is grown to enclose it with the editor's fit-to-contents padding, never shrunk.

**Request**

```json
{ "documentId": "doc_9f1c2b3a4d5e", "description": "a login flow with two steps", "diagramType": "flowchart" }
```

- `documentId` (optional): updates that document (see above). Omit it to create a new document from the
  generated content instead.
- `description`: the prompt — a paragraph, notes, or a pasted Mermaid block. Limited to 20,000 characters.
- `diagramType`: `flowchart`, `architecture` or `sequence`. `freeform` is refused (`400`): it has no kind
  vocabulary to generate valid kinds against. A `sequence` prompt teaches that the order of the edge lines is time
  (its own cheat-sheet, not the flowchart one), is never laid out (a sequence has no stored geometry), and an update
  puts each message where the reply wrote it (`moveEdge` for the messages it reordered, `addEdge` with `before`/`after`
  for the ones it inserted) and writes no geometry.

**Privacy**: only `description`, and, when `documentId` is given, that document's own whole content as DSL
(everything `read_document format=dsl` would show — geometry and style included, so the model can keep them for
anything that stays), are sent to the model. Nothing else in the document, and no other document, is sent.

**Response** `200 OK`

```json
{
  "document": { "...": "the created or updated document, as GET /api/documents/{id} returns it" },
  "warnings": [],
  "addedNodeIds": ["n_login", "n_dashboard"],
  "addedEdgeIds": ["e_1"],
  "changedElementCount": 0,
  "removedElementCount": 0,
  "provenance": { "provider": "ollama", "model": "llama3.1" }
}
```

`changedElementCount`/`removedElementCount` count nodes and edges the reply changed or removed that were already
in the document (always `0` when creating a new document); the panel shows them alongside the added count so a
restructuring update's effect is visible before the person digs into the diagram itself.

**Errors**

- `400` empty/oversized `description`, or `diagramType` not `flowchart`/`architecture`/`sequence` (problem details body).
- `404` `documentId` names no document, or AI is off (see above — the two are not distinguished in the response).
- `422` the model's output still did not parse after the retry, or the generated content could not be written
  (e.g. the reply declared two elements with the same id, or the resulting document failed schema validation).
- `502` the model could not be reached, or rejected the request, or the sidecar could not lay the result out.

```sh
curl -s -X POST http://localhost:5180/api/ai/generate-from-text -H 'Content-Type: application/json' \
  -d '{"documentId":"doc_9f1c2b3a4d5e","description":"a login flow with two steps","diagramType":"flowchart"}'
```

## POST /api/ai/diagram-to-document

"Diagram to document" (build-spec-v0.1.md section 9, MVP AI feature 2; design/atlas/ai-panel.md): a diagram becomes
Markdown — a design doc, a runbook or a plain explanation — through the configured `IModelProvider`. The model is
given the document's outline, exactly the compact form `read_document format=outline` returns, and is asked to cite
each element it discusses by writing its id in square brackets (`[n_checkout]`); the web panel turns a cited node id
back into a click that selects and reveals that node on the canvas. The request only reads: nothing is written to
the document, so there is no checkpoint and no `aiProvenance` entry. `404` while `GET /api/health`'s `ai.enabled` is
false, the same rule `generate-from-text` follows.

**Request**

```json
{ "documentId": "doc_9f1c2b3a4d5e", "outputKind": "runbook" }
```

- `documentId`: the document to read. Required.
- `outputKind`: `design-doc`, `runbook` or `explanation`. Required; anything else is `400`.

**Privacy**: only the outline form is sent — each node's id, kind, label and container, and each edge's id, kind,
label and two endpoints. Geometry, style, props and any image are never sent, so this works on an air-gapped site
with the same privacy as `read_document format=outline`.

**Response** `200 OK`

```json
{
  "markdown": "# Payment runbook\n\n1. Drain the queue [n_pay].",
  "provenance": { "provider": "ollama", "model": "llama3.1" }
}
```

**Errors**

- `400` missing `documentId` or an `outputKind` outside the three above (problem details body).
- `404` `documentId` names no document, or AI is off (see above — the two are not distinguished in the response).
- `502` the model could not be reached or rejected the request.

```sh
curl -s -X POST http://localhost:5180/api/ai/diagram-to-document -H 'Content-Type: application/json' \
  -d '{"documentId":"doc_9f1c2b3a4d5e","outputKind":"runbook"}'
```

## GET /api/documents/{id}/thumbnail

A picture of the document, small, for the Documents page's cards. The response is `200` with `Content-Type: image/svg+xml`: an SVG
that is always **320 by 200** (`viewBox="0 0 320 200"`), the diagram scaled down to fit and centred on the diagram theme's paper
colour (a small diagram is shown at most one and a half times its size, never blown up to fill the box). It is the renderer behind the
PDF export and the MCP `render_document` tool (`src/Draughtsman.Mcp/Rendering/SvgRenderer.cs`), so it needs no browser, Chromium or
extra component and works on every install, including air-gapped ones, and it carries the same colours: the document's own `theme`
overrides over the organisation's default diagram theme (Admin > Themes) or the shipped Overpass theme. Orthogonal connectors are
routed first by the sidecar, as for a PDF, and a document with nodes that have no position is laid out first. An image node draws as
its plain frame (no asset is read or embedded). It works for every document type, `sequence` included. Same sign-in and same rules as
`GET /api/documents/{id}` (a read-only API token may call it); a missing or binned document is `404 Document not found`.

**Serve it as an image.** The web app shows it through `<img src>`, never inlined into the page, and the response carries the policy an
uploaded SVG is served under, `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox`, and
`X-Content-Type-Options: nosniff`, so a script in it could not run even if the URL were opened on its own. Labels are escaped by the
same code as every render, so a label that is markup is text in the file.

**Caching.** `Cache-Control: private, no-cache`: a browser keeps a thumbnail, a shared cache never does, and the browser asks again every
time. The `ETag` is the document's version tag (the one `GET /api/documents/{id}` hands out) followed by `.` and a short *variant*, a token
for what else the picture depends on: the organisation's default diagram theme and the build of the server. So a `If-None-Match` carrying
the current tag is a `304 Not Modified` with no body (answered from the stored version tag alone, with nothing read, parsed or drawn), an edit
changes the tag, and a changed theme or an upgrade makes a browser's copy stale instead of leaving old pictures in it. The Documents page
adds the document's last-modified time to the address (`?v=`), which the server ignores, so a page still open after an edit asks again.

The server also keeps finished thumbnails in memory, in a bounded least-recently-used cache keyed by document, version and variant: at most
**200 thumbnails or about 20 MB**, whichever is reached first, one entry per document, nothing written to disk and nothing added to the
database. An edit, a theme change or a restart simply draws again; two requests for the same thumbnail at the same time share one
drawing. Measured on the shipped examples (a development Mac, in process, through the real sidecar), drawing one takes 2 to 12 ms (the
first after a start, 40 ms), a cache hit about 1 ms and a `304` under 2 ms, so a page of fifty diagrams costs well under a second the
first time and nothing to speak of after.

**Large diagrams.** What a thumbnail may cost is bounded by size, counted as nodes plus edges:

| Elements | The thumbnail | `X-Draughtsman-Thumbnail` |
| --- | --- | --- |
| 0 | A dashed empty frame with a plus: a clean empty state, no text | `empty` |
| 1 to 400 | The whole picture: labels, routed connectors, shape detail | `full` |
| 401 to 6,000 | Boxes and straight lines in the theme's colours: no labels, markers or routes (the 2,000-node, 2,500-edge benchmark is about 150 KB) | `simplified` |
| More than 6,000 | A neutral grey placeholder | `too-large` |

A full picture whose SVG would pass 256 KiB is replaced by the simplified one. A `sequence` document, which stores no positions and has
no boxes-and-lines form, and a document of more than 400 elements with nodes that have no position (laying it out is not worth the work
for a thumbnail) are the placeholder instead. The `X-Draughtsman-Thumbnail` response header says which of these was drawn. If the layout
engine is not available or a document cannot be drawn at all (an id used twice), the answer is still `200` with the placeholder
(`unavailable`) and a `Cache-Control: private, no-store` with no `ETag`, so a page of cards loads and the next request tries again; a
connector that could not be routed is drawn straight for the same reason.

**Limits.** Thumbnails are drawn at most `limits.thumbnailConcurrency` (default 2) at a time across the instance; a request over that
waits its turn rather than being refused, because a page of cards asks for every thumbnail together. Requests are also limited per signed-in
user per minute, `limits.thumbnailPerUserPerMinute` (default 600, loose because a repeat visit revalidates every card; 0 turns it off); one
over it is `429` with `Retry-After`, and the page shows the type's glyph for that card instead of a broken image.

```sh
curl -s -o order.svg -D - http://localhost:5180/api/documents/doc_9f1c2b3a4d5e/thumbnail -H 'Authorization: Bearer dst_...'
```

## GET /api/documents/{id}/pdf

Renders a document as a single-page PDF through the optional Chromium component (build-spec-v0.1.md section 7). It
renders the same SVG the MCP `render_document` path produces (`src/Draughtsman.Mcp/Rendering/SvgRenderer.cs`), laying
the document out first if any node has no geometry, then prints it through headless Chromium with vector text over a
white page (every shipped diagram theme's export background). Response is `200` with `Content-Type: application/pdf`.
Orthogonal connectors with no stored waypoints are routed first by the sidecar's `route.document`, which runs the
editor's own router, so the PDF draws the paths the editor shows; if the sidecar cannot route them the endpoint returns
`503 The connectors could not be routed` rather than a PDF with straight lines.

When the component is absent — Chromium not enabled in `draughtsman.yaml` (`export.chromium.enabled`), or its browser
cannot be launched — the endpoint returns `501 PDF export is not available` with `detail` naming how to enable it.
`GET /api/health` reports whether it is present as `export.pdf` (`true`/`false`, proved by printing a page, not by configuration or a launch),
which is what the SPA reads to offer the File-menu Export PDF entry. A missing document is `404`. A render has a hard limit, `export.chromium.timeoutSeconds`
(default 60): a browser that has not printed by then is stopped (its driver and every process below it, by process id) and the endpoint answers
`504 PDF export timed out`, with `detail` saying what to do; `429` means every render slot is busy (`limits.pdfConcurrency`).

The colours are the shipped default diagram theme's for every document type (kind defaults, the document's own `theme`
overrides, each element's own style, the theme's export background), so the PDF matches the client's themed PNG and SVG;
it uses the organisation's default theme (Admin > Themes) when one is set and the shipped Overpass theme otherwise. A viewer's personal pick in the editor's theme menu is view state the server does not know.

Optional query parameters choose the page (`docs/pdf-export.md`); anything else is `400`:

| Parameter | Values | Default |
| --- | --- | --- |
| `pageSize` | `fit`, `a4`, `a3`, `letter` | `fit`: the page is cut to the drawing |
| `orientation` | `landscape`, `portrait` (ignored for `fit`) | `landscape` |
| `margin` | millimetres, 0 to 50 | `0` for `fit`, `10` otherwise |

A fixed page scales the drawing down (never up) to sit inside its margins, centred, on one page.

```sh
curl -s -o order.pdf "http://localhost:5180/api/documents/doc_9f1c2b3a4d5e/pdf?pageSize=a4&orientation=landscape&margin=10"
```

### `POST /api/import/sql`

Reads database DDL into a diagram of ER entities (`docs/er-entities.md`): one `custom:er/entity` node per table (a
`custom:er/weak-entity` for a weak one) and one crow's-foot `association` edge per foreign key, plus a dashed one for each
relationship a comment declares that the database does not enforce. Stateless like the other importers: nothing is saved and nothing is
placed, so post the result to `POST /api/layout` (which sizes each table from its columns) before saving it.

**Request**

- Body: the SQL as `text/plain`, up to 1,000,000 characters.
- `?tags=off` skips table tags (default `on`; anything else is `400`): with it on, a `[TAG]` in the comment above a `CREATE TABLE`
  becomes `props.tag`, recolours the table and adds a legend (docs/er-entities.md).
- `?implied=off` skips relationships declared only in comments (default `on`; anything else is `400`): with it on, a comment
  such as `-- -> UserTypes.Id (no FK)` after a column, or a listed `dbo.Users.TypeId -> dbo.UserTypes.Id`, becomes a dashed edge
  (below). Off draws the foreign keys only and raises no `implied-link-skipped` warning.
- `?dialect=tsql|postgres|mysql|sqlite` names the dialect (`mssql`, `sqlserver`, `pg`, `postgresql`, `mariadb` and `sqlite3`
  are accepted too). Absent or `auto` detects it from the text, falling back to T-SQL. The dialect only steers the
  tokenizer's quirks (`[bracket]` identifiers against Postgres array types, `#` comments and backslash string escapes in
  MySQL), so a wrong hint on portable DDL costs nothing, and a bracketed SSMS script still reads under a Postgres hint.
- `?type=architecture` (default) or `freeform`: the two types that can hold an entity. `flowchart` is `400`.
- `?title=` sets the title; otherwise `Imported schema`.

**Response** `200 OK`: the same `{ document, warnings }` envelope as `/api/import/mermaid`. Ids are `t_<table>` for
nodes (schema dots become underscores: `t_journey_UserIdentities`) and `e_1`, `e_2`, ... for edges, in source order. Each edge
attaches from the foreign key column's row in the child to the referenced key's row in the parent, as the column ports
`col:<name>` (`docs/er-entities.md` `Column ports`): the first column pair of a composite key, the parent's primary key when the
`REFERENCES` names no column, and the table itself for a column the table lacks.

What is read (a purpose-built tolerant parser in `Draughtsman.Core`, no dependency): `CREATE TABLE` with columns, types
with sizes (`nvarchar(50)`, `numeric(10,2)`, `nvarchar(max)`, `character varying(255)`, `timestamp(3) with time zone`,
`text[]`, `enum('a','b')`), `NULL` / `NOT NULL`, `PRIMARY KEY` (inline, named `CONSTRAINT`, composite), `UNIQUE`,
`CREATE UNIQUE [CLUSTERED | NONCLUSTERED] INDEX name ON t (cols)` (with `IF NOT EXISTS`, `CONCURRENTLY`, `ONLY`, `USING m`,
sort orders, `INCLUDE`, `WITH (...)`), inline
`REFERENCES t` and `REFERENCES t (col)`, table-level `FOREIGN KEY (a, b) REFERENCES t (x, y)`, and `ALTER TABLE ... ADD
[CONSTRAINT n] PRIMARY KEY | UNIQUE | FOREIGN KEY` (with `WITH CHECK`, `ONLY`, `IF EXISTS`). Bare, `[bracketed]`,
`"double quoted"` and `` `backtick` `` identifiers, schema-qualified (`dbo.Users`, `journey.UserIdentities`, `db.dbo.t`).
Stepped over safely, parentheses, string literals and `::` casts included: `DEFAULT` expressions, `COLLATE`,
`CHARACTER SET`, `IDENTITY(1,1)`, `AUTO_INCREMENT`, `GENERATED ... AS IDENTITY`, `CHECK`, computed columns, `ON DELETE /
ON UPDATE` actions, `DEFERRABLE`, `ENGINE=...`, `WITH (...)`, `ON [PRIMARY]`, MySQL `KEY` / `INDEX` items. Comments (`--`,
`/* */`, `#` for MySQL) and `GO` batch separators are ignored silently, and so is SSMS's `ALTER TABLE t CHECK CONSTRAINT
fk`. A T-SQL procedure, function or trigger runs to the next `GO`, and a Postgres `$$` body is one string, so neither is
read as tables.

How a table becomes a node:

- Label: the table name; a schema other than `dbo` / `public` is kept as a prefix (`journey.Orders`).
- Kind and shape: `entity` / `custom:er/entity`, except a **weak entity**, `weak-entity` / `custom:er/weak-entity`: a table
  whose primary key is composite (two or more columns) and includes a column of a foreign key to *another* table that is in the
  source. A single-column key that is also a foreign key, a foreign key outside the key, a composite key with no foreign key
  column, a foreign key to a missing table and a foreign key to the table itself all stay `entity`. Only real foreign keys
  count; a comment-declared link never makes a table weak. A many-to-many link table (its key is two foreign keys) is
  therefore drawn weak too.
- `props.columns`: one `[PK] [FK] name type [not null | null]` line per column. A column that is both primary and
  foreign key carries both markers; a composite key is one `PK` row per column; a composite foreign key is one `FK` row
  per column. A name with spaces is double quoted. A column is `not null` when the source says so, when it is in the
  primary key, or when it is an identity / serial column, and `null` otherwise.
- A foreign key to a table that is not in the pasted SQL is a `missing-reference` warning and draws no edge (the column
  keeps its `FK` marker). Two identical foreign keys (say inline and again by `ALTER TABLE`) are one edge. A table named
  by different case, or `dbo.` / `public.` versus no schema, is the same table; the same name in another schema is not.

How a foreign key becomes an edge: kind `association`, `routing.mode` `orthogonal`, drawn **from the referencing table to
the referenced one**, labelled with the foreign key's column names (`OrderId, LineNo`). The crow's-foot ends
(`style.arrowStart` at the referencing table, `arrowEnd` at the referenced one):

| Relationship | `arrowStart` | `arrowEnd` |
| --- | --- | --- |
| One-to-many (the normal case) | `zeroOrMany` | `oneAndOnlyOne`, or `zeroOrOne` when any foreign key column is nullable |
| One-to-one: the foreign key columns are exactly the child's primary key, exactly a `UNIQUE` constraint of it, or exactly the columns of a `CREATE UNIQUE INDEX` on it | `zeroOrOne` | as above |

"Exactly" means the same set of columns in any order: a unique key on only some of a composite foreign key's columns, or on
more, leaves it one-to-many. A unique index counts when its key is a plain list of columns (an expression such as
`lower(email)` is not one), and, if it is filtered, only when the filter is `col IS NOT NULL` clauses joined by `AND`; any
other filter leaves rows out of the index, so it proves nothing and is skipped with an `unsupported-syntax` warning.

A reference with no column list (`constraint FK references Customers`) targets the referenced table's primary key.

Relationships a comment declares (`?implied=on`, the default). Some schemas leave a relationship out of the database and
write it in a comment. Two shapes are read, and nothing else, so a comment that merely mentions an arrow in prose draws
nothing:

1. **Inline**, in a comment on the same line as a column's definition inside its `CREATE TABLE`, the comment text starting with
   `->` and the target `Table.Column` (or `schema.Table.Column`):
   `TypeId int NOT NULL DEFAULT 1, -- -> UserTypes.Id (no FK)`.
2. **Listed**, a whole comment line (a `--` comment, or a line of a `/* */` block, anywhere in the script) that is exactly
   `[schema.]Table.Column -> [schema.]Table.Column`, optionally led by a `-` or `*` bullet:
   `dbo.ChangeHistory.UserId -> dbo.Users.Id`.

After the target, only an optional `(no FK)` and then either the end of the comment or a `;` and any note may follow
(`-- -> AccountTypes.Id (no FK); V1 ONLY concept`). Names may be bare, `[bracketed]`, `"quoted"` or `` `backticked` ``.
Prose does not match: a comment that does not start with the arrow (inline) or with a qualified name (listed), an arrow in
mid-sentence (`UserId -> Users.Id (no FK).`, `StatusId (-> AppStatus)`), or text after the target that is not `(no FK)` or a
`;` note.

Each accepted link becomes an `association` edge like a foreign key's, with the same `col:<name>` ports and crow's-foot ends
(NOT NULL child column: `oneAndOnlyOne` at the parent; nullable: `zeroOrOne`; the child column is unique: `zeroOrOne` at the
child), but `style.dash` is `dashed` and the label is the column and the marker: `TypeId (no FK)`. The column is **not** marked
`FK`, since the database has no foreign key there. The same link written twice (say inline and again listed) is one edge, and a
link that a real foreign key already declares for the same column pair draws no second edge; both are dropped silently. A
link whose shape is accepted but that cannot be drawn is one `implied-link-skipped` warning at the comment's line, naming why:
its table or column is not in the source, or an inline arrow is not on a column's line. The DDL export leaves an edge whose
label ends in `(no FK)` out (it documents the schema; writing it as a constraint would enforce what the source chose not to).

Warnings never fail the import. Codes (with `line`, the one-based line of the pasted SQL):

| Code | Meaning |
| --- | --- |
| `implied-link-skipped` | A comment-declared relationship (above) was not drawn: it names a table or column that is not in the source, or an inline `-> Table.Column` is not on a column's line. |
| `unsupported-syntax` | A statement was skipped, naming it: `CREATE INDEX` (a non-unique one; a unique one is read, unless its key is an expression or its filter is not `IS NOT NULL`), `CREATE SCHEMA`, `USE`, `SET`, `GRANT`, `DROP`, `INSERT`, views and anything else that is not `CREATE TABLE` or `ALTER TABLE ... ADD`; also a temporary table and a `CREATE TABLE` with no column list (`AS SELECT`, `LIKE`). |
| `parse-error` | A column or foreign key inside a table could not be read and was skipped; the rest of the table was kept. |
| `missing-reference` | A foreign key (or an `ALTER TABLE`) names a table that is not in the source. |
| `duplicate-table` | A table was defined twice; the first definition was kept. |
| `empty-diagram` | The source produced no tables. |

Text with no tables is still `200`, with `empty-diagram` and an empty document. **Errors:** `400` empty body, unknown
`dialect`, or a `type` other than `architecture` / `freeform` (problem details); `413` source over 1,000,000 characters
(a `Content-Length` over that is rejected before the body is read).

```sh
curl -s -X POST 'http://localhost:5180/api/import/sql?dialect=tsql&title=Shop' \
  -H 'Content-Type: text/plain' --data-binary @schema.sql
```

### `POST /api/export/sql`

Accepts a complete entity diagram as `application/json` and returns `CREATE TABLE` DDL as `text/plain`. Stateless: nothing
is saved. `?dialect=tsql|postgres|mysql|sqlite` (default `tsql`; anything else, including `auto`, is `400`). Invalid JSON is
`400`; a document that fails the shared schema is `422`.

- Each entity node (`custom:er/*` shape, or kind `entity` / `weak-entity`) is a table: label is the name (a `schema.` prefix
  is kept in T-SQL and PostgreSQL, folded into the name in MySQL and SQLite), `props.columns` the columns, `PK` rows the
  primary key, `not null` the nullability (a primary key is always `not null`). Types are translated for the target where
  the dialects spell them differently (`nvarchar(max)` to `text`, `bit` to `boolean`, `uniqueidentifier` to `uuid`,
  `serial` to `int identity(1,1)`, ...) and otherwise left as written, so a same-dialect round trip does not change them.
- Each `association` edge between two entities is a foreign key on the referencing (crow's-foot, many) end, naming the
  columns in the edge's label and referencing the other table's primary key; with no crow's foot the edge is read as
  child-to-parent, the way the importer draws it. A one-to-one end (a `one` / `zeroOrOne` / `oneAndOnlyOne` at the
  referencing end) also writes a `UNIQUE` constraint unless the columns are already the primary key. An edge with no
  usable label uses the child's unclaimed `FK` columns, and failing that gets new columns named `<Parent>_<key>`.
- Tables come first, then every foreign key as `ALTER TABLE ... ADD CONSTRAINT` (T-SQL ends each statement with `go`,
  the others with `;`). SQLite cannot add a constraint later, so its foreign keys are inline in `CREATE TABLE`.
- What could not be carried (a node that is not an entity, a many-to-many edge, a relationship to a parent with no
  primary key, an entity with no columns, a column added for a relationship) is a `-- warning:` comment at the head of the
  script, so the script says what it left out. An edge whose label ends in `(no FK)` (the importer's dashed edge for a
  relationship the source did not enforce) is left out silently: it is not a constraint.

Import, export and import again gives the same tables, columns, keys and relationships in the same dialect. The one
thing that cannot survive is the `FK` marker on a column whose target table was not in the diagram: there is no edge to
write the constraint from.

### `POST /api/export/mermaid`

Accepts a complete document as `application/json` and returns Mermaid source as `text/plain`, for pasting into a README
or any page that renders Mermaid. Stateless: nothing is saved. Invalid JSON is `400`; a document that fails the shared
schema is `422` Problem Details. The mirror of `POST /api/import/mermaid`: what comes out imports back as an equivalent
diagram (same participants and messages in the same order and kinds, or the same boxes, links and subgraphs). Ids are
rewritten to plain Mermaid ids (`p_user` becomes `user`, a keyword such as `end` becomes `x_end`), so the importer names
them differently on the way back. Lines end in `\n`, and no geometry, routing, colour or style is exported: Mermaid lays
the picture out itself.

- **`sequence`** becomes a `sequenceDiagram`: `title`, then `participant`/`actor` lines with `as` aliases in node order,
  then the messages in edge order. The arrow follows the kind and its style: `sync` `->>`, `return` `-->>`, `async` `-)`,
  a dashed line `-->>` / `--)`, no head `->` / `-->`, a head at both ends `<<->>` / `<<-->>`. `activate` is a `+` before
  the receiver, `deactivate` a `-` (Mermaid closes the sender's bar); a message with both keeps the `+` and adds a
  `deactivate` line after it. A self message is `A->>A`. A message to an id no node declares gets a lifeline of its own.
  Fragments and notes do not exist in the model yet (docs/sequence-diagrams.md, milestone 2), so there is nothing to flatten.
- **A document made only of ER entities** (every node `custom:er/entity` or `custom:er/weak-entity`, or of kind `entity` /
  `weak-entity`, and every edge joining two of them) becomes an `erDiagram` with the title as front matter. Each entity is a
  block of attributes, one per column line in Mermaid's order `type name [PK, FK]` (`nvarchar(100) Name`,
  `int Customer_Id PK, FK`; a type or name is folded to Mermaid's word characters, `decimal(10, 2)` becoming
  `decimal(10_2)`); nullability, geometry and column ports are dropped. Each edge is a relationship
  `Order }o--|| Customer : "CustomerId"`: `arrowStart` (the from end) and `arrowEnd` give the two cardinalities
  (`one`/`oneAndOnlyOne` `||`, `zeroOrOne` `|o` / `o|`, `oneOrMany` `}|` / `|{`, `many`/`zeroOrMany` `}o` / `o{`), a dashed
  or dotted line is written `..` (non-identifying) and a solid one `--`, and the edge label is the relationship text (`""`
  when there is none). An end with no crow's foot is written as exactly one, and a weak entity as a plain one, each with a
  `%%` comment. An entity name that is not a plain word is quoted, and two entities with one label are kept apart
  (`Order Lines`, `Order Lines 2`). [`POST /api/import/mermaid`](#mermaid-erdiagram) reads this text back, so an exported entity diagram round trips (nullability, geometry, column ports and the weak entity's double border are what the export drops).
- **Every other type**, and an ER document that also holds anything that is not an entity (a legend container, a note),
  becomes a `flowchart TD` with the title as front matter. A shape maps only where Mermaid's drawing
  means the same thing: `rectangle` `[ ]` (`subprocess` `[[ ]]`, `note` `> ]`, as the importer reads them back), `rounded`
  `( )`, `diamond` `{ }`, `cylinder` `[( )]`, `pill` `([ ])`, `hexagon` `{{ }}`, `parallelogram` `[/ /]`,
  `predefined-process` `[[ ]]`, `on-page-connector` `(( ))`. Any other shape is a rectangle with a `%%` comment above it
  (`%% n_b: shape 'document' has no Mermaid equivalent; drawn as a rectangle`), so the loss is in the text and never an
  error. A container, or any node with children, is a `subgraph`, nested as the document nests it. An edge is a link
  `-->` (`---` with no head, `-.->` dashed or dotted, `==>` for a stroke width of 3 or more, `<-->` and `o--o` for heads
  at the start), its label `|"text"|`; the kind's default look (`association` no heads, `dependency` dashed, ...) applies
  under the edge's own style. An arrowhead Mermaid has no spelling for (a crow's foot, a diamond) is a plain arrow with a
  `%%` comment above the link.
- Text Mermaid reads as syntax is escaped in the entity form the importer decodes: `;` `#59;`, `#` `#35;`, a double quote
  in a flowchart label `#quot;`, a newline `<br/>`.

```sh
curl -s -X POST http://localhost:5180/api/export/mermaid \
  -H 'Content-Type: application/json' --data-binary @docs/examples/checkout-sequence.json
```

The editor's File menu Export Mermaid calls this and saves the text as a `.mmd` file; Copy as Mermaid puts it on the clipboard, and a browser that refuses the clipboard
shows the text in a dialog to copy by hand (`design/atlas/mermaid-export.md`). MCP `read_document format=mermaid` returns
the same text.

The response carries `X-Draughtsman-Export-Warnings`: how many shapes, arrowheads or other things had no Mermaid
spelling and were simplified (each has a `%%` comment in the text). `0` is a faithful export; the SPA says the export is
lossy when it is not `0`. When it is not, `X-Draughtsman-Export-Notes` lists them: a percent-encoded JSON array of the
messages (at most 25, with a last entry counting the rest), which the SPA shows as a list beside the saved file or the copy.
It is absent from a faithful export. File > Export Mermaid saves the text as `<title>.mmd`; Copy as Mermaid is the clipboard
route.

## POST /api/layout

Lays out a document with ELK (ADR 0002) and returns it with geometry on every node. Stateless: nothing is stored. Nodes may arrive with no `geometry`; sizes that are given are kept, positions are always recomputed.

Query string: `algorithm=layered|stress|tree` (default follows the document type: stress for freeform, layered otherwise) and `direction=down|right|up|left` (layered only, default down). `tree` is ELK's mr.tree: a top-down parent-above-children arrangement for org charts, in which an `assistant` node is placed beside its manager on an offset side branch.

Body: a document object.

Response `200`:

```json
{
  "document": { "...": "the same document, laid out" },
  "warnings": [
    "edge e_9 references a missing node (n_ghost); left unrouted"
  ]
}
```

`warnings` lists what the layout left alone rather than failed on: an edge to a missing node, a self-loop, an edge from a container to a node inside it, an edge across containers under `stress` or `tree`, a duplicate id. It is empty when everything was placed. The import endpoints use the same `{ document, warnings }` envelope.

A `sequence` document is returned unchanged, with no warnings and no geometry invented (`algorithm` and `direction` are ignored): its positions are derived from the order of its edges and nodes on every render (`docs/sequence-diagrams.md`), so there is nothing to place. The same holds for MCP `layout` (`create_document`'s `layout` and `patch_document`'s `layout`), which never asks the sidecar for a sequence document.

Errors:

| Status | When |
| --- | --- |
| `400` | The body is not a JSON object, or `algorithm` / `direction` is not one of the values above. |
| `422` | The body is not a document the layout can use (`detail` says which member), or ELK rejected the graph. |
| `502` | The sidecar failed for a reason the request cannot fix, or answered with something the server could not read. |
| `503` | The sidecar is not running. The reason is in the server log; it restarts on its own. |
| `504` | The layout did not finish within `sidecar.layoutTimeoutSeconds`; the sidecar has been restarted. |

## POST /mcp

The MCP server (build-spec-v0.1.md section 9): `list_documents`, `read_document`, `create_document`,
`patch_document`, `render_document`, `diff_documents`, `critique_document` and `get_schema`, mounted as streamable
HTTP on the main port when `mcp.enabled` (the default). Tool implementation is `src/Draughtsman.Mcp`; tests are
`tests/Draughtsman.Mcp.Tests` (tool logic against fakes) and `tests/Draughtsman.Server.Tests/McpHttpToolsTests.cs`
(the real transport). The same tools are also served over stdio for an agent on the same machine:
`draughtsman mcp --stdio` runs the whole binary in that mode instead of the web host, with `ILogger` output forced
to stderr so stdout stays a clean JSON-RPC stream (from source use `dotnet run --no-launch-profile ... -- mcp --stdio`:
the launch profile makes `dotnet run` print a line to stdout first).

- Tool errors are self-correcting. Every call's arguments are checked against the tool's own published input schema
  before the SDK binds them (`ToolArgumentValidator`, one call-tool filter registered by both hosts), so a missing
  required argument, a wrong type or a value outside a schema enum comes back as an `isError` result naming the tool
  and the argument (`create_document: missing required argument 'title' (a string)...`), never an argument value. An
  unknown `format` (and the like) is rejected by the tool itself with the accepted values listed. Anything else
  unexpected still returns the SDK's generic `An error occurred invoking '<tool>'.` with no exception text, and the
  detail stays in the server log.
- `patch_document`'s ops are `addNode`, `removeNode`, `updateNode`, `addEdge`, `removeEdge`, `updateEdge`, `moveEdge`,
  `setProp`, `removeProp` and `restyle`. Edge order is meaning in a `sequence` document (the order of the edges is time,
  `docs/sequence-diagrams.md`), so `addEdge` takes an optional `before` or `after` (the id of an existing edge) to insert
  mid-stream instead of at the end, and `moveEdge` (`{ "op": "moveEdge", "id": "m_5", "before": "m_2" }`, or `after`) moves an
  existing edge next to another, changing nothing else. Both fail the whole call rather than guess: an unknown id, both
  `before` and `after`, an empty or non-string reference, a `moveEdge` with neither, or an edge placed next to itself. They
  are additive contract: a `patch_document` that uses neither behaves as before. There is no `moveNode` yet (participant
  order is the order of `nodes[]`).
- `create_document` also takes `drawio`: a draw.io file's XML, the same reader as `POST /api/import/drawio` (type `flowchart`, `architecture` or `freeform`; the optional `page` argument counts from 1 and defaults to 1). Its positions arrive with the file, so it is laid out only when `layout` is true. Each warning comes back as `<code>: <message>`, a `kind-guessed` one asking for a look at that kind; a file that is not draw.io, has a DOCTYPE or is past a limit is an error naming the reason.
- `create_document` also takes `visio`: a Visio (`.vsdx`) drawing, read by the same importer as `POST /api/import/visio`. A `.vsdx` is a binary ZIP package and a tool argument is text, so **`content` is the file base64-encoded** (`base64 -i diagram.vsdx`; whitespace and a `data:` URI prefix are tolerated; up to 3,000,000 bytes before encoding, checked before it is decoded). `type` is `flowchart`, `architecture` or `freeform`; the `page` argument counts from 1 and defaults to 1 (it applies to `drawio` and `visio`). Positions arrive with the file, so it is laid out only when `layout` is true. Each warning comes back as `<code>: <message>`; a file that is not a `.vsdx`, an older `.vsd`, content that is not base64, a hostile package or one past a limit is an error naming the reason.
- `create_document` and `patch_document` take `dsl`/`mermaid`/`sql`/`json` and addressable ops respectively (`sql` is the
  `POST /api/import/sql` importer, for architecture or freeform documents, and is always laid out because it carries no
  positions; `mermaid` is `POST /api/import/mermaid`'s importer, a flowchart for type `flowchart`/`architecture` or a
  `sequenceDiagram` for type `sequence`, and a type that does not match the source is refused with a message naming the
  mismatch, since `create_document` sets the type from its argument and would otherwise relabel it); `patch_document`
  always checkpoints the document first and returns that checkpoint's id, so a patch is always one click from
  reversible even though replacing the live snapshot is not itself versioned. Both accept an optional `actor`
  parameter (`user:<name>` or `agent:<name>/<model>` over stdio, where it is self-reported, and default to
  `agent:mcp-client/unknown` when omitted; over HTTP `agent:<name>/<model>` only, on top of the token's owner). It is not
  the `X-Draughtsman-Actor` header, which `/mcp` does not read.
- `create_document` compares what the `dsl` or `json` content says about itself with its `title` and `type` arguments
  instead of silently overriding either. A `type` the content states that differs from the `type` argument is refused (the
  content was read against that type's kinds, so changing it would relabel the diagram, as a Mermaid source mismatch is
  already refused). A non-empty content `title` that differs from the `title` argument is replaced by the argument, and the
  result's `warnings` says so. A new document id is always assigned: an `id` in the content is ignored.
  `json` takes the same document as `POST /api/documents` (the server fills `schemaVersion` and `meta`, drops any claimed
  `meta.aiProvenance`, and a node still needs `id`, `kind`, `label` and `shape`); it differs in two ways only: `title` and
  `type` may be left out of the body, since they are arguments here, and a body `id` is never kept.
- An unknown `patch_document` op kind fails the whole call with a message naming the valid ones
  (`addNode`, `removeNode`, `updateNode`, `addEdge`, `removeEdge`, `updateEdge`, `moveEdge`, `setProp`, `removeProp`,
  `restyle`, `setFlythrough`), and the tool's own description lists all of them.
- `render_document` draws orthogonal connectors (`routing.mode` orthogonal, no stored waypoints) along the editor's own
  routes, fetched from the sidecar's `route.document` (the same router the canvas and the client PNG/SVG export run,
  `web/src/canvas/routeDocument.ts`); a stored-waypoint (pinned) connector still draws its waypoints, and straight and
  curved connectors are unchanged. If the sidecar cannot route, the picture is still returned with a warning saying its
  orthogonal connectors are drawn as straight lines. A container's label sits top-left in its header band, as on the canvas.
  A node's label is wrapped to its shape's label rectangle the way the canvas wraps it (`web/src/canvas/text.ts`,
  `src/Draughtsman.Mcp/Rendering/NodeLabelLayout.cs`): greedy word wrap to the rectangle less 8 either side, an explicit `\n` always a
  break, a word wider than a line split between characters, and the block centred on the rectangle (a container's runs down from
  its top-left). The same drawing serves the thumbnail and the PDF. Widths come from the canvas's character-class estimate
  at the 13px the server draws in, not from a font, so a face much wider than Helvetica or Arial can run into the padding; a
  block taller than its rectangle overflows it, as on the canvas, and nothing shrinks or clips the text. Edge and message labels
  are not wrapped.
- `patch_document` also has `setFlythrough`: `{ "op": "setFlythrough", "flythrough": { "keyframes": [...], "loop": true } }`
  replaces the document's camera path whole (`docs/flythrough.md`), and `"flythrough": null` removes it. It names no node or
  edge, so the write's provenance entry does not list it, and the schema checks the result once the batch is applied (an empty
  `keyframes`, a view with no area, a time outside 0 to 600000 or an unknown member fails the whole call). Every other op
  leaves the path untouched.
- `diff_documents` returns `{ fromVersion, toVersion, diff }` where `diff` lists `addedNodes`, `removedNodes`,
  `changedNodes`, `addedEdges`, `removedEdges` and `changedEdges` (matched by id, field by field) and, since sequence
  diagrams, `movedNodes` and `movedEdges`: elements that kept their id and fields but changed place among those both
  revisions hold, each `{ "id": "m_5", "after": "m_1" }` (`after` null when it is now first), the fewest moves that turn one
  order into the other. A reorder is a change because order is meaning (the edge order of a `sequence` document is time);
  the two lists are empty for an identical order, and for a flowchart or architecture they are simply informational.
  `diff` also carries `flythroughChange` `{ from, to }` (each a whole camera path, or absent when that revision has none)
  when the two revisions' paths differ, and omits it when they are the same; the path is reported, never turned into ops.
  It also carries `documentChanges` (a retitle, a type change, a theme override added, changed or removed, as `{ field, from, to }`
  with `theme.<token>` per token) and `volatileChanges` (the saved time, author and AI provenance, which every save rewrites,
  listed apart from every other change), and each `changedNodes` and `changedEdges` entry carries `props`, one `{ field, from, to }`
  per prop key that differs (`from` absent for a key added, `to` absent for one removed) beside the whole `props` field.
- `read_document format=mermaid` is `POST /api/export/mermaid`'s text (a `sequenceDiagram` for a `sequence` document, a
  `flowchart` for any other type); `dsl`, `json` and `outline` are unchanged.
- `read_document format=outline` numbers a `sequence` document's edges in time order (`  3. m_a: p_api -> p_api [sync]
  "validate"`, with `+activate` / `+deactivate` marks) under the heading `edges (in time order):`; other types keep the
  unnumbered `edges:` list.
- `render_document` draws a `sequence` document from the scene the sidecar derives with the editor's own function
  (`sequence.scene`, `web/src/model/sequenceScene.ts`): header boxes, dashed lifelines, activation bars, and each message
  as a line with its label above it, in its kind's look (`sync` solid and filled, `async` solid and open, `return` dashed
  and open, `create` dashed and open, `destroy` solid and filled, from the vocabulary's `defaultStyle`), plus the
  combined-fragment frames (a `<g data-id>` per fragment: the frame, an operator tab, a dashed divider and a guard per
  operand), each `note` as a folded box with its text, a created participant's header at its create row and the X that
  ends a destroyed lifeline. A `fragment` node draws as a frame and a `note` edge as a box, never through the generic
  node and edge drawing. It refuses, rather than draws a guess, when the sidecar cannot supply
  the scene: `Cannot render: a sequence diagram's layout is derived by the sidecar, and the sidecar is unavailable: ...`
  (the PDF endpoint answers `503`).
- `render_document` draws the diagram and nothing else: no footer or mark is added to the SVG or the PDF, over HTTP or over `draughtsman mcp --stdio`. `read_document` (`json`, `dsl`, `outline`, `mermaid`) is data, as it always was.
- `render_document` produces `svg` by default and `pdf` when the optional Chromium component is present, and returns
  `{ svg, pdf, warnings }` - `svg` carries the markup and `pdf` carries base64 bytes, one populated for the format
  asked and the other null. `warnings` is empty for an ordinary document and otherwise names each edge the picture
  could not show and the endpoint id it points at, plus anything the layout step it ran first reported, so a caller
  learns what the render actually did without a second call. It lays a document out (chaining to the sidecar the way
  `/try` does, degrading to a clear tool error rather than a partial picture when the sidecar is down) and draws each
  edge through its `routing.waypoints` the way the canvas does, though the ends clip to the node boundary rather than
  attaching at `from.port` / `to.port`. Its output is a plain geometry-and-label serialiser
  (`src/Draughtsman.Mcp/Rendering/SvgRenderer.cs`), not the theme-token resolution `web/src/theme/resolveStyle.ts` and
  the canvas use - that resolution lives in TypeScript and porting it was out of scope. Asking for `pdf` when the
  component is absent fails the call with a message naming `export.chromium.enabled`/`export.chromium.path`; `png`
  needs the same Chromium component and is not wired here.
- `get_schema` returns the committed `docs/schema/document.schema.json` verbatim, plus the requested type's kind
  vocabulary JSON (`src/Draughtsman.Core/Schemas/*.json`) when `type` is `flowchart`, `architecture` or `sequence` - all read
  from the shipped files at call time, never restated in code. Whenever a `type` is named it also returns
  `genericIcons`, the unbranded generic-technology icon library (`src/Draughtsman.Core/Schemas/generic-icons.json`),
  whose icons are placed as the presentation shape `custom:generic/<name>`; the library is not tied to a document
  type, so it comes back for `freeform` too. Every icon names a real architecture `paletteKind`, so placing one never
  trips the unknown-kind lint; seven of them also carry an architecture kind of their own (`load-balancer`,
  `api-gateway`, `cache`, `cdn`, `firewall`, `object-storage`, `function`) whose `defaultShape` names the icon. A named
  type also returns `stateShapes`, the UML state-machine shape library
  (`src/Draughtsman.Core/Schemas/state-shapes.json`), whose nine shapes are placed as `custom:state/<name>` and drawn
  natively by both renderers. Each names its own flowchart kind (`initial`, `final`, `choice`, `fork-join`,
  `shallow-history`, `deep-history`, `entry-point`, `exit-point`, `composite-state`) as its `defaultShape`, so the DSL
  prints by kind and the vocabulary's one-line description teaches when to use it; `composite-state` is a container, so
  a child parented to it is as valid as one inside a `container`. It also returns `bpmnShapes`, the BPMN 2.0 shape
  library (`src/Draughtsman.Core/Schemas/bpmn-shapes.json`, described in `docs/bpmn.md`): 35 shapes placed as
  `custom:bpmn/<name>`, each naming its own flowchart kind `bpmn-<name>` as its `defaultShape`, alongside the glyph line
  art and layout numbers both renderers read. The flowchart vocabulary teaches each kind, and adds the `message-flow`,
  `bpmn-association` and `data-association` edge kinds; sequence flow is the existing `flow` kind, and pools and lanes are
  the existing `pool` and `lane` kinds.
  A named type also returns `umlShapes`, the UML class
  shape library (`src/Draughtsman.Core/Schemas/uml-shapes.json`): `custom:uml/class`, `interface`, `lollipop` and
  `socket`, each the `defaultShape` of an architecture kind (`class`, `interface`, `provided-interface`,
  `required-interface`) and usable in `freeform`. A class or interface is one node whose label is its name and whose
  members are the `attributes` and `operations` props (arrays of strings, one member per string, a leading `+ - # ~`
  being the visibility glyph); `docs/uml-class-compartments.md` has the model and the DSL spelling.
  A named type also returns `basicShapes`, the general-purpose shape library
  (`src/Draughtsman.Core/Schemas/basic-shapes.json`, described in `docs/basic-shapes.md`): `custom:basic/ellipse`,
  `triangle`, `trapezoid`, `chevron`, `cross`, `arrow`, `double-arrow`, `note`, `callout`, `brace`, `cube`, `folder` and
  `component`, usable in every type but `sequence`. They have no kind of their own: each is path data both renderers read,
  placed on any kind (`note` for the annotation marks `note`, `callout` and `brace`), and the DSL always writes the shape.
  A named type also returns `erShapes`, the ER entity (database table) shape library
  (`src/Draughtsman.Core/Schemas/er-shapes.json`): `custom:er/entity` and `weak-entity`, each the `defaultShape` of an
  architecture kind (`entity`, `weak-entity`) and usable in `freeform`. An entity is one node whose label is the table
  name and whose rows are the `columns` prop (an array of strings, one column per string, written
  `[PK] [FK] name type [not null | null]`, for example `PK Id int not null`); the library's `description` is the full
  column grammar and its `examples` the parser's conformance list. Relate two entities with an `association` edge whose
  `style.arrowStart`/`style.arrowEnd` are crow's-foot ends (`one`, `zeroOrOne`, `oneAndOnlyOne`, `many`, `zeroOrMany`,
  `oneOrMany`); `docs/er-entities.md` has the model and the DSL spelling. An edge end can attach to one column's row with the
  derived port `col:<name>` (`t_order.col:CustomerId`; `docs/er-entities.md` `Column ports`), and `portRef` in the document schema is the
  one place the wider port spelling is admitted.
- `critique_document` analyses the current snapshot against deterministic structural rules (kinds plus graph
  topology, no model judgement) and returns `{ findings: [...] }`. Each finding has a stable `rule` id, a `severity`
  (`error` | `warning`), a one-sentence `message` and the `nodeIds` / `edgeIds` that are its evidence; every evidence
  id names an element that exists, so an agent can follow a finding back to the graph. The rules are
  `dangling-edge` (an edge endpoint that no longer exists; `error`), `dangling-parent` and `non-container-parent`
  (`error`), `unknown-column` (an edge end on a table column port, `col:<name>`, whose table has no such column, or that is not
  a table; `warning`, and it runs on every document type), `entity-without-primary-key` (an ER entity or weak entity, shape
  `custom:er/*`, none of whose columns is marked `PK`, or that has no columns; `warning`, every document type), and `decision-single-branch`, `decision-branch-unlabelled`, `dead-end`, `unreachable-node`,
  `isolated-node`, `empty-container` and `database-unused` (`warning`), and, for a `sequence` document only,
  `unbalanced-activation` (`warning`: a message with `deactivate` when its sender has no open activation bar, or an
  `activate` nothing closes, a `destroy` closing every bar still open on its target; the evidence is the message and the
  participant), `empty-fragment` (`warning`: a `fragment` no message is in, directly or through a fragment nested in
  it; evidence the fragment), `non-contiguous-fragment` (`warning`: a message that is not in a fragment sits between its
  first and last member, so its frame is drawn around it too; evidence the fragment and the intruding messages) and
  `unknown-fragment` (`warning`: a message whose `in` names no fragment node, evidence the message, or a fragment whose
  operator is not one of `alt opt loop par break critical neg assert ref seq strict ignore consider`, evidence the
  fragment). A freeform document is analysed only for
  `dangling-edge`, `dangling-parent`, `unknown-column` and `entity-without-primary-key`, because it has no kind vocabulary, so the kind-keyed rules have nothing to
  key on. The analyser is `src/Draughtsman.Core/Critique/CritiqueAnalyser.cs` and needs no data beyond the document,
  so it never calls the sidecar. Two checks are unbuilt: "a service with no error path" has no error-edge kind in the
  vocabularies to key on, and "a container whose children contradict its label" needs model judgement, which this
  deterministic analyser deliberately excludes. `dead-end` fires on `process`, `subprocess` and the BPMN activity kinds; `io`
  is a legitimate sink. In a BPMN diagram a start event, or an intermediate event nothing flows into (an error boundary event,
  a message or link catch), counts as an entry for `unreachable-node`, BPMN activities and gateways are the nodes it
  judges, and a `bpmn-annotation` is decorative like a `note`, so it is never `isolated-node`. `database-unused` counts a `data` edge at either end, so a read-only consumer drawn `db -> service` counts.
- `layout: "affected"` on `patch_document` relays out when any node in the patched document has no geometry, not
  only a node the ops touched, because `POST /api/layout` (and the sidecar behind it) always recomputes every
  position; there is no partial-relayout primitive to preserve untouched nodes' positions, so `"affected"` cannot
  mean anything narrower than "lay out if geometry is missing, else leave positions alone".
- A write whose actor is an agent appends one `meta.aiProvenance` entry per call - not per op - naming the agent,
  its model, the action (`created` or `updated`) and every element id the call touched, the shape
  `docs/examples/order-service.json` shows. A `user:` actor writes no provenance entry; the `audit` row is written
  either way.
- `patch_document` writes under the same base-version guard as `PUT /api/documents/{id}` (see
  [Base versions](#base-versions)), with no header to carry: the base version is the snapshot the ops were applied
  to. A write that landed while the patch was being built is not overwritten - the call fails with the ops
  unapplied, and the agent reads the document again and re-sends them.
- `create_document` and `patch_document` reject a document whose `nodes` or `edges` contain a duplicate id. The
  shared `docs/schema/document.schema.json` does not express that constraint, so `POST`/`PUT /api/documents` still
  accept one; tightening the REST contract is a separate decision.
- `/mcp` over HTTP requires `Authorization: Bearer <token>` like every other route (see [Auth](#auth)); a request
  with no token, an unknown one, or a revoked one is `401` before any tool call runs. Every `401` carries
  `WWW-Authenticate: Bearer` and a plain-text hint naming the missing token, so an MCP client says "needs a token"
  rather than starting an OAuth flow (OAuth is not implemented; `/.well-known/*` answers `404`, never the SPA shell).
  `patch_document`'s `ops` may be a JSON array or a string holding that JSON. `draughtsman mcp --stdio` is
  unaffected: it never binds HTTP at all, so it stays inside the trust boundary of the machine and data folder it
  runs on, same as before this section existed. **`X-Draughtsman-Actor` is not read on `/mcp` at all**: the table above is
  for the REST routes. On `/mcp` the recorded actor is the token's owner (a user), and the only way to label the writer as
  an agent is the tools' own `actor` argument, which accepts `agent:<name>/<model>` only (a `user:` value is refused) and
  never changes whose token is acting. A header sent to `/mcp` is ignored, not validated.

## Audit

Every create, replace and delete writes a row to the `audit` table: document id, actor name, actor kind
(`user` or `agent`), model (agents only), action, the acting user's id and time. An admin reads them with
`GET /api/admin/audit?documentId=` ([Security events](#security-events)); they are also readable through `IStore.ListAudit`.

The audit table is document-scoped, so an instance-level change has no row there (sign-ins, password, role and token events go to the [security event log](#security-events) instead, and an admin reads either through `GET /api/admin/audit` and `GET /api/admin/security-events`): admin branding changes
([Branding](#branding)) are logged instead, with the acting admin's user id and which slot changed, under the
`Draughtsman.Server.Branding` logger category. The bytes and the product-name text are never logged. AI settings changes ([AI settings](#ai-settings)) are logged the same way under `Draughtsman.Server.Ai.Settings`: what changed and who, never a value and never the API key.

## Storage

The store behind the API is chosen in `draughtsman.yaml` in the data folder:

```yaml
storage:
  provider: sqlite          # sqlite (default) | postgres
  connectionString: ""      # empty means <data folder>/data.db for SQLite; required for postgres
  allowMigrationWithoutBackup: false   # SQLite: upgrade even if the safety copy cannot be made (see below)
```

Migrations run at startup. For SQLite the server first checkpoints the write-ahead log and copies `data.db` to
`data.db.pre-migration-<stamp>` next to it, **proves the copy** (the same size, it opens, `PRAGMA integrity_check` is `ok`), applies the migrations, then
checks the result: `integrity_check` must be `ok` and **no table may hold fewer rows than in the copy**. A migration that throws, or a result that fails
either check, **restores the copy over the database** (byte for byte, deleting the `-wal` and `-shm` first), refuses to start and exits with code `4`; the
data file is exactly as it was and the copy stays. Only after a good upgrade are older copies pruned, keeping the newest `retention.preMigrationCopies` (3 by
default; zero or less keeps all). If the copy cannot be made or does not read back whole, or the database cannot be opened at all (an unwritable data folder, a damaged file),
nothing is changed and the server exits with code `5`, unless `storage.allowMigrationWithoutBackup` is `true`, which lets an upgrade whose copy cannot be made go ahead without an automatic way back
(take your own backup first). To go back by hand, restore with the server stopped, and
delete the sibling `data.db-wal` and `data.db-shm` before putting the copy back: a write-ahead log left behind by the
migrated database is replayed onto the restored file and silently returns the instance to the migrated schema
(`draughtsman restore` does this for you). For an existing Postgres database with migrations pending, the server cannot
snapshot or roll back for you, so it logs a warning first that names the pending migrations and the exact `pg_dump` and restore commands; take the snapshot before upgrading.
`draughtsman config check` reports all of this without starting the server ([`upgrade-backup.md`](upgrade-backup.md#before-you-upgrade)). The exit codes of a start that stops here are `3` (database newer
than this build), `4` (upgrade undone) and `5` (refused before changing anything).

**An older build never opens a newer database.** If the database has migrations applied that this build does not contain
(a newer Draughtsman has already run on it), the server logs a critical message naming them and the way back (start the newer
version, or restore the copy or snapshot taken before the upgrade, with the path of the newest pre-migration copy when
there is one), changes nothing, serves nothing and exits with code `3`. `mcp --stdio` and `reset-admin` refuse the same way,
and `draughtsman restore` refuses a bundle whose database is newer than the build.

Everything the instance owns lives in the data folder (`./data` or `DRAUGHTSMAN_DATA`); a Postgres install keeps the
folder for everything except the database. **Do not back it up with a plain copy while the server runs**: see
[Backup and restore](#backup-and-restore) and [`upgrade-backup.md`](upgrade-backup.md).

**Retention.** The `retention` section of `draughtsman.yaml` bounds what the server makes automatically. A daily pass
(first run two minutes after start) deletes automatic checkpoints (the snapshot an agent edit takes first, version name
`checkpoint`) beyond `retention.checkpointsPerDocument` per document, newest kept (50 by default), and sessions that expired,
or were revoked, more than `retention.expiredSessionDays` ago (7). A version someone named is never touched. Zero or less
means keep everything. The same pass removes documents that have been in [the bin](#bin) longer than `retention.binDays` (30)
and records each. The pass logs what it removed and never stops the server.

The Postgres leg of the storage test suite runs only when `DRAUGHTSMAN_TEST_POSTGRES` is set, and skips otherwise.
Locally: `docker run --rm -d -p 5432:5432 -e POSTGRES_PASSWORD=draughtsman --name draughtsman-pg postgres:17-alpine`
then `DRAUGHTSMAN_TEST_POSTGRES='Host=localhost;Username=postgres;Password=draughtsman;Database=postgres' dotnet test`.

## Backup and restore

`draughtsman backup [--out <file.zip|folder>] [--include-secrets]` and `draughtsman restore <bundle.zip> [--overwrite]
[--sha256 <hex>]` are verbs of the same binary (`Cli/ServerCli.cs`, `Backup/`); neither starts the web host, and
`--DRAUGHTSMAN_DATA=<folder>` picks the data folder as for the other verbs. Exit `0` done, `1` refused or failed (nothing
changed), `2` usage.

- **The bundle** is one zip: `manifest.json` (format `draughtsman-backup/1`, time, version, provider, the newest applied
  migration, whether it holds secrets, row counts, and the size and SHA-256 of every file), `README.txt`, and under `data/`:
  `data.db`, `draughtsman.yaml`, `assets/`, `branding/`, `themes/` (and a chrome theme file kept in the data
  folder's root). Beside it, `<bundle>.sha256` in `sha256sum` format. The file is owner-only on Unix and is never overwritten.
- **The database is a snapshot, not a copy.** SQLite's `VACUUM INTO` writes one complete image as of a single read
  transaction, including what is still in the write-ahead log, so it is safe while the server is running and saving. A plain
  copy of a live instance is not: the newest commits sit in `data.db-wal` (a copy of `data.db` alone misses them) and a copy
  spanning a checkpoint is torn. `BackupConsistencyTests` reproduces both and shows the snapshot whole in the same situations.
- **Secrets are opt-in.** The key ring (`keys/`), the stored AI settings (`ai/`) and the ring's own backups (`backups/`) are left
  out. `--include-secrets` adds them, puts `-SENSITIVE` in a default file name, and writes a `SENSITIVE:` notice in the manifest
  and README; store that bundle like a password vault. Without it, a restored instance has a new key ring and the admin
  re-enters the AI API key; sessions and API tokens (in the database) carry over.
- **Postgres is not pretended.** On a Postgres instance `backup` makes no bundle: it prints the exact `pg_dump` command for
  the configured connection (the password is never printed; use `PGPASSWORD` or `.pgpass`) and the matching `pg_restore`, and
  exits `1`.
- **`restore` verifies before it writes.** It checks the zip against `<bundle>.sha256` (or `--sha256`), reads the manifest,
  refuses an unknown format, a newer schema than this build knows, any entry the manifest does not list, any unsafe path, and
  any file whose size or SHA-256 differs; then it extracts into a staging folder, checks the database with `PRAGMA
  integrity_check` and the manifest's document count, and only then moves the files into place. A data folder that is not
  empty is refused unless `--overwrite` is given, which first moves the database (with any `-wal`/`-shm`/`-journal`), assets,
  branding, themes and settings into `<data>/pre-restore-<time>/`; nothing is deleted. Stop the server first. A bundle
  from an older version is migrated on the first start, with the usual pre-migration copy.

## Diagrams in git: `diff` and `check`

`draughtsman diff <before.dsl> <after.dsl> [--format markdown|json] [--ignore-layout]` and `draughtsman check <file.dsl>
[--format text|json]` are verbs of the same binary (`Cli/DiagramFileCommands.cs`). They read only the files named, never start
the web host, and need no data folder or settings. `diff` runs the same `DocumentDiff` as `diff_documents` over two parsed
files and describes the result by label (`Draughtsman.Core.Diffing.SemanticDiff`); exit `0` no semantic change, `1` the diagram
changed, `2` a file could not be read or parsed. `check` prints the parse warnings and the `critique_document` findings; exit `0`
without an error-severity finding, `1` with one, `2` for an unreadable or unparsable file. The report shapes, the layout
and housekeeping rules, and GitHub Actions and GitLab CI recipes are in [`git-sync.md`](git-sync.md).

## Admin export

### `GET /api/admin/export`

Every document as JSON and DSL, and every uploaded image, in one zip: `documents/<id>.json`, `documents/<id>.dsl`,
`assets/<asset id>.<ext>`, a `manifest.json` (format `draughtsman-export/1`, each document's id, title and type, each asset's
id, type, size and SHA-256, and anything skipped with the reason) and a `README.txt`. It is a way out in open formats, not a
backup: the current version of each document only, with no checkpoints, audit trail, users or settings. Documents in [the bin](#bin) are left out; `?includeBin=true` adds them under `bin/<id>.json` and `bin/<id>.dsl`, marked with `deletedAt` in the manifest, never mixed in with the live ones. A cookie-authenticated
**admin** only (`CookieAdminOnly`; an editor is `403`, an API token is `403` even when its owner is an admin, anonymous is
`401`), answered with `Content-Disposition: attachment; filename="draughtsman-export-<time>.zip"` and `Cache-Control: no-store`.
Each call is recorded as a `data.exported` security event with the counts.
