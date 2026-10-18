# Security policy

Draughtsman is open-source, self-hosted software published by Overpass Limited under the GNU AGPL-3.0. This page says how to
report a vulnerability in it, what we treat as in scope, and what to expect from us. Timescales below are targets, not
promises or service levels.

## Reporting a vulnerability

Email **draughtsman@overpass.co.uk** with the subject line `Security: Draughtsman`. There is no separate security mailbox and
no published PGP key yet. If you need an encrypted channel, say so in a first message that contains no details and we will
arrange one. If GitHub's private vulnerability reporting is enabled on the repository (the **Security** tab shows a
**Report a vulnerability** button when it is), you may use that instead; email always works.

**Please do not open a public issue, pull request or discussion, or post details elsewhere, before we have had a chance to
respond.** The source is public, so a fix that is pushed before an advisory is out tells everyone where the problem is: we
will agree the timing with you.

Useful in a report:

- the version (`draughtsman --version`, or **About** in the app) and how it is run (release archive, Docker image, or built
  from source; SQLite or Postgres; behind a reverse proxy or not);
- what you did, step by step, and what happened instead of what should have;
- the impact as you see it: whose data, what an attacker needs first (an account, which role, network position);
- a proof of concept if you have one, and whether you have shared the details with anyone.

A report about a particular organisation's own deployment (their proxy, their accounts, their configuration) is best sent
to that organisation, not to us. The support address an instance shows may be one its operator has set.

## Supported versions

Only the latest release is supported. 0.1.0 is the first version and has not been released; until it is, fixes go to `main`.
Once there is more than one release, a fix will be made in the latest one and we will not backport it. If you run from
`main`, you are running unreleased code: report what you find against it all the same.

## What happens next (targets)

| Step | Target |
| --- | --- |
| We acknowledge your report | within 3 working days |
| We tell you whether we can reproduce it and how serious we think it is | within 10 working days |
| A fix or a mitigation for a confirmed high or critical issue | within 30 days of confirming it; lower severity in a normal release |
| Public advisory | with the fixed release |

We ask you to keep the details private until the fix is out, and for no more than 90 days from your report. If we need
longer we will say why. If we cannot reproduce a report or do not think it is a vulnerability we will explain, and you are
free to disagree. Nothing here is a bug bounty: we do not pay for reports.

## In scope

- The server (`src/`): the REST API, authentication, sessions, API tokens, the admin endpoints, storage, backup and restore,
  the key ring and stored AI key, the PDF export.
- The web app (`web/`) served by the server.
- The layout sidecar (`sidecar/`).
- The MCP surface: `/mcp` over HTTP and `draughtsman mcp --stdio`.
- The Docker image built from the `Dockerfile`, and the release archives.

Typical findings we want: authentication or authorisation bypass; a non-administrator reaching an administrator function; a request
acting as another user; cross-site scripting or request forgery; injection; path traversal in uploads, backup or restore; secret
disclosure (the AI key, tokens, password hashes) in a response, log or file; a request that makes the server contact a host
we did not expect; a way to run code on the server; a denial of service by an unauthenticated caller or an ordinary
editor.

## Out of scope

- Exposing the plain-HTTP listener to a network without TLS, running with `server.host` open and no proxy, or any other
  deployment choice that ignores [`docs/deployment.md`](docs/deployment.md) and the install guide.
- Findings that need a shell on the server, or write access to its data folder. Shell access is the documented recovery
  trust boundary ([Lost-admin recovery](docs/api.md#lost-admin-recovery)): whoever has it can already read every document and
  hash and edit the database.
- Denial of service by an authenticated administrator, and resource use inside the configured limits.
- Social engineering of Overpass staff or of an operator's users, and physical attacks.
- Third-party services, including whichever AI provider an administrator configures, an operator's reverse proxy, identity
  or backup systems.
- Vulnerabilities in a dependency with no path to exploitation in Draughtsman. Please report those upstream; a note to us
  is welcome. The dependency list is in `THIRD-PARTY-NOTICES.txt`.
- Output of an automated scanner with no demonstrated impact, and missing headers or cookie flags that the code already sets.

Test only against an instance you own or are authorised to test. Do not read, change or keep other people's data beyond
what is needed to show the problem, and stop when you have shown it. If you follow this page in good faith, we will not
pursue or support legal action against you for the research. That is a statement of intent by Overpass Limited, not a
guarantee about anyone else (an operator, a host or an authority), and it does not cover testing a system you do not
have permission to test.

## The security model, briefly

Detail is in [`docs/api.md`](docs/api.md#auth) (accounts, sessions, tokens, CSRF, throttling) and
[`docs/deployment.md`](docs/deployment.md) (running it safely). What it does not do is in
[`docs/data-flow.md`](docs/data-flow.md).

- **Network.** The server listens on `127.0.0.1:5180` by default and speaks plain HTTP; TLS is the operator's reverse proxy.
  `server.knownProxies`, `server.forceHttps` and `server.allowedHosts` are the settings that go with that.
- **Sign-in on every route.** A global fallback policy requires an authenticated user for every endpoint; only health, the
  theme, sign-in, first-run and the two password-reset calls (ask for a link, spend a link), and the web app's own static files are anonymous. A test requests every registered route
  anonymously and expects a refusal.
- **Accounts.** Local accounts, Argon2id password hashes, a 12-character minimum. The first administrator is created with a
  one-time setup code printed to the server's console. A forgotten password is recovered with a single-use reset link:
  an administrator creates one (no email needed) or the person asks for one by email when email is configured. The link's secret
  is 32 random bytes, stored only as a hash, valid for 60 minutes (emailed) or 24 hours (administrator), usable once, carried in
  the URL fragment so it is in no log or `Referer`, and using it signs the person out everywhere. Asking for one never changes the
  account, answers every address identically and is rate limited. A lost *administrator* account with nobody to create a link is
  recovered with `draughtsman reset-admin` on the host.
- **Sessions and tokens.** The browser gets an HttpOnly, `SameSite=Lax` session cookie (`Secure` over HTTPS), held server-side
  as a hash and checked on every request, so sign-out and deactivation take effect at once. Cookie requests that change data
  need an anti-forgery token. API tokens are stored as hashes, can be scoped and given an expiry, and cannot manage accounts,
  mint further tokens or change AI settings; those need a browser session.
- **Roles.** Administrator and Editor, and **one organisation of trust**: there is no Viewer role and no per-document permission, so
  every signed-in user can read every document and an Editor can change or delete any of them (a delete goes to a bin and is
  recoverable). That is the design, not a vulnerability. Failed sign-ins are throttled per account-and-address and per address.
- **Limits.** Request body sizes, per-user rate limits, a Host header allow-list, and security headers including a
  Content-Security-Policy with `frame-ancestors 'none'` on every response.
- **Data at rest.** The database is not encrypted by Draughtsman; the data folder is owner-only on Unix. The AI API key is
  stored encrypted under a key ring that is itself protected by DPAPI, the macOS Keychain, a key you supply or a certificate.
  On Linux and Docker with none of those configured the ring is plain text.
- **Outbound.** None of its own unless an AI provider or a remote Postgres is configured. See `docs/data-flow.md`.
- **Child processes.** The layout sidecar and optional Chromium PDF renderer run with an allow-listed environment (no AI key);
  the PDF page runs with JavaScript off and no network.

### Known limitations

We list these so you do not have to find them:

- **The AI endpoint is restricted by provider, not sealed.** A keyed provider (OpenAI, Anthropic, Azure OpenAI) may reach only its
  published host unless the operator lists another in `draughtsman.yaml`; Ollama may reach this machine and private addresses;
  link-local and cloud-metadata addresses are refused for every provider; a key is never sent over plain `http` except to this
  machine; redirects are not followed; a change of host forgets the stored key; and each change is on the security log
  ([`docs/ai-endpoint-policy.md`](docs/ai-endpoint-policy.md)). What remains: Test connection against Ollama can still probe
  private addresses and ports, a proxy (`HTTPS_PROXY`) resolves names itself so DNS rebinding between the server's check and the
  proxy's lookup is not closed, and a host the operator lists is trusted with the key. Leave AI off if that matters.
- **`'unsafe-eval'` in the Content-Security-Policy.** The web app compiles the document schema with Ajv in the browser. There
  is no `'unsafe-inline'` script and no external script host.
- **Security events are kept indefinitely** and include email addresses and source IP addresses.
- **Release archives are not yet code-signed or notarised.** Check them against `SHA256SUMS`.
- **TLS, backups of the key-encryption key, and network egress controls are the operator's.**

## Credit

If your report leads to a fix we will name you in the advisory, using the name or handle you give us, unless you ask us not
to. We offer no payment and run no bug bounty, and we make no claim of certifications or audits.
