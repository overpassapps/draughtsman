import { memo, type ReactNode } from 'react';
import type { ShapePart } from '../model/memberParts.ts';
import type { NodeKindStyle } from '../theme/diagramTheme.ts';
import type { PlacedNode } from './documentIndex.ts';
import type { Rect } from './geometry.ts';
import { fmt, renderShape } from './shapes.tsx';
import { LABEL_FONT_SIZE, LABEL_LINE_HEIGHT, LABEL_PADDING, labelFont, wrapText, type LineBreaker, type TextMeasurer } from './text.ts';

export interface NodeBodyProps {
  node: PlacedNode;
  style: NodeKindStyle;
  /** Containers put the label top-left so their children have the room; everything else centres it. */
  container: boolean;
  measure?: TextMeasurer | undefined;
  /**
   * Breaks the label into lines where the label editor would (text.ts `createDomLineBreaker`); without one the label is
   * wrapped at spaces against `measure`.
   */
  breakLines?: LineBreaker | undefined;
  /** Leaves the label out while the label editor draws it instead. */
  hideLabel?: boolean | undefined;
  /**
   * With `hideLabel`: the one part of a UML class or ER entity the editor covers (its name or a member compartment), so
   * the rest of the box stays drawn; without it such a shape hides all its text. Other shapes ignore it.
   */
  editingPart?: ShapePart | undefined;
  /** Far zoomed out: draw the node as its plain filled box and nothing else (virtualise.ts `fullDetail`). */
  simplified?: boolean | undefined;
  /** An image node's picture href: the served asset URL on the canvas, an inlined data URI for export. */
  imageHref?: string | undefined;
}

function nodeTransform(node: PlacedNode): string {
  const { x, y, w, h, rotation } = node.geometry;
  const translate = `translate(${String(fmt(x))} ${String(fmt(y))})`;
  if (!rotation) {
    return translate;
  }
  return `${translate} rotate(${String(fmt(rotation))} ${String(fmt(w / 2))} ${String(fmt(h / 2))})`;
}

interface LabelProps {
  text: string;
  box: Rect;
  style: NodeKindStyle;
  container: boolean;
  measure?: TextMeasurer | undefined;
  breakLines?: LineBreaker | undefined;
}

function Label({ text, box, style, container, measure, breakLines }: LabelProps) {
  if (text.length === 0) {
    return null;
  }
  const fontSize = style.fontSize ?? LABEL_FONT_SIZE;
  const font = labelFont(style.font, fontSize);
  const lineHeight = fontSize * LABEL_LINE_HEIGHT;
  const width = box.w - LABEL_PADDING * 2;
  const lines = breakLines ? breakLines(text, width, font) : wrapText(text, width, font, measure);
  const x = container ? box.x + LABEL_PADDING : box.x + box.w / 2;
  const firstY = container ? box.y + LABEL_PADDING + fontSize / 2 : box.y + box.h / 2 - ((lines.length - 1) * lineHeight) / 2;
  return (
    <text
      className="label"
      fill={style.textColor}
      fontFamily={style.font}
      fontSize={fontSize}
      fontWeight={style.bold ? 'bold' : undefined}
      textAnchor={container ? 'start' : 'middle'}
      dominantBaseline="central"
      // Spaces are drawn as typed, as the label editor shows them (text.ts).
      xmlSpace="preserve"
      style={{ userSelect: 'none', pointerEvents: 'none' }}
    >
      {lines.map((line, i) => (
        <tspan key={i} x={fmt(x)} y={fmt(firstY + i * lineHeight)}>
          {line}
        </tspan>
      ))}
    </text>
  );
}

/**
 * The far-zoom level of detail (virtualise.ts `fullDetail`): the node's box in its fill and outline, drawn straight in
 * world space with no group, transform, label, dash or shape detail, since that is all that reads at that size.
 */
function SimplifiedBody({ node, style }: { node: PlacedNode; style: NodeKindStyle }) {
  if (node.shape === 'text') {
    return null;
  }
  const { x, y, w, h, rotation } = node.geometry;
  return (
    <rect
      className="shape"
      x={fmt(x)}
      y={fmt(y)}
      width={fmt(w)}
      height={fmt(h)}
      fill={style.fill}
      // The image placeholder's frame is drawn in the text colour at full detail; keep that frame visible here.
      stroke={node.shape === 'image' ? style.textColor : style.stroke}
      strokeWidth={node.shape === 'image' ? 1 : style.strokeWidth}
      transform={rotation ? `rotate(${String(fmt(rotation))} ${String(fmt(x + w / 2))} ${String(fmt(y + h / 2))})` : undefined}
    />
  );
}

/** The node's own drawing: shape plus label, in world space. Memoised so panning re-renders only the wrapper tree. */
export const NodeBody = memo(function NodeBody({ node, style, container, measure, breakLines, hideLabel, editingPart, simplified, imageHref }: NodeBodyProps) {
  const { w, h } = node.geometry;
  if (simplified) {
    return <SimplifiedBody node={node} style={style} />;
  }
  // A UML class or ER entity draws its own name and members from the label and props; the editor covering one part hides just that part.
  const shape = renderShape(node.shape, w, h, style, imageHref, { label: node.label, props: node.props, hide: hideLabel === true ? (editingPart ?? 'all') : undefined });
  return (
    <g transform={nodeTransform(node)}>
      {shape.element}
      {!hideLabel && shape.hideLabel !== true && <Label text={node.label} box={shape.labelBox} style={style} container={container} measure={measure} breakLines={breakLines} />}
    </g>
  );
});

export interface NodeViewProps extends NodeBodyProps {
  idPrefix: string;
  /** Rendered inside the container's clip; empty for leaf nodes. */
  children?: ReactNode;
  /** True when the node has no position in the document and is drawn at a provisional spot. */
  unplaced?: boolean;
}

/**
 * One `<g data-id>` per node. Children of a container are clipped to its unrotated box. The `node` class on the group
 * is a contract: DiagramCanvas reads it to tell a press on a node from a press on an edge or on empty canvas.
 *
 * Memoised (checklist item 29): `DiagramSvg`'s `drawn` rebuilds fresh `<NodeView>` elements for every *visible* node
 * whenever the visible set changes at all - not just the ones entering or leaving - since it recomputes from
 * `index.rootNodes` rather than diffing the old and new sets. Every prop here (`node`, `style`, `measure`,
 * `breakLines`, `idPrefix`) is a stable reference across such a recompute unless that specific node's own data
 * actually changed (documented at each source: `resolveStyles`, `indexDocument`), and `hideLabel`/`simplified`/
 * `container`/`unplaced` are primitives, so the default shallow comparison correctly bails for every node whose
 * visibility didn't just change, leaving only the boundary crossers to actually re-render (pinned in
 * `DiagramSvg.reconcile.test.tsx`, which spies on `NodeViewInner` - the unmemoised body exported alongside `NodeView`
 * so a render-count test can tell whether `memo` actually bailed rather than just calling the exotic component).
 */
export function NodeViewInner({ node, style, container, measure, breakLines, hideLabel, editingPart, simplified, imageHref, idPrefix, children, unplaced }: NodeViewProps) {
  const clipId = `${idPrefix}clip-${node.id}`;
  const { x, y, w, h } = node.geometry;
  return (
    <g data-id={node.id} data-kind={node.kind} data-unplaced={unplaced ? 'true' : undefined} className="node">
      <NodeBody node={node} style={style} container={container} measure={measure} breakLines={breakLines} hideLabel={hideLabel} editingPart={editingPart} simplified={simplified} imageHref={imageHref} />
      {children !== undefined && children !== null && children !== false && (
        <>
          <clipPath id={clipId}>
            <rect x={fmt(x)} y={fmt(y)} width={fmt(w)} height={fmt(h)} />
          </clipPath>
          <g className="children" clipPath={`url(#${clipId})`}>
            {children}
          </g>
        </>
      )}
    </g>
  );
}

export const NodeView = memo(NodeViewInner);
