import { memo } from 'react';
import type { Edge, Point } from '../model/types.ts';
import type { EdgeKindStyle } from '../theme/diagramTheme.ts';
import { smoothCurvePath } from './curve.ts';
import { edgeLabelAt, measureLabelBox } from './labelEditing.ts';
import { markerId } from './markerIds.ts';
import { DASH_ARRAYS, fmt } from './shapes.tsx';
import { estimateTextWidth, LABEL_FONT_SIZE, type TextMeasurer } from './text.ts';

export interface EdgeViewProps {
  edge: Edge;
  points: Point[];
  style: EdgeKindStyle;
  /** Font family for the label. */
  font: string;
  /** Colour of the label's backing plate: the surface the edge is drawn on. */
  labelBackground: string;
  labelColor: string;
  idPrefix: string;
  /** Whether this render is an interaction surface; the invisible hit stroke is mounted only when it is. */
  interactive: boolean;
  measure?: TextMeasurer | undefined;
  /** Set when `points` is the router's (edgeRoutes.ts): `fallback` marks a route that could not avoid every node. */
  routeStatus?: 'routed' | 'fallback' | undefined;
  /** Leaves the label out while the edge label editor draws it instead. */
  hideLabel?: boolean | undefined;
  /** The label's plate centre, when `labelLayout.ts` has nudged it clear of another edge's; else `edgeLabelAt`'s point. */
  labelAt?: Point | undefined;
  /** Far zoomed out: a plain solid line, no arrowheads, dash or label (virtualise.ts `fullDetail`). */
  simplified?: boolean | undefined;
}

/**
 * Width, in device pixels, of the invisible stroke laid under an edge's visible line so it can be clicked without
 * landing on a pixel-perfect hit. `non-scaling-stroke` holds it there at every zoom, so the target stays the same
 * size to the hand whether the diagram is fitted to the window or magnified. Mirrors the interaction target size
 * tools of this kind converge on (Miro, Figma, Lucidchart all use roughly ten to twelve px); nothing else in this
 * codebase defines an interaction target, so this is the width to follow rather than introduce a second convention.
 */
export const EDGE_HIT_WIDTH = 12;

function EdgeLabel({ text, at, font, background, colour, measure }: { text: string; at: Point; font: string; background: string; colour: string; measure: TextMeasurer }) {
  const { width, height } = measureLabelBox(text, font, measure);
  return (
    <g className="edge-label" style={{ pointerEvents: 'none' }}>
      <rect x={fmt(at.x - width / 2)} y={fmt(at.y - height / 2)} width={fmt(width)} height={fmt(height)} rx={2} fill={background} />
      <text x={fmt(at.x)} y={fmt(at.y)} fill={colour} fontFamily={font} fontSize={LABEL_FONT_SIZE} textAnchor="middle" dominantBaseline="central" style={{ userSelect: 'none' }}>
        {text}
      </text>
    </g>
  );
}

/**
 * `labelAt` is the one prop `DiagramSvg`'s `placeLabels` recomputes into a fresh `{x, y}` object every time `drawn`
 * rebuilds - even for an edge whose label never moved - so the default (reference-equality) comparison `memo` would
 * use can never bail while any edge is labelled. Every other prop is a stable reference across such a rebuild unless
 * that edge's own data actually changed, so comparing `labelAt` by value and everything else by reference gives the
 * same correctness with the bailout `EdgeView`'s own memo comment already claims (checklist item 29).
 */
function edgeViewPropsEqual(prev: EdgeViewProps, next: EdgeViewProps): boolean {
  if (prev.edge !== next.edge || prev.points !== next.points || prev.style !== next.style || prev.font !== next.font) {
    return false;
  }
  if (prev.labelBackground !== next.labelBackground || prev.labelColor !== next.labelColor || prev.idPrefix !== next.idPrefix) {
    return false;
  }
  if (prev.interactive !== next.interactive || prev.measure !== next.measure || prev.routeStatus !== next.routeStatus) {
    return false;
  }
  if (prev.hideLabel !== next.hideLabel || prev.simplified !== next.simplified) {
    return false;
  }
  if (prev.labelAt === next.labelAt) {
    return true;
  }
  return prev.labelAt?.x === next.labelAt?.x && prev.labelAt?.y === next.labelAt?.y;
}

/**
 * One `<g data-id>` per edge: a polyline through its route with arrowhead markers and an optional label, and on an
 * interaction surface an invisible wide stroke on the same route beneath it, which is what a click lands on. Simplified,
 * the line loses its arrowheads, dash and label but keeps that hit stroke, so an edge stays clickable however far out.
 */
export const EdgeView = memo(function EdgeView({ edge, points, style, font, labelBackground, labelColor, idPrefix, interactive, measure, routeStatus, hideLabel = false, simplified = false, labelAt }: EdgeViewProps) {
  const start = simplified ? undefined : markerId(idPrefix, style.arrowStart, style.stroke);
  const end = simplified ? undefined : markerId(idPrefix, style.arrowEnd, style.stroke);
  const label = edge.label;
  const path = points.map((p) => `${String(fmt(p.x))},${String(fmt(p.y))}`).join(' ');
  // A curved connector draws a smooth spline through its endpoints and waypoints rather than the straight polyline
  // every other mode draws; the hit stroke and the visible line follow the same curve so a click lands on the line.
  const curved = edge.routing?.mode === 'curved';
  const curvePath = curved ? smoothCurvePath(points) : undefined;
  return (
    <g data-id={edge.id} data-kind={edge.kind} data-route={routeStatus} className="edge">
      {interactive &&
        (curvePath === undefined ? (
          <polyline
            className="hit-area"
            points={path}
            fill="none"
            stroke="transparent"
            strokeWidth={EDGE_HIT_WIDTH}
            strokeLinejoin="round"
            vectorEffect="non-scaling-stroke"
            style={{ pointerEvents: 'stroke' }}
          />
        ) : (
          <path
            className="hit-area"
            d={curvePath}
            fill="none"
            stroke="transparent"
            strokeWidth={EDGE_HIT_WIDTH}
            strokeLinejoin="round"
            vectorEffect="non-scaling-stroke"
            style={{ pointerEvents: 'stroke' }}
          />
        ))}
      {curvePath === undefined ? (
        <polyline
          points={path}
          fill="none"
          stroke={style.stroke}
          strokeWidth={style.strokeWidth}
          strokeDasharray={simplified ? undefined : DASH_ARRAYS[style.dash]}
          strokeLinejoin="round"
          markerStart={start === undefined ? undefined : `url(#${start})`}
          markerEnd={end === undefined ? undefined : `url(#${end})`}
        />
      ) : (
        <path
          d={curvePath}
          fill="none"
          stroke={style.stroke}
          strokeWidth={style.strokeWidth}
          strokeDasharray={simplified ? undefined : DASH_ARRAYS[style.dash]}
          strokeLinejoin="round"
          markerStart={start === undefined ? undefined : `url(#${start})`}
          markerEnd={end === undefined ? undefined : `url(#${end})`}
        />
      )}
      {!hideLabel && !simplified && label !== undefined && label.length > 0 && (
        <EdgeLabel text={label} at={labelAt ?? edgeLabelAt(points, curved)} font={font} background={labelBackground} colour={labelColor} measure={measure ?? estimateTextWidth} />
      )}
    </g>
  );
}, edgeViewPropsEqual);
