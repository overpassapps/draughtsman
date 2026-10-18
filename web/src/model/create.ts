/**
 * The node a create action places (design/atlas/shape-connector-palette.md: "click places at the viewport centre
 * offset by one grid step from the last placed node and selects it, so repeated clicks stack neatly"). Pure: the
 * caller owns the Y.Doc write, the undo step and the selection.
 *
 * Kind first, shape second: each document type places its everyday kind with that kind's default shape from the
 * vocabulary in src/Draughtsman.Core/Schemas/<type>.json, which create.test.ts checks this table against. Freeform
 * has no vocabulary, so it places a plain rectangle. The id comes from `paste`, which already guarantees one unused
 * by the document.
 *
 * A caller with a specific kind and shape in hand (the shape panel, design/atlas/shape-connector-palette.md) passes
 * `template` to place that instead of the document type's default (at `template.size` when it names one); the plain "Add shape" rail action omits it and
 * gets the same node as before.
 */

import { CLIPBOARD_FORMAT, CLIPBOARD_VERSION, paste, randomIdGenerator, type IdGenerator } from './clipboard.ts';
import type { Document, DocumentType, Node, Point, Props } from './types.ts';

export interface NodeTemplate {
  kind: string;
  shape: string;
  label: string;
  /** The size to place the node at when it is not the everyday 160 x 80 box (a BPMN event is a small circle). */
  size?: { w: number; h: number };
  /** Props the node is placed with (a UML class's starter members); copied, never shared with the template. */
  props?: Props;
}

/** What a create places for each document type. */
export const DEFAULT_NODE: Readonly<Record<DocumentType, NodeTemplate>> = {
  architecture: { kind: 'service', shape: 'rounded', label: 'Service' },
  flowchart: { kind: 'process', shape: 'rounded', label: 'Process' },
  freeform: { kind: 'shape', shape: 'rectangle', label: 'Shape' },
  // Placed through the palette or the DSL only: a sequence diagram derives its geometry (model/sequenceScene.ts).
  sequence: { kind: 'participant', shape: 'rectangle', label: 'Participant' },
};

/** Size of a created node, in canvas units: the size of the example document's everyday nodes. */
export const NEW_NODE_SIZE = { w: 160, h: 80 };

/**
 * A new node of `template` (the document type's default kind when omitted), centred on `centre` (to the nearest
 * canvas unit), or stepped down and right by `step` for as long as a node already sits at that exact spot, so
 * repeated creates at the same view fan out instead of hiding each other.
 */
export function createNode(
  document: Document,
  centre: Point,
  step: number,
  generateId: IdGenerator = randomIdGenerator,
  template: NodeTemplate = DEFAULT_NODE[document.type],
): Node {
  const size = template.size ?? NEW_NODE_SIZE;
  const taken = new Set(document.nodes.flatMap((node) => (node.geometry ? [`${String(node.geometry.x)},${String(node.geometry.y)}`] : [])));
  // Whole canvas units: the view centre is rarely a round number, and the model should not carry its fractions.
  let x = Math.round(centre.x - size.w / 2);
  let y = Math.round(centre.y - size.h / 2);
  // Bounded by the node count: each step can only land on a spot some existing node occupies.
  for (let i = 0; i <= document.nodes.length && taken.has(`${String(x)},${String(y)}`); i++) {
    x += step;
    y += step;
  }
  const draft: Node = { id: 'draft', kind: template.kind, label: template.label, shape: template.shape, geometry: { x, y, w: size.w, h: size.h }, ...(template.props === undefined ? {} : { props: structuredClone(template.props) }) };
  const [node] = paste(document, { format: CLIPBOARD_FORMAT, version: CLIPBOARD_VERSION, nodes: [draft], edges: [] }, { x: 0, y: 0 }, generateId).nodes;
  if (node === undefined) {
    throw new Error('paste returned no node for a one-node payload');
  }
  return node;
}
