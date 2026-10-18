# Administrator's guide

For the person who looks after a Draughtsman instance: first run, people, forgotten passwords and email, API tokens, themes and
branding, AI settings, and keeping an eye on it. Install first ([`install.md`](install.md)) and put it behind TLS if anyone else
will use it ([`deployment.md`](deployment.md)). Backups and upgrades are in [`upgrade-backup.md`](upgrade-backup.md); when
something is wrong see [`troubleshooting.md`](troubleshooting.md).

What was run to write this: a real server on a scratch data folder, driven through its API and, for the page texts, a
real browser. Anything that could not be run is marked **unverified**.

## First run and the setup code

A fresh instance has no accounts. Every page sends you to **Create the first administrator**, which asks for an email, a
display name, a password (12 characters or more) and a **setup code**.

The setup code is printed in the server's log at start-up, and only while no account exists (a Windows service is the exception,
below: it keeps the code in a file that only Administrators can read):

```
warn: draughtsman[0]
      ==================================================================
      First-run setup code: GQ9JTK9KS4
      Enter this on the first-run page (or POST it as setupCode to /api/auth/first-run) to create the
      administrator account. Valid only until an administrator exists; restart to get a new code.
      ==================================================================
```

Where the log is depends on how you installed it (the first-run page names the right place for the host it detects):

| Install | Where the setup code is |
| --- | --- |
| Started in a terminal | The terminal window. |
| Docker | `docker logs <container name>`; with compose `docker compose logs draughtsman`. |
| Linux service | `journalctl -u draughtsman` (unverified: no systemd host was available). |
| macOS service | `<data folder>/logs/draughtsman.log`, by default `/Library/Application Support/Draughtsman/logs/draughtsman.log`. |
| Windows service | **Not in any log.** `draughtsman first-run-code --DRAUGHTSMAN_DATA "C:\ProgramData\Draughtsman"` from an elevated prompt, or the file `setup-code.txt` in the data folder, which only Administrators and SYSTEM can read. The Application event log (source `draughtsman`) says where the file is. Verified on Windows. |

Why not the event log on Windows: every local user can read it, and anyone who saw the code first could create the administrator.
`draughtsman first-run-code [--DRAUGHTSMAN_DATA <folder>]` prints the code and refuses, with a message, once an account exists or when
the account running it cannot read the file; the file is deleted the moment the first administrator exists, and a restart before then
replaces it with a fresh code. The optional file log (`logging.file.enabled`) shows `[redacted]` for the code on every platform.

The code lives only in memory (on Windows, also in that file). A restart before the first account exists issues a new code and the old one stops working.
Type it as printed, or paste it: leading and trailing spaces and letter case are ignored. Wrong guesses are throttled (five from one address, then `429` for
15 minutes); a right code is never counted, so a password that is too short does not use up your guesses. Once the first administrator exists, first
run is closed for good, for everyone: there is no setup code to find afterwards and nothing to reopen it. If that
administrator is lost, see [Lost administrator](#lost-administrator-password).

## People and roles

Open **Users** under the **Admin** menu in the top bar (on a narrow window the bar folds into one **Menu** button, with the same pages inside). There are two roles:

| | Administrator | Editor |
| --- | --- | --- |
| Create, open, edit, rename, export and import diagrams | yes | yes |
| Delete a diagram (it goes to the bin) | yes | yes, **including diagrams other people made** (see below) |
| Restore from the bin | any entry | entries they created or deleted |
| Delete from the bin for good | any entry, browser session only | only diagrams they created, browser session only |
| Name a version, compare, restore a version, duplicate | yes | yes |
| Use the AI panel and the MCP tools (with a token) | yes | yes |
| Create their own API tokens, change their own password | yes | yes |
| See About | yes | yes |
| Users: add, change role, reset password, create a reset link, deactivate | yes | no |
| Ask for a reset link by email on the sign-in page (when email is set up), and use a link they were sent | yes | yes |
| Branding, Themes, AI settings, Email, Status, Security log | yes | no |
| Whole-instance export, per-document audit trail | yes | no |

Everything in the lower half is refused to an Editor with `403`; this was checked against a running server (users, the
security log, audit, export, branding, AI, status and theme administration all answered `403` for an Editor, while
`POST /api/tokens` answered `201`).

**The trust model is "one organisation".** There is no Viewer role and no per-document permission. Every signed-in person
can read every diagram in the instance, and an Editor can overwrite or delete any of them. This was checked: an Editor
deleted a diagram an administrator had made (`204`, then gone from the list). A delete is **recoverable**: the diagram goes to
the bin with its versions, and the entry remembers who deleted it (next section). Give the Editor role to people you would
trust with colleagues' work, and take backups ([`upgrade-backup.md`](upgrade-backup.md)).

On the **Users** page an administrator can:

- **Add user**: email, name, role, and an initial password of at least 12 characters (the rule is printed under the field,
  and a shorter one is answered on the page with `Password must be at least 12 characters.`). Tell the person the password through a channel of your choice and ask them to change it at
  **Account**.
- **Change role** with the drop-down. You cannot remove your own Admin role (`You cannot remove your own Admin role.`) and
  you cannot deactivate yourself, so the last active administrator cannot be removed from the page.
- **Create reset link**: for a person who has forgotten their password (next section). You never see or choose the new one.
- **Reset password**: you type the new one (same rule, shown beside the field). This signs the person out everywhere and
  **revokes their API tokens**. Their open window goes to the sign-in screen with the line "You were signed out because your
  password was changed, or because you signed out in another window", and edits they had not saved are kept in their browser and
  offered back when they sign in and reopen the document. The line never says who made the change.
- **Deactivate**: the person is signed out at once (their sign-in screen says the account was deactivated) and cannot sign in or use a token. Accounts are never deleted, so
  documents and the security log keep a name.

Everyone can change their own password at **Account** (their name in the top bar): current password, new password twice.
It signs them out everywhere else and leaves their API tokens alone.

**Sign-in limits.** Only failed sign-ins count. Five failures for one account from one address, or twenty from one
address across accounts, within 15 minutes: further sign-ins from there are refused with `429 Too many failed attempts from
this account or network address; try again in 15 minutes.`, even with the right password. Verified: five wrong
passwords, then the correct password, still `429`. A restart clears the counters; so does waiting. Behind a reverse proxy
that is not listed in `server.knownProxies`, everyone shares the proxy's address and its budget of twenty, so other people's failed attempts can lock
you out of a correct sign-in for 15 minutes; the server logs a warning an hour naming the unlisted peer when it sees forwarding headers from one
([`deployment.md`](deployment.md#serverknownproxies)). If the page says `Signed in, but your browser did not keep the session`, the password was right:
browse over `https://` while `server.forceHttps` is on ([`troubleshooting.md`](troubleshooting.md#i-can-reach-the-server-but-not-use-it)).

### Forgotten passwords

A person who has forgotten their password can get back in two ways. Both end on the same page, where **they** choose the new
password (at least 12 characters, the rule is shown above the field); you never see it.

**1. A reset link from you (needs no email).** On **Users**, choose **Create reset link** beside the person. A dialog says what
it does, then shows the link once, with a **Copy link** button, when it expires, and this warning: *anyone who has this link can
set the person's password until it expires*. Send it by a channel you trust (a chat message, a phone call, in person). It
works **once**, and expires after 24 hours (`auth.passwordReset.adminLinkHours`, 1 to 168). Closing the dialog is final: the
server keeps only a hash of the link and cannot show it again, so create another if you lose it. Creating a link ends any earlier
link for that person. Only an administrator signed in on a browser can create one: not an Editor, and not an API token, even an
administrator's. A deactivated account cannot have one (reactivate it first).

**2. "Forgot password?" on the sign-in page (needs email).** When outgoing email is set up
([Admin, Email](#email), or [`deployment.md`](deployment.md#outgoing-email)) and the public address names this instance, the sign-in card offers
**Forgot password?**. The person types their address and is told *"If an account with that address can be reset, we have emailed
it a link."* Whatever address they type, and whether or not an account has it, the page, the status and the time taken are the
same, so the form cannot be used to find out who has an account. If an account does exist and is active, the link is emailed; it
works once and expires after 60 minutes (`auth.passwordReset.emailedLinkMinutes`, 5 to 1440). When email is not set up the
sign-in card says *"Forgot your password? Ask your administrator for a reset link."* instead and shows no link to click. Set
`auth.passwordReset.selfService: false` to keep it off even with email working.

What using a link does, for either kind:

- The person lands on **Choose a new password**, which states before they type that saving **signs them out everywhere else**.
  A short password or a mismatch is said on the page and does not spend the link. Nothing signs them in afterwards: they go to the
  sign-in page and use the new password.
- **Every session of theirs is revoked** (any browser still signed in is signed out on its next request). **Their API tokens are
  left alone**, the same as when someone changes their own password: forgetting a password says nothing about a leaked token, and
  silently stopping every script and agent would teach people to avoid recovering. If you think a token is also exposed,
  revoke it under **Tokens** ("Everyone's tokens"), or use **Reset password** above, which revokes tokens too.
- Every other link for that person ends at the same moment, and so does any link when their password is changed by anyone else
  (you, the person at **Account**, or `reset-admin`) or the account is deactivated.
- A link that is unknown, used, expired, ended or tampered with gets one identical answer: *"This reset link is not valid. It may
  have expired or already been used. Ask for a new one."* The security log records the reason class for you.

Two things protect the person whose address a stranger types. Asking never changes anything on their account (it adds one link and
one email, and never ends a link they already hold), so nobody can lock them out or keep replacing the link in their inbox; and at
most three emails go to one address in 15 minutes, however many people ask. Asking is also limited to ten requests per network
address in 15 minutes, and three for the same email from the same address (`429` after that).

**Where the link goes.** The secret is in the part of the address after the `#` (`https://…/reset-password#token=…`). A browser never
sends that part to the server, so it is in no access log, no proxy log and no `Referer`, and an email scanner that fetches the link
cannot spend it. The page reads it, removes it from the address bar at once, and sends it to the server in the body of the request.
Treat a link like a password until it expires; the server logs and the security log record that one was created, by whom, for whom
and when it expires, never the link.

The security log (**Security**) records: *Password reset requested*, *Reset link created by an administrator*, *Reset email sent*
(or *could not be sent*, with the reason), *Reset link used*, *Reset link refused* (with the class: not recognised, already used,
expired, ended, account deactivated) and *Password changed with a reset link*.

### Email

**Admin, Email** is where you tell Draughtsman how to send email, so people can use **Forgot password?**. You do not need to edit a file
or restart anything: fill in the form, press **Save**, and it applies at once. (You can still put it all in `draughtsman.yaml`; see
[`deployment.md`](deployment.md#outgoing-email).)

1. **Start from** a provider (Microsoft 365, Google Workspace, Amazon SES, SendGrid, Postmark, Mailgun, or *My own mail server*).
   Picking one only fills in the server, the port (587) and the encryption (STARTTLS), plus the user name where the provider fixes it
   (SendGrid's is `apikey`), and shows the one thing that usually trips that provider up (a verified sender or domain, an app password,
   a setting your provider may have switched off). Nothing is saved by choosing one.
2. Fill in the rest: **User name**, **Password**, the **From address** (what people see), an optional **Reply-to**, and the **Public
   address**: the address people use to reach this instance, for example `https://draughtsman.example.com`. The links in emails are
   built on it and on nothing else.
3. **Save**. The page tells you if anything is wrong, in plain sentences naming the field, and saves nothing until it is right.
4. **Test saved settings** sends one message to your own address using what is saved. **Test these values** sends one using what is
   typed in the form *without saving it*, which is how you check a password before you commit to it. Either way you are told whether it
   worked and, if not, which kind of problem it was: *Could not connect to host:port*, *did not accept the user name or password*, *the
   secure connection (STARTTLS) failed*, *refused the recipient address*, *did not answer within 20 seconds*. You are never shown the
   mail server's own reply. Tests are limited to five in 15 minutes and always go to your own address.

**The password** is write-only. The page says **Password: set** or **not set** and never shows it. With one saved you choose **Keep it**
(the default, so editing another field never loses it), **Replace it** (type the new one), or **Remove it**. It is stored encrypted on
the server (under the key ring in `keys/`; if the page warns the ring is not protected at rest, see "Protecting the key ring" in the
README, or put the password in an environment variable or a password file instead).

**What draughtsman.yaml and the environment do.** A value set in `draughtsman.yaml` wins and its field is **read-only** on the page,
with the reason beside it (*Set in draughtsman.yaml*). A password supplied by the environment variable or the password file named in
the yaml wins the same way. Everything the file does not set is yours to change here. A key left at its default in the file (empty,
port 587, `starttls`) counts as not set, so it locks nothing. **Remove saved settings** forgets everything saved on this page,
including the password, and returns to the file alone.

Things to know:

- **Port 465 (implicit TLS) is not supported.** Many providers offer 587 with STARTTLS as well: use that. Encryption **None** is only
  accepted for a server on this machine, or one that needs no user name.
- **Your provider may refuse an ordinary password.** Microsoft 365 and Google Workspace usually need an app password or a dedicated
  mailbox (and, for Microsoft, SMTP AUTH switched on for it). Amazon SES, SendGrid, Postmark and Mailgun use credentials you make in
  their own console, and send only from addresses or domains you have verified there. This is the first thing to check when a test says
  the user name or password was not accepted.
- **This lets the server connect to any mail server you name.** That is what an outgoing-mail setting is, so only an administrator on a
  browser can change it (not an Editor, and not an API token, even an administrator's), and the mail it sends in a test goes only to you.
- Changes are recorded in the security log (*Email settings changed*, naming what changed and never a value; *Email settings removed*;
  *Test email sent* and *failed*).
- The saved settings are in `email/settings.json` in the data folder. A backup leaves that folder out unless you add `--include-secrets`.

### Lost administrator password

If **no** administrator can sign in, a reset link cannot help (an administrator has to create it, and "Forgot password?" needs
email), so the recovery is on the host and, on purpose, there is no way to recover an administrator account over the network
without it. It is unchanged by the features above. On the server itself, as a user who can write the data folder:

```sh
DRAUGHTSMAN_DATA=/var/lib/draughtsman draughtsman reset-admin admin@example.com --generate
```

Output on a real instance:

```
Password reset for admin@example.test. Their sessions and API tokens were revoked.
New password (shown once): VurCs6PtZWtB5XekJEFzk6ss
```

It sets a new password for that administrator, reactivates the account if it was deactivated, revokes its sessions **and API
tokens** (a token you were using stops working: mint a new one), and writes an `admin.recovered` event naming the operating
system user who ran it. It also ends any reset link that was outstanding for that account. Without `--generate` the password comes from `DRAUGHTSMAN_NEW_PASSWORD`, standard input or a hidden
prompt; it is never a command-line argument. An Editor's address is refused (`... is not an administrator. Re-run with
--promote to make them one.`, exit `1`) unless you add `--promote`; an unknown address is `No account with the email '...'.`
(exit `1`). In Docker use `docker exec <container> draughtsman reset-admin <email> --generate` (or `dotnet /app/draughtsman.dll reset-admin ...`).
On a systemd install run it with `sudo` so it can read the dynamic user's data folder (unverified). The trust model is that
anyone with shell access to the host could already do this by editing the database; the command makes it supported and
logged ([`api.md`](api.md#lost-admin-recovery)).

## API tokens

Anyone signed in can create tokens for themselves under **Tokens** (`/admin/tokens`; every user sees their own). An
administrator also sees **Everyone's tokens** on the same page: owner, name, created, last used, expires, access and status
for every account (never a secret), with a Revoke button on each live one. A revoke from there is recorded in the security log
as `token.revoked`, with you as the actor and the owner as the account. A token is for a script or an AI agent: it carries its owner's role, so an Editor's
token can do what an Editor can, and a token can never reach the Users, administration or security pages, mint further
tokens, or call the `/api/admin/*` routes, even when its owner is an administrator.

When you create one you choose:

- **Expires in**: 7 days, 30 days, 90 days (the default), 180 days or 1 year. The default and ceiling are the operator's:
  `auth.tokens.defaultLifetimeDays` (90) and `auth.tokens.maxLifetimeDays` (365). Asking for more is refused: `A token must
  last between 1 and 365 days.` An expired token is refused with the same `401` as a revoked or unknown one.
- **Read-only**: the token can list and read documents; any other method gets `403 Read-only token`. Over MCP a read-only
  token can list, read, render, diff, critique and fetch schemas, but not create or patch.

The secret (`dst_...`) is shown **once**, when you create it. The page lists name, created, last used, expires, access and
status. Revoke from the same page. Dates everywhere in the app read like `1 Oct 2026, 02:50` in your own time zone (the zone and seconds are on hover). Tokens made before expiry existed (none, on a new install) keep working with no expiry and
are flagged "No expiry" so their owners can replace them. A password reset (by an administrator or `reset-admin`) revokes the
account's tokens.

Use a token with MCP (the full agent setup is in [`SKILL.md`](../SKILL.md)):

```sh
claude mcp add --transport http draughtsman https://draughtsman.example.com/mcp --header "Authorization: Bearer dst_..."
```

Without a token `/mcp` answers `401` with `WWW-Authenticate: Bearer` and the text `Authentication required. Send an API
token as 'Authorization: Bearer <token>' ...`.

## The bin and version history

**Deleting is not permanent.** A deleted diagram goes to the **Bin** (the **Bin** switch next to **Documents** on the home page),
with its saved versions, and stays for `retention.binDays` days (30 by default; `0` or less keeps it until someone deletes it
for good). A daily pass then removes it permanently and records each removal in the security log (`document.purged`, by `retention
policy`). Restoring brings it back exactly as it was. Its id stays taken meanwhile.

| Who | Sees in the bin | Can restore | Can delete for good |
| --- | --- | --- | --- |
| Administrator | everything | everything | everything |
| Editor | entries they created, or deleted | those entries | only diagrams they created |
| API token | what its owner may see | what its owner may restore | never: a browser session is required |

An Editor who deletes a colleague's diagram can restore it, and so can the colleague; only an administrator or the diagram's creator
can delete it for good, and only from the browser. Checked against a running server: an
Editor's delete answered `204`; the entry showed in the Editor's bin with `canPurge: false`; the Editor's permanent delete
answered `403 Not yours to delete permanently`; the administrator saw it with `canPurge: true`, restored it, and later deleted it for good
(`204`). The security log records `document.deleted`, `document.restored` and `document.purged`.

**History.** In the editor's right-hand panel the **History** tab lists every stored version of the open diagram, newest first, with
time, who (agents are marked as such) and size. An agent's edit first stores an *automatic checkpoint* of the diagram as it was,
so an agent change can always be undone from here. A person can **name a version** (named versions are never pruned; at most
`retention.checkpointsPerDocument`, 50 by default, automatic checkpoints are kept per diagram). **Compare** shows what was
added, removed and changed since a version, in the panel and marked on the canvas; **Restore** puts the diagram back to a version
after saving the current state as a checkpoint, so a restore is itself restorable. The **Review** tab runs the rule-based
critique (structural checks, not an opinion) over the diagram on the canvas. The **Duplicate** action copies a diagram. The home
list has search, type filter and sort. All of these are the same functions the MCP tools use, exposed in the editor.

Backups include the bin and `GET /api/admin/export` leaves it out unless asked (`?includeBin=true`)
([`upgrade-backup.md`](upgrade-backup.md#the-bin-and-your-backups)).

## Themes (the organisation's look)

**Themes** (`/admin/themes`) holds the organisation's colours and fonts: the app's own (the chrome) and the diagrams'. The
shipped **Overpass default** cannot be edited; create a theme from it (or from another theme), edit it, and make one the
**default**, which every signed-in person gets and which the sign-in page wears too. You can also **import** a theme file
and export one to keep or move to another instance. Themes apply on the next page load with no restart. They are files in
the data folder (`themes/org/<id>.yaml`, and `themes/org/_settings.json` naming the default), so a backup carries them.
Verified: creating "Acme Corp" from the default and making it the default changed `GET /api/theme` to name it, with no
restart.

The pictures on the Documents page's cards (and the PDF export) are drawn in the **default** theme's diagram colours; change the default and the cards redraw on the next page load.

Colours are checked as hexadecimal values only, fonts as plain family names; the page warns about low contrast but never
blocks a save. The product name, logo and favicon are **not** part of a theme: they are Branding.

**From a website.** Under the theme list, **From a website** turns a site's colours into a *draft*: paste its CSS (or the page's HTML) and press **Read colours from this CSS**. You get the draft in the usual editor, unsaved, with the app and diagram previews and the contrast table, a list of **how each colour was chosen** and a list of every colour that was **adjusted for contrast**, and nothing is created until you press **Create theme from draft**. Both light and dark are filled: the one the site has is used, the other is derived and the list says so. Fonts and the corner radius stay at the Overpass defaults (fonts are never copied from a site), and nothing from the site is stored.

The same section has a **Web address** box only if the operator turned it on (`themes.fetchFromUrl.enabled: true` in `draughtsman.yaml`; it is **off by default**, because turning it on lets an administrator make this server fetch a page, and a self-hosted install otherwise sends nothing anywhere). When it is on, **Fetch and read colours** fetches that page and up to 8 stylesheets it names: https only, 2 MiB and 15 seconds in all, no cookies or credentials, redirects only within the same host, and never to a loopback, private, link-local or cloud-metadata address. To read an **internal** site's colours, list its host name in `themes.fetchFromUrl.allowedHosts` (exact names, no wildcards); that lifts the address rule for that host only. Each fetch is recorded in the **Security log** (`theme.url_fetch`: who, which host, what happened, never the page). `draughtsman config check` says whether it is on and which hosts are listed. Pasting CSS works the same whichever you choose, and is the way to do it on an air-gapped network. Unverified: fetching a real public site over https (the tests use a local site and a fake resolver, so no test makes a request off the machine).

Two older mechanisms still exist: `theme.chrome` in `draughtsman.yaml` names a chrome theme file (looked up in the data
folder's `themes/`, then the data folder, then the shipped themes) which is the fallback when no organisation theme is
the default, and `theme.diagram` is accepted but not read by this version (see [`draughtsman.example.yaml`](draughtsman.example.yaml)).
Prefer the Themes page.

## Branding

**Branding** (`/admin/branding`): the product name, a logo, an optional light-on-dark logo and a favicon. Logos are PNG
or SVG, the favicon PNG, SVG or ICO up to 256 by 256. They apply at once, with no restart; **Reset all branding** returns
to the theme's own values. Until you set a logo or a product name, Draughtsman shows its own mark and favicon; once you set
either, only yours is shown in the header and on the sign-in page (About keeps a separate block for the product itself). An uploaded SVG is checked against a strict allow-list (no script, event handlers, external
references or `foreignObject`) and served as an image under a restrictive policy. Branding changes are logged
(`Draughtsman.Server.Branding`), not written to the security log.

## AI settings

Open **AI** (`/admin/ai`) to choose how the AI panel in the editor works. It is off until a provider is set (the panel is
hidden, not broken). The page applies at once with no restart; `draughtsman.yaml`'s `ai` section stays the fallback and
**Reset to draughtsman.yaml** drops the overrides **and the stored key**.

| Provider | Needs | Notes |
| --- | --- | --- |
| Ollama (local) | Endpoint (default `http://localhost:11434`), model name as `ollama list` shows it | No key. Nothing leaves your network, **except** models whose name ends in `:cloud`, which Ollama itself routes to its cloud. |
| OpenAI or compatible | Model, key; endpoint if not OpenAI's own | Any server that speaks the OpenAI Chat Completions API (vLLM, LiteLLM, a corporate gateway, OpenRouter). A host other than `api.openai.com` has to be listed in `draughtsman.yaml` first ([below](#where-the-ai-endpoint-may-point)). |
| Anthropic | Model, key | Messages API. |
| Azure OpenAI | The resource's own endpoint (`https://<resource>.openai.azure.com`), the **deployment name** as the model, key, optional API version | A resource on a custom domain has to be listed in `draughtsman.yaml` first. |

**Test connection** sends one short prompt and says in plain English what happened. Verified messages: `Could not reach
127.0.0.1:1. Check the endpoint, and that the server is running and reachable from this machine.` and `No API key is set. Paste
one here, or set the environment variable DRAUGHTSMAN_AI_KEY.` The message is built from the outcome, never from the
provider's reply, so it cannot echo a key.

### Local models

Run Ollama on the same machine (or a GPU box on your network) and point the page at it. Use the largest model you can
run: the model has to write DSL the parser accepts, and the server asks it once more, quoting the offending line, when the
first reply does not parse. The output quality of any particular local model was not assessed for this guide. Local
inference is slow, so raise the timeout if needed (`ai.timeoutSeconds`, default 300) and see the proxy timeouts in
[`deployment.md`](deployment.md#what-a-proxy-must-do).

From **Docker**, `localhost` is the container itself, not your machine. Checked from inside the image on Docker Desktop:
`http://host.docker.internal:11434` reached the host's Ollama, `http://localhost:11434` did not. On Linux add
`--add-host=host.docker.internal:host-gateway` to `docker run` (unverified), and make Ollama listen on an address the container can
reach (`OLLAMA_HOST=0.0.0.0` puts it on every interface, so firewall it).

### The API key and the key ring

The key is **write-only**: once saved it cannot be shown again, and no response, log line or error message contains it. It
is stored encrypted in `<data>/ai/settings.json` with ASP.NET Core Data Protection, using a key ring in `<data>/keys/`. If the
ring were plain text beside that file, the encryption would protect nothing against anyone who can copy the folder (a
backup, a mounted volume), so the ring is protected at rest by something that is **not** in the data folder. The page tells
you which protection is in force and warns when there is none. `keyRing.protection` in `draughtsman.yaml` picks:

| Mode | Protects the ring with | When to choose it |
| --- | --- | --- |
| `auto` (default) | The first of: a certificate, a key-encryption key, the operating system store | Almost always. |
| `dpapi` | Windows DPAPI, tied to the service account | Windows servers. |
| `keychain` | The macOS login Keychain (a key-encryption key stored in it) | macOS. The server log says `Key ring protection: key-encryption key in the macOS Keychain (item "Draughtsman data key ring", account ...)`. If the Keychain cannot be used (no login keychain, as for a service), the server does not start: it waits 20 seconds, then stops with a message naming a key-encryption key or `none` as the two safe choices. |
| `key` | A key-encryption key from the environment variable `DRAUGHTSMAN_KEY_ENCRYPTION_KEY`, or a file named by `keyRing.keyFile` outside the data folder | Linux and Docker. Generate with `openssl rand -base64 32` and keep it where the data folder's backups cannot reach it. |
| `certificate` | A PKCS#12 `.pfx` with an RSA key (`keyRing.certificate.path`, password from `DRAUGHTSMAN_KEY_CERTIFICATE_PASSWORD`) | When the organisation already issues one. |
| `none` | Nothing | Deliberate opt-out only. |

On Linux or Docker with none of the first three configured, `auto` leaves the ring unprotected and says so at start-up (checked
in the Docker image: `Key ring protection: The key ring is not protected: anyone who can read this server's data folder can
decrypt the stored AI API key...`) and on the AI page. The environment variable `DRAUGHTSMAN_AI_KEY` (or whatever
`ai.apiKeyEnvironmentVariable` names) wins over the stored key, so a deployment secret cannot be replaced from the browser.

The stored AI key and the saved SMTP password are tied to this key ring and nothing else: **not to the folder the program is installed in**, so replacing
the program, unpacking a new version elsewhere or moving it does not touch them (builds before this tied them to that folder; the first start of this one reads such a secret and
re-protects it, keeping the earlier file under `<data>/backups/secrets-<time>/`; [`upgrade-backup.md`](upgrade-backup.md#where-to-put-the-new-version) says what to do when the old folder cannot be found,
which Admin > AI, Admin > Email and `draughtsman config check` report by name).

If the protection later goes missing (variable removed, Keychain item deleted, restored on another machine) the server still
starts, the page says the stored key has to be entered again, and entering it works. Restoring the original protection
makes the old key readable again. Nothing is discarded. A restored backup has no key ring unless made with
`--include-secrets` ([`upgrade-backup.md`](upgrade-backup.md#what-is-in-a-backup)), so re-enter the key after a restore.
The first start after turning protection on migrates an existing ring and leaves a copy of the old, plain ring under
`<data>/backups/key-ring-<time>/`; delete it once Test connection works, because it still holds the plain key.

### Where the AI endpoint may point

The AI page can no longer send the provider key to just any address. A stolen administrator session, a hijacked browser or a rogue
administrator could otherwise aim the endpoint at a host they control (and read the key from the request) or use Test connection to
probe internal addresses. What is allowed ([`ai-endpoint-policy.md`](ai-endpoint-policy.md) has the detail and the residual risks):

- **OpenAI, Anthropic and Azure OpenAI** may use only their own host (`api.openai.com`, `api.anthropic.com`, `*.openai.azure.com`).
  A corporate gateway, an OpenAI-compatible server or an Azure resource on its own domain is added once, by whoever can edit
  `draughtsman.yaml`: list the host under `ai.allowedEndpointHosts` (a name, an IP address, or `*.example.com` for the subdomains),
  and restart. The admin page cannot add to that list, which is the point.
- **Ollama** may use this machine and private addresses (the normal case); a public host has to be listed too.
- **Link-local and cloud-metadata addresses** (`169.254.169.254` and the rest) are refused for every provider, listed or not, and so
  is a host name that resolves to one.
- **A key is never sent over plain `http`** except to this machine, and **redirects are not followed**.
- **Changing the host (or port) forgets the stored key**, so enter it again. The environment key
  (`DRAUGHTSMAN_AI_KEY` or what `ai.apiKeyEnvironmentVariable` names) goes only to the host in `draughtsman.yaml`, or the provider's
  own when the file names none; for any other host the page says it is held back.
- Every change of host, and every refused endpoint, is on the [Security log](#security-log) (`AI endpoint host changed`, `AI endpoint refused`).

After an upgrade from a build without this, an endpoint in `draughtsman.yaml` (or saved on this page earlier) that the rules refuse
leaves AI off, and the page and the log say why: list the host and restart. A refused endpoint on the page is a message beside the
field and nothing is saved.

What is left: Test connection against Ollama can still tell reachable from not for a private address and port, a proxy
(`HTTPS_PROXY`) resolves names itself so a DNS change between the server's check and the proxy's lookup is not closed, and a host
you list is trusted with the key. All three are in [`ai-endpoint-policy.md`](ai-endpoint-policy.md#risks-that-remain). Treat administrator
accounts with the same care as the server.

### What AI sends

See [`data-flow.md`](data-flow.md) for the full list per provider. In short: **From text** sends the prompt and, when
updating an open diagram, the diagram's whole current content as DSL; **To document** sends only the outline (ids, kinds,
labels, containers, connections). With no provider set nothing is sent.

## Security log

**Security** (`/admin/security`) lists, newest first: sign-ins and failures, throttle trips, sign-outs, first run, accounts
created, role changes, deactivations and reactivations, password resets and changes, API tokens created and revoked, host-shell
recoveries, whole-instance exports, and diagrams deleted, restored and purged from the bin. Each row has the time, who did it, which account it concerned, the address it came
from and a short detail; filter by event type (the sign-in, account and token events appear under a plain-English label; the
diagram events appear under their own ids, `document.deleted`, `document.restored` and `document.purged`). It never holds a password, token or hash. The address is the client's only
if `server.knownProxies` is set correctly behind a proxy ([`deployment.md`](deployment.md)). Events are kept indefinitely;
nothing in the product edits or deletes them. The same events go to the log under the `Draughtsman.Server.Security`
category. A document's own trail (who created, replaced or deleted it) is `GET /api/admin/audit?documentId=...`
([`api.md`](api.md#security-events)); there is no page for it yet.

The log contains e-mail addresses and IP addresses, which is personal data in many jurisdictions; the data-protection
statement is in [`data-flow.md`](data-flow.md).

## Status

**Status** (`/admin/status`) is a read-only list of the parts whose failure the product otherwise survives quietly:

| Line | Meaning |
| --- | --- |
| Layout service | The Node sidecar that does layout, Tidy, SQL import, AI generation and exported connector routes. When it is down the page says so and gives the reason. Editing and saving keep working. |
| PDF export (server side; the editor's File > Export PDF… prints from the browser either way) | Off, or on and working, from the same launch probe `GET /api/health` uses, so an enabled component whose browser or driver is not usable shows as unavailable with the reason: **the driver** (the `.playwright` folder beside the executable is missing, incomplete or will not run: unpack the archive again; installing a browser does not help) or **the browser** (no Chromium or Chrome could be launched: install the pinned one or set `export.chromium.path`). [`pdf-export.md`](pdf-export.md). |
| AI provider | Off, or the provider in use. |
| Storage | The provider and the database file. |
| Data folder | The folder, and where the log is for the way this server was started. |
| Connection | The direct address this request came from, whether the server trusts it as a proxy, the scheme it believes, the address the sign-in lock-out counts you as, and how many of the [Connection check](#connection-check)'s server-side findings passed, warned or failed, with a link to it. |
| Version | Version and build date. |

`GET /api/health` is the one anonymous route: `{"status":"ok"}`, or `degraded` when the sidecar is down. Signed-in people
also see capabilities; administrators see version, storage and the PDF hint. Use it for monitoring: alert on anything other
than `ok`.

## Connection check

**Connection check** (`/admin/connection`, next to Status in the Admin menu) is where an install behind nginx, Caddy, Cloudflare or Docker finds out why sign-in does not stick, why a
save fails, or why everyone shares one lock-out, without reading a log. It runs in your browser against your own server, makes no call outside it,
shows nothing secret, and changes nothing. Open it when something about the install is wrong, and after you change a proxy or `server.knownProxies`: **Run again** repeats it.

It reports each finding as **Pass**, **Warning** or **Fail** (the word is always printed; colour is only a second cue), says what it saw, and for a warning or failure gives the exact change for nginx, Caddy,
Cloudflare and `draughtsman.yaml`, copy-pasteable, under **What to change**; **What was seen** shows the raw header values and addresses behind a verdict.

| Group | Finding | What it tells you |
| --- | --- | --- |
| What this browser sees | Session cookie round trip | A test cookie is set with the session cookie's attributes and the browser must send it back. A fail is the "Signing in…" that never finishes, or a session that does not stick. |
| | ETag survives the proxy | A proxy or CDN that weakens the `ETag` (`W/"..."`) or strips it. A warning, not a failure: this version still saves (it accepts a weakened tag, and the editor also sends the version in `X-Document-Version`), so nothing needs fixing for that, but a script quoting a stripped `ETag` has nothing to quote, and builds before the weak-tag fix refused a weakened one with `400 Invalid If-Match header` on save and rename (issue 63): upgrade if you are on one. |
| | Responses are not re-compressed in transit | A `content-encoding` the server did not add: something in between compressed the response (usually why the ETag was weakened). A warning, never a failure: compression in transit is harmless, and nothing needs fixing for saves. |
| | Server clock and your clock | Over two minutes apart is a warning; a server clock 30 days or more behind the browser's is a fail, because the session cookie is then expired the moment it is set. |
| What the server sees | Reverse proxy and client address | The direct peer, the raw `X-Forwarded-Proto`, `X-Forwarded-For`, `X-Forwarded-Host` and `CF-Connecting-IP` it sent, whether the peer is in `server.knownProxies`, the scheme the server believes, and the address the sign-in throttle uses for you. A proxy that is not listed is a warning with its address filled into the line to add. |
| | Secure cookies and sign-in | `server.forceHttps` against the address in your browser: with `forceHttps` on over plain `http://` the browser drops the Secure cookie (a fail). |
| | Host header and allowed hosts | The `Host` the proxy sends against `server.allowedHosts`. |
| | Data folder and database; Free disk space | Whether the server's account can write the folder and the database file (a database owned by another account is a fail), and how much room is left. |
| | This server | Version, build date, platform, whether it runs in a container, the account. |

The command-line equivalents, for when the page cannot be reached or the server is stopped, are `draughtsman doctor` and `draughtsman support-bundle`; the whole route from symptom to fix is in
[`troubleshooting.md`](troubleshooting.md#troubleshooting-an-install). The page and its routes are [documented](api.md#connection-check) for anyone scripting them: administrator cookie session only, never a token.

## About and Help

Every signed-in person has **About** (version, build date, the support contact, a documentation link and
the third-party licence notices) and a Help menu. Two settings in `draughtsman.yaml` change what they point at:
`about.supportContact` (an address or any text) and `about.docsUrl` (an `http(s)` address, opened in a new tab; `""` hides the
entry). Left unset, Help > Documentation opens **this server's own copy of the guides** at `/docs/` when the release archive
carries them (it does), so an air-gapped site has documentation without any setting; it needs a signed-in session like every
other page and shows the Markdown as plain text. An install with no `docs/` folder (the Docker image) falls back to the vendor's
site.

## Logs

The server writes to the console. Under systemd that is the journal, under Docker `docker logs`, under the macOS service
`<data>/logs/draughtsman.log`, under the Windows service the Application event log (source `draughtsman`; the first-run setup code is never written there). Set `logging.file.enabled: true` to also
write daily files under `<data>/logs/` (14 days kept, 10 MiB a file; both adjustable). By default the framework's own
chatter is at `Warning`, so a busy day does not write thousands of lines; `logging.frameworkLevel: Information` shows every
request and SQL command when you need them. A save refused over its version tag (`If-Match`), and proxy headers arriving from an address that is not in `server.knownProxies`, each write one
Warning that says why ([`troubleshooting.md`](troubleshooting.md#what-the-server-logs-about-a-refused-save)); `draughtsman support-bundle` collects the log file (with `logging.file.enabled: true`) and everything else a support request needs, redacted.

## Changing settings

`draughtsman.yaml` is read at start-up: edit, then restart. The pages above (themes, branding, AI settings) apply
at once. [`draughtsman.example.yaml`](draughtsman.example.yaml) lists every setting with its default.

Things the server does by itself, once a day, so the data folder does not grow without bound: it deletes automatic
checkpoints beyond the newest 50 per document, sessions that expired more than 7 days ago, diagrams that have been in the bin
for more than 30 days (`retention.binDays`), and keeps the newest 3 `data.db.pre-migration-*` copies. A live diagram or a named
version a person saved is never touched. Uploaded images are capped
at 10 MiB each and 5 GiB in total (`assets.maxSizeBytes`, `assets.quotaBytes`).

## What this version does not have

So that nothing here is assumed: no single sign-on (OIDC), no SMS codes or security questions for a forgotten password, no Viewer role or per-document
permission, and no per-keystroke history (restore points are server checkpoints and named versions). Rate limits and request-size limits
exist (`limits` in `draughtsman.yaml`).
