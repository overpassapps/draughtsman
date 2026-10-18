/**
 * Arrowhead markers. Markers cannot inherit the referencing line's stroke in every renderer that will consume the
 * exported SVG, so one marker is emitted per (arrowhead, colour) pair actually used by the mounted edges. Sizes are
 * in stroke widths (markerUnits) so a heavier edge gets a proportionally larger head.
 *
 * Every glyph is drawn so the line approaches from the left and the connector's end sits on the right edge of its
 * viewBox; `orient="auto-start-reverse"` turns it around for the source end, so the same path serves both directions.
 * The basic heads (arrow, open, diamond, circle) keep their 0 0 8 8 box; the ER crow's foot and UML ends need more
 * room for their toes and diamonds, so they draw in a 0 0 12 12 box with the same reference-point convention.
 */

import type { ReactElement } from 'react';
import { markerId, type MarkerKey } from './markerIds.ts';

function filled(d: string, colour: string): ReactElement {
  return <path d={d} fill={colour} />;
}

function hollow(d: string, colour: string): ReactElement {
  return <path d={d} fill="none" stroke={colour} strokeLinejoin="miter" />;
}

/** The marker attributes and inner element that draw one arrowhead. */
function spec(arrow: MarkerKey['arrow'], colour: string): { viewBox: string; refX: number; refY: number; markerWidth: number; markerHeight: number; orient: 'auto' | 'auto-start-reverse'; element: ReactElement } {
  switch (arrow) {
    case 'arrow':
      return { viewBox: '0 0 8 8', refX: 8, refY: 4, markerWidth: 8, markerHeight: 8, orient: 'auto-start-reverse', element: filled('M0,0 L8,4 L0,8 Z', colour) };
    case 'open':
      return { viewBox: '0 0 8 8', refX: 8, refY: 4, markerWidth: 8, markerHeight: 8, orient: 'auto-start-reverse', element: <path d="M0,0 L8,4 L0,8" fill="none" stroke={colour} strokeWidth={1} strokeLinejoin="miter" /> };
    case 'diamond':
      return { viewBox: '0 0 8 8', refX: 8, refY: 4, markerWidth: 8, markerHeight: 8, orient: 'auto-start-reverse', element: filled('M0,4 L4,0 L8,4 L4,8 Z', colour) };
    case 'circle':
      return { viewBox: '0 0 8 8', refX: 8, refY: 4, markerWidth: 8, markerHeight: 8, orient: 'auto-start-reverse', element: <circle cx={4} cy={4} r={3.5} fill={colour} /> };
    case 'one':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: hollow('M10,1 L10,11', colour) };
    case 'many':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: hollow('M3,6 L11,2 M3,6 L11,6 M3,6 L11,10', colour) };
    case 'zeroOrOne':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: (<>
        <circle cx={5} cy={6} r={2.5} fill="none" stroke={colour} />
        <path d="M10,1 L10,11" fill="none" stroke={colour} />
      </>) };
    case 'oneOrMany':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: (<>
        <path d="M10,1 L10,11" fill="none" stroke={colour} />
        <path d="M3,6 L11,2 M3,6 L11,6 M3,6 L11,10" fill="none" stroke={colour} />
      </>) };
    case 'zeroOrMany':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: (<>
        <circle cx={5} cy={6} r={2.5} fill="none" stroke={colour} />
        <path d="M3,6 L11,2 M3,6 L11,6 M3,6 L11,10" fill="none" stroke={colour} />
      </>) };
    case 'oneAndOnlyOne':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: hollow('M10,1 L10,11 M8,1 L8,11', colour) };
    case 'inheritance':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: <path d="M2,1 L12,6 L2,11 Z" fill="none" stroke={colour} strokeLinejoin="miter" /> };
    case 'aggregation':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: <path d="M5,6 L9,2 L13,6 L9,10 Z" fill="none" stroke={colour} strokeLinejoin="miter" /> };
    case 'composition':
      return { viewBox: '0 0 12 12', refX: 10, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse', element: filled('M5,6 L9,2 L13,6 L9,10 Z', colour) };
  }
}

export function ArrowMarker({ prefix, arrow, colour }: { prefix: string } & MarkerKey) {
  const { viewBox, refX, refY, markerWidth, markerHeight, orient, element } = spec(arrow, colour);
  return (
    <marker
      id={markerId(prefix, arrow, colour)}
      viewBox={viewBox}
      markerWidth={markerWidth}
      markerHeight={markerHeight}
      refX={refX}
      refY={refY}
      orient={orient}
      markerUnits="strokeWidth"
    >
      {element}
    </marker>
  );
}