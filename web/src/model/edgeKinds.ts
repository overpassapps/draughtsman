/**
 * The edge kinds the properties panel offers for a document (design/atlas/properties-panel.md, Connector section;
 * checklist item 12). Each document type offers its vocabulary's edge kinds, in the vocabulary's order, from
 * src/Draughtsman.Core/Schemas/<type>.json, which edgeKinds.test.ts checks this table against. Freeform has no
 * vocabulary, so it offers the kinds the diagram theme styles. A kind already on the edge that the list does not name
 * is kept and offered too, flagged as unknown, never silently replaced: unknown kinds are allowed and lint flags them.
 */

import type { DocumentType } from './types.ts';

/** Each vocabulary's edge kinds, in the order the vocabulary lists them. Freeform has none. */
export const EDGE_KINDS: Readonly<Record<DocumentType, readonly string[]>> = {
  architecture: ['flow', 'data', 'dependency', 'association', 'dotted-report'],
  flowchart: ['flow', 'association', 'message-flow', 'bpmn-association', 'data-association'],
  freeform: [],
  sequence: ['sync', 'async', 'return', 'note', 'interaction-use', 'create', 'destroy'],
};

/**
 * Where a vocabulary lists more kinds than a user picks from a connector's panel, the ones it offers. A sequence diagram's
 * `note`, `interaction-use`, `create` and `destroy` are rows made by their own commands (or the DSL and the Mermaid
 * importer), not a message's kind to switch to: the panel offers sync, async and return, and shows a row's own special
 * kind (unflagged, since it is in the vocabulary) only while that row is selected.
 */
const PICKABLE: Readonly<Partial<Record<DocumentType, readonly string[]>>> = {
  sequence: ['sync', 'async', 'return'],
};

export interface EdgeKindOption {
  kind: string;
  /** True for the edge's own kind when the list does not otherwise name it. */
  unknown: boolean;
}

/**
 * The options for an edge of kind `current` in a document of `type`. `themedKinds` are the edge kinds the diagram
 * theme styles, excluding its `default` entry; they are the list only where the type has no vocabulary.
 */
export function edgeKindOptions(type: DocumentType, themedKinds: readonly string[], current: string): EdgeKindOption[] {
  const vocabulary = EDGE_KINDS[type].length > 0 ? EDGE_KINDS[type] : themedKinds;
  const listed = PICKABLE[type] ?? vocabulary;
  const options = listed.map((kind) => ({ kind, unknown: false }));
  if (listed.includes(current)) {
    return options;
  }
  return [...options, { kind: current, unknown: !vocabulary.includes(current) }];
}
