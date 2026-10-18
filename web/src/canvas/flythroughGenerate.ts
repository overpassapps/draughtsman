/**
 * A first tour from the diagram's own structure, for the Tour panel's "Generate from containers" (docs/flythrough.md):
 * an overview of everything, then one shot of each top-level container in document order, then one shot of whatever
 * sits outside every container. Each shot carries the container's label as its caption. A pure function of the
 * document index, so it frames what is drawn (rotation and clipping included) and never reads the viewport.
 *
 * The result is ordinary keyframes the author can reorder, caption and delete: generating only saves the first ten
 * clicks. A diagram with no container has no structure to walk, so it generates nothing rather than one shot per node.
 */

import type { Document, ViewRect } from '../model/types.ts';
import { STATE_COMPOSITE_KIND } from '../model/stateShapes.ts';
import { isSwimlaneKind } from '../model/swimlaneKinds.ts';
import { MAX_KEYFRAMES } from '../model/flythrough.ts';
import { isContainer, type DocumentIndex } from './documentIndex.ts';
import { padRect, unionRects, type Rect } from './geometry.ts';

/** World units of air around every generated shot, so a container's border is not on the window's edge. */
export const SHOT_PADDING = 48;

export interface GeneratedShot {
  view: ViewRect;
  caption: string;
}

function shot(rect: Rect, caption: string): GeneratedShot {
  const padded = padRect(rect, SHOT_PADDING);
  return { view: { x: padded.x, y: padded.y, w: padded.w, h: padded.h }, caption };
}

/** The shots, in tour order; empty when the diagram has no top-level container with an area. */
export function shotsFromContainers(index: DocumentIndex): GeneratedShot[] {
  const containers = index.rootNodes.filter((node) => isContainer(index, node) && index.nodeBounds.get(node.id) !== undefined);
  const overview = index.contentBounds;
  if (containers.length === 0 || overview === undefined) {
    return [];
  }
  const shots: GeneratedShot[] = [shot(overview, 'Overview')];
  for (const container of containers) {
    const bounds = index.nodeBounds.get(container.id);
    if (bounds !== undefined && bounds.w > 0 && bounds.h > 0) {
      shots.push(shot(bounds, container.label.trim() === '' ? 'Group' : container.label.trim()));
    }
  }
  const loose = unionRects(
    index.rootNodes.flatMap((node) => {
      const bounds = index.nodeBounds.get(node.id);
      return isContainer(index, node) || bounds === undefined || bounds.w <= 0 || bounds.h <= 0 ? [] : [bounds];
    }),
  );
  if (loose !== undefined) {
    shots.push(shot(loose, 'Everything else'));
  }
  return shots.slice(0, MAX_KEYFRAMES);
}

/**
 * A cheap scan for whether the document has anything the shots could be made of: a node of a container kind, or one with a
 * parent (so some node has children). It reads the nodes only, so the Tour panel can keep its Generate button current on
 * every edit without building an index; `shotsFromContainers` is what generation actually asks, on the click.
 */
export function mayHaveContainers(document: Pick<Document, 'nodes'>): boolean {
  return document.nodes.some((node) => node.parent !== undefined || node.kind === 'container' || node.kind === STATE_COMPOSITE_KIND || isSwimlaneKind(node.kind));
}

/** Whether "Generate from containers" has anything to generate. */
export function canGenerateFromContainers(index: DocumentIndex): boolean {
  return shotsFromContainers(index).length > 0;
}
