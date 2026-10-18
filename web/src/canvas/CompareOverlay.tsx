/**
 * The marks of a version comparison (design/atlas/version-history.md): elements the document gained since the compared
 * version outlined solid, changed ones dashed, and removed nodes dotted at the place the old version had them, each
 * with a +, ~ or - badge so the meaning never rests on colour alone. Drawn in screen space like the selection overlay,
 * above the diagram, and never takes a press. View state only: nothing here is saved, selected or undone.
 */

import type { CompareMarks } from '../model/compare.ts';
import type { Id, Point } from '../model/types.ts';
import type { DocumentIndex } from './documentIndex.ts';
import { fmt } from './shapes.tsx';
import { worldToScreen, type Viewport } from './viewport.ts';

export interface CompareOverlayProps {
  marks: CompareMarks;
  index: DocumentIndex;
  viewport: Viewport;
}

const BADGE = 16;

function Badge({ x, y, symbol }: { x: number; y: number; symbol: string }) {
  return (
    <g className="compare-badge" transform={`translate(${String(fmt(x))} ${String(fmt(y))})`}>
      <rect width={BADGE} height={BADGE} rx={3} />
      <text x={BADGE / 2} y={BADGE / 2 + 4} textAnchor="middle">{symbol}</text>
    </g>
  );
}

export function CompareOverlay({ marks, index, viewport }: CompareOverlayProps) {
  const boxes: { id: Id; status: 'added' | 'changed'; x: number; y: number; w: number; h: number }[] = [];
  const lines: { id: Id; status: 'added' | 'changed'; points: Point[] }[] = [];
  for (const [status, ids] of [['added', marks.added], ['changed', marks.changed]] as const) {
    for (const id of ids) {
      const bounds = index.nodeBounds.get(id);
      if (bounds !== undefined) {
        const origin = worldToScreen(viewport, bounds);
        boxes.push({ id, status, x: origin.x, y: origin.y, w: bounds.w * viewport.zoom, h: bounds.h * viewport.zoom });
        continue;
      }
      const path = index.edgePaths.get(id);
      if (path !== undefined) {
        lines.push({ id, status, points: path.points.map((point) => worldToScreen(viewport, point)) });
      }
    }
  }
  const gone = marks.removed.map(({ id, geometry }) => {
    const origin = worldToScreen(viewport, geometry);
    return { id, x: origin.x, y: origin.y, w: geometry.w * viewport.zoom, h: geometry.h * viewport.zoom };
  });
  if (boxes.length === 0 && lines.length === 0 && gone.length === 0) {
    return null;
  }
  const symbol = { added: '+', changed: '~' } as const;
  return (
    <g className="compare-marks" pointerEvents="none" aria-hidden="true">
      {gone.map(({ id, x, y, w, h }) => (
        <g key={`removed:${id}`} data-compare="removed" data-compare-id={id}>
          <rect className="compare-mark compare-removed" x={fmt(x)} y={fmt(y)} width={fmt(w)} height={fmt(h)} />
          <Badge x={x - 2} y={y - BADGE + 2} symbol="−" />
        </g>
      ))}
      {lines.map(({ id, status, points }) => (
        <polyline key={`${status}:${id}`} className={`compare-mark compare-${status} compare-line`} data-compare={status} data-compare-id={id} fill="none" points={points.map((point) => `${String(fmt(point.x))},${String(fmt(point.y))}`).join(' ')} />
      ))}
      {boxes.map(({ id, status, x, y, w, h }) => (
        <g key={`${status}:${id}`} data-compare={status} data-compare-id={id}>
          <rect className={`compare-mark compare-${status}`} x={fmt(x)} y={fmt(y)} width={fmt(w)} height={fmt(h)} />
          <Badge x={x - 2} y={y - BADGE + 2} symbol={symbol[status]} />
        </g>
      ))}
    </g>
  );
}
