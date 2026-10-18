/**
 * Shape primitives. Each shape is drawn in its node's local space (origin at the node's top-left, no rotation) and
 * reports the box its label should be laid out in. Every colour and width comes from the resolved style; nothing
 * here names a colour.
 */

import { createElement, type ReactElement } from 'react';
import { basicShape, resolveBox, resolvePath, type BasicShape } from '../model/basicShapes.ts';
import { BPMN_GLYPHS, BPMN_LAYOUT, BPMN_VIEWBOX, bpmnBracket, bpmnShape, type BpmnGlyphElement, type BpmnShape } from '../model/bpmnShapes.ts';
import { GENERIC_ICON_VIEWBOX, ICON_LABEL_BAND, ICON_SIZE_RATIO, iconForShape, type GenericIcon, type GenericIconElement } from '../model/icons.ts';
import { orgShape, type OrgShape } from '../model/orgShapes.ts';
import { ACTOR_SHAPE, SEQUENCE_METRICS } from '../model/sequenceScene.ts';
import { STATE_SHAPE_PREFIX } from '../model/stateShapes.ts';
import { laneHeaderHeight, poolHeaderHeight, LANE_SHAPE, POOL_SHAPE } from '../model/swimlanes.ts';
import type { Dash, Props } from '../model/types.ts';
import { ER_METRICS, erShape, keyText, layoutEntity, nameRow as entityNameRow, nullText, type ErShape } from '../model/erShapes.ts';
import type { ShapePart } from '../model/memberParts.ts';
import { layoutClass, nameRow, symbolBandHeight, umlShape, UML_METRICS, type UmlMember, type UmlPart, type UmlShape } from '../model/umlShapes.ts';
import type { NodeKindStyle } from '../theme/diagramTheme.ts';
import type { Rect } from './geometry.ts';

export interface ShapeRender {
  element: ReactElement | null;
  /** Where the label goes, in the node's local space. */
  labelBox: Rect;
  /**
   * True for a shape that draws no label of its own (the state pseudostates: an initial dot or a fork bar with a name
   * through it reads as noise). The node's own label is kept in the model, just not drawn.
   */
  hideLabel?: boolean;
}

/**
 * What a shape may need from its node beyond its size: a UML class or an ER entity draws its own name and rows, which
 * live in the node's label and props. `hide` leaves one part undrawn while the editor covers it (`all` while labels are
 * not legible at the current zoom). Absent, such a shape draws as a proportional glyph with no text (the palette's
 * thumbnail).
 */
export interface ShapeContent {
  label: string;
  props?: Props | undefined;
  hide?: ShapePart | 'all' | undefined;
}

export const DASH_ARRAYS: Record<Dash, string | undefined> = { solid: undefined, dashed: '6 4', dotted: '2 3' };

export const ROUNDED_RADIUS = 8;

/** Rounds to two decimals so attribute strings stay short and snapshots stable. */
export function fmt(value: number): number {
  return Math.round(value * 100) / 100;
}

export function points(list: [number, number][]): string {
  return list.map(([x, y]) => `${String(fmt(x))},${String(fmt(y))}`).join(' ');
}

/**
 * The wavy-bottom page outline (ISO 5807 document), in the node's local space. One step of `multi-document`'s
 * stack and the whole of `document`; the caller chooses the page's position and size.
 */
function documentOutline(x: number, y: number, w: number, h: number): string {
  return `M${String(fmt(x))},${String(fmt(y))} H${String(fmt(x + w))} V${String(fmt(y + h * 0.85))} C${String(fmt(x + w * 0.75))},${String(fmt(y + h * 0.55))} ${String(fmt(x + w * 0.25))},${String(fmt(y + h * 1.15))} ${String(fmt(x))},${String(fmt(y + h * 0.85))} Z`;
}

interface Paint {
  fill: string;
  stroke: string;
  strokeWidth: number;
  strokeDasharray?: string | undefined;
}

export function paintOf(style: NodeKindStyle): Paint {
  return { fill: style.fill, stroke: style.stroke, strokeWidth: style.strokeWidth, strokeDasharray: DASH_ARRAYS[style.dash] };
}


/** One SVG primitive of a generic icon, in the library's own viewBox space; colour and width are set by the group. */
function iconElement(el: GenericIconElement, key: number): ReactElement {
  switch (el.tag) {
    case 'path':
      return <path key={key} d={el.d ?? ''} />;
    case 'rect':
      return <rect key={key} x={fmt(el.x ?? 0)} y={fmt(el.y ?? 0)} width={fmt(el.width ?? 0)} height={fmt(el.height ?? 0)} rx={el.rx === undefined ? undefined : fmt(el.rx)} />;
    case 'circle':
      return <circle key={key} cx={fmt(el.cx ?? 0)} cy={fmt(el.cy ?? 0)} r={fmt(el.r ?? 0)} />;
    case 'ellipse':
      return <ellipse key={key} cx={fmt(el.cx ?? 0)} cy={fmt(el.cy ?? 0)} rx={fmt(el.rx ?? 0)} ry={fmt(el.ry ?? 0)} />;
    case 'line':
      return <line key={key} x1={fmt(el.x1 ?? 0)} y1={fmt(el.y1 ?? 0)} x2={fmt(el.x2 ?? 0)} y2={fmt(el.y2 ?? 0)} />;
    case 'polygon':
      return <polygon key={key} points={el.points ?? ''} />;
    case 'polyline':
      return <polyline key={key} points={el.points ?? ''} />;
  }
}

/**
 * A generic icon node: the icon drawn as line art in a square at the top of the box, its label below it (the shared
 * labelBox, so NodeView's label machinery places and wraps it as for any node). Stroke and text colour come from the
 * resolved style, so the icon re-skins with the theme; the stroke width is scaled back out of the icon's viewBox so
 * one theme stroke width reads the same on every icon. A transparent hit rectangle covers the box, because the line
 * art itself is a thin target.
 */
function iconRender(icon: GenericIcon, w: number, h: number, style: NodeKindStyle): ShapeRender {
  const bandY = h * ICON_LABEL_BAND;
  const iconSide = Math.min(w, bandY) * ICON_SIZE_RATIO;
  const ix = (w - iconSide) / 2;
  const iy = (bandY - iconSide) / 2;
  const scale = iconSide / GENERIC_ICON_VIEWBOX;
  return {
    element: (
      <>
        <rect className="icon-hit" width={fmt(w)} height={fmt(h)} fill="transparent" stroke="none" />
        <g className="icon" transform={`translate(${String(fmt(ix))} ${String(fmt(iy))}) scale(${String(fmt(scale))})`} fill="none" stroke={style.stroke} strokeWidth={fmt(style.strokeWidth / scale)} strokeLinecap="round" strokeLinejoin="round">
          {icon.elements.map((el, i) => iconElement(el, i))}
        </g>
      </>
    ),
    labelBox: { x: 0, y: bandY, w, h: h - bandY },
  };
}

/** A boundary icon's corner glyph (a fraction of the shorter side, capped so it stays a corner mark on a large box). */
const BOUNDARY_ICON_RATIO = 0.22;
const BOUNDARY_ICON_MAX = 32;
const BOUNDARY_ICON_INSET = 8;

/**
 * A boundary icon (VPC, subnet, availability zone): the whole box drawn as the container's labelled outline, with the
 * icon drawn small in the top-right corner. NodeView places the label top-left because the node's kind is `container`
 * (documentIndex.ts `isContainer`), so the labelBox is the full box and the corner glyph never sits under the text.
 */
function boundaryRender(icon: GenericIcon, w: number, h: number, style: NodeKindStyle, paint: Paint): ShapeRender {
  const side = Math.min(Math.min(w, h) * BOUNDARY_ICON_RATIO, BOUNDARY_ICON_MAX);
  const ix = w - side - BOUNDARY_ICON_INSET;
  const iy = BOUNDARY_ICON_INSET;
  const scale = side / GENERIC_ICON_VIEWBOX;
  return {
    element: (
      <>
        <rect className="shape" width={fmt(w)} height={fmt(h)} {...paint} />
        <rect className="icon-hit" width={fmt(w)} height={fmt(h)} fill="transparent" stroke="none" />
        <g className="icon" transform={`translate(${String(fmt(ix))} ${String(fmt(iy))}) scale(${String(fmt(scale))})`} fill="none" stroke={style.stroke} strokeWidth={fmt(style.strokeWidth / scale)} strokeLinecap="round" strokeLinejoin="round">
          {icon.elements.map((el, i) => iconElement(el, i))}
        </g>
      </>
    ),
    labelBox: { x: 0, y: 0, w, h },
  };
}

/**
 * The UML state-machine shapes (model/stateShapes.ts), drawn natively in the node's local space. They mirror
 * SvgRenderer.cs shape for shape: a pseudostate's glyph is a fraction of the node's shorter side centred in the box,
 * the fork/join bar spans the width with a thickness capped so it stays a bar, and the composite state is a rounded
 * boundary with a name compartment. Every pseudostate hides its label (its own name would sit on the glyph); only the
 * composite state shows one, in the top compartment.
 */
const STATE_GLYPH_MIN = { initial: 0.22, finalRing: 0.3, finalDot: 0.15, history: 0.28, point: 0.24 } as const;
const STATE_H_FONT = 0.34;
const STATE_BAR = { fraction: 0.16, max: 14 } as const;
const STATE_NAME_BAND = { fraction: 0.35, max: 28 } as const;

/** The thickness of the fork/join bar, so it stays a bar however tall the node is resized. */
export function stateBarThickness(h: number): number {
  return Math.min(h * STATE_BAR.fraction, STATE_BAR.max);
}

/** The height of a composite state's name compartment, where the label sits. */
export function stateNameBand(h: number): number {
  return Math.min(h * STATE_NAME_BAND.fraction, STATE_NAME_BAND.max);
}

function stateShapeRender(shape: string, w: number, h: number, style: NodeKindStyle): ShapeRender | undefined {
  if (!shape.startsWith(STATE_SHAPE_PREFIX)) {
    return undefined;
  }
  const full: Rect = { x: 0, y: 0, w, h };
  const cx = w / 2;
  const cy = h / 2;
  const min = Math.min(w, h);
  const paint = paintOf(style);
  const solid = { fill: style.textColor, stroke: 'none' as const };
  const name = shape.slice(STATE_SHAPE_PREFIX.length);

  switch (name) {
    case 'initial':
      return { element: <circle className="shape" cx={fmt(cx)} cy={fmt(cy)} r={fmt(min * STATE_GLYPH_MIN.initial)} {...solid} />, labelBox: full, hideLabel: true };
    case 'final':
      return {
        element: (
          <>
            <circle className="shape" cx={fmt(cx)} cy={fmt(cy)} r={fmt(min * STATE_GLYPH_MIN.finalRing)} {...paint} />
            <circle className="shape-detail" cx={fmt(cx)} cy={fmt(cy)} r={fmt(min * STATE_GLYPH_MIN.finalDot)} {...solid} />
          </>
        ),
        labelBox: full,
        hideLabel: true,
      };
    case 'choice':
      return {
        element: <polygon className="shape" points={points([[cx, 0], [w, cy], [cx, h], [0, cy]])} {...paint} />,
        labelBox: full,
        hideLabel: true,
      };
    case 'fork-join': {
      const thickness = stateBarThickness(h);
      return { element: <rect className="shape" x={0} y={fmt(cy - thickness / 2)} width={fmt(w)} height={fmt(thickness)} {...solid} />, labelBox: full, hideLabel: true };
    }
    case 'shallow-history':
    case 'deep-history': {
      const r = min * STATE_GLYPH_MIN.history;
      return {
        element: (
          <>
            <circle className="shape" cx={fmt(cx)} cy={fmt(cy)} r={fmt(r)} {...paint} />
            <text className="shape-detail" x={fmt(cx)} y={fmt(cy)} fill={style.textColor} fontFamily={style.font} fontSize={fmt(min * STATE_H_FONT)} fontWeight="bold" textAnchor="middle" dominantBaseline="central">
              {name === 'deep-history' ? 'H*' : 'H'}
            </text>
          </>
        ),
        labelBox: full,
        hideLabel: true,
      };
    }
    case 'entry-point':
    case 'exit-point': {
      const r = min * STATE_GLYPH_MIN.point;
      const inbound = name === 'entry-point';
      const tip = inbound ? { x: cx - r, y: cy } : { x: cx + r, y: cy };
      const tail = inbound ? { x: 0, y: cy } : { x: w, y: cy };
      const dir = inbound ? -1 : 1;
      return {
        element: (
          <>
            <circle className="shape" cx={fmt(cx)} cy={fmt(cy)} r={fmt(r)} {...paint} />
            <path className="shape-detail" d={`M${String(fmt(tail.x))},${String(fmt(tail.y))} H${String(fmt(tip.x - dir * 6))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} />
            <polygon className="shape-detail" points={points([[tip.x, tip.y], [tip.x - dir * 7, tip.y - 4], [tip.x - dir * 7, tip.y + 4]])} fill={style.stroke} stroke="none" />
          </>
        ),
        labelBox: full,
        hideLabel: true,
      };
    }
    case 'composite-state': {
      const band = stateNameBand(h);
      return {
        element: (
          <>
            <rect className="shape" width={fmt(w)} height={fmt(h)} rx={fmt(Math.min(ROUNDED_RADIUS, h / 2))} {...paint} />
            <path className="shape-detail" d={`M0,${String(fmt(band))} H${String(fmt(w))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} />
          </>
        ),
        labelBox: { x: 0, y: 0, w, h: band },
      };
    }
    default:
      return undefined;
  }
}

/**
 * A general-purpose shape (model/basicShapes.ts): ellipse, triangle, note, brace and the rest. Its outline and details are
 * path data in the node's box, resolved here and by BasicShapeGeometry for the server renderer, so both draw the same
 * numbers. The first part is the outline, painted with the node's fill and stroke; later parts are details, which take
 * the fill too only when the library says so. An outline with no fill (the brace) is only a line, so a transparent
 * rectangle keeps the whole box clickable.
 */
function basicRender(shape: BasicShape, w: number, h: number, paint: Paint): ShapeRender {
  const [outline, ...details] = shape.parts;
  const stroked = { stroke: paint.stroke, strokeWidth: paint.strokeWidth, strokeDasharray: paint.strokeDasharray };
  return {
    element: (
      <>
        {outline?.fill === true ? (
          <path className="shape" d={resolvePath(outline.d, w, h)} {...paint} />
        ) : (
          <>
            <rect className="shape" width={fmt(w)} height={fmt(h)} fill="transparent" stroke="none" />
            {outline !== undefined && <path className="shape-detail" d={resolvePath(outline.d, w, h)} fill="none" {...stroked} />}
          </>
        )}
        {details.map((part, index) => (
          <path key={index} className="shape-detail" d={resolvePath(part.d, w, h)} fill={part.fill === true ? paint.fill : 'none'} {...stroked} />
        ))}
      </>
    ),
    labelBox: resolveBox(shape.labelBox, w, h),
  };
}

/**
 * An org-chart box: a rectangle whose chrome is data in the org library (model/orgShapes.ts), shared with the server
 * renderer. `outline` picks the single, inset-double or dashed border; `elements` are fractional lines inside the box
 * (the assistant's left spine). The label is centred in the box (or inset clear of the double border and the spine),
 * so a role box's name-and-title label lands on two lines.
 */
function orgRender(box: OrgShape, w: number, h: number, paint: Paint): ShapeRender {
  const inset = Math.min(w, h) * 0.08;
  const outlineDash = box.outline === 'dashed' ? DASH_ARRAYS.dashed : paint.strokeDasharray;
  const outline = (
    <>
      <rect className="shape org" width={fmt(w)} height={fmt(h)} {...paint} strokeDasharray={outlineDash} />
      {box.outline === 'double' && (
        <rect className="shape-detail" x={fmt(inset)} y={fmt(inset)} width={fmt(w - inset * 2)} height={fmt(h - inset * 2)} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} />
      )}
    </>
  );
  const detail = box.elements.map((el, i) => (
    <line
      key={i}
      className="shape-detail"
      x1={fmt(el.x1 * w)}
      y1={fmt(el.y1 * h)}
      x2={fmt(el.x2 * w)}
      y2={fmt(el.y2 * h)}
      fill="none"
      stroke={paint.stroke}
      strokeWidth={paint.strokeWidth}
      strokeDasharray={paint.strokeDasharray}
    />
  ));
  const labelBox: Rect =
    box.outline === 'double'
      ? { x: inset * 1.5, y: inset * 1.5, w: w - inset * 3, h: h - inset * 3 }
      : box.name === 'assistant'
        ? { x: w * 0.2, y: 0, w: w * 0.8, h }
        : { x: 0, y: 0, w, h };
  return { element: <>{outline}{detail}</>, labelBox };
}

/** `stroke-width` to `strokeWidth`: a glyph primitive carries SVG attribute names, React wants the camel-case props. */
function camelAttribute(name: string): string {
  return name.replace(/-([a-z])/g, (_, letter: string) => letter.toUpperCase());
}

/**
 * One primitive of a BPMN glyph, its attributes copied through (numbers rounded like every other attribute here). A
 * throwing shape fills the art with the stroke colour; a `detail` primitive then draws in the node's fill colour so an
 * envelope's flap still reads on the filled body. Mirrors SvgRenderer.cs `BpmnGlyphElement`.
 */
function bpmnGlyphElement(element: BpmnGlyphElement, key: number, shape: BpmnShape, style: NodeKindStyle): ReactElement {
  const { tag, solid, detail, ...attributes } = element;
  const props: Record<string, unknown> = { key };
  for (const [name, value] of Object.entries(attributes)) {
    props[camelAttribute(name)] = typeof value === 'number' ? fmt(value) : value;
  }
  const throwing = shape.throwing === true;
  if (solid === true || throwing) {
    props.fill = detail === true && throwing ? style.fill : style.stroke;
  }
  if (detail === true && throwing) {
    props.stroke = style.fill;
  }
  return createElement(tag, props);
}

/** A BPMN glyph scaled from its 24 grid into a `side` square whose top-left is (gx, gy). */
function bpmnGlyph(shape: BpmnShape, gx: number, gy: number, side: number, style: NodeKindStyle): ReactElement | null {
  const elements = shape.glyph === undefined ? undefined : BPMN_GLYPHS[shape.glyph];
  if (elements === undefined) {
    return null;
  }
  const scale = side / BPMN_VIEWBOX;
  return (
    <g className="icon bpmn-glyph" transform={`translate(${String(fmt(gx))} ${String(fmt(gy))}) scale(${String(fmt(scale))})`} fill="none" stroke={style.stroke} strokeWidth={fmt(style.strokeWidth / scale)} strokeLinecap="round" strokeLinejoin="round">
      {elements.map((element, i) => bpmnGlyphElement(element, i, shape, style))}
    </g>
  );
}

/**
 * A BPMN shape (model/bpmnShapes.ts), drawn natively and mirrored shape for shape by SvgRenderer.cs `BpmnMarkup`. An
 * event is an ellipse filling its box (a circle on the square node the palette places) whose border weight is its
 * position: single for start, an inset second ring for intermediate, a heavier stroke for end. A gateway is a diamond
 * with a centred glyph, an activity a rounded rectangle with a marker glyph in its corner (a sub-process's at the
 * bottom), and a call activity a thick border. The data object is a page with a folded corner, the data store a
 * cylinder with two lips, and the text annotation an open bracket. Events, gateways and data draw their label in a band
 * below the outline (BPMN's convention; `outsideLabelBounds` widens the export and fit-to-screen bounds to match), the
 * rest inside, clear of their marker. Every number comes from the library's shared layout block.
 */
function bpmnRender(shape: BpmnShape, w: number, h: number, style: NodeKindStyle, paint: Paint): ShapeRender {
  const l = BPMN_LAYOUT;
  const cx = w / 2;
  const cy = h / 2;
  const min = Math.min(w, h);
  const thick = style.strokeWidth * l.thickBorder;
  const weight = shape.border === 'thick' ? thick : style.strokeWidth;
  const pull = shape.border === 'thick' ? (thick - style.strokeWidth) / 2 : 0;
  const detailPaint = { fill: 'none', stroke: paint.stroke, strokeWidth: fmt(style.strokeWidth), strokeDasharray: paint.strokeDasharray };
  const below: Rect = { x: -(Math.max(w, l.labelMinWidth) - w) / 2, y: h + l.labelGap, w: Math.max(w, l.labelMinWidth), h: l.labelBand };

  switch (shape.family) {
    case 'event': {
      const ring = min * l.eventRingInset;
      const glyphSide = min * l.eventGlyph;
      return {
        element: (
          <>
            <ellipse className="shape bpmn" cx={fmt(cx)} cy={fmt(cy)} rx={fmt(w / 2 - pull)} ry={fmt(h / 2 - pull)} {...paint} strokeWidth={fmt(weight)} />
            {shape.border === 'double' && <ellipse className="shape-detail" cx={fmt(cx)} cy={fmt(cy)} rx={fmt(w / 2 - ring)} ry={fmt(h / 2 - ring)} {...detailPaint} />}
            {bpmnGlyph(shape, cx - glyphSide / 2, cy - glyphSide / 2, glyphSide, style)}
          </>
        ),
        labelBox: below,
      };
    }
    case 'gateway': {
      const glyphSide = min * l.gatewayGlyph;
      return {
        element: (
          <>
            <polygon className="shape bpmn" points={points([[cx, 0], [w, cy], [cx, h], [0, cy]])} {...paint} />
            {bpmnGlyph(shape, cx - glyphSide / 2, cy - glyphSide / 2, glyphSide, style)}
          </>
        ),
        labelBox: below,
      };
    }
    case 'activity': {
      const corner = shape.glyph !== undefined && shape.placement === 'corner';
      const bottom = shape.glyph !== undefined && shape.placement === 'bottom';
      const markerX = bottom ? (w - l.markerSize) / 2 : l.markerInset;
      const markerY = bottom ? h - l.markerInset - l.markerSize : l.markerInset;
      const top = corner ? l.markerInset + l.markerSize / 2 : 0;
      const foot = bottom ? l.markerSize + l.markerInset : 0;
      return {
        element: (
          <>
            <rect className="shape bpmn" x={fmt(pull)} y={fmt(pull)} width={fmt(w - pull * 2)} height={fmt(h - pull * 2)} rx={fmt(Math.min(l.cornerRadius, h / 2))} {...paint} strokeWidth={fmt(weight)} />
            {(corner || bottom) && bpmnGlyph(shape, markerX, markerY, l.markerSize, style)}
          </>
        ),
        labelBox: { x: 0, y: top, w, h: h - top - foot },
      };
    }
    case 'data': {
      if (shape.name === 'data-store') {
        const ry = Math.min(h * 0.12, 14);
        const rx = w / 2;
        const body = `M0,${String(fmt(ry))} A${String(fmt(rx))},${String(fmt(ry))} 0 0 1 ${String(fmt(w))},${String(fmt(ry))} V${String(fmt(h - ry))} A${String(fmt(rx))},${String(fmt(ry))} 0 0 1 0,${String(fmt(h - ry))} Z`;
        const lips = `M0,${String(fmt(ry))} A${String(fmt(rx))},${String(fmt(ry))} 0 0 0 ${String(fmt(w))},${String(fmt(ry))} M0,${String(fmt(ry * 2.4))} A${String(fmt(rx))},${String(fmt(ry))} 0 0 0 ${String(fmt(w))},${String(fmt(ry * 2.4))}`;
        return {
          element: (
            <>
              <path className="shape bpmn" d={body} {...paint} />
              <path className="shape-detail" d={lips} {...detailPaint} />
            </>
          ),
          labelBox: below,
        };
      }
      const fold = Math.min(w * l.dataFold, l.dataFoldMax);
      return {
        element: (
          <>
            <path className="shape bpmn" d={`M0,0 H${String(fmt(w - fold))} L${String(fmt(w))},${String(fmt(fold))} V${String(fmt(h))} H0 Z`} {...paint} />
            <path className="shape-detail" d={`M${String(fmt(w - fold))},0 V${String(fmt(fold))} H${String(fmt(w))}`} {...detailPaint} />
          </>
        ),
        labelBox: below,
      };
    }
    case 'annotation': {
      const bracket = bpmnBracket(w);
      return {
        element: (
          <>
            <rect className="icon-hit" width={fmt(w)} height={fmt(h)} fill="transparent" stroke="none" />
            <path className="shape bpmn" d={`M${String(fmt(bracket))},0 H0 V${String(fmt(h))} H${String(fmt(bracket))}`} {...detailPaint} />
          </>
        ),
        labelBox: { x: bracket, y: 0, w: w - bracket, h },
      };
    }
  }
}

/**
 * A swimlane pool or lane: a rectangle with a title band along its top, told apart by the band's height (a pool's is
 * deeper). Orientation lives in the model (swimlanes.ts) and changes how lanes are arranged, not how the band is
 * drawn, so a lane of a vertical pool and of a horizontal one look the same. The label box is the whole box: a
 * container label is drawn top-left (NodeView), which is inside the band either way.
 */
function swimlaneRender(shape: string, w: number, h: number, paint: Paint): ShapeRender {
  const band = shape === LANE_SHAPE ? laneHeaderHeight(h) : poolHeaderHeight(h);
  return {
    element: (
      <>
        <rect className="shape swimlane" width={fmt(w)} height={fmt(h)} {...paint} />
        <path className="shape-detail" d={`M0,${String(fmt(band))} H${String(fmt(w))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} />
      </>
    ),
    labelBox: { x: 0, y: 0, w, h },
  };
}

/** One line of class (or entity) text: node-local, anchored, in the node's font at the fixed compartment size (metrics in uml-shapes.json, er-shapes.json). */
function umlLine(key: string | number, x: number, y: number, anchor: 'start' | 'middle', style: NodeKindStyle, text: string, bold = false, fontSize: number = UML_METRICS.fontSize): ReactElement {
  return (
    <text
      key={key}
      className="uml-text"
      x={fmt(x)}
      y={fmt(y)}
      fill={style.textColor}
      fontFamily={style.font}
      fontSize={fontSize}
      fontWeight={bold ? 'bold' : undefined}
      textAnchor={anchor}
      dominantBaseline="central"
      xmlSpace="preserve"
      style={{ userSelect: 'none', pointerEvents: 'none' }}
    >
      {text}
    </text>
  );
}

function umlMembers(members: UmlMember[], hasGlyph: boolean, top: number, style: NodeKindStyle): ReactElement[] {
  const m = UML_METRICS;
  return members.flatMap((member, i) => {
    const y = top + m.padY + m.rowHeight * i + m.rowHeight / 2;
    const lines = [umlLine(`${String(i)}-text`, hasGlyph ? m.padX + m.glyphColumn : m.padX, y, 'start', style, member.text)];
    return member.visibility === '' ? lines : [umlLine(`${String(i)}-glyph`, m.padX, y, 'start', style, member.visibility), ...lines];
  });
}

/**
 * A UML class, interface, lollipop or socket (model/umlShapes.ts; the server twin is SvgRenderer.UmlMarkup). A class
 * or interface is one rectangle cut into name, attribute and operation compartments whose heights come from the
 * node's content, so its label and members are drawn here and the shape hides the node's own label. A lollipop or
 * socket is a stem and a ball or half-circle in the top band of its box; its label is the ordinary one, in the band
 * below. Without `content` a class draws as a proportional glyph with no text.
 */
function umlRender(shape: UmlShape, w: number, h: number, style: NodeKindStyle, paint: Paint, content: ShapeContent | undefined): ShapeRender {
  const m = UML_METRICS;
  if (shape.form === 'symbol') {
    const band = symbolBandHeight(h);
    const cy = band / 2;
    const r = band * m.symbolRadius;
    const line = { fill: 'none', stroke: paint.stroke, strokeWidth: paint.strokeWidth, strokeDasharray: paint.strokeDasharray };
    return {
      element: (
        <>
          <rect className="icon-hit" width={fmt(w)} height={fmt(h)} fill="transparent" stroke="none" />
          {shape.name === 'lollipop' ? (
            <>
              <path className="shape-detail" d={`M0,${String(fmt(cy))} H${String(fmt(w - 2 * r))}`} {...line} />
              <circle className="shape uml" cx={fmt(w - r)} cy={fmt(cy)} r={fmt(r)} {...paint} />
            </>
          ) : (
            <>
              <path className="shape-detail" d={`M0,${String(fmt(cy))} H${String(fmt(w - r))}`} {...line} />
              <path className="shape uml" d={`M${String(fmt(w))},${String(fmt(cy - r))} A${String(fmt(r))},${String(fmt(r))} 0 0 0 ${String(fmt(w))},${String(fmt(cy + r))}`} {...line} />
            </>
          )}
        </>
      ),
      labelBox: { x: 0, y: band, w, h: h - band },
    };
  }
  const separators = (attributesTop: number, operationsTop: number): ReactElement => (
    <path className="shape-detail" d={`M0,${String(fmt(attributesTop))} H${String(fmt(w))} M0,${String(fmt(operationsTop))} H${String(fmt(w))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} />
  );
  if (content === undefined) {
    return {
      element: (
        <>
          <rect className="shape uml" width={fmt(w)} height={fmt(h)} {...paint} />
          {separators(h * 0.34, h * 0.67)}
        </>
      ),
      labelBox: { x: 0, y: 0, w, h },
      hideLabel: true,
    };
  }
  const layout = layoutClass(shape, content.label, content.props, h);
  const hidden = (part: UmlPart): boolean => content.hide === 'all' || content.hide === part;
  const name = nameRow(layout, w);
  return {
    element: (
      <>
        <rect className="shape uml" width={fmt(w)} height={fmt(h)} {...paint} />
        {separators(layout.attributes.top, layout.operations.top)}
        {!hidden('name') && layout.stereotype !== undefined && umlLine('stereotype', w / 2, name.y - m.rowHeight / 2, 'middle', style, `\u00ab${layout.stereotype}\u00bb`)}
        {!hidden('name') && content.label.length > 0 && umlLine('name', w / 2, name.y + m.rowHeight / 2, 'middle', style, content.label, true)}
        {!hidden('attributes') && umlMembers(layout.attributes.members, layout.attributes.hasGlyph, layout.attributes.top, style)}
        {!hidden('operations') && umlMembers(layout.operations.members, layout.operations.hasGlyph, layout.operations.top, style)}
      </>
    ),
    labelBox: name,
    hideLabel: true,
  };
}

/**
 * An ER entity (model/erShapes.ts; the server twin is SvgRenderer.ErMarkup): one rectangle with a name band, then one
 * row per column drawn as real columns (key marker, name, type, nullability), each left-aligned at the offset the
 * layout derives from the widest cell, with a divider after the leading primary-key rows. A weak entity adds an inner
 * border. Like a class it draws its own name, so the shape hides the node's label; without `content` it draws as a
 * proportional glyph with no text.
 */
function erRender(shape: ErShape, w: number, h: number, style: NodeKindStyle, paint: Paint, content: ShapeContent | undefined): ShapeRender {
  const m = ER_METRICS;
  const inset = shape.double ? m.doubleInset : 0;
  const rule = (y: number): string => `M${String(fmt(inset))},${String(fmt(y))} H${String(fmt(w - inset))}`;
  const frame = (
    <>
      <rect className="shape er" width={fmt(w)} height={fmt(h)} {...paint} />
      {shape.double && <rect className="shape-detail" x={fmt(inset)} y={fmt(inset)} width={fmt(w - inset * 2)} height={fmt(h - inset * 2)} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} />}
    </>
  );
  const separators = (...ys: number[]): ReactElement => <path className="shape-detail" d={ys.map(rule).join(' ')} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} />;
  if (content === undefined) {
    return { element: <>{frame}{separators(h * 0.25)}</>, labelBox: { x: 0, y: 0, w, h }, hideLabel: true };
  }
  const layout = layoutEntity(content.label, content.props, h);
  const hidden = (part: ShapePart): boolean => content.hide === 'all' || content.hide === part;
  const name = entityNameRow(w);
  const rows = hidden('columns')
    ? []
    : layout.rows.flatMap((row, i) => {
        const y = layout.body.top + m.padY + m.rowHeight * i + m.rowHeight / 2;
        const cell = (column: 'key' | 'name' | 'type' | 'nullable', text: string, bold = false): ReactElement[] => {
          const x = layout.offsets[column];
          return x === undefined || text === '' ? [] : [umlLine(`${String(i)}-${column}`, x, y, 'start', style, text, bold, m.fontSize)];
        };
        return [...cell('key', keyText(row), true), ...cell('name', row.name), ...cell('type', row.type), ...cell('nullable', nullText(row))];
      });
  return {
    element: (
      <>
        {frame}
        {separators(layout.headerHeight, ...(layout.keyDivider === undefined ? [] : [layout.keyDivider]))}
        {!hidden('name') && content.label.length > 0 && umlLine('name', w / 2, name.y + m.rowHeight / 2, 'middle', style, content.label, true, m.fontSize)}
        {rows}
      </>
    ),
    labelBox: name,
    hideLabel: true,
  };
}

/**
 * A sequence diagram's actor header: a UML stick figure centred over the lifeline in the top `actorFigureHeight` of the
 * box (no outline, so the lifeline hangs from a person, not a box), the name in the band under it. Head, body, arms and
 * legs are fractions of the figure's height (that band less 2 either side), mirrored by SvgRenderer.Sequence.cs `ActorMarkup`.
 */
function actorRender(w: number, h: number, style: NodeKindStyle): ShapeRender {
  const band = SEQUENCE_METRICS.actorFigureHeight;
  const fh = band - 4;
  const top = 2;
  const cx = w / 2;
  const r = fh * 0.16;
  const neck = top + 2 * r;
  const arms = neck + fh * 0.12;
  const hip = top + fh * 0.65;
  const armReach = fh * 0.36;
  const legReach = fh * 0.28;
  const line = (d: string): ReactElement => <path className="shape-detail" d={d} fill="none" stroke={style.stroke} strokeWidth={style.strokeWidth} strokeLinecap="round" />;
  return {
    element: (
      <>
        <rect className="shape" width={fmt(w)} height={fmt(h)} fill="transparent" stroke="none" />
        <circle className="shape-detail" cx={fmt(cx)} cy={fmt(top + r)} r={fmt(r)} fill={style.fill} stroke={style.stroke} strokeWidth={style.strokeWidth} />
        {line(`M${String(fmt(cx))},${String(fmt(neck))} V${String(fmt(hip))} M${String(fmt(cx - armReach))},${String(fmt(arms))} H${String(fmt(cx + armReach))} M${String(fmt(cx - legReach))},${String(fmt(top + fh))} L${String(fmt(cx))},${String(fmt(hip))} L${String(fmt(cx + legReach))},${String(fmt(top + fh))}`)}
      </>
    ),
    labelBox: { x: 0, y: band, w, h: Math.max(0, h - band) },
  };
}

export function renderShape(shape: string, w: number, h: number, style: NodeKindStyle, imageHref?: string, content?: ShapeContent): ShapeRender {
  const paint = paintOf(style);
  const full: Rect = { x: 0, y: 0, w, h };
  if (shape === POOL_SHAPE || shape === LANE_SHAPE) {
    return swimlaneRender(shape, w, h, paint);
  }
  if (shape === ACTOR_SHAPE) {
    return actorRender(w, h, style);
  }
  const state = stateShapeRender(shape, w, h, style);
  if (state !== undefined) {
    return state;
  }
  const icon = iconForShape(shape);
  if (icon !== undefined) {
    return icon.boundary === true ? boundaryRender(icon, w, h, style, paint) : iconRender(icon, w, h, style);
  }
  const basic = basicShape(shape);
  if (basic !== undefined) {
    return basicRender(basic, w, h, paint);
  }
  const org = orgShape(shape);
  if (org !== undefined) {
    return orgRender(org, w, h, paint);
  }
  const bpmn = bpmnShape(shape);
  if (bpmn !== undefined) {
    return bpmnRender(bpmn, w, h, style, paint);
  }
  const uml = umlShape(shape);
  if (uml !== undefined) {
    return umlRender(uml, w, h, style, paint, content);
  }
  const er = erShape(shape);
  if (er !== undefined) {
    return erRender(er, w, h, style, paint, content);
  }
  switch (shape) {
    case 'document':
      return {
        element: <path className="shape" d={documentOutline(0, 0, w, h)} {...paint} />,
        labelBox: { x: 0, y: 0, w, h: h * 0.7 },
      };
    case 'predefined-process':
      return {
        element: <>
          <rect className="shape" width={fmt(w)} height={fmt(h)} {...paint} />
          <path className="shape-detail" d={`M${String(fmt(w * 0.12))},0 V${String(fmt(h))} M${String(fmt(w * 0.88))},0 V${String(fmt(h))}`} {...paint} fill="none" />
        </>,
        labelBox: { x: w * 0.12, y: 0, w: w * 0.76, h },
      };
    case 'manual-input':
      return {
        element: <polygon className="shape" points={points([[0, h * 0.25], [w, 0], [w, h], [0, h]])} {...paint} />,
        labelBox: { x: 0, y: h * 0.25, w, h: h * 0.75 },
      };
    case 'off-page-connector':
      return {
        element: <polygon className="shape" points={points([[0, 0], [w, 0], [w, h * 0.65], [w / 2, h], [0, h * 0.65]])} {...paint} />,
        labelBox: { x: 0, y: 0, w, h: h * 0.65 },
      };
    case 'delay':
      // Flat left edge, half-ellipse right end (ISO 5807 delay); the ellipse reaches x = w at mid-height.
      return {
        element: <path className="shape" d={`M0,0 H${String(fmt(w / 2))} A${String(fmt(w / 2))},${String(fmt(h / 2))} 0 0 1 ${String(fmt(w / 2))},${String(fmt(h))} H0 Z`} {...paint} />,
        labelBox: { x: 0, y: 0, w: w * 0.8, h },
      };
    case 'manual-operation': {
      // Trapezoid, longest parallel side at the top; the left/right side sits at x = inset/2 at half height,
      // which is why defaultPorts puts the W/E ports at 0.1 / 0.9 for inset = 0.2w.
      const inset = w * 0.2;
      return {
        element: <polygon className="shape" points={points([[0, 0], [w, 0], [w - inset, h], [inset, h]])} {...paint} />,
        labelBox: { x: inset, y: 0, w: w - inset * 2, h },
      };
    }
    case 'display':
      // CRT: flat top, right and bottom edges, a left side bowed out to x = 0 at mid-height.
      return {
        element: <path className="shape" d={`M${String(fmt(w * 0.25))},0 H${String(fmt(w))} V${String(fmt(h))} H${String(fmt(w * 0.25))} A${String(fmt(w * 0.25))},${String(fmt(h / 2))} 0 0 0 ${String(fmt(w * 0.25))},0 Z`} {...paint} />,
        labelBox: { x: w * 0.25, y: 0, w: w * 0.75, h },
      };
    case 'stored-data':
      // Straight left edge, right side bowed out to x = w at mid-height.
      return {
        element: <path className="shape" d={`M0,0 H${String(fmt(w * 0.75))} C${String(fmt(w))},0 ${String(fmt(w))},${String(fmt(h * 0.25))} ${String(fmt(w))},${String(fmt(h / 2))} C${String(fmt(w))},${String(fmt(h * 0.75))} ${String(fmt(w))},${String(fmt(h))} ${String(fmt(w * 0.75))},${String(fmt(h))} H0 Z`} {...paint} />,
        labelBox: { x: 0, y: 0, w: w * 0.85, h },
      };
    case 'multi-document': {
      const stepX = w * 0.1;
      const stepY = h * 0.1;
      const pageW = w * 0.8;
      const pageH = h * 0.8;
      return {
        element: (
          <>
            <path className="shape" d={documentOutline(stepX * 2, 0, pageW, pageH)} {...paint} />
            <path className="shape" d={documentOutline(stepX, stepY, pageW, pageH)} {...paint} />
            <path className="shape" d={documentOutline(0, stepY * 2, pageW, pageH)} {...paint} />
          </>
        ),
        labelBox: { x: 0, y: stepY * 2, w: pageW, h: pageH * 0.8 },
      };
    }
    case 'merge':
      return {
        element: <polygon className="shape" points={points([[0, 0], [w, 0], [w / 2, h]])} {...paint} />,
        labelBox: { x: w * 0.25, y: 0, w: w * 0.5, h: h * 0.6 },
      };
    case 'extract':
      return {
        element: <polygon className="shape" points={points([[w / 2, 0], [w, h], [0, h]])} {...paint} />,
        labelBox: { x: w * 0.25, y: h * 0.4, w: w * 0.5, h: h * 0.6 },
      };
    case 'on-page-connector':
      // An ellipse inscribed in the node box: a circle on a square node, and the only outline whose four side
      // midpoints (and so defaultPorts) land on it whatever the box's aspect.
      return {
        element: <ellipse className="shape" cx={fmt(w / 2)} cy={fmt(h / 2)} rx={fmt(w / 2)} ry={fmt(h / 2)} {...paint} />,
        labelBox: { x: w * 0.15, y: h * 0.2, w: w * 0.7, h: h * 0.6 },
      };
    case 'summing-junction':
      return {
        element: (
          <>
            <ellipse className="shape" cx={fmt(w / 2)} cy={fmt(h / 2)} rx={fmt(w / 2)} ry={fmt(h / 2)} {...paint} />
            <path className="shape-detail" d={`M${String(fmt(w * 0.15))},${String(fmt(h * 0.15))} L${String(fmt(w * 0.85))},${String(fmt(h * 0.85))} M${String(fmt(w * 0.15))},${String(fmt(h * 0.85))} L${String(fmt(w * 0.85))},${String(fmt(h * 0.15))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} strokeDasharray={paint.strokeDasharray} />
          </>
        ),
        labelBox: { x: w * 0.15, y: h * 0.2, w: w * 0.7, h: h * 0.6 },
      };
    case 'or':
      return {
        element: (
          <>
            <ellipse className="shape" cx={fmt(w / 2)} cy={fmt(h / 2)} rx={fmt(w / 2)} ry={fmt(h / 2)} {...paint} />
            <path className="shape-detail" d={`M${String(fmt(w * 0.15))},${String(fmt(h / 2))} H${String(fmt(w * 0.85))} M${String(fmt(w / 2))},${String(fmt(h * 0.15))} V${String(fmt(h * 0.85))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} strokeDasharray={paint.strokeDasharray} />
          </>
        ),
        labelBox: { x: w * 0.15, y: h * 0.2, w: w * 0.7, h: h * 0.6 },
      };
    case 'collate':
      // Horizontal bowtie: full-width top and bottom edges, crossing diagonals.
      return {
        element: <polygon className="shape" points={points([[0, 0], [w, 0], [0, h], [w, h]])} {...paint} />,
        labelBox: { x: 0, y: 0, w, h },
      };
    case 'sort':
      return {
        element: (
          <>
            <polygon className="shape" points={points([[w / 2, 0], [w, h / 2], [w / 2, h], [0, h / 2]])} {...paint} />
            <path className="shape-detail" d={`M0,${String(fmt(h / 2))} H${String(fmt(w))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} strokeDasharray={paint.strokeDasharray} />
          </>
        ),
        labelBox: { x: w * 0.25, y: h * 0.25, w: w * 0.5, h: h * 0.5 },
      };
    case 'loop-limit': {
      const bevel = Math.min(w * 0.2, h * 0.5);
      return {
        element: <polygon className="shape" points={points([[bevel, 0], [w - bevel, 0], [w, bevel], [w, h], [0, h], [0, bevel]])} {...paint} />,
        // A fixed inset rather than one tied to the bevel, so the label's centre matches the server renderer's anchor.
        labelBox: { x: w * 0.12, y: h * 0.15, w: w * 0.76, h: h * 0.8 },
      };
    }
    case 'internal-storage':
      return {
        element: (
          <>
            <rect className="shape" width={fmt(w)} height={fmt(h)} {...paint} />
            <path className="shape-detail" d={`M0,${String(fmt(h * 0.2))} H${String(fmt(w))} M${String(fmt(w * 0.2))},0 V${String(fmt(h))}`} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} strokeDasharray={paint.strokeDasharray} />
          </>
        ),
        labelBox: { x: w * 0.2, y: h * 0.2, w: w * 0.8, h: h * 0.8 },
      };
    case 'card': {
      const cut = Math.min(w * 0.2, h * 0.5);
      return {
        element: <polygon className="shape" points={points([[cut, 0], [w, 0], [w, h], [0, h], [0, cut]])} {...paint} />,
        labelBox: { x: cut * 0.5, y: cut * 0.5, w: w - cut, h: h - cut },
      };
    }
    case 'rounded':
      return { element: <rect className="shape" width={fmt(w)} height={fmt(h)} rx={fmt(Math.min(ROUNDED_RADIUS, h / 2))} {...paint} />, labelBox: full };
    case 'pill':
      return { element: <rect className="shape" width={fmt(w)} height={fmt(h)} rx={fmt(h / 2)} {...paint} />, labelBox: { x: h / 4, y: 0, w: w - h / 2, h } };
    case 'diamond':
      return {
        element: (
          <polygon
            className="shape"
            points={points([
              [w / 2, 0],
              [w, h / 2],
              [w / 2, h],
              [0, h / 2],
            ])}
            {...paint}
          />
        ),
        // Wide enough that a long word ("successful?") stays whole on a default 160 px decision: the outline is narrower
        // at the label's top and bottom lines than at its middle, but a one- or two-line label fits inside at this width.
        labelBox: { x: w * 0.19, y: h / 4, w: w * 0.62, h: h / 2 },
      };
    case 'hexagon': {
      const inset = Math.min(w * 0.2, h / 2);
      return {
        element: (
          <polygon
            className="shape"
            points={points([
              [inset, 0],
              [w - inset, 0],
              [w, h / 2],
              [w - inset, h],
              [inset, h],
              [0, h / 2],
            ])}
            {...paint}
          />
        ),
        labelBox: { x: inset, y: 0, w: w - inset * 2, h },
      };
    }
    case 'parallelogram': {
      const skew = Math.min(w * 0.2, h / 2);
      return {
        element: (
          <polygon
            className="shape"
            points={points([
              [skew, 0],
              [w, 0],
              [w - skew, h],
              [0, h],
            ])}
            {...paint}
          />
        ),
        labelBox: { x: skew, y: 0, w: w - skew * 2, h },
      };
    }
    case 'cylinder': {
      const ry = Math.min(h * 0.12, 14);
      const rx = w / 2;
      const body = `M0,${String(fmt(ry))} A${String(fmt(rx))},${String(fmt(ry))} 0 0 1 ${String(fmt(w))},${String(fmt(ry))} V${String(fmt(h - ry))} A${String(fmt(rx))},${String(fmt(ry))} 0 0 1 0,${String(fmt(h - ry))} Z`;
      const lip = `M0,${String(fmt(ry))} A${String(fmt(rx))},${String(fmt(ry))} 0 0 0 ${String(fmt(w))},${String(fmt(ry))}`;
      return {
        element: (
          <>
            <path className="shape" d={body} {...paint} />
            <path className="shape-detail" d={lip} fill="none" stroke={paint.stroke} strokeWidth={paint.strokeWidth} strokeDasharray={paint.strokeDasharray} />
          </>
        ),
        labelBox: { x: 0, y: ry * 2, w, h: h - ry * 2 },
      };
    }
    case 'text':
      return { element: null, labelBox: full };
    case 'image':
      // The uploaded picture, drawn through <image href> so a script in an SVG asset can never run in this document
      // (the asset is served as an image with a restrictive CSP too). A frame keeps the node visible while the image
      // loads. With no asset (or an unreadable one) a dashed frame with a picture glyph stands in.
      if (imageHref !== undefined) {
        return {
          element: (
            <>
              <image href={imageHref} x={0} y={0} width={fmt(w)} height={fmt(h)} preserveAspectRatio="xMidYMid meet" />
              <rect className="shape" width={fmt(w)} height={fmt(h)} fill="none" stroke={style.stroke} strokeWidth={style.strokeWidth} strokeDasharray={paint.strokeDasharray} />
            </>
          ),
          labelBox: full,
        };
      }

      return {
        element: (
          <>
            <rect className="shape" width={fmt(w)} height={fmt(h)} {...paint} stroke={style.textColor} strokeWidth={1} strokeDasharray={DASH_ARRAYS.dashed} />
            <path
              className="shape-detail"
              d={`M6,${String(fmt(Math.min(h, 22)))} l5,-6 l4,4 l5,-8 l6,10 Z`}
              fill="none"
              stroke={style.textColor}
              strokeWidth={1}
            />
          </>
        ),
        labelBox: full,
      };
    default:
      // rectangle, and a `custom:*` shape no library knows (a known `custom:generic/*`, `custom:org/*` or `custom:bpmn/*` is drawn above).
      return { element: <rect className="shape" width={fmt(w)} height={fmt(h)} {...paint} />, labelBox: full };
  }
}
