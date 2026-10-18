# Password recovery

## Screen

Four surfaces for a person who has forgotten their password, and for the administrator who helps them: the **"Forgot password?" step on the sign-in card**, the **set-a-new-password page** that both kinds of link open, the administrator's **"Create reset link" dialog** on the Users page, and the **Email page** under Admin that says how outgoing email is set and sends a test message.

Key pattern questions: where does "Forgot password?" sit so it is found without crowding the sign-in button; what does the page say after a request when it must say the same thing whether or not the address has an account; how is the set-password page laid out so the rule is visible before the person types (not only after they fail) and the consequence (signed out everywhere else) is stated before they commit; how does a dialog hand an administrator a secret that exists only once, with its expiry and a warning that anyone holding it can set the password; how does an Email page report "it did not work" precisely without becoming a configuration editor.

Constraints from the build spec that shape all four: the reset secret travels in the URL **fragment** (never the path or query, so it is in no log and no Referer); the sign-in page shows "Forgot password?" only when emailed links are set up and otherwise says to ask an administrator; an Editor sees no admin control; every error for a bad link is one identical sentence.

## Mobbin queries

- `search_screens "forgot password screen with email field and a send reset link button"` (web)
- `search_screens "set a new password page with two password fields and password rule hint"` (web)
- `search_screens "modal showing a generated invite link with a copy link button and an expiry note"` (web)

## References

1. Lucidchart — "Forgot password?" under the sign-in form, then an email-only page — (not on Mobbin; from the product, recalled rather than re-captured) — the link lives beside the password field, not in a menu.
2. Figma / FigJam — "Reset password" email page, then a "check your email" confirmation — (not on Mobbin for the auth pages; from the product) — a request page that confirms without saying whether the address exists.
3. Miro — "Forgot password" from the sign-in card, emailed link, set-password page — (not on Mobbin; from the product) — the same two-step shape.
4. Whimsical — emailed link only, no administrator path — (not on Mobbin; from the product) — what Draughtsman adds is the administrator-issued link for installs with no email.
5. Basecamp — "Forgot your password?" — https://mobbin.com/screens/56115193-6dfd-44af-9a0d-1c66d08d95b7 — one card, one field, one button, a "Never mind, go back" link and a spam-folder hint.
6. Basecamp — "Reset your password" — https://mobbin.com/screens/bfb3cdb0-c83a-47e3-bbc7-025eeb5edcbc — "12+ characters" hint under the first field, a confirm field, "Never mind, go to the login screen".
7. Clerk — "Set new password" — https://mobbin.com/screens/9dea0e0d-7be2-424d-aada-d912274e3c14 — inline error under the field, and a "Sign out of all other devices" statement on the page (Draughtsman does it always, so states it instead of offering a choice).
8. Squarespace — "Reset Password" — https://mobbin.com/screens/1e963e28-8d06-4fa4-a917-0e9f6e90b24f — says which account the new password is for.
9. Calendly — share dialog with the link in a field and a one-click "Copied" button — https://mobbin.com/screens/daec72aa-4bb6-47e5-9934-4380e60fe0a9 — the copy affordance and its confirmation.
10. Linear — signs in by emailed code and has no password to reset — (not on Mobbin for this; from the product) — the dense-SaaS counter-example: no recovery page because no password; rejected for Draughtsman, whose accounts are passwords on a self-hosted instance (magic-link sign-in is out of scope).

## What the references agree on

- The request page is a single card with one email field, one button and a way back; nothing else competes with it.
- The page after a request does not say whether the address exists; it says a link has been sent "if" an account matches.
- The set-password page has two fields (new and confirm), shows the minimum length as a hint under the first, and ends in one button.
- A sentence about other sessions is a feature of the better ones (Clerk), not an afterthought.
- A copy button turns into a confirmation ("Copied") for a moment; the link sits in a read-only field that selects on focus.

## What Draughtsman does

### Sign-in card

- Below the Sign in button, a quiet text button **Forgot password?**, only when `GET /api/auth/status` says `passwordReset.selfService`. It swaps the card to the forgot step in place (no route change): title **Reset your password**, one line ("Enter the email address you sign in with. If an account matches, we will email you a link."), the email field (focused, pre-filled from what was typed in the sign-in field), **Email me a link**, and **Back to sign in**.
- After a request the card shows the server's own sentence, identical for every address ("If an account with that address can be reset, we have emailed it a link. The email can take a few minutes to arrive."), with a spam-folder hint and **Back to sign in**. A 429 shows the throttle sentence and keeps the form.
- When self-service is not available the sign-in card shows, in the same place, plain text: **Forgot your password? Ask your administrator for a reset link.** (Not a link: nothing to click.)

### Set-new-password page (`/reset-password`)

- Outside the app shell, like sign-in and first-run: the same centred auth card with the instance's logo and product name.
- Reads the secret from the URL fragment (`#token=…`) on load, **removes it from the address bar and history at once** (`history.replaceState`), and keeps it in memory only. No fragment: the card says "This page opens from a reset link. Open the link you were sent, or ask for a new one." with a link to sign in.
- Title **Choose a new password**. Fields: **New password** and **Confirm new password** (both `autocomplete="new-password"`), with "At least 12 characters." under the first, always visible, wired with `aria-describedby`. Under the fields, always visible and before the button: "Saving signs you out everywhere else. Your API tokens keep working."
- Submit disabled until both fields are filled; a mismatch or a short password is said inline, in the server's words, and **does not spend the link**.
- On success the card becomes **Password changed**: "Every other session was signed out. Sign in with your new password." and a **Go to sign in** button (focused). Nothing signs the person in automatically.
- On a refused link (unknown, used, expired or tampered with, all identical): "This reset link is not valid. It may have expired or already been used. Ask for a new one." with **Go to sign in** and, when self-service is on, a hint that the sign-in page can email a new one.
- Reachable when signed in as someone else (an administrator helping at a desk); the page does not assume the viewer is signed out and does not use the shell.

### Users page: Create reset link

- Each active person's row (not the administrator's own) gets **Create reset link** beside **Reset password**. An Editor never reaches this page (the page is administrator-only and shows "Not allowed" otherwise); there is no control to hide for them.
- Click opens a modal dialog (`role="dialog"`, `aria-modal`, focus moved in and returned to the button on close, Escape closes). First state: "Create a reset link for {name}?" with the facts: it lets them choose a new password without you seeing it; it works once; it expires in {n} hours; creating it ends any earlier link for them; using it signs them out everywhere. Buttons **Create link** and **Cancel**.
- Second state, after creating: the link in a read-only field (selects on focus), **Copy link** (becomes **Copied** for two seconds; announced politely), the expiry as a date and time through the app's one date format, and a warning band: **Anyone who has this link can set {name}'s password until it expires. Send it only to {name}, by a channel you trust. It is shown once and cannot be shown again.** Closing the dialog discards it from the page; the server kept only a hash.
- The link is the server's `link` when `server.publicUrl` is set, otherwise built from this page's own origin.

### Email page (`/admin/email`, in the Admin menu)

*Amendment 2026-10-01 (email-settings-ui lane): the page is now an editor, not only a report. The original read-only text is kept below as history; this section is what is built.*

The page has four stacked parts, one column, labels above fields (the same form shape as the AI settings page):

1. **Status line** (`role="status"`): "Email is working: reset emails go through smtp.example.com:587." or "Email is off." plus the server's list of problems (`role="alert"`) when something is missing. **Forgot password?** is named here: "On" the moment email can send and a public address is known.
2. **Start from** (a `<select>` above the form): *Choose a provider…*, Microsoft 365, Google Workspace, Amazon SES, SendGrid, Postmark, Mailgun, *My own mail server*. Choosing one **fills the form fields only** (host, port 587, STARTTLS, and a user name where the provider fixes one: SendGrid `apikey`) and shows a one-line gotcha under the select (verified sender or domain, app password, why basic sign-in may be refused). It stores nothing; a field locked by `draughtsman.yaml` is skipped. Amazon SES also needs the region in the host, so it fills `email-smtp.<region>.amazonaws.com` with the cursor on the region.
3. **The form**: Mail server (host), Port, Encryption (STARTTLS, or None), User name, Password, From address, Reply-to (optional), and **Public address** (the link base, "https://draughtsman.example.com"). The password field is write-only and never pre-filled: beside it a status reads **Password: set** or **Password: not set**, and with one set a segmented choice **Keep / Replace / Clear** (Keep is the default; typing in the field switches to Replace; Clear removes it on the next Save). A hint under Encryption: "STARTTLS is required unless the server is on this machine or needs no user name. Port 465 (implicit TLS) is not supported: use the provider's 587 option."
   - **Locked fields.** A field whose value `draughtsman.yaml` (or, for the password, an environment variable or password file) already sets is **read-only** (`aria-readonly`, not `disabled`, so it stays in the tab order and is announced) with the reason under it in the hint style: "Set in draughtsman.yaml. Edit that file to change it." or "Read from the environment variable DRAUGHTSMAN_SMTP_PASSWORD." The rule, stated once at the top of the form: *Settings in draughtsman.yaml or the environment win; everything else is yours to change here.* A yaml key left at its shipped default (port 587, STARTTLS, empty strings) counts as not set.
   - Buttons: **Save** (validates on the server, stores, applies at once; a success status "Saved. Email settings apply now, no restart."), **Send a test email** (see 4), **Remove saved settings** (confirm step, returns to `draughtsman.yaml` alone and forgets the stored password). Save never sends mail. Field errors come from the server as plain sentences naming the field and never echo a secret.
4. **Test**: two buttons. **Test saved settings** sends one message to the administrator's own address using what is stored right now. **Test these values** sends the same message using the form as typed, *without storing anything*; it is the way to find out a password is right before saving it. A typed password is used for that call only; with the password on Keep it uses the stored one. Both share a 5-per-5-minutes limit and the result sits under the buttons as a status (success) or an alert (a short class of error: "Could not connect to host:port", "The mail server did not accept the user name or password", "STARTTLS was refused", "The server refused the sender", "Timed out"). The server's own reply text is never shown.

States: loading ("Loading email settings…"), load error with Retry, saved, unsaved changes (Save enabled only when the form differs from what was loaded), busy (all buttons disabled), file provider set in yaml (everything but Public address and From locked, with the reason).

Permissions: Administrator on a browser session only. An Editor gets the "not allowed" page; an API token gets 403 on every call. A change records a security event `email.settings_changed` (who, and the *names* of what changed: "host, port, password replaced"; never a value of any secret).

Honest limits written on the page and in the docs: implicit TLS (port 465) is unsupported by the framework mail client; the server connects to whatever host and port an administrator enters, which is what an outgoing-mail setting is, so only administrators can set it and the reply text of that server is never shown back.

Mobbin: the coverage gap noted in `README.md` applies; the form shape follows the AI settings page and the common "SMTP settings" screens in self-hosted admin tools (Ghost, Discourse, Mattermost, Nextcloud: host, port, security, user, password, from, test button), with the provider presets after Ghost's and Discourse's setup guides rather than a bespoke wizard.

*Original (2026-10-01, read-only) behaviour, superseded:* a read-only summary, a Send-test button, and a note to edit `draughtsman.yaml` and restart.

## Keyboard and accessibility

- All four are plain forms and one dialog: Tab order follows reading order; Enter submits; Escape closes the dialog and goes back from the forgot step.
- Focus: the email field on the forgot step, the first field on the set-password page, the dialog's primary button, then the **Go to sign in** button on success. Errors are `role="alert"`, success statuses `role="status"`; the hint under each password field is tied to it with `aria-describedby`.
- Copy confirmation is announced (`role="status"`); the focus ring is the existing `--chrome-focus-ring`; filled buttons are `--chrome-brand`; no colour pair outside `CHROME_CONTRAST_PAIRS`.
- The set-password page and the dialog work at phone width (single column, 16 px gutters).
- Dates go through `app/DateTime.tsx`, never `toLocaleString`.

## What Draughtsman deliberately won't do

- **Put the secret in a query string or the path.** Both end up in access logs, proxy logs and Referer headers; the fragment never leaves the browser, and a mail scanner that fetches the link cannot spend it.
- **Say whether an address has an account**, ever: the same words, status and timing for every address.
- **Sign the person in after a reset.** One more step, and the link alone never becomes a session.
- **Offer a "keep my other sessions" checkbox** (Clerk's choice). A reset is exactly when other sessions should end.
- **Let the administrator type or see the new password** on this path (the existing "Reset password" does, and stays for the cases where it is wanted).
- **Show a password strength meter** (Etsy). The rule is length; the page states it.
- **Show a stored SMTP password**, or return it from any call: it is write-only (replace or clear), the way the AI key is.
- **Send a test to an address the administrator types.** The test goes to the signed-in administrator's own address only, so the button cannot be used to send mail to anyone.
- **Let the browser override `draughtsman.yaml`.** A value the file or the environment sets locks that field; an operator's deployment setting cannot be silently replaced from the page.
- **Show the mail server's own words** when a send fails, only a short class of failure.
- Security questions, SMS codes and magic-link sign-in: out of scope.

## Open questions

- None blocking. Revisit once Mobbin carries Lucidchart, Whimsical and FigJam auth screens (see the coverage note in `README.md`).

---
2026-10-01 · password-recovery lane (Claude)
