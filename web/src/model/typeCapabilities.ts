/**
 * What the editor may do to a document of each type. One flag today: `derivedGeometry`, true for `sequence`, whose
 * positions are computed from element order on every render (model/sequenceScene.ts) and so have nothing to write.
 * With it set the canvas offers no resize, rotate, free move, snapping, connection handles from ports, segment or
 * waypoint drags, re-attach or curved routing (design/atlas/sequence-editor.md), and the Yjs geometry and routing
 * writers in editor.ts refuse the document, so a stray call cannot store a position the scene would ignore.
 */

import type { DocumentType } from './types.ts';

export interface TypeCapabilities {
  /** Positions are derived from element order, never stored: free geometry and routing edits do not apply. */
  derivedGeometry: boolean;
}

export function typeCapabilities(type: DocumentType | undefined): TypeCapabilities {
  return { derivedGeometry: type === 'sequence' };
}
