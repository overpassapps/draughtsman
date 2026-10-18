/**
 * "Connect selected shapes" (QA S5): the keyboard way to make connectors, for anyone who cannot or would rather not drag
 * from a shape's handle. The selected shapes are joined in the order they were selected, A to B, B to C and so on, each
 * pair on the ports that face each other (canvas/ports.ts `pairPorts`, the same pairing a drop on a shape's body makes),
 * and a pair that already has a connector from the first to the second is left as it is. Pure over the document and the
 * selection, so the order and the ports are testable without an editor.
 */

import { indexDocument } from '../canvas/documentIndex.ts';
import { connectablePorts, pairPorts } from '../canvas/ports.ts';
import type { Connection } from '../model/connect.ts';
import type { Document, Id } from '../model/types.ts';

/** The connectors that join the selected shapes, in selection order; none when fewer than two shapes with a position are selected. */
export function connectionsForSelection(document: Document, selection: ReadonlySet<Id>): Connection[] {
  const index = indexDocument(document);
  // Only drawn shapes count: an edge id or a message in the selection is not something to join.
  const ids = [...selection].filter((id) => index.placed.has(id));
  const connections: Connection[] = [];
  for (let i = 1; i < ids.length; i++) {
    const from = index.placed.get(ids[i - 1] ?? '');
    const to = index.placed.get(ids[i] ?? '');
    if (from === undefined || to === undefined || from.id === to.id) {
      continue;
    }
    if (document.edges.some((edge) => edge.from.node === from.id && edge.to.node === to.id)) {
      continue;
    }
    // Neither end is pinned: both are bodies, so each takes the side that faces the other (the placeholder port is replaced).
    const [first] = connectablePorts(from);
    const [other] = connectablePorts(to);
    if (first === undefined || other === undefined) {
      continue;
    }
    const ports = pairPorts({ node: from, hit: { port: first, pinned: false } }, { node: to, hit: { port: other, pinned: false } });
    connections.push({ from: { node: from.id, port: ports.a }, to: { node: to.id, port: ports.b } });
  }
  return connections;
}
