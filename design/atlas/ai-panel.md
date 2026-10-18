# AI panel

## Screen
The panel where a person turns text into a diagram (paste a paragraph, a spec, a DSL block or a Mermaid block), and later asks for a document from a diagram. Key pattern questions: prompt input; streaming preview; accept and reject; provenance badge; how "bring your own provider" and an unconfigured provider show up; how a DSL or Mermaid paste is distinguished from a natural-language prompt.

## Mobbin queries
- `search_screens "AI assistant side panel inside an editor with prompt text area, generate button and streaming result preview"`
- `search_screens "AI generated suggestion shown inline with accept and discard buttons and an AI badge"`
- `search_flows "Whimsical creating a flowchart"` (returned Miro "Generating a diagram with AI" and Craft "Generating a flow chart")

Coverage note: Lucidchart and Whimsical are not on Mobbin and are cited from the products.

## References
1. Miro — Create with AI panel, flow "Generating a diagram with AI" — https://mobbin.com/flows/9493f1a0-ae3e-416a-bde9-d2838feb0e77 — a left panel with a type chip row (Flowchart, Mind Map, ER Diagram, UML Sequence, UML Class), a large prompt area and a "Generate diagram" button; the result lands on the board inside a frame with a floating bar showing the object count.
2. Craft — Mermaid to Excalidraw, flow "Generating a flow chart" — https://mobbin.com/flows/646845e8-e66e-4be9-90f1-7d142ac05e6e — a two-pane modal: Mermaid source on the left, live preview on the right, a note on supported diagram types, and "Insert" with its shortcut shown. The best shipped model for a paste-a-block flow.
3. Mural — Mural AI side panel with inline menu — https://mobbin.com/screens/9dfc2233-6356-479b-a6ee-2a23490f0d2c — a right panel that is a conversation: "You" and "Mural AI" turns, each result a card; an inline AI menu on the selection (Rewrite, Summarise, Translate); a small disclaimer "AI-generated content may be incorrect".
4. Canva — Magic Write inline result — https://mobbin.com/screens/f7fd7a5c-8762-4cbe-9b04-a2e6d1b53ea2 — the generated text shown in a card over the document with "More like this", "This but…", copy and a primary "Insert" showing its shortcut; "AI can make mistakes" in small type.
5. Remote — Revise with AI, side-by-side — https://mobbin.com/screens/44344cb5-9817-4d0e-b696-e8ba936b2f82 — the original dimmed on the left, the proposal on the right, and "Try again" and "Use this" at the bottom of the proposal.
6. Framer — Agent panel — https://mobbin.com/screens/214392a2-3b1d-492b-bacf-0b2452ef66c9 — a right panel as a tab next to Style; a model picker under the prompt; attachments in the prompt. Shows the provider picker placed where it does not dominate.

Not on Mobbin (cited from the product, unverified against the library):
- Lucidchart — Generate diagram with AI — a prompt modal with diagram type, result placed on the page; edits through a "Refine" prompt.
- Whimsical — AI flowchart — "Generate flowchart" from a prompt, the result appearing as normal editable shapes, a "Regenerate" option.

## What the references agree on
- The AI surface is a side panel, not a modal, when it is conversational; a modal when it is a one-shot paste with preview (Craft).
- Diagram type is chosen before generating, as chips. The three options (Flowchart, Architecture, Sequence) wrap onto a second line in the 240px panel rather than clip at its edge (QA-05).
- The result is previewed before it is committed, and committed with a verb ("Insert", "Use this"), with "Try again" or "Regenerate" beside it.
- Generated content is labelled as AI and carries a one-line accuracy disclaimer.
- The model or provider is shown as a small picker near the prompt, not as a headline.
- Results land on the canvas as ordinary editable objects, grouped or framed so they can be moved or undone as one.

## What Draughtsman does
- **Right panel, replacing the properties panel while open** (toggle `Cmd/Ctrl+J`, top bar button with Lucide `sparkles`, and "From text" on the home screen). It shares the right column so the shell never has three panels.
- **Prompt area first.** A large text area that accepts anything. Below it, a **detected input** line: "Draughtsman DSL", "Mermaid flowchart", "Mermaid erDiagram" or "Text prompt". Detection is deterministic (grammar sniff), and the label is a segmented control the user can override. DSL and Mermaid go straight to the parser and ELK with no model call; only Text prompt calls the configured provider.
- **Type chips** for Text prompt: Flowchart, Architecture (the document's current type is preselected; changing it for an existing document is disabled). Freeform is not generated.
- **Provider row** under the prompt: the configured provider and model as a small read-only label (Anthropic, OpenAI, Azure OpenAI, Ollama) with a link to Admin. When no provider is configured the Text prompt option is disabled with the line "No AI provider configured. Paste DSL or Mermaid, or ask an admin to add one." DSL and Mermaid always work, including on air-gapped sites.
- **Generate** (`Cmd/Ctrl+Enter`) streams the DSL into a **Preview** section in the panel as it arrives, and renders a ghost of the graph on the canvas behind the panel at 50% opacity as soon as the DSL parses. Nothing is written to the document yet.
- **Accept and reject:** "Insert" (primary, `Cmd/Ctrl+Enter` again) commits through the same command path as `patch_document`, as one undo step, and selects the new nodes. "Try again" regenerates with the same prompt; "Discard" clears. For an existing document with content, Insert offers "Add to document" or "Replace document", the latter with an inline confirm.
- **Provenance:** every node and edge inserted here carries `aiProvenance` (provider, model, time, prompt hash). On the canvas, such nodes show a small `sparkles` badge in the top-right corner until the user edits them, and the properties panel shows the Provenance section. A document-level badge in the top bar reads "3 AI edits" and opens a list of checkpoints.
- **Warnings** from the parser or from ELK (unknown kinds, missing labels) appear as a list under the preview, each a link that highlights the node.
- **Diagram to document** (the second MVP AI feature) is a second tab in the same panel: choose an output (Design doc, Runbook, Plain explanation), Generate, then the Markdown appears in the panel with Copy and Download. It reads the outline form only.
- A short line at the bottom of the panel: "Generated content can be wrong. Check it before you share it."

## Keyboard and accessibility
- `Cmd/Ctrl+J` opens the panel with focus in the prompt; `Escape` in the prompt closes the panel if it is empty, otherwise clears focus to the canvas.
- Streaming output is an `aria-live="polite"` region updated per line, not per token, so screen readers are not flooded.
- Insert, Try again and Discard are buttons in a fixed footer so they are reachable while the preview scrolls; their shortcuts are shown and listed in the command palette.
- The ghost preview on the canvas is decorative and `aria-hidden`; the DSL preview text is the accessible form.
- The AI badge on nodes has an `aria-label` "Created by AI" and is also listed in the Provenance section, so it is never the only indicator.
- No motion: streaming appends text; the ghost fades in over 120ms or instantly under reduced motion.

## What Draughtsman deliberately won't do
- **No chat transcript as the primary surface** (Mural, Jasper, ClickUp). The panel is prompt, preview, decide. "Ask the diagram" is phase 2 and will be its own tab.
- **No tone, length or persona pickers** (ClickUp, X). Diagrams have kinds, not tones.
- **No model-name headline or model marketing** (Framer showing "Opus 4.8", Fabric). Provider is a small label and an admin concern.
- **No AI image, sticker or "magic" branding** (Canva, Semrush). The word used is "Generate".
- **No sending the canvas as an image** to a provider. The outline form is what is sent; images require a per-document opt-in (spec section 9) and that control is in Admin, not here.
- **No silent insertion.** Nothing reaches the document without Insert, and everything inserted is one undo step and one checkpoint.

## Open questions
- Should the ghost preview render on the canvas at all in M2, or is the DSL preview enough for the prototype? Default for the build: DSL preview only until layout in the sidecar is stable, then add the ghost.

---
2026-09-21 · Claude (fm/atlas-editor-shell crewmate)
