import { describe, expect, it } from 'vitest';
import { parseDocument } from '../model/parse.ts';
import type { Edge, Geometry, Node } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';
import { routingSnapshot } from './edgeRoutes.ts';
import { attachmentAt } from './ports.ts';
import { routeDocumentEdges } from './routeDocument.ts';

const meta = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' };

// Two tables, 270 x 92 each, the child left and the parent lower right. Row centres are 42, 60 and 78 below a table's top.
const orderBox: Geometry = { x: 0, y: 0, w: 270, h: 92 };
const order: Node = { id: 't_order', kind: 'entity', label: 'Order', shape: 'custom:er/entity', geometry: orderBox, props: { columns: ['PK Id int not null', 'FK CustomerId int not null', 'Placed datetime2 not null'] } };
const customer: Node = { id: 't_customer', kind: 'entity', label: 'Customer', shape: 'custom:er/entity', geometry: { x: 500, y: 200, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'Name nvarchar(100) not null', 'Email nvarchar(100) null'] } };

function edge(mode: 'orthogonal' | 'straight' | 'curved', from: string | undefined, to: string | undefined, extra: Partial<Edge> = {}): Edge {
  return {
    id: 'e_fk',
    kind: 'association',
    from: { node: 't_order', ...(from === undefined ? {} : { port: from }) },
    to: { node: 't_customer', ...(to === undefined ? {} : { port: to }) },
    routing: { mode },
    ...extra,
  };
}

function docOf(...edges: Edge[]) {
  return parseDocument({ id: 'doc_cols', schemaVersion: 1, title: 'Cols', type: 'architecture', nodes: [order, customer], edges, meta });
}

describe('an edge on column rows, straight', () => {
  it('leaves the child’s FK row and arrives at the parent’s key row, on the facing sides', () => {
    const points = indexDocument(docOf(edge('straight', 'col:CustomerId', 'col:Id'))).edgePaths.get('e_fk')?.points;
    // Order's CustomerId row is 60 below its top (right side, x = 270); Customer's Id row is 200 + 42 (left side, x = 500).
    expect(points).toEqual([{ x: 270, y: 60 }, { x: 500, y: 242 }]);
  });

  it('attaches to the far side when the other table is on the other side', () => {
    const swapped = docOf(edge('straight', 'col:CustomerId', 'col:Id', { from: { node: 't_customer', port: 'col:Name' }, to: { node: 't_order', port: 'col:Placed' } }));
    // Customer's Name row (200 + 60) on its left edge, Order's Placed row (78) on its right edge.
    expect(indexDocument(swapped).edgePaths.get('e_fk')?.points).toEqual([{ x: 500, y: 260 }, { x: 270, y: 78 }]);
  });

  it('falls back to the table box when a column does not exist, and only that end', () => {
    const points = indexDocument(docOf(edge('straight', 'col:Nope', 'col:Id'))).edgePaths.get('e_fk')?.points;
    const [start, end] = points ?? [];
    expect(end).toEqual({ x: 500, y: 242 });
    // The unresolved end is where an edge with no port at all leaves the table, not on any row.
    const plain = indexDocument(docOf(edge('straight', undefined, 'col:Id'))).edgePaths.get('e_fk')?.points[0];
    expect(start).toEqual(plain);
    expect(start).not.toEqual({ x: 270, y: 60 });
  });

  it('keeps an edge with no column ports exactly as it was', () => {
    const before = indexDocument(docOf(edge('straight', undefined, undefined))).edgePaths.get('e_fk')?.points;
    // Centre to centre, clipped at each box's outline (here where it meets the child's bottom edge): not on a row.
    expect(before?.[0]).toEqual({ x: 250, y: 92 });
    expect(before?.[1]).toEqual({ x: 520, y: 200 });
  });

  it('nudges two edges on the same row apart along the side, as it does for a shared declared port', () => {
    const second: Edge = { ...edge('straight', 'col:CustomerId', 'col:Name'), id: 'e_two' };
    const index = indexDocument(docOf(edge('straight', 'col:CustomerId', 'col:Id'), second));
    expect(index.edgePaths.get('e_fk')?.points[0]).toEqual({ x: 270, y: 60 });
    expect(index.edgePaths.get('e_two')?.points[0]).toEqual({ x: 270, y: 66 });
  });
});

describe('an edge on column rows, orthogonal', () => {
  it('routes from the FK row to the key row, leaving and arriving horizontally', () => {
    const index = indexDocument(docOf(edge('orthogonal', 'col:CustomerId', 'col:Id')));
    const snapshot = routingSnapshot(index);
    const request = snapshot.inputs.get('e_fk')?.request;
    expect(request?.start).toEqual({ point: { x: 270, y: 60 }, side: 'right', nodeId: 't_order' });
    expect(request?.end).toEqual({ point: { x: 500, y: 242 }, side: 'left', nodeId: 't_customer' });
    const route = routeDocumentEdges(index).get('e_fk');
    expect(route?.points[0]).toEqual({ x: 270, y: 60 });
    expect(route?.points[route.points.length - 1]).toEqual({ x: 500, y: 242 });
    expect(route?.points[1]?.y).toBe(60);
    expect(route?.points[route.points.length - 2]?.y).toBe(242);
  });

  it('attaches at the table, floating, when the column does not exist', () => {
    const request = routingSnapshot(indexDocument(docOf(edge('orthogonal', 'col:Nope', 'col:Id')))).inputs.get('e_fk')?.request;
    // The unresolved end floats exactly as an end with no port does (a side midpoint, not a row); the other is still its row.
    const floating = routingSnapshot(indexDocument(docOf(edge('orthogonal', undefined, 'col:Id')))).inputs.get('e_fk')?.request;
    expect(request?.start).toEqual(floating?.start);
    expect(request?.start.point.y).not.toBe(60);
    expect(request?.end.point).toEqual({ x: 500, y: 242 });
  });

  it('follows the row when the table gains a column above it', () => {
    const grown: Node = { ...order, geometry: { ...orderBox, h: 110 }, props: { columns: ['PK Id int not null', 'Region text', 'FK CustomerId int not null', 'Placed datetime2 not null'] } };
    const document = parseDocument({ id: 'doc_cols', schemaVersion: 1, title: 'Cols', type: 'architecture', nodes: [grown, customer], edges: [edge('orthogonal', 'col:CustomerId', 'col:Id')], meta });
    expect(routingSnapshot(indexDocument(document)).inputs.get('e_fk')?.request.start.point).toEqual({ x: 270, y: 78 });
  });

  it('routes a self-referencing foreign key out of one side and back into it', () => {
    const document = parseDocument({
      id: 'doc_cols',
      schemaVersion: 1,
      title: 'Cols',
      type: 'architecture',
      nodes: [{ ...order, props: { columns: ['PK Id int not null', 'FK ParentId int null'] } }],
      edges: [{ id: 'e_fk', kind: 'association', from: { node: 't_order', port: 'col:ParentId' }, to: { node: 't_order', port: 'col:Id' }, routing: { mode: 'orthogonal' } }],
      meta,
    });
    const route = routeDocumentEdges(indexDocument(document)).get('e_fk');
    expect(route?.points[0]).toEqual({ x: 270, y: 60 });
    expect(route?.points[route.points.length - 1]).toEqual({ x: 270, y: 42 });
    expect(route?.points.some((p) => p.x > 270)).toBe(true);
  });
});

describe('an edge between column rows of tables stacked one above the other (QA: the foreign key hooked round the table)', () => {
  const cols = ['PK Id int not null', 'FK CustomerId int not null', 'Placed datetime2 not null'];
  const table = (id: string, x: number, y: number, w = 270): Node => ({ id, kind: 'entity', label: id, shape: 'custom:er/entity', geometry: { x, y, w, h: 92 }, props: { columns: cols } });
  const stacked = (topX: number, bottomX: number, mode: 'orthogonal' | 'straight' = 'orthogonal', bottomW = 270) =>
    parseDocument({
      id: 'doc_stack',
      schemaVersion: 1,
      title: 'Stack',
      type: 'architecture',
      nodes: [table('a', topX, 0), table('b', bottomX, 260, bottomW)],
      edges: [{ id: 'e', kind: 'association', from: { node: 'a', port: 'col:CustomerId' }, to: { node: 'b', port: 'col:Id' }, routing: { mode } }],
      meta,
    });

  it('leaves and enters by the same side, so the route is one short bracket rather than a hook', () => {
    // b sits left of a but their boxes overlap in x; equal widths make the two sides equally close, and a tie is the right side.
    const index = indexDocument(stacked(100, 20));
    const request = routingSnapshot(index).inputs.get('e')?.request;
    expect(request?.start).toEqual({ point: { x: 370, y: 60 }, side: 'right', nodeId: 'a' });
    expect(request?.end).toEqual({ point: { x: 290, y: 302 }, side: 'right', nodeId: 'b' });
    expect(routeDocumentEdges(index).get('e')?.points).toEqual([{ x: 370, y: 60 }, { x: 382, y: 60 }, { x: 382, y: 302 }, { x: 290, y: 302 }]);
  });

  it('takes the left side when that is where the two tables’ edges are closest, from either end', () => {
    // b is narrower and inside a's span: the left edges are 30 apart, the right edges 90.
    const index = indexDocument(stacked(100, 130, 'orthogonal', 150));
    const request = routingSnapshot(index).inputs.get('e')?.request;
    expect(request?.start).toEqual({ point: { x: 100, y: 60 }, side: 'left', nodeId: 'a' });
    expect(request?.end).toEqual({ point: { x: 130, y: 302 }, side: 'left', nodeId: 'b' });
    const points = routeDocumentEdges(index).get('e')?.points ?? [];
    // The bracket stands ROUTE_CLEARANCE (12) off the table's edge.
    expect(points[1]).toEqual({ x: 88, y: 60 });
    expect(points.every((p) => p.x <= 130)).toBe(true);
  });

  it('draws the straight line between the same two sides, as the router does', () => {
    expect(indexDocument(stacked(100, 20, 'straight')).edgePaths.get('e')?.points).toEqual([{ x: 370, y: 60 }, { x: 290, y: 302 }]);
  });

  it('keeps facing sides when the tables are side by side, as before', () => {
    const request = routingSnapshot(indexDocument(stacked(0, 300))).inputs.get('e')?.request;
    expect(request?.start.side).toBe('right');
    expect(request?.end.side).toBe('left');
  });
});

describe('connector hit-testing on an entity', () => {
  const placed = { ...order, geometry: orderBox };

  it('attaches to the row under the pointer', () => {
    expect(attachmentAt(placed, { x: 200, y: 60 }, 10).id).toBe('col:CustomerId');
  });

  it('attaches to the nearest table port over the name band, as before', () => {
    expect(attachmentAt(placed, { x: 135, y: 5 }, 10).id).toBe('top');
  });

  it('attaches to a table port outside the box, as before', () => {
    expect(attachmentAt(placed, { x: 275, y: 46 }, 10).id).toBe('right');
  });

  it('never offers a column on a node that is not a table', () => {
    const box = { ...placed, shape: 'rectangle' };
    expect(attachmentAt(box, { x: 200, y: 60 }, 10).id).not.toMatch(/^col:/);
  });
});
