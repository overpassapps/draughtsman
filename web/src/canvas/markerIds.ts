/** Ids and keys for arrowhead markers; see markers.tsx for the elements. */

import type { ArrowHead } from '../model/types.ts';

export interface MarkerKey {
  arrow: Exclude<ArrowHead, 'none'>;
  colour: string;
}

export function markerId(prefix: string, arrow: ArrowHead, colour: string): string | undefined {
  if (arrow === 'none') {
    return undefined;
  }
  return `${prefix}arrow-${arrow}-${colour.replace(/[^A-Za-z0-9]/g, '_')}`;
}

export function markerKey(arrow: ArrowHead, colour: string): string {
  return `${arrow}|${colour}`;
}
