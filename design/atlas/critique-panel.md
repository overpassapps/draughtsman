# Review (critique) panel

## Screen
The Review tab of the editor's right-hand panel: the results of the deterministic critique (`CritiqueAnalyser`, the same code behind MCP's `critique_document`) for the document as it is on the canvas. It is PR-08's second half ("critique … MCP-only"). Key pattern questions: how to list structural findings so each can be acted on; how to relate a finding to the elements it is about; whether it runs on its own or on request.

## Mobbin queries
- `search_screens "accessibility or design checker side panel listing issues grouped by severity, clicking an issue selects the element on the canvas"` (web, standard)
- `search_screens "validation panel with warnings and errors counts listing problems with a refresh button"` (web, standard): no close analogue came back (Square's import-error dialog, DoorDash, Mintlify, Klaviyo); only Square's "Review and fix errors" is cited, for the count-per-status summary.

## References
1. Supabase — Security Advisor — https://mobbin.com/screens/e3ac7f2c-bdd0-42a4-ac13-76fb893fd06d — Errors and Warnings as counted tabs, a table of issue type, affected entity and description, Refresh and Export at the top right. The model for severity grouping and a manual re-run.
2. MagicPath — layers and properties — https://mobbin.com/screens/703ff338-4533-4ecb-9157-b2326cf2cf8a — selecting an element in a tree highlights it on the canvas; the model for "click a finding, see the element".
3. Square — Review and fix errors — https://mobbin.com/screens/2bf8396f-b5eb-4c32-ad6e-ee71118f27ae — a summary sentence ("5 out of 5 items could not be imported") over two counted status rows; the model for the one-line summary above the list.
4. Cofounder — Operations panel — https://mobbin.com/screens/0461ec48-e6a2-46a1-a6ee-d7788301951e — a right-hand panel of collapsible sections, each with a count, beside the canvas.

Not on Mobbin (from the products): Lucidchart — Diagram validation (rules in a panel, with a count badge); Figma — Accessibility and plugin audit lists (findings that select layers on click).

## What the references agree on
- Findings are grouped or tabbed by severity with a count, and each row names the thing it is about.
- Selecting a row selects and reveals the element on the canvas.
- A run is cheap and repeatable; the panel has a clear "last checked" state and a Refresh.

## What Draughtsman does
- **Placement:** a "Review" button in the panel's view switcher. It runs when the tab is opened and when Refresh is pressed, and once more when the document changes while the tab is open (debounced at 800 ms), by posting the document as it is on the canvas to `POST /api/critique`, so unsaved edits are judged too. No background run when the tab is closed.
- **Summary line:** "3 findings: 1 error, 2 warnings" or "No findings. The structure checks passed." (the honest wording: this is structure, not a judgement of the diagram's content).
- **List:** errors first, then warnings, then by rule id (the analyser's own stable order). A row shows the severity (icon and word), the rule id in a small monospace tag (they are contract; an agent keys on them), the message, and chips for the elements it is evidence for, labelled with the node's label (or the edge's "from → to"). Clicking a row, or a chip, selects those elements and centres the first one on the canvas.
- **Findings about elements that no longer exist** cannot occur: the analyser only lists ids that are in the document.
- **Read-only:** nothing here edits the document. Fixing is done on the canvas, or by asking the agent; no auto-fix.
- **Errors in the request** (the server unreachable) show a line with Try again; the last good result stays visible, marked "as of <time>".

## Keyboard and accessibility
- The tab is an `aria-pressed` button like its siblings; results are a labelled list; Refresh is a button; each row is a button whose name is "<severity>: <message>". A polite live region announces the summary line after each run.
- Escape from inside the panel returns to the canvas, as the other panels do. Severity is carried by the word and icon, never colour alone.

## What Draughtsman deliberately won't do
- **No scoring, grades or AI opinion** (the analyser is deterministic on purpose: its rules stay reproducible).
- **No per-rule muting or ignore list** in this lane; a finding that is deliberate is a rule the vocabulary should stop raising, which is a code change.
- **No export of the list** (Supabase): the MCP tool already returns it as JSON.

## Open questions
- Whether findings should also draw a badge on the canvas elements. Not built: it would add a second marks layer beside compare's.

---
2026-10-01 · Claude (fm/history-bin-list crewmate)
