/**
 * The document types a new diagram can be, and the shared "create and open" both the home screen's New diagram menu
 * and the editor's File → New diagram menu use (design/atlas/document-list.md, "New document … opening a small menu").
 * Each type names the document model's own vocabulary (web/src/model/types.ts `DocumentType`); the blank document the
 * server fills in is the same one creating anywhere else produces, with no nodes and the type's default layout.
 */

import { createDocument } from '../model/documentClient.ts';
import { DIAGRAM_TEMPLATES } from './diagramTemplates.ts';
import type { DocumentType, Edge, Node } from '../model/types.ts';
import { navigate } from './router.ts';

export interface DiagramTypeOption {
  type: DocumentType;
  label: string;
  /** Nodes and edges a new diagram of this type opens with; absent means blank. */
  starter?: { nodes: Node[]; edges: Edge[] };
}

/**
 * A sequence diagram derives its geometry (docs/sequence-diagrams.md), so a blank one would be an empty canvas with
 * nothing to point at: it opens with a person, a system and the call between them, in the shapes sequence.json's kinds
 * default to. No geometry or routing is written.
 */
export const SEQUENCE_STARTER: { nodes: Node[]; edges: Edge[] } = {
  nodes: [
    { id: 'p_user', kind: 'actor', label: 'User', shape: 'rounded' },
    { id: 'p_system', kind: 'participant', label: 'System', shape: 'rectangle' },
  ],
  edges: [{ id: 'm_1', kind: 'sync', label: 'Request', from: { node: 'p_user' }, to: { node: 'p_system' } }],
};

/** The types the model supports, in the atlas's order. */
export const DIAGRAM_TYPES: readonly DiagramTypeOption[] = [
  { type: 'flowchart', label: 'Flowchart' },
  { type: 'architecture', label: 'Architecture' },
  { type: 'freeform', label: 'Freeform' },
  { type: 'sequence', label: 'Sequence', starter: SEQUENCE_STARTER },
];

/** A new document's default title, keyed by its type so two fresh diagrams are told apart in the list. */
export function defaultDiagramTitle(type: DocumentType): string {
  const option = DIAGRAM_TYPES.find((candidate) => candidate.type === type);
  return `Untitled ${option?.label.toLowerCase() ?? 'diagram'}`;
}

// Ids just created by createAndOpen, so the editor they navigate to can open with the title already active and
// selected (Lucidchart-style rename, design/atlas/document-list.md): the default title is a placeholder to replace,
// not one worth a round trip through a route query parameter.
const pendingTitleEdit = new Set<string>();

// Ids created with a right-hand panel to open on arrival: the home screen's "From text" card opens the AI panel.
const pendingPanel = new Set<string>();

/** Creates a blank diagram of `type` and navigates to it in the editor. The home screen and the File menu share this. `openAi` lands on the AI panel. */
export async function createAndOpen(type: DocumentType, options: { openAi?: boolean; template?: string } = {}): Promise<void> {
  // A "More diagram types" template (diagramTemplates.ts) is a document of an existing type that begins with its shapes.
  const template = options.template === undefined ? undefined : DIAGRAM_TEMPLATES.find((candidate) => candidate.id === options.template);
  const starter = template === undefined ? DIAGRAM_TYPES.find((option) => option.type === type)?.starter : { nodes: template.nodes(), edges: template.edges() };
  const id = await createDocument(template?.title ?? defaultDiagramTitle(type), template?.type ?? type, starter);
  if (options.openAi === true) {
    pendingPanel.add(id);
  } else {
    pendingTitleEdit.add(id);
  }
  // Forced: a caller inside the editor has already asked about unsaved edits (guardedLeave) before creating anything.
  navigate(`/d/${encodeURIComponent(id)}`, { force: true });
}

/** Consumes whether `id` was just created by createAndOpen: true at most once, for the editor's first render of it. */
export function consumePendingTitleEdit(id: string): boolean {
  return pendingTitleEdit.delete(id);
}

/** Consumes whether `id` was created to open on the AI panel: true at most once, for the editor's first render of it. */
export function consumePendingAiPanel(id: string): boolean {
  return pendingPanel.delete(id);
}
