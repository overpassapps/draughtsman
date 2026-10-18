/**
 * What a gesture in progress draws over the diagram: the smart guides of a snapped drag or resize (checklist items 5,
 * 6 and 7) and the marquee box (item 2). Drawn in screen space like the selection overlay, so a guide stays one pixel
 * wide at every zoom, and with no pointer events, so it never takes a press. View state only, and gone on release.
 */

import type { Point } from '../model/types.ts';
import type { Rect } from './geometry.ts';
import { fmt } from './shapes.tsx';
import type { Guide } from './snapping.ts';
import { worldToScreen, type Viewport } from './viewport.ts';

export interface SnapGuidesProps {
  guides: readonly Guide[];
  viewport: Viewport;
}

/** Length of the end ticks on an equal-spacing guide, in screen pixels. */
const SPACING_TICK = 4;

function segment(from: Point, to: Point, className: string, key: string) {
  return <line key={key} className={className} x1={fmt(from.x)} y1={fmt(from.y)} x2={fmt(to.x)} y2={fmt(to.y)} />;
}

export function SnapGuides({ guides, viewport }: SnapGuidesProps) {
  if (guides.length === 0) {
    return null;
  }
  return (
    <g className="snap-guides">
      {guides.map((guide, i) => {
        const from = worldToScreen(viewport, guide.from);
        const to = worldToScreen(viewport, guide.to);
        if (guide.kind !== 'equal-spacing') {
          return (
            <g key={i} className={`snap-guide snap-guide-${guide.kind}`} data-guide={guide.kind}>
              {segment(from, to, 'snap-guide-line', 'line')}
            </g>
          );
        }
        // The gap the node matched, marked with a tick at each end so it reads as a measurement, not an alignment.
        const vertical = from.x === to.x;
        const tick = (p: Point, key: string) =>
          vertical ? segment({ x: p.x - SPACING_TICK, y: p.y }, { x: p.x + SPACING_TICK, y: p.y }, 'snap-guide-line', key) : segment({ x: p.x, y: p.y - SPACING_TICK }, { x: p.x, y: p.y + SPACING_TICK }, 'snap-guide-line', key);
        return (
          <g key={i} className="snap-guide snap-guide-equal-spacing" data-guide={guide.kind}>
            {segment(from, to, 'snap-guide-line', 'line')}
            {tick(from, 'from')}
            {tick(to, 'to')}
          </g>
        );
      })}
    </g>
  );
}

export interface MarqueeBoxProps {
  /** The marquee in world units. */
  rect: Rect;
  viewport: Viewport;
}

export function MarqueeBox({ rect, viewport }: MarqueeBoxProps) {
  const origin = worldToScreen(viewport, { x: rect.x, y: rect.y });
  return <rect className="marquee" x={fmt(origin.x)} y={fmt(origin.y)} width={fmt(rect.w * viewport.zoom)} height={fmt(rect.h * viewport.zoom)} />;
}
