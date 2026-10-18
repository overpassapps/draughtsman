/**
 * The drop-target highlight (checklist item 27): while a node is dragged, the container it would drop into on release
 * is outlined, so the user can see whether letting go puts the node inside it. Drawn in screen space like the
 * selection overlay, around the container's visible (clipped) bounds, and never takes a press. View state only.
 */

import type { Id } from '../model/types.ts';
import type { DocumentIndex } from './documentIndex.ts';
import { fmt } from './shapes.tsx';
import { worldToScreen, type Viewport } from './viewport.ts';

export interface DropTargetOverlayProps {
  targets: ReadonlySet<Id>;
  index: DocumentIndex;
  viewport: Viewport;
}

export function DropTargetOverlay({ targets, index, viewport }: DropTargetOverlayProps) {
  const boxes = [...targets].flatMap((id) => {
    const bounds = index.nodeBounds.get(id);
    return bounds === undefined ? [] : [{ id, origin: worldToScreen(viewport, bounds), w: bounds.w * viewport.zoom, h: bounds.h * viewport.zoom }];
  });
  if (boxes.length === 0) {
    return null;
  }
  return (
    <g className="drop-targets">
      {boxes.map(({ id, origin, w, h }) => (
        <rect key={id} className="drop-target" data-drop-target={id} x={fmt(origin.x)} y={fmt(origin.y)} width={fmt(w)} height={fmt(h)} />
      ))}
    </g>
  );
}
