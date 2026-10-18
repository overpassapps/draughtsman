# Design atlas

The design atlas is the set of per-screen pattern notes that ground every Draughtsman screen in real, shipped UI before a line of component code is written. It exists because the build agent must not invent layouts from generic priors (spec section 10).

## Rules

- **No UI work starts without its atlas entry.** A task that builds or materially changes one of the screens below writes (or updates) the screen's note first, then builds, and the PR description cites the note by path.
- **References come from the official Mobbin MCP** at `https://api.mobbin.com/mcp` (Streamable HTTP, OAuth on first use, paid Mobbin plan required), using its `search_screens`, `search_flows` and `search_sections` tools. The archived community Mobbin server is not to be used.
- **One note per screen**, at `design/atlas/<screen>.md` with a kebab-case name (for example `design/atlas/editor-shell.md`, `design/atlas/empty-loading-error-states.md`).
- **Mobbin coverage gap (checked 2026-09-21).** Mobbin's web library holds no Lucidchart, Whimsical or FigJam screens; deep, standard and flow searches naming each app return other products. Notes cite Lucidchart and Whimsical from the products themselves, marked "(not on Mobbin; from the product)", and use Figma's design editor for the Figma/FigJam slot. Re-check when Mobbin adds them and replace the citations.
- **Entries exist for all eight screens** (written 2026-09-21). A task that materially changes a screen updates its note in the same PR.
- **Prototype surfaces get a short note too**, outside the eight: `try-page.md` covers the `/try` paste-and-run page. A short note names the references it could not find rather than inventing them, and says what the screen does not commit to.

## Required note structure

Every note uses these headings in this order.

1. **Screen** — which screen, and the key pattern questions it must answer (copy them from the table below, add any that emerged).
2. **Mobbin queries** — at least two query angles. List the exact query string and the tool used for each, e.g. `search_screens "diagram editor toolbar"` and `search_sections "canvas properties panel"`.
3. **References** — 4 to 6 references. For each: product, screen or flow name, the Mobbin link if available, and one line on what it shows. The set **must** include Lucidchart, Figma/FigJam, Miro, Whimsical, and at least one dense non-diagramming SaaS (Linear, Notion or similar) so the chrome does not copy a single competitor.
4. **What the references agree on** — the patterns that recur across the set.
5. **What Draughtsman does** — the decision for this screen, specific enough to build from.
6. **Keyboard and accessibility** — shortcuts, focus order, ARIA and motion for this screen, consistent with the conventions below.
7. **What Draughtsman deliberately won't do** — patterns seen in the references that are rejected, and why.
8. **Open questions** — anything that needs the maintainers' call, or an empty list.
9. Footer line: date and author agent.

### Template

```markdown
# <Screen name>

## Screen
<One paragraph. Key pattern questions: …>

## Mobbin queries
- `search_screens "<query>"`
- `search_sections "<query>"`

## References
1. Lucidchart — <screen/flow> — <link> — <what it shows>
2. Figma/FigJam — …
3. Miro — …
4. Whimsical — …
5. Linear (or Notion) — …
6. <optional sixth>

## What the references agree on
- …

## What Draughtsman does
- …

## Keyboard and accessibility
- …

## What Draughtsman deliberately won't do
- …

## Open questions
- …

---
<YYYY-MM-DD> · <author agent>
```

## Screens that need an entry before build

| Screen | Note file | Key pattern questions |
| --- | --- | --- |
| Editor shell | `editor-shell.md` | Toolbar placement, left shape panel vs floating palette, right properties panel, zoom controls |
| Shape and connector palette | `shape-connector-palette.md` | Search, recents, categories, drag vs click-to-place |
| Properties panel | `properties-panel.md` | Contextual sections, colour picker, text controls, kind/prop editing |
| Document list / home | `document-list.md` | Grid vs list, workspace switcher, recent, templates |
| Export dialog | `export-dialog.md` | Format, scale, background, page selection, AI outline option |
| AI panel | `ai-panel.md` | Prompt input, streaming preview, accept/reject, provenance badge |
| Login and admin | `login-admin.md` | Local + OIDC, theme upload, storage status |
| Empty, loading and error states | `empty-loading-error-states.md` | Every screen above, including a first-run with no documents |

## Conventions every note must check against

From spec section 10. The "What Draughtsman does" section of each note must be consistent with all of these.

- **Keyboard-first.** Every action has a shortcut and every shortcut appears in the searchable command palette (Cmd/Ctrl+K).
- **WCAG 2.2 AA for the chrome.** Contrast checked when the theme tokens are compiled (themes/README.md), focus rings visible, panels navigable by keyboard, canvas actions available from a menu as well as by mouse.
- **Density is a user setting** (compact/comfortable); default compact, because the user is a professional who opens this every day.
- **Motion is minimal and disableable.** No animation on the canvas beyond snap feedback.
- **Icons from one set (Lucide) at one stroke width.** No mixed icon libraries.
- **Copy is plain British English**, short, and never jokey in the chrome.

## Relationship to the theme system

Notes describe layout, hierarchy and interaction. They do not specify colours, radii or fonts; those come from the chrome theme tokens in `themes/` (see `themes/README.md`). A note may say "the primary action uses the accent token", never a hex value.
