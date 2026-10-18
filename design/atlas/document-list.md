# Document list / home

## Screen
The first screen after login: the documents in the workspace, a way to make a new one, and the way into admin. Key pattern questions: grid versus list; workspace switcher; recents; templates; search; how much home chrome a single-user MVP should carry.

## Mobbin queries
- `search_screens "files home page with recent files grid of thumbnails, templates row and workspace switcher in sidebar"`
- `search_screens "Miro dashboard listing boards with grid and list view toggle"`
- `search_screens "Linear projects list dense table with status and sort controls"`

Coverage note: Lucidchart and Whimsical are not on Mobbin and are cited from the products.

## References
1. Figma — Recents home — https://mobbin.com/screens/7ee6fa7d-5aa8-443e-972c-440f4da3109f — left sidebar with search, Recents, Templates and the team; a row of "new file" buttons by type at the top; a thumbnail grid with title, edited time and a starred marker; grid or list toggle and sort at the right.
2. Miro — Team boards, list view — https://mobbin.com/screens/8da05883-1477-4cce-8c48-1e1644b8e26d — templates row at the top ("Blank board", "Flowchart", …), then "Boards in this team" with filter, owner and sort dropdowns, grid or list toggle, and a table of name, online users, space and owner.
3. Miro — Team boards, grid view — https://mobbin.com/screens/e7323368-0d5e-4f26-a15f-90641830acc5 — the same list as large thumbnails with date and owner under each.
4. Linear — Projects list — https://mobbin.com/screens/7da63cdb-3049-4a96-9df7-ba2aa1dbfc81 — a dense single-line-per-row table with health, priority, lead and target date; the sidebar holds workspace switcher, Inbox, My issues and teams. The compact-density reference.
5. Coda — Workspace home — https://mobbin.com/screens/ba6f5461-ff84-4795-b8f6-a11d5a7526a9 — Recent, Trending, My shortcuts as tabs above a list with icon, title, folder and last-viewed time; Templates and "Blank doc" as two buttons top right.
6. MagicPath — My files — https://mobbin.com/screens/1b590837-7bce-4982-aa61-cf2b1b10ddc2 — a "Create a new design" empty card as the first grid tile, followed by file thumbnails; sort and grid toggle top right.

Not on Mobbin (cited from the product, unverified against the library):
- Lucidchart — Documents home — sidebar with Recent, Favourites, Team; "New" button opening templates; grid or list; folder tree.
- Whimsical — Workspace home — sidebar of folders, a board grid with type icons (flowchart, wireframe, doc) on each card.

## What the references agree on
- A left sidebar carries the workspace name (as a switcher when there is more than one), search, and a short list of views: Recent, Starred or Favourites, then folders or teams.
- The top of the content area offers new-document entry points, usually as a row of typed templates including a blank one.
- The document collection supports both grid (thumbnail-led) and list (row-led) views with a toggle, plus sort by last opened or modified.
- Each item shows title, type icon, last modified time and owner; a star or favourite marker is common.
- A search field is always visible and usually bound to `Cmd/Ctrl+K` or `/`.
- Trial, upgrade and "invite members" chrome is everywhere in SaaS homes and is the main clutter.

## What Draughtsman does

**As built for thumbnails (2026-10-02).** The grid card the decisions below describe is built, with the thumbnail served rather than generated on save (docs/api.md `GET /api/documents/{id}/thumbnail`), because the server can draw any document on request in a few milliseconds and keep the result by document version, so nothing is stored and nothing goes stale:
- **Grid is the default; List is the table above.** A two-button group labelled Layout (Grid, List; `aria-pressed`) sits at the right of the search row and is remembered per browser (localStorage, guarded: a blocked store means the grid). The grid has no column headers, so a **Sort by** select beside the type filter offers the table's three sortable columns each way round; the order is the same in both views.
- **Card:** the thumbnail on top in a 320 by 200 ratio (reserved before the image loads, so nothing shifts), then the type glyph with its visually hidden name, the title as the card's one button, the relative time (absolute on hover) and the kebab at the bottom right, which opens upwards over the picture. Rename happens in the card, Delete shows the same Undo line.
- **Whole card clickable, one focus stop.** The title button's hit area is stretched over the card (`::after`), so the mouse can press the picture while the keyboard goes title, then Actions, exactly as along a table row; the focus ring is drawn round the card (a transparent button outline, so forced-colours still draws one). This replaces the earlier note's "thumbnail as `aria-hidden`" with an image whose alt text names the document (`Preview of <title>`); the glyph shown when a picture cannot be had is the decoration and is hidden.
- **States:** a neutral skeleton over the box until the image loads (a shimmer, off under `prefers-reduced-motion`); the type glyph if the request fails or is rate limited, so a card is never a broken-image icon; the server draws a dashed empty frame for an empty document, plain boxes and lines for a very large one and a grey placeholder for an enormous one.
- **Themes:** the picture is drawn on the diagram theme's own paper, as an export is, so in a dark chrome it is a light tile with the chrome's border; everything around it is chrome tokens. Phone width: one column (`auto-fill`, 240 px minimum, never wider than the page), the toggle and Sort by wrap with the search row.
- **Not here:** star, folders, owner and "modified by" on the card, and a recents row are still later.

**As built for the bin, search and duplicate lane (2026-10-01, audit findings OP-02 and PR-06).** The shipped home is one column, not the sidebar described further down, so this lane adds to it without inventing the sidebar:
- **Search** is one field under the page title, filtering by title as you type (server-side, `GET /api/documents?q=`, debounced). `/` focuses it when nothing else has focus. It clears with its own button and with Escape.
- **Sort** is on the column headers of the table, which is what the references do for a dense list (Linear, Zoho): Title, Type, Last modified are buttons carrying `aria-sort`; a second press reverses. Default is last modified, newest first. The choice is remembered for the session only (no per-user store exists yet). A Type filter is one select beside the search field.
- **Row:** the type icon (already there, now also the one visual identity of a row; no rendered thumbnails in the table: they are the grid's, see the thumbnails paragraph above), title, modified time, and the kebab: Rename, Duplicate, Delete. Duplicate creates "<title> (copy)" and opens nothing; the list reloads with it on top.
- **Delete moves to the bin.** No confirm modal is needed because it is recoverable: a toast line under the header says "Moved "<title>" to the bin" with an Undo button (restores at once) for ten seconds.
- **Bin** is a second view beside "Documents" in the page header (two toggle buttons, `aria-pressed`). Its table has Title, Deleted (who and when), Days left (the retention period, `retention.binDays`, 30 by default), and per row **Restore** and **Delete forever**. Delete forever is a separate confirmation dialog that names the document and says it cannot be undone; it is the second step, never the first. A line above the table states the retention rule in words. An Editor sees only the entries they created or deleted; an administrator sees all (docs/api.md "Bin"). A row the caller may not remove for good shows Restore only.
- **Empty states:** an empty bin says "The bin is empty. Deleted documents stay here for 30 days." A search with no match says so and offers Clear search, never the first-run empty state.

Decisions carried over from the earlier draft of this note, unchanged:
- **Sidebar (220px):** instance name and logo from the chrome theme (workspace switcher only when the user has more than one workspace, phase 2); search field (`Cmd/Ctrl+K`, the same command palette as the editor with documents as its results); views: All, Recent, Starred; folders (phase 2, hidden until then); at the bottom Admin (admins only) and the account menu.
- **Header row:** "New document" primary button (accent token) opening a small menu: Flowchart, Architecture, Freeform, From text (opens a new document with the AI panel ready for DSL or Mermaid paste), Import JSON. Templates are phase 2; the menu is the templates row until then.
- **View toggle and sort:** grid or list, remembered per user; sort by last modified (default), title, created. List is the default in compact density because this is a daily tool with many documents; grid is a click away for people who recognise diagrams by shape.
- **List row:** type icon (Lucide `workflow` for flowchart, `boxes` for architecture, `shapes` for freeform), title, last modified (relative, with the absolute date on hover), modified by, star. Row click opens; a kebab menu holds Rename, Duplicate, Export, Move to bin.
- **Grid card:** a rendered SVG thumbnail from the document's diagram theme on white paper, title, last modified. Thumbnails are drawn server-side (on request and kept by document version rather than generated on save, see the as-built paragraph above) so the grid never waits on the browser.
- **Bin** as a view (see the as-built paragraph above); deleted documents are recoverable for 30 days.
- **No hero, no resources, no onboarding cards** once a document exists. First-run is covered in `empty-loading-error-states.md`.

## Keyboard and accessibility
- `Cmd/Ctrl+K` opens the palette with documents first; `N` creates a new document; arrow keys move through rows or cards; `Enter` opens; `Delete` moves to bin with an undo toast (no confirm modal); `S` stars.
- The list is a real table with column headers so screen readers can navigate by column; the grid is a list of links with the thumbnail as `aria-hidden` and the title as the link text.
- View toggle and sort are `aria-pressed` and `aria-sort` respectively; the current view is also announced on change.
- Focus rings on rows and cards use the focus token; hover is never the only affordance for the kebab menu, which is reachable by `Shift+F10` or the context menu key.

## What Draughtsman deliberately won't do
- **No trial banners, upgrade buttons, "invite members" or "explore templates" promos** (Miro, Figma, Coda, Langdock). Nothing in the app sells.
- **No online-user avatars or presence columns** (Miro) until realtime lands.
- **No resources, learning or community sections** (Coda, Fibery). Help is one menu item.
- **No folder tree in the MVP** (Lucidchart, Dropbox). Star and search cover a single user's needs; folders come with sharing in phase 2.
- **No mixed file types** (Langdock, Dropbox). Everything here is a Draughtsman document.

## Open questions
- The sidebar is still phase 2; thumbnails shipped on 2026-10-02 (see the as-built paragraph above).
- Should the free hosted tier (section 13, open) show a different home? Default for the build: no; one home, and the difference is in admin.

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)
2026-10-01 · Claude (fm/history-bin-list crewmate): bin, search, sort and duplicate as built; Mobbin references for the bin are in `version-history.md` (Trash screens: AirOps, Magnific, Zoho CRM, Airtable, Grok).
2026-10-02 · Claude (fm/doc-thumbnails crewmate): grid of thumbnail cards with a Grid/List toggle as built. Mobbin references above (Figma, Miro, MagicPath) are the grid, toggle and card layout this follows; no new queries.
