/** Reproducible full-feature editing session shared with the C# DSL round-trip tests.
 * Regenerate docs/examples/editor-session.json with web/scripts/generate-editor-session.ts.
 */
import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json' with { type: 'json' };
import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json' with { type: 'json' };
import { connect } from './connect.ts';
import { addNodes, applyGroup, documentToYDoc, removeElements, setEdgeKind, setEdgeLabel, setEdgeWaypoints, setElementOrder, setNodeGeometries, setNodeLabel, yDocToDocument } from './editor.ts';
import { group } from './grouping.ts';
import type { Document } from './types.ts';

export function editorSessionFixture(): Document {
  const doc = documentToYDoc({
    id: 'doc_editor_session', schemaVersion: 1, title: 'Full editing session', type: 'freeform', nodes: [], edges: [],
    meta: { createdAt: '2026-09-24T12:00:00.000Z', updatedAt: '2026-09-24T12:00:00.000Z', createdBy: 'editor' },
  });
  const kinds = new Map([...architecture.nodeKinds, ...flowchart.nodeKinds].map((kind) => [kind.name, kind]));
  let index = 0;
  for (const kind of kinds.values()) {
    addNodes(doc, [{ id: `n_${kind.name}`, kind: kind.name, label: kind.name, shape: kind.defaultShape,
      geometry: { x: index * 200, y: 60, w: 160, h: 80 },
      props: { nested: { data: [null, true, 3.25, 'quoted "text"'] } }, style: { fill: 'token:accent', strokeWidth: 2, dash: 'dashed' } }]);
    index++;
  }
  for (const shape of ['hexagon', 'parallelogram', 'custom:library/name']) {
    addNodes(doc, [{ id: `n_shape_${String(index)}`, kind: 'note', label: shape, shape, geometry: { x: index++ * 200, y: 300, w: 140, h: 90 } }]);
  }
  setNodeGeometries(doc, new Map([['n_service', { x: -37.5, y: 126.25, w: 237, h: 93, rotation: 37 }]]));
  setNodeLabel(doc, 'n_service', 'API "renamed"\nUnicode £ →');
  const grouped = group(yDocToDocument(doc), new Set(['n_service', 'n_database']), 40, () => 'c_inner');
  if (grouped) applyGroup(doc, grouped);
  const nested = group(yDocToDocument(doc), new Set(['c_inner', 'n_queue']), 40, () => 'c_outer');
  if (nested) applyGroup(doc, nested);
  for (const kind of architecture.edgeKinds) {
    const id = connect(doc, { from: { node: 'n_service', port: { id: 'right', x: 1, y: 0.5 } }, to: { node: 'n_database', port: { id: 'left', x: 0, y: 0.5 } } }, () => `e_${kind.name}`);
    setEdgeKind(doc, id, kind.name);
    setEdgeLabel(doc, id, `${kind.name} edge "label"`);
    setEdgeWaypoints(doc, new Map([[id, [{ x: 250.5, y: 160 }, { x: 260, y: 180 }]]]));
  }
  addNodes(doc, [{ id: 'n_deleted', kind: 'note', shape: 'rectangle', label: 'Remove me' }]);
  removeElements(doc, new Set(['n_deleted']));
  const current = yDocToDocument(doc);
  setElementOrder(doc, { nodes: [...current.nodes].reverse(), edges: [...current.edges].reverse() });
  return yDocToDocument(doc);
}
