/**
 * The words the editor uses for another writer's change merged into the open document (checklist item 30): a brief
 * announcement when it merged cleanly, and a standing notice naming each clash with the user's unsaved edits. Plain
 * British English, what happened and then what to do (design/atlas/empty-loading-error-states.md, copy rules).
 */

import type { MergeConflict } from '../model/remoteMerge.ts';
import type { Document } from '../model/types.ts';

/** The announcement for a change that merged without touching anything the user had edited. */
export function appliedNotice(by: string | undefined): string {
  return by === undefined ? 'Changes saved elsewhere are now shown.' : `Changes from ${by} are now shown.`;
}

/** The standing notice for clashes: what clashed, which side is on screen, and what a save does with it. */
export function conflictNotice(conflicts: readonly MergeConflict[], document: Document, by: string | undefined): string {
  const who = by ?? 'another writer';
  const items = conflicts.map((conflict) => describe(conflict, document));
  return `You and ${who} both changed ${items.join('; ')}. Your version is shown, and saving keeps it.`;
}

function describe(conflict: MergeConflict, document: Document): string {
  if (conflict.kind === 'document') {
    return conflict.reason === 'order' ? 'the stacking order' : `the document ${conflict.fields.join(' and ')}`;
  }
  const name = nameOf(conflict, document);
  switch (conflict.reason) {
    case 'both-changed':
      return `${name} (${conflict.fields.join(', ')})`;
    case 'deleted-remotely':
      return `${name} (removed there, still here)`;
    case 'deleted-locally':
      return `${name} (removed here, edited there)`;
    case 'order':
      return name;
  }
}

/** A node by its label when it has one on screen, otherwise any element by its id. */
function nameOf(conflict: MergeConflict, document: Document): string {
  if (conflict.kind === 'node') {
    const label = document.nodes.find((node) => node.id === conflict.id)?.label;
    if (label !== undefined && label.trim().length > 0) {
      return `“${label}”`;
    }
  }
  return `${conflict.kind} ${conflict.id}`;
}
