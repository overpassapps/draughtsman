# Help, keyboard shortcuts, About and the admin status page

## Screen
Four small surfaces that answer "how do I, what am I running, and is it healthy": the editor's Help menu (keyboard-shortcut sheet, documentation link, About), the About page every signed-in user can open, the admin-only Status page, and the sample-document offer on an empty instance. Key pattern questions: where does a user find the shortcut list without knowing one; what may an Editor see of the build; how does an admin learn that a component is degraded when the product still works without it; how are samples offered without being forced on anyone.

## Mobbin queries
- `search_screens "keyboard shortcuts cheat sheet dialog listing shortcuts grouped by section"` (web, standard mode, run 2026-09-30)
- `search_screens "system status page in admin settings showing service health rows with green and red status"` (web, standard mode, run 2026-09-30; the library holds no diagramming product's status page, so the closest matches are generic settings and status pages)

## References
1. Cofounder — Keyboard Shortcuts dialog — https://mobbin.com/screens/2f5bbe84-7929-4d5e-8a3e-8a899c4c89a3 — a small centred dialog, muted uppercase section labels (Canvas, Chat), one row per command with a key cap on the right, and a "Support & resources > Documentation" row at the foot. The shape to copy: shortcuts and the docs link in one place.
2. Perplexity — Keyboard Shortcuts dialog — https://mobbin.com/screens/b0a63cb0-ff2e-49a9-90ab-b4dff7f8084d — sections (General, Navigation, Interface), key caps as separate small boxes, and a legend for the modifier glyphs (Command, Option, Control, Shift) in the footer.
3. Magnific — Settings, Shortcuts tab — https://mobbin.com/screens/0a05feaf-9f16-4a6b-b4af-bd5cdc978a79 — the shortcut list on a canvas tool, grouped Basics and Control; chords are written as `key + key`.
4. Shopify — Keyboard shortcuts — https://mobbin.com/screens/51f33f53-363c-449e-bcaf-1878225245f7 — three columns of sections; the first row is "Keyboard help ?", so the sheet opens from the key it lists. Too dense for a 640px dialog; the `?` convention is the part to take.
5. Mintlify — Admin settings — https://mobbin.com/screens/87509397-eb8e-4bbf-9b4a-b3f798150e57 — a settings page of labelled rows with a short description on the left and the state on the right, disabled rows explaining why ("This feature is only available for ..."). The pattern for the status rows.
6. DoorDash Merchant — Store availability — https://mobbin.com/screens/28676734-9c93-487a-b2fc-df87e1176c75 — a status card with a one-word state ("Paused"), one sentence saying what it affects, and the action beneath. The wording pattern for a degraded component.

Not on Mobbin (cited from the product, unverified against the library): Lucidchart and Whimsical both have a Help menu with "Keyboard shortcuts" and a docs link; Figma opens its shortcut sheet with `Cmd/Ctrl+Shift+?`; Linear's About is a short modal with version and a link to the changelog.

## What the references agree on
- The shortcut sheet is a modal dialog grouped by the same sections as the menus, key caps on the right, opened from the Help menu and from `?`. The docs link sits in the same place, not in a separate page.
- Status and about information is plain rows: a label, the value, and one sentence when the value is not the good one. No charts.
- A component that is off or degraded says what stops working and what to do, in that order.

## What Draughtsman does
### Help menu (editor top bar)
- **Command palette** (existing), **Keyboard shortcuts** (`?`), **Documentation** (opens the configured docs URL in a new tab, `rel="noopener noreferrer"`), **About Draughtsman** (navigates to `/about`, guarded for unsaved edits like every other navigation).
- The shortcut sheet is built from the action registry (`web/src/editor/actions.ts`): every registered action that has a shortcut, grouped by its menu (File, Edit, View, Arrange), so it cannot drift from what the keys do. A chord is drawn as key caps (`Cmd` on macOS, `Ctrl` elsewhere). Copy, Cut and Paste are listed too. The dialog is a modal with `role="dialog"`, focus trapped, Escape and the close button dismiss it and focus returns to the control that opened it.

### About (`/about`, every signed-in user)
- A plain page in the shell: product name and logo from the chrome brand, **Version** and **Build date**, **Support** (a mailto link), **Documentation** link, and **Third-party licences** (a link to `/THIRD-PARTY-NOTICES.txt`, which the instance serves).
- An Editor sees exactly that. An Admin additionally sees a link beneath it to System status (`/admin/status`). Nothing admin-only is fetched for an Editor: the page calls `GET /api/about`, which returns the same body to everyone and has no data folder or path in it.
- Spec section 8: Draughtsman and Overpass appear in the product only here, so this is where the support contact lives.

### Admin status (`/admin/status`, Admin only, cookie session)
- A settings-style page of rows, each with a label, the state as a word (OK, Not running, Off, Not installed) and one sentence when the state is not good, never colour alone:
  - **Layout service (sidecar):** running / starting / unavailable / stopped, its version, Node version, restarts, and the error the supervisor recorded. Unavailable reads "Layout, Tidy, SQL import and AI generation are not working. Editing and saving still work."
  - **PDF export:** "Off" when `export.chromium.enabled` is false, with the setting named; "Enabled" with the Chromium path in use, or "Enabled but not available" when the component cannot launch.
  - **AI provider:** off, or the provider and model, with a link to `/admin/ai`.
  - **Storage:** provider (SQLite or Postgres) and, for SQLite, the database file inside the data folder.
  - **Data folder:** the absolute path, and where the server writes its log on this kind of install (journalctl, the Application event log, the `logs` folder, or container output).
  - **Version and build date.**
- Read-only. No restart or repair buttons: an instance has no process manager to ask.
- The nav link "Status" shows for an Admin; a non-admin who types the URL gets the same "Only an administrator can see this" panel the other admin pages show, and the endpoint answers 403.

### Sample documents (home, empty instance only)
- When the instance has no documents the home empty state offers "Add four sample diagrams" (one flowchart, one architecture, one sequence, one ER) as a secondary text button under the creation cards. It creates four ordinary documents, each a fresh id, and opens none of them. It is never offered once any document exists, never runs on its own and is not a server seed.

## Keyboard and accessibility
- `?` opens the shortcut sheet when focus is not in a text field, the label editor or another dialog. The Help menu entry shows `?`.
- The status page's states are words in the row, with `role="status"` only on the page-level banner that appears when something is degraded.
- Links that leave the app open in a new tab and say so in their accessible name ("Documentation, opens in a new tab").

## What Draughtsman deliberately won't do
- **No phone-home version check or "update available" banner.** An air-gapped instance cannot answer it and must not try.
- **No restart or "fix" buttons on the status page.** It reports; the operator acts.
- **No tour or coaching overlay** (see empty-loading-error-states.md).
- **No sample diagrams created automatically**, and no sample content in the database on a fresh install.

## Open questions
- Documentation (qa-pdf-archive, 2026-10-01): `about.docsUrl` unset means this server's own copy of the guides at `/docs/` (read-only, behind sign-in, plain text) when the release archive carries `docs/`, otherwise the vendor's address; `""` hides the entry. About shows a local link as "The guides on this server" and a configured one as its address; Help > Documentation opens either in a new tab. Rendering the Markdown as pages is not built.
- The documentation URL and the support address are configuration (`about.docsUrl`, `about.supportContact`). The shipped defaults are a placeholder and the landing page's address; the maintainer confirms the real docs location before release.

---
2026-09-30 · Claude (fm/first-run-onboarding crewmate)

---
2026-10-05 · Claude (fm/logo-in-product crewmate)

## Amendment 2026-10-05 (the Draughtsman mark and the About identity blocks)

The maintainer chose the Draughtsman logo (a compass mark, with the plain-text line "by Overpass"). It is the product's default identity only; an organisation's own branding always wins. No Mobbin references: this is one chosen asset placed in existing screens, not a new layout.

- **The rule:** the mark never replaces, and never sits beside, an organisation's own logo or product name. It is drawn only when the theme manifest has loaded, names no `logo`, and the product name is still `Draughtsman` (`web/src/theme/defaultBrand.ts` `usesDefaultBrand`). While the manifest is loading, nothing is drawn, so an organisation never sees the mark flash before its own logo.
- **Where it shows by default:** the shell header (28 px, left of the product-name text), the top of the sign-in, first-run and password-reset cards (40 px, above the product-name line), and the browser tab (favicon). In each the product-name text stays, so the mark is decorative (`alt=""`).
- **About:** two blocks, kept apart by a rule under each. "The product" always shows the lockup (mark, "Draughtsman", "by Overpass") as one image with the alt text "Draughtsman by Overpass". "This instance's branding" appears only when the organisation has set a logo or a product name, and shows its logo (alt "<name> logo") and name; the lockup is never in it.
- **Light and dark:** navy mark on a light surface, white strokes with the red feet on a dark one, chosen from the chrome theme's mode; a `system` theme lets `prefers-color-scheme` choose, exactly as the theme's own colour tokens do. The files are `web/public/brand/` (copied from the logo, never redrawn).
- **Favicon:** `web/index.html` ships the Draughtsman SVG, 16 and 32 px PNG fallbacks and the apple touch icon. An organisation's favicon (or, failing that, its logo, as before) replaces all of them in the page head, and removing it puts them back.
- **Contrast:** the lockup's text is navy on the light surfaces (about 16:1) and white on the dark ones; the red feet clear the 3:1 a graphic needs against every shipped surface (`web/scripts/brandAssets.test.ts`).
- **Not changed:** PNG, SVG and PDF exports.
