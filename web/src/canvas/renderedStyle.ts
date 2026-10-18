/**
 * The per-node and edge-label style decisions the SVG render makes beyond resolveStyle.ts, shared so an overlay drawn
 * over an element (the label editors) takes the same font and colours the render uses.
 */

import { bpmnShape, labelOutside } from '../model/bpmnShapes.ts';
import type { Document, Id, Node } from '../model/types.ts';
import type { DiagramTheme, NodeKindStyle, Surface } from '../theme/diagramTheme.ts';
import { effectiveTheme, nodeKindDefault, resolveNodeStyle, surfaceBackground, surfaceTextColor } from '../theme/resolveStyle.ts';
import type { DocumentIndex } from './documentIndex.ts';

/**
 * True when the surface, not the theme, decides this node's label colour: the label is drawn straight on the
 * surface (see-through fill, the `text` shape (which paints no fill whatever its kind's fill says, so a rectangle-kind
 * heading drawn as text sits on the surface too), or a BPMN shape whose label sits below its outline rather than on its fill, and the node
 * sits on the surface rather than inside a container that paints behind it) and nothing gave it a colour. The two ways a document can give it one are asked about differently on
 * purpose: an element's own style counts by presence, because naming even the kind's own colour is a deliberate
 * act, while a theme override counts only where it actually differs from the workspace theme, so an override
 * that changed nothing — a mistyped `token:` name — leaves the surface to keep the label legible.
 */
function labelSitsOnSurface(workspace: DiagramTheme, effective: DiagramTheme, node: Node, style: NodeKindStyle, nested: boolean): boolean {
  const bpmn = bpmnShape(node.shape);
  const labelOffFill = style.fill === 'transparent' || node.shape === 'text' || (bpmn !== undefined && labelOutside(bpmn));
  return (
    !nested &&
    labelOffFill &&
    node.style?.textColor === undefined &&
    nodeKindDefault(effective, node.kind).textColor === nodeKindDefault(workspace, node.kind).textColor
  );
}

/**
 * True when a container that paints a fill sits behind `node`, so the surface never backs its label: some ancestor's
 * resolved fill is not see-through. A see-through container (the shipped themes' `container` kind) paints nothing, so
 * a label inside it still sits on the surface and takes the surface's text colour.
 */
export function sitsOnFilledContainer(effective: DiagramTheme, node: Node, nodesById: ReadonlyMap<Id, Node>): boolean {
  const seen = new Set<Id>([node.id]);
  let parent = node.parent === undefined ? undefined : nodesById.get(node.parent);
  while (parent !== undefined && !seen.has(parent.id)) {
    seen.add(parent.id);
    if (resolveNodeStyle(effective, parent).fill !== 'transparent' && parent.shape !== 'text') {
      return true;
    }
    parent = parent.parent === undefined ? undefined : nodesById.get(parent.parent);
  }
  return false;
}

/** The style with the surface's text colour when `labelSitsOnSurface` says the surface decides it. */
export function withSurfaceText(workspace: DiagramTheme, effective: DiagramTheme, node: Node, style: NodeKindStyle, nested: boolean, surfaceText: string | undefined): NodeKindStyle {
  return surfaceText !== undefined && labelSitsOnSurface(workspace, effective, node, style, nested) ? { ...style, textColor: surfaceText } : style;
}

/** One node's style as this render resolves it on `surface`; the label editor takes its font and text colour here. */
export function resolveRenderedNodeStyle(workspace: DiagramTheme, document: Document, index: DocumentIndex, node: Node, surface: Surface = 'canvas'): NodeKindStyle {
  const effective = effectiveTheme(workspace, document);
  const nested = sitsOnFilledContainer(effective, node, index.nodesById);
  return withSurfaceText(workspace, effective, node, resolveNodeStyle(effective, node), nested, surfaceTextColor(effective, surface));
}

/** How an edge label is drawn: straight on the surface, over a plate of the surface's own colour. */
export interface EdgeLabelStyle {
  font: string;
  /** The plate behind the text: the surface the edge is drawn on. */
  background: string;
  colour: string;
}

/** Edge label style on `surface` for an already effective theme (theme plus the document's overrides). */
export function edgeLabelStyle(effective: DiagramTheme, surface: Surface = 'canvas'): EdgeLabelStyle {
  return {
    font: effective.fonts.label,
    background: surfaceBackground(effective, surface),
    colour: surfaceTextColor(effective, surface) ?? effective.nodes.default.textColor,
  };
}
