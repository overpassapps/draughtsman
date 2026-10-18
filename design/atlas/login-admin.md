# Login and admin

## Screen
Two screens: the login page (local accounts in the MVP, OIDC in phase 2) and the admin area (theme, storage, AI provider, users). Key pattern questions: local plus OIDC on one page; theme upload; storage status; keeping admin plain enough that a client's IT person can use it without a manual.

## Mobbin queries
- `search_screens "login page with email and password fields and a continue with single sign-on button"`
- `search_screens "workspace admin settings page with sections for authentication, branding logo upload and storage usage"`
- `search_screens "Linear projects list dense table with status and sort controls"` (for the sidebar-and-sections settings pattern)

Coverage note: Lucidchart and Whimsical are not on Mobbin and are cited from the products. Neither has a distinctive login or admin pattern; both use vendor SSO pages and a settings sidebar.

## References
1. Campsite — Sign in — https://mobbin.com/screens/a9bc5e81-2c12-4737-b76d-30cfce4077f9 — logo, "Sign in", SSO buttons (Google, SSO) above an "or" divider, then email and password, forgot password, one primary button. Centred, no illustration.
2. TheyDo — Login — https://mobbin.com/screens/3b700270-5264-4eea-9a78-cb1070628c28 — a single card: work email, password, Login, "Or", "Login with SSO" as a secondary button. The smallest version of the pattern.
3. Plain — Settings, General — https://mobbin.com/screens/d88dd0d6-6974-4cd7-b1e5-aa2ee74e15dd — settings sidebar grouped (Admin, Channels, Workflow) with SSO as its own page; logo upload with accepted formats and size; workspace ID with copy; a Save button with an inline "Workspace updated!" confirmation.
4. Canny — Settings, Branding — https://mobbin.com/screens/c79654e5-2bbe-430e-8383-94cc6d2f0813 — logo and icon upload with recommended sizes; theme picker; brand colour with a **live contrast check** showing sample text on light and dark with pass and fail chips.
5. folk — Workspace details — https://mobbin.com/screens/e5873d8d-d7c0-4a9c-b88d-67fe71e6de0b — usage as small cards (used, remaining, bar) per quota; a red "Delete workspace" in a Data deletion section at the bottom.
6. Linear — Workspace sidebar — https://mobbin.com/screens/7da63cdb-3049-4a96-9df7-ba2aa1dbfc81 — the dense settings-sidebar reference: short labels, grouped, no icons on secondary items.

## What the references agree on
- Login is one centred column: logo, heading, SSO options, a divider, email and password, a single primary button, forgot-password link. No marketing panel needed.
- SSO appears as a button when it is the main route and as a text link when it is the exception.
- Inline status (reset email sent, wrong password) sits above the form in a coloured band, not in a toast.
- Admin is a left settings sidebar with grouped short labels and one page per topic; each page is a vertical form with label, one-line description, control.
- Uploads show accepted formats and recommended size next to the button.
- Usage and quotas are small cards with a bar. Destructive actions sit at the bottom in their own section.
- Branding pages preview the result (Canny's contrast check) rather than asking the admin to trust a hex value.

## What Draughtsman does
### Login
- **One centred card** on the chrome theme's surface: the instance logo and product name from the chrome theme (so a client sees their brand, not Overpass), "Sign in", email, password, "Sign in" primary button, "Forgotten your password?" link. The Overpass mark appears nowhere here.
- **OIDC slot:** when `auth.oidc` is configured (phase 2), a "Continue with <provider name>" button appears above the form with an "or" divider, as Campsite does. When local accounts are disabled, only the button shows. The layout does not change between the two, so the phase 2 addition is one conditional.
- **Errors** are an inline band above the form (`role="alert"`): "Email or password is incorrect." Never "user not found".
- **First run** with no users goes to a "Create the first administrator" form in the same card (see `empty-loading-error-states.md`).
- No "keep me signed in" checkbox; sessions are long-lived and revocable from Admin.

### Admin
- **Route** `/admin`, reachable from the home sidebar for admins only. Left settings sidebar (200px) with short labels grouped: **General** (Instance, Users), **Appearance** (Chrome theme, Diagram themes), **Integrations** (AI provider, MCP), **System** (Storage, Export component, Logs). One page per label, a vertical form, Save per page with an inline "Saved" confirmation.
- **Instance:** product name, logo upload (SVG or PNG, 240×240 recommended, shown next to the button), data folder path read-only with copy, version and "check for updates" disabled with the note "No phone-home. Updates are a new binary or image."
- **Users:** table of name, email, role (Admin, Editor), last sign-in, with Add user (local) and Reset password. Roles beyond two are phase 2.
- **Chrome theme:** upload a `chrome.yaml` (and logo), or edit the token values in a form generated from the file. Below the form, a **live preview strip** (top bar, a button, a panel) rendered from the tokens, and a contrast report from the same check the token compiler runs (themes/README.md): a row per token pair with the ratio and a pass or warn chip. Warnings never block Save.
- **Diagram themes:** list of shipped and uploaded diagram themes with a small rendered sample of each; set the workspace default; upload `diagram-*.yaml`.
- **AI provider:** provider (None, Anthropic, OpenAI, Azure OpenAI, Ollama), model, endpoint; the key field shows "Set by DRAUGHTSMAN_AI_KEY" and is read-only because keys never live in the file (spec section 11). A "Test" button sends a one-token request and reports the result inline. Per-document image sending opt-in default lives here.
- **Key ring notice (AI provider page):** under the API key summary, one note about the ring that encrypts the stored key. Unprotected (no key-encryption key on Linux/Docker, protection off, or set up but unusable): the danger banner style, saying anyone who can read the data folder can decrypt the stored key and naming what to set. Protected: one quiet muted line naming what protects it. While a pre-migration backup of the old plain-text ring still exists: a second boxed note naming the folder and asking the admin to delete it after Test connection succeeds (it never clears by itself and the server never deletes it). Plain text only; the server writes the wording so the log, the docs and the page agree.
- **MCP:** enabled toggle, port and token display with regenerate, a copy-ready `claude mcp add` snippet.
- **Storage:** provider (SQLite or Postgres) and connection status; a usage card (documents, checkpoints, size, last backup copy) with a bar; "Backup is copying this folder" with the path.
- **Export component:** whether the optional Chromium PDF component is installed, with the install command for the current platform.
- **Danger zone** at the bottom of Instance: none in the MVP. Deleting the instance is deleting the folder.

### Account, tokens and the security log (AU-03, AU-04, AU-05)
Built as header-bar pages like the other admin pages (the left settings sidebar above is still the target shape).
- **Account** at `/account`, reached from the user's name in the bar (a link, for every role). One narrow form card: Current password, New password, Repeat new password, "Change password". Beneath the heading, one plain line: "Changing your password signs you out everywhere else. Your API tokens keep working." Errors are an inline band above the form (`role="alert"`); the wrong-current-password message is the server's own and says nothing more. Success is a `role="status"` line, "Password changed. Your other sessions were signed out.", and the three fields clear. The repeat field is checked in the browser (mismatch never reaches the server); length is checked against `MIN_PASSWORD_LENGTH`. No "forgotten password" flow here: a lost admin password is recovered on the host (`reset-admin`, docs/api.md `## Auth`), and the page does not pretend otherwise.
- **Tokens** (`/admin/tokens`, every role, own tokens only). The create form gains **Expires in** (7 days, 30 days, 90 days as the default, 180 days, 1 year) and a **Read-only** checkbox whose hint says what it does ("can list and read documents; cannot create, change or delete"). The table gains **Expires** (a date, or "No expiry" for a token that pre-dates expiry, with "Created before token expiry existed; revoke and replace it." as its title/tooltip-free note below the table) and **Access** (Full or Read-only) columns; Status reads Active, Revoked or Expired, and Last used reads "Never" until first use. The secret is still shown once.
- **Security log** at `/admin/security`, admins only (the bar link says "Security"). A table, newest first: When, Event, Who, Account, From (IP), Detail. Event names are readable phrases ("Sign-in failed", "Password changed", "Role changed"), never the wire ids. A filter row above it: an Event select (All events plus one per kind), a Newer/Older pager, 50 rows a page. No search box, no export, no delete: the log is read-only evidence and the page offers nothing that edits it. Empty state: "Nothing has been recorded yet." Secrets are never in it, so there is nothing on the page to redact.
- **Never on these pages:** a password or token value anywhere but the one-time create panel; a "show password" eye on the change form is fine (a toggle, not stored); no dialog-over-dialog, no confirmation step for changing your own password (the current password is the confirmation).

## Keyboard and accessibility
- Login: the email field is focused on load; `Enter` submits; the error band is `role="alert"` and focus moves to it on failure; the password field has a show/hide toggle with an accessible label.
- Admin: the settings sidebar is a `nav` with `aria-current` on the active page; each page is a form with a visible heading; `Cmd/Ctrl+S` saves the page; `Cmd/Ctrl+K` works here too and lists admin pages by name.
- Every upload button states formats and size in visible text, not only in a tooltip.
- The contrast report is a table with ratio values as text, so the pass or warn chip is never the only signal.
- Toggles use `role="switch"` with the description linked by `aria-describedby`.

## What Draughtsman deliberately won't do
- **No third-party sign-in buttons that are not the instance's configured provider** (Google, GitHub, Apple rows on Coda, Elicit). One OIDC provider per instance, named after itself.
- **No sign-up link, trial banners, plans or billing pages** (Front, Intercom, folk, ClickUp). Accounts are made by an admin.
- **No hosting-region selector, white-label upsell, or "delete workspace" button** (Intercom, ClickUp, folk).
- **No API keys typed into a web form.** Keys come from the environment; the page only shows whether one is set.
- **No illustration or marketing column on the login page** (Coda). The client's logo is the branding.

## Open questions

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)

## Amendment 2026-10-01 (final usability QA: S3, S5, S6, S8, S9, P12, P17, P18)

- **Top bar.** Documents, one **Admin** menu (Users, Branding, Themes, AI settings, Security, Status; a disclosure button over real links, Escape closes and returns to the button), **Tokens** and **About** stay on the bar for everyone; an Editor sees no Admin menu and none of its pages anywhere. Under 930 px the brand and one **Menu** button remain; opening it lists every link (the admin pages inline under an "Admin" heading), the signed-in name and Sign out, 44 px tall rows. Every page starts with a **Skip to main content** link, and the shell supplies the page's one `<main id="main-content">` (the editor included).
- **Tokens (admin).** Under "Your tokens" and the create form, an administrator also gets "Everyone's tokens": owner (name, email), name, created, last used, expires, access, status, and Revoke per live token (accessible name `Revoke <token> for <owner>`). Never a secret. Editors see only their own.
- **Users.** Each field is its label, input and hint in one column, so labels never wrap away from their inputs. The Add user and Reset password fields print "At least 12 characters."; a shorter one is answered beside the field with the server's own `Password must be at least 12 characters.`, not by a disabled button. The Reset field has a visible label and says the person is signed out everywhere and loses their tokens.
- **Sign-in after a session ended.** One line above the form (not red: it explains, it does not accuse): password changed or signed out elsewhere, account deactivated, or session expired, plus "the changes you had not saved are kept in this browser" only when an editor really kept them. It never names an administrator.
- **First run.** "Where do I find it?" is closed until asked, so Create administrator is on screen at 860 px.
- **Not allowed.** An Editor at an admin address sees "Administrators only", the sentence, and a Back to documents link; `/admin` opens Users.

## Amendment: no licence page

Draughtsman is open-source software (AGPL-3.0-only) and has no licence file, so the Licence page, its admin-only notice under the app bar, the licence line on About and the evaluation footer on exported pictures are gone. The Admin menu lists Users, Branding, Themes, AI settings, Email, Security, Status and Connection check.
