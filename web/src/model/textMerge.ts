/**
 * Applies text a person edited from an older version of the diagram onto the diagram as it is now: the Text pane's
 * "keep mine" path. `applyDocumentDiff` is the exact path (it refuses unless the diagram is still what the text was
 * made from); this is the three-way one, for when a collaborator, an agent or the canvas changed the diagram while the
 * text was being typed. It reuses `remoteMerge`, with the roles turned round: the Y.Doc is the diagram as it is now, the
 * text's parse is the "remote", and the text it started from is the base. So a field only the text changed takes the
 * text's value, a field only the diagram changed keeps the diagram's, and a field both changed differently keeps the
 * diagram's and is reported, never silently dropped (`takeMine` is how the person asks for theirs instead).
 *
 * Both are one local write: a single transaction under LOCAL_ORIGIN, so one undo step once the caller bounds it. The
 * diagram's `meta` belongs to the server and is never taken from text.
 */

import * as Y from 'yjs';
import { HEADER_MAP, LOCAL_ORIGIN, yDocToDocument } from './editor.ts';
import { parseDocument } from './parse.ts';
import { mergeRemote, takeRemote, type MergeConflict } from './remoteMerge.ts';
import type { Document } from './types.ts';

export interface TextMergeResult {
  /** Whether anything was written to the Y.Doc. */
  changed: boolean;
  /** What the text and the diagram both changed, or removed on one side and edited on the other; the diagram's side stands. */
  conflicts: MergeConflict[];
}

function checkText(mine: Document, base: Document): void {
  parseDocument(mine);
  if (mine.id !== base.id) {
    throw new Error('Keep the document id and format version unchanged when applying text.');
  }
  const ids = [...mine.nodes, ...mine.edges].map((element) => element.id);
  if (new Set(ids).size !== ids.length) {
    throw new Error('Every node and edge needs a unique id.');
  }
}

/** Merges `mine` (the parsed text) into the diagram, as a change from `base` (the diagram the text was made from). */
export function mergeTextEdit(doc: Y.Doc, base: Document, mine: Document): TextMergeResult {
  checkText(mine, base);
  const current = yDocToDocument(doc);
  const conflicts: MergeConflict[] = [];
  let changed = false;
  doc.transact(() => {
    const result = mergeRemote(doc, base, { ...mine, meta: current.meta }, LOCAL_ORIGIN);
    changed = result.changed;
    conflicts.push(...result.conflicts);
    // The type is a header field the live-sync merge does not carry (a remote writer never retypes a diagram), but text can.
    if (mine.type !== base.type) {
      if (current.type === base.type) {
        doc.getMap<unknown>(HEADER_MAP).set('type', mine.type);
        changed = true;
      } else if (current.type !== mine.type) {
        conflicts.push({ kind: 'document', id: mine.id, reason: 'both-changed', fields: ['type'] });
      }
    }
  }, LOCAL_ORIGIN);
  return { changed, conflicts };
}

/**
 * Takes the text's side of each conflict a `mergeTextEdit` reported, which is `takeRemote` with the same roles turned
 * round (a second local write, so a second undo step; a clash on `type` is a header field it writes like any other).
 */
export function takeMine(doc: Y.Doc, mine: Document, conflicts: readonly MergeConflict[]): void {
  takeRemote(doc, mine, conflicts, LOCAL_ORIGIN);
}

/** What clashed, in the words the Text pane shows: the element by its label when it has one, and which side removed it. */
export function describeTextConflicts(conflicts: readonly MergeConflict[], document: Document): string[] {
  return conflicts.map((conflict) => {
    if (conflict.kind === 'document') {
      return conflict.reason === 'order' ? 'the stacking order' : `the diagram ${conflict.fields.join(' and ')}`;
    }
    const label = conflict.kind === 'node' ? document.nodes.find((node) => node.id === conflict.id)?.label : undefined;
    const name = label !== undefined && label.trim().length > 0 ? `“${label}” (${conflict.id})` : `${conflict.kind} ${conflict.id}`;
    switch (conflict.reason) {
      case 'both-changed':
        return `${name}: ${conflict.fields.join(', ')}`;
      case 'deleted-remotely':
        return `${name}: your text removed it, but it was changed on the diagram`;
      case 'deleted-locally':
        return `${name}: it was removed from the diagram, but your text changed it`;
      case 'order':
        return `${name}: stacking order`;
    }
  });
}
