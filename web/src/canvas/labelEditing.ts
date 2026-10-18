/**
 * Pure layout and text helpers for the label editor (LabelEditor.tsx), kept apart from the component so they are
 * unit-testable without a DOM render.
 */

import type { Geometry, Point } from '../model/types.ts';
import type { NodeKindStyle } from '../theme/diagramTheme.ts';
import { curveMidpoint } from './curve.ts';
import type { PlacedNode } from './documentIndex.ts';
import { pointAlongPolyline, type Rect } from './geometry.ts';
import { erShape, layoutEntity, partAt as entityPartAt } from '../model/erShapes.ts';
import type { MemberPart } from '../model/memberParts.ts';
import { isClassShape, layoutClass, partAt, umlShape } from '../model/umlShapes.ts';
import { renderShape } from './shapes.tsx';
import { LABEL_FONT_SIZE, LABEL_LINE_HEIGHT, LABEL_PADDING, labelFont, type TextMeasurer } from './text.ts';
import type { Viewport } from './viewport.ts';

/** How an edit ended: from the keyboard (focus goes back to the canvas) or by focus moving elsewhere (it stays there). */
export type LabelEditEnd = 'keyboard' | 'blur';

/** Padding of an edge label's plate around its text, in canvas units; the edge label editor (EdgeLabelEditor.tsx) pads the same. */
export const EDGE_LABEL_PAD_X = 4;
export const EDGE_LABEL_PAD_Y = 2;

/** The size of an edge label's plate for `text`, the same box `labelLayout.ts` places to keep two labels from overlapping. */
export function measureLabelBox(text: string, font: string, measure: TextMeasurer): { width: number; height: number } {
  return {
    width: measure(text, labelFont(font)) + EDGE_LABEL_PAD_X * 2,
    height: LABEL_FONT_SIZE * LABEL_LINE_HEIGHT + EDGE_LABEL_PAD_Y * 2,
  };
}

/**
 * The CSS transform that puts an element laid out in a node's local space (origin at its unrotated top-left, CSS
 * pixels as world units) where the SVG draws that node: the viewport's translate and scale, then the node's translate
 * and its rotation about its own centre, the same chain as DiagramSvg's world group and NodeView's transform.
 */
export function nodeFrameTransform(viewport: Viewport, geometry: Geometry): string {
  const { x, y, w, h, rotation } = geometry;
  const place = `translate(${String(viewport.x)}px, ${String(viewport.y)}px) scale(${String(viewport.zoom)}) translate(${String(x)}px, ${String(y)}px)`;
  if (!rotation) {
    return place;
  }
  return `${place} translate(${String(w / 2)}px, ${String(h / 2)}px) rotate(${String(rotation)}deg) translate(${String(-w / 2)}px, ${String(-h / 2)}px)`;
}

/**
 * Where an edge's label sits: halfway along the route it is drawn on, so it follows the edge when it re-routes. A
 * curved connector measures that halfway by arc length along the spline (curve.ts `curveMidpoint`), since the
 * polyline's midpoint falls off a bent curve; orthogonal and straight labels keep the polyline midpoint.
 */
export function edgeLabelAt(points: Point[], curved = false): Point {
  return curved ? curveMidpoint(points) : pointAlongPolyline(points, 0.5);
}

/** The CSS transform that puts a world point where the SVG draws it: the viewport's translate and scale, then the point. */
export function pointFrameTransform(viewport: Viewport, at: Point): string {
  return `translate(${String(viewport.x)}px, ${String(viewport.y)}px) scale(${String(viewport.zoom)}) translate(${String(at.x)}px, ${String(at.y)}px)`;
}

/**
 * Where the text sits inside the shape's label box: NodeView's inset, line height and vertical placement. The box is
 * padded by LABEL_PADDING either side, so the field wraps at the width NodeView breaks the label's lines at.
 */
export interface LabelEditorLayout {
  /** The shape's label box in node-local space, as renderShape reports it. */
  box: Rect;
  lineHeight: number;
  /** Top inset for a container label; undefined when the text block is centred vertically in the box. */
  top: number | undefined;
}

export function labelEditorLayout(node: PlacedNode, style: NodeKindStyle, container: boolean): LabelEditorLayout {
  const { w, h } = node.geometry;
  const box = renderShape(node.shape, w, h, style, undefined, { label: node.label, props: node.props }).labelBox;
  const fontSize = style.fontSize ?? LABEL_FONT_SIZE;
  const lineHeight = fontSize * LABEL_LINE_HEIGHT;
  // NodeView centres a container's first line on LABEL_PADDING + fontSize / 2 below the box top; a CSS line
  // box centres its text, so the line box starts half a line height above that.
  const top = container ? LABEL_PADDING + fontSize / 2 - lineHeight / 2 : undefined;
  return { box, lineHeight, top };
}

const BLOCKS = new Set(['DIV', 'P', 'LI']);

/**
 * The plain text of an edited field. Only text is read: whatever markup a browser inserts for Enter (a `<div>` or a
 * `<br>`) becomes a line break, a `<br>` that only keeps an empty line open is dropped, and every other element
 * contributes its text alone, so the label can never carry HTML however the markup got there.
 */
export function plainTextOf(root: Node): string {
  let out = '';
  const walk = (parent: Node): void => {
    parent.childNodes.forEach((child, i) => {
      if (child.nodeType === 3) {
        out += child.nodeValue ?? '';
        return;
      }
      if (!(child instanceof Element)) {
        return;
      }
      if (child.tagName === 'BR') {
        // The last <br> in a line is the browser's placeholder for that line, not a break of its own.
        if (i < parent.childNodes.length - 1) {
          out += '\n';
        }
        return;
      }
      if (BLOCKS.has(child.tagName) && out.length > 0 && !out.endsWith('\n')) {
        out += '\n';
      }
      walk(child);
    });
  };
  walk(root);
  // A plaintext-only field in Chrome keeps a trailing line break open with a second "\n" in the same text node, the
  // placeholder a <br> is elsewhere, so one Enter at the end reads as "\n\n" and must count as one.
  const last = root.lastChild;
  if (last?.nodeType === 3 && (last.nodeValue ?? '').endsWith('\n\n') && out.endsWith('\n\n')) {
    out = out.slice(0, -1);
  }
  return out.replace(/\u00a0/g, ' ');
}

/**
 * The member compartment of a UML class or ER entity a world point falls in (a double-click on the attributes,
 * operations or columns), or undefined for the name band, a point off the box, or a node that has no compartments. The
 * point is taken into the node's own unrotated frame first, the inverse of the rotation NodeView draws it with.
 */
export function compartmentAt(node: PlacedNode | undefined, world: Point): MemberPart | undefined {
  if (node === undefined) {
    return undefined;
  }
  const uml = umlShape(node.shape);
  const entity = erShape(node.shape);
  if (!isClassShape(uml) && entity === undefined) {
    return undefined;
  }
  const { x, y, w, h, rotation } = node.geometry;
  let dx = world.x - (x + w / 2);
  let dy = world.y - (y + h / 2);
  if (rotation) {
    const radians = (-rotation * Math.PI) / 180;
    [dx, dy] = [dx * Math.cos(radians) - dy * Math.sin(radians), dx * Math.sin(radians) + dy * Math.cos(radians)];
  }
  const localX = dx + w / 2;
  const localY = dy + h / 2;
  if (localX < 0 || localX > w || localY < 0 || localY > h) {
    return undefined;
  }
  if (entity !== undefined) {
    return entityPartAt(layoutEntity(node.label, node.props, h), localY) === 'columns' ? 'columns' : undefined;
  }
  const part = isClassShape(uml) ? partAt(layoutClass(uml, node.label, node.props, h), localY) : 'name';
  return part === 'name' ? undefined : part;
}

/** A committed label without a line break at either end: a leading or trailing Enter is not text, so the box and its exports stay as tall as the words. */
export function trimLabelBreaks(text: string): string {
  return text.replace(/^\n+|\n+$/g, '');
}

/** The lines a committed member field holds: trailing space trimmed, blank lines dropped, order kept. */
export function membersOfText(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trimEnd())
    .filter((line) => line.trim().length > 0);
}
