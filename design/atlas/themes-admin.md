# Themes (admin)

## Screen
The admin Themes page at `/admin/themes`: an organisation lists, creates, edits, duplicates, deletes, exports and imports themes, and chooses the default every signed-in person receives. It is the "Appearance" page `login-admin.md` describes (chrome theme and diagram themes), built as one page because an organisation brands both together.

## Mobbin queries
Not re-run for this page. It applies the references already recorded in `login-admin.md` (Canny's Branding page with its live contrast check, Plain's settings-page pattern of a Save button with an inline confirmation); the coverage gap for Lucidchart, Whimsical and FigJam in `README.md` stands.

## What the page does
- **List.** One card per theme: a swatch (the theme's own text, brand, accent and canvas colours), its name, a "Default" badge, its mode and its diagram base. The shipped Overpass default is always first, cannot be edited, and is the base a new theme starts from. Actions: Edit, Make default, Duplicate, Export, Delete (asks first; the default cannot be deleted).
- **Create and import.** A name plus a base (the Overpass default, or a copy of any existing theme), or a theme file chosen from disk. A name already in use is refused on create and numbered on import.
- **Editor.** Name, mode, then the chrome colours for the dark or light set (brand, text on brand, surface, raised surface, border, text, muted text, accent, danger), corner radius and fonts; then the diagram: a starting theme, its palette, the canvas background, the export paper, the grid and two fonts. Every colour is a picker plus a hex field. Save, Revert.
- **Live previews** beside the form, re-rendered on every keystroke from the draft: the app chrome (a scoped copy of the shell bar, buttons, link, danger text and input using the shell's own classes) and a sample diagram on both the editor canvas and the export paper.
- **Contrast.** The token compiler's WCAG AA checks, shown where the colour is: a warning line under each chrome colour that takes part in a failing pair, a table of every chrome pair with its ratio as text (so the result is never a colour alone), and a list of the diagram's failing pairs. Warnings never block Save.
- **Logo, favicon and product name** stay on the Branding page, and apply across every theme. The page warns when a theme can show a light surface and no logo has been uploaded for one.

## Keyboard and accessibility
Every control is a native input, select or button with a visible label; the colour set choice is a radio group; each preview has an accessible name; errors and warnings are `role="alert"` / `role="status"` text. The import control is a visually hidden file input inside a visible label, focus ring on the label.

## What it deliberately won't do
- No free-form CSS and no font files: a colour is hex and a font is a list of family names, so a theme cannot carry markup, a second declaration or a remote resource.
- No per-kind editing of every node style; a palette edit restyles every kind that shares the colour and the base theme's hierarchy survives.
- No per-user or per-workspace themes: one default per instance.

## Amendment 2026-10-01 (final usability QA: S4, P19)

- Palette fields are labelled by role ("Primary accent", "Darkest neutral", "Paper"), never by the colour they start as, because the person can change that colour. The keys saved themes store overrides under are unchanged.
- The contrast table holds every pair app.css draws: text on surface and raised surface, brand text on brand, accent and danger on the surfaces. Filled controls use `brand`, the token brand text is held to; `accent` colours links, rings and strokes only. A test fails if app.css draws a pair the table does not hold.

## Amendment 2026-10-05 (From a website)

A "From a website" section on the Themes page, between Create and the editor, turns a site's colours into a draft theme. It is an aid to the editor, never a second way to save.

- **Paste CSS (always there).** A text box that takes a stylesheet, or a whole page's HTML with its styles inline, and a "Read colours" button. It sends the text to `POST /api/admin/themes/extract` and works with no network and on an air-gapped install.
- **Web address (only when the server says so).** A URL box and a "Fetch and read colours" button appear only when `GET /api/admin/themes/extract/options` reports `url: true` (the operator set `themes.fetchFromUrl.enabled`). When it is off the box does not exist, and one line says the operator has not enabled it and pasting works. The line beside the box says plainly that the server will fetch that page and up to 8 of its stylesheets, and keeps nothing.
- **The draft.** The result opens in the existing editor, unsaved, with the app and diagram previews and the contrast table it already has, so every colour, the mode and the diagram palette can be changed before anything is stored. The button there reads "Create theme from draft". Above it: "How this was chosen" (one line per colour: where it came from) and "Adjusted for contrast" (every colour the extractor moved to pass the checks, from and to, and why). Both are plain lists.
- **Saving.** "Create theme from draft" is the existing create (a name, the Overpass base) followed by the existing save of the edited draft; if the save is refused, the created theme is removed again. Nothing is created until that button is pressed, and closing the section discards the draft.
- **Accessibility.** The section is a labelled region with native controls; results and errors are `role="status"` / `role="alert"` text; the reasons are a list, not colour alone.
- **Won't do.** It never copies fonts or images, never stores the page or its CSS, and never saves by itself.
