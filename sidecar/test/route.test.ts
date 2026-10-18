import { describe, expect, it } from 'vitest';
import { routeDocument } from '../src/route/methods.ts';
import { ErrorCodes, RpcError } from '../src/rpc.ts';
import type { Document } from '../src/model/types.ts';
import { loadExample } from './helpers.ts';

type Route = { status: string; points: { x: number; y: number }[] };

/** Two tables diagonal from each other with a wall between them, joined by one edge in `mode`. */
function walled(mode: string, extra: object = {}): Document {
  const example = loadExample();
  return {
    ...example,
    type: 'freeform',
    nodes: [
      { id: 'a', kind: 'rectangle', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 60 } },
      { id: 'b', kind: 'rectangle', label: 'B', shape: 'rectangle', geometry: { x: 400, y: 300, w: 100, h: 60 } },
      { id: 'wall', kind: 'rectangle', label: 'Wall', shape: 'rectangle', geometry: { x: 180, y: 120, w: 140, h: 120 } },
    ],
    edges: [{ id: 'e', kind: 'flow', from: { node: 'a' }, to: { node: 'b' }, routing: { mode, ...extra } }],
  } as Document;
}

const axisAligned = (points: Route['points']) => points.every((p, i) => i === 0 || p.x === points[i - 1]!.x || p.y === points[i - 1]!.y);

describe('route.document', () => {
  it('routes an orthogonal edge with no waypoints along axis-aligned segments', () => {
    const { routes } = routeDocument({ document: walled('orthogonal') });
    const route = routes['e'] as Route;
    expect(route.status).toBe('routed');
    expect(route.points.length).toBeGreaterThan(2);
    expect(axisAligned(route.points)).toBe(true);
  });

  it('leaves straight, curved and pinned edges to the renderer', () => {
    expect(routeDocument({ document: walled('straight') }).routes).toEqual({});
    expect(routeDocument({ document: walled('curved') }).routes).toEqual({});
    expect(routeDocument({ document: walled('orthogonal', { waypoints: [{ x: 50, y: 400 }] }) }).routes).toEqual({});
  });

  it('attaches an end on a table column to that row, and a missing column to the table', () => {
    const table = (id: string, x: number, y: number, columns: string[]) => ({ id, kind: 'entity', label: id, shape: 'custom:er/entity', geometry: { x, y, w: 270, h: 92 }, props: { columns } });
    const document = (from: string) =>
      ({
        id: 'doc_cols',
        schemaVersion: 1,
        title: 'Cols',
        type: 'architecture',
        nodes: [table('t_order', 0, 0, ['PK Id int not null', 'FK CustomerId int not null']), table('t_customer', 500, 200, ['PK Id int not null'])],
        edges: [{ id: 'e', kind: 'association', from: { node: 't_order', port: from }, to: { node: 't_customer', port: 'col:Id' }, routing: { mode: 'orthogonal' } }],
        meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
      }) as Document;

    const onRow = routeDocument({ document: document('col:CustomerId') }).routes['e'] as Route;
    expect(onRow.points[0]).toEqual({ x: 270, y: 60 });
    expect(onRow.points[onRow.points.length - 1]).toEqual({ x: 500, y: 242 });
    expect(axisAligned(onRow.points)).toBe(true);

    const missing = routeDocument({ document: document('col:Nope') }).routes['e'] as Route;
    expect(missing.points[0]?.y).not.toBe(60);
  });

  it('rejects a param that is not a document', () => {
    expect(() => routeDocument({ document: { nodes: [], edges: 'x' } })).toThrow(RpcError);
    try {
      routeDocument(null);
    } catch (error) {
      expect((error as RpcError).code).toBe(ErrorCodes.invalidParams);
    }
  });
});
