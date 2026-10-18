import { describe, expect, it } from 'vitest';
import { layoutDocument } from '../src/layout/elk.ts';
import { layoutElk } from '../src/layout/methods.ts';
import type { Document } from '../src/model/types.ts';
import { ErrorCodes, RpcError } from '../src/rpc.ts';
import { assertWellFormedLayout, exampleWithoutGeometry, loadExample } from './helpers.ts';

describe('layout.elk', () => {
  it('gives every node of the order-service example geometry with no overlaps', async () => {
    const input = exampleWithoutGeometry();

    const { document, warnings } = await layoutDocument(input, {});

    expect(warnings).toEqual([]);
    expect(document.nodes.map((n) => n.id)).toEqual(input.nodes.map((n) => n.id));
    assertWellFormedLayout(document.nodes);
    for (const node of document.nodes) {
      expect(node.geometry.x).toBeGreaterThanOrEqual(40);
      expect(node.geometry.y).toBeGreaterThanOrEqual(40);
      expect(Number.isInteger(node.geometry.x) && Number.isInteger(node.geometry.y)).toBe(true);
    }
    const byId = new Map(document.nodes.map((n) => [n.id, n]));
    // Layered, top to bottom: the web app feeds the API, which feeds the database.
    expect(byId.get('n_web')!.geometry.y).toBeLessThan(byId.get('n_api')!.geometry.y);
    expect(byId.get('n_api')!.geometry.y).toBeLessThan(byId.get('n_db')!.geometry.y);
    expect(byId.get('n_api')!.parent).toBe('c_vpc');
    // Everything but geometry and edge waypoints is untouched.
    expect(byId.get('n_api')!.props).toEqual({ owner: 'platform' });
    expect(byId.get('n_api')!.ports?.map((p) => p.id)).toEqual(['in', 'db', 'cache']);
    expect(document.meta).toEqual(input.meta);
    expect(document.edges.map((e) => e.id)).toEqual(['e_1', 'e_2', 'e_3']);
  });

  it('keeps sizes that were given, replaces positions, and treats zero size as missing', async () => {
    const input = loadExample();
    input.nodes.find((n) => n.id === 'n_db')!.geometry = { x: 0, y: 0, w: 0, h: 0 };
    input.nodes.find((n) => n.id === 'n_web')!.geometry = { x: 999, y: 999, w: 300, h: 70, rotation: 15 };

    const { document } = await layoutDocument(input, {});

    const web = document.nodes.find((n) => n.id === 'n_web')!;
    expect(web.geometry.w).toBe(300);
    expect(web.geometry.h).toBe(70);
    expect(web.geometry.rotation).toBe(15);
    expect(web.geometry.x).not.toBe(999);
    const db = document.nodes.find((n) => n.id === 'n_db')!;
    expect(db.geometry.w).toBe(160);
    expect(db.geometry.h).toBe(90);
    assertWellFormedLayout(document.nodes);
  });

  it('produces orthogonal waypoints for layered and drops stale ones', async () => {
    const input = loadExample();
    // e_3 carries a waypoint from the hand-drawn layout; it must not survive a relayout unchanged.
    const { document } = await layoutDocument(input, { algorithm: 'layered' });

    const e3 = document.edges.find((e) => e.id === 'e_3')!;
    expect(e3.routing?.mode).toBe('orthogonal');
    expect(e3.routing?.waypoints).not.toEqual([{ x: 480, y: 240 }]);
    for (const edge of document.edges) {
      for (const point of edge.routing?.waypoints ?? []) {
        expect(Number.isInteger(point.x) && Number.isInteger(point.y)).toBe(true);
      }
    }
  });

  it('lays out left to right when asked', async () => {
    const { document } = await layoutDocument(exampleWithoutGeometry(), { direction: 'right' });

    const byId = new Map(document.nodes.map((n) => [n.id, n]));
    expect(byId.get('n_web')!.geometry.x + byId.get('n_web')!.geometry.w).toBeLessThanOrEqual(byId.get('n_api')!.geometry.x);
    assertWellFormedLayout(document.nodes);
  });

  it('uses stress for freeform documents and still fills every geometry', async () => {
    const input: Document = {
      ...exampleWithoutGeometry(),
      type: 'freeform',
      nodes: exampleWithoutGeometry().nodes.map((n) => {
        const { parent: _parent, ...rest } = n;
        return rest;
      }).filter((n) => n.id !== 'c_vpc'),
    };

    const { document, warnings } = await layoutDocument(input, {});

    expect(warnings).toEqual([]);
    assertWellFormedLayout(document.nodes);
    expect(document.nodes).toHaveLength(4);
    for (const edge of document.edges) {
      expect(edge.routing?.waypoints).toBeUndefined();
    }
  });

  it('lays out a freeform document with a container using stress', async () => {
    const input: Document = { ...exampleWithoutGeometry(), type: 'freeform' };

    const { document, warnings } = await layoutDocument(input, {});

    // e_1 joins the web app (top level) to the API (inside the VPC); stress cannot route across levels.
    expect(warnings).toEqual(['edge e_1 crosses containers, which the stress algorithm does not route; left straight']);
    assertWellFormedLayout(document.nodes);
    expect(document.nodes.find((n) => n.id === 'c_vpc')!.geometry.w).toBeGreaterThan(160);
  });

  it('warns about edges it cannot route instead of failing', async () => {
    const input = exampleWithoutGeometry();
    input.edges.push(
      { id: 'e_missing', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_ghost' } },
      { id: 'e_self', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_web' } },
      { id: 'e_container', kind: 'flow', from: { node: 'c_vpc' }, to: { node: 'n_api' }, routing: { mode: 'straight', waypoints: [{ x: 1, y: 1 }] } },
    );
    input.nodes.push({ id: 'n_orphan', kind: 'service', label: 'Orphan', shape: 'rounded', parent: 'c_nowhere' });

    const { document, warnings } = await layoutDocument(input, {});

    expect(warnings).toEqual([
      'node n_orphan has parent c_nowhere which does not exist; laid out at top level',
      'edge e_missing references a missing node (n_ghost); left unrouted',
      'edge e_self is a self-loop; left unrouted',
      'edge e_container connects a container to a node inside it; left unrouted',
    ]);
    assertWellFormedLayout(document.nodes.filter((n) => n.id !== 'n_orphan'));
    expect(document.nodes.find((n) => n.id === 'n_orphan')!.geometry.w).toBe(160);
    expect(document.edges.find((e) => e.id === 'e_container')!.routing).toEqual({ mode: 'straight' });
  });

  it('leaves an edge on an entity column row unpinned, since the shared router places that end', async () => {
    const table = (id: string): Document['nodes'][number] => ({ id, kind: 'entity', label: id, shape: 'custom:er/entity', props: { columns: ['PK Id int not null', 'FK ParentId int'] } });
    const doc: Document = {
      ...loadExample(),
      nodes: [table('t_a'), table('t_b'), table('t_c')],
      edges: [
        { id: 'e_ab', kind: 'association', from: { node: 't_a' }, to: { node: 't_b' }, routing: { mode: 'orthogonal' } },
        { id: 'e_bc', kind: 'association', from: { node: 't_b' }, to: { node: 't_c' }, routing: { mode: 'orthogonal' } },
        // Spans two layers, so ELK bends it; the same span on a column row must come back without bends.
        { id: 'e_plain', kind: 'association', from: { node: 't_a' }, to: { node: 't_c' }, routing: { mode: 'orthogonal' } },
        { id: 'e_col', kind: 'association', from: { node: 't_a', port: 'col:Id' }, to: { node: 't_c', port: 'col:ParentId' }, routing: { mode: 'orthogonal' } },
      ],
    };

    const { document } = await layoutDocument(doc, { algorithm: 'layered' });

    const byId = new Map(document.edges.map((e) => [e.id, e]));
    expect(byId.get('e_plain')!.routing?.waypoints?.length ?? 0).toBeGreaterThan(0);
    expect(byId.get('e_col')!.routing).toEqual({ mode: 'orthogonal' });
    expect(byId.get('e_col')!.from.port).toBe('col:Id');
  });

  // S7: an imported schema (tables joined by labelled foreign keys) used to come back with tables ~40 px apart and the
  // relationships squeezed through the gap, their labels floating over the tables. Room between boxes is the cure.
  it('leaves room between tables that are joined by labelled relationships', async () => {
    const table = (id: string, columns: string[]): Document['nodes'][number] => ({ id, kind: 'entity', label: id, shape: 'custom:er/entity', props: { columns } });
    const doc: Document = {
      ...loadExample(),
      nodes: [
        table('t_customers', ['PK Id int not null', 'Name text']),
        table('t_orders', ['PK Id int not null', 'FK CustomerId int not null']),
        table('t_items', ['PK Id int not null', 'FK OrderId int not null', 'FK ProductId int not null']),
        table('t_products', ['PK Id int not null', 'Title text']),
      ],
      edges: [
        { id: 'e_1', kind: 'association', label: 'CustomerId', from: { node: 't_orders' }, to: { node: 't_customers' }, routing: { mode: 'orthogonal' } },
        { id: 'e_2', kind: 'association', label: 'OrderId', from: { node: 't_items' }, to: { node: 't_orders' }, routing: { mode: 'orthogonal' } },
        { id: 'e_3', kind: 'association', label: 'ProductId', from: { node: 't_items' }, to: { node: 't_products' }, routing: { mode: 'orthogonal' } },
      ],
    };

    const { document } = await layoutDocument(doc, { algorithm: 'layered' });

    const boxes = document.nodes.map((n) => n.geometry);
    let tightest = Infinity;
    for (let i = 0; i < boxes.length; i++) {
      for (let j = i + 1; j < boxes.length; j++) {
        const a = boxes[i]!;
        const b = boxes[j]!;
        const gapX = Math.max(b.x - (a.x + a.w), a.x - (b.x + b.w));
        const gapY = Math.max(b.y - (a.y + a.h), a.y - (b.y + b.h));
        tightest = Math.min(tightest, Math.max(gapX, gapY));
      }
    }
    expect(tightest).toBeGreaterThanOrEqual(120);
  });

  // S7: an imported flowchart's edges carry no routing, which draws as a straight line between box centres and slants
  // wherever a diamond sits over a box that is not exactly centred under it.
  it('makes every edge of a layered layout orthogonal, even one that arrived with no routing, and leaves a stress layout alone', async () => {
    const plain = (): Document => ({
      ...loadExample(),
      type: 'flowchart',
      nodes: ['a', 'b', 'c'].map((id) => ({ id, kind: 'process', label: id, shape: 'rectangle' })),
      edges: [
        { id: 'e_1', kind: 'flow', from: { node: 'a' }, to: { node: 'b' } },
        { id: 'e_2', kind: 'flow', from: { node: 'b' }, to: { node: 'c' } },
      ],
    });

    const layered = await layoutDocument(plain(), { algorithm: 'layered' });
    expect(layered.document.edges.map((e) => e.routing?.mode)).toEqual(['orthogonal', 'orthogonal']);

    const stress = await layoutDocument(plain(), { algorithm: 'stress' });
    expect(stress.document.edges.every((e) => e.routing?.mode !== 'orthogonal')).toBe(true);
  });

  it('sizes a node whose shape names an inherited object member with the default size', async () => {
    const input = exampleWithoutGeometry();
    input.nodes.push({ id: 'n_proto', kind: 'process', label: 'Proto', shape: 'constructor', ports: [{ id: 'in', x: 0.5, y: 0 }] });
    input.edges.push({ id: 'e_proto', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_proto', port: 'in' } });

    const { document } = await layoutDocument(input, { algorithm: 'layered' });

    const proto = document.nodes.find((n) => n.id === 'n_proto')!;
    expect({ w: proto.geometry.w, h: proto.geometry.h }).toEqual({ w: 160, h: 80 });
    for (const point of document.edges.find((e) => e.id === 'e_proto')?.routing?.waypoints ?? []) {
      expect(Number.isFinite(point.x) && Number.isFinite(point.y)).toBe(true);
    }
    assertWellFormedLayout(document.nodes);
  });

  it('handles nested containers with absolute geometry', async () => {
    const input: Document = {
      id: 'doc_nested',
      schemaVersion: 1,
      title: 'Nested',
      type: 'architecture',
      nodes: [
        { id: 'c_outer', kind: 'container', label: 'Outer', shape: 'rectangle' },
        { id: 'c_inner', kind: 'container', label: 'Inner', shape: 'rectangle', parent: 'c_outer' },
        { id: 'n_a', kind: 'service', label: 'A', shape: 'rounded', parent: 'c_inner' },
        { id: 'n_b', kind: 'service', label: 'B', shape: 'rounded', parent: 'c_inner' },
        { id: 'n_c', kind: 'database', label: 'C', shape: 'cylinder', parent: 'c_outer' },
        { id: 'n_d', kind: 'actor', label: 'D', shape: 'pill' },
      ],
      edges: [
        { id: 'e_ab', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } },
        { id: 'e_bc', kind: 'data', from: { node: 'n_b' }, to: { node: 'n_c' } },
        { id: 'e_da', kind: 'flow', from: { node: 'n_d' }, to: { node: 'n_a' } },
      ],
      meta: { createdAt: '2026-09-21T00:00:00.000Z', updatedAt: '2026-09-21T00:00:00.000Z', createdBy: 'test' },
    };

    const { document, warnings } = await layoutDocument(input, {});

    expect(warnings).toEqual([]);
    assertWellFormedLayout(document.nodes);
    const byId = new Map(document.nodes.map((n) => [n.id, n]));
    expect(byId.get('c_inner')!.geometry.x).toBeGreaterThan(byId.get('c_outer')!.geometry.x);
    expect(byId.get('n_a')!.geometry.x).toBeGreaterThan(byId.get('c_inner')!.geometry.x);
  });

  it('routes an edge into a port declared on a container to the container itself', async () => {
    const input = exampleWithoutGeometry();
    const vpc = input.nodes.find((n) => n.id === 'c_vpc')!;
    vpc.ports = [{ id: 'in', x: 0.5, y: 0 }];
    input.edges.push({ id: 'e_into_vpc', kind: 'flow', from: { node: 'n_web' }, to: { node: 'c_vpc', port: 'in' } });

    const { document, warnings } = await layoutDocument(input, { algorithm: 'layered' });

    expect(warnings).toEqual([]);
    assertWellFormedLayout(document.nodes);
    expect(document.edges.map((e) => e.id)).toContain('e_into_vpc');
    expect(document.nodes.find((n) => n.id === 'c_vpc')!.ports).toEqual([{ id: 'in', x: 0.5, y: 0 }]);
  });

  it('lays out an org chart top-down with tree, a parent above each child and no overlaps', async () => {
    const { document, warnings } = await layoutDocument(orgChart(), { algorithm: 'tree' });

    expect(warnings).toEqual([]);
    assertWellFormedLayout(document.nodes);
    const byId = new Map(document.nodes.map((n) => [n.id, n]));
    // Top-down: every parent sits strictly above its child.
    expect(byId.get('n_ceo')!.geometry.y).toBeLessThan(byId.get('n_cto')!.geometry.y);
    expect(byId.get('n_cto')!.geometry.y).toBeLessThan(byId.get('n_dev')!.geometry.y);
    expect(byId.get('n_ceo')!.geometry.y).toBeLessThan(byId.get('n_cfo')!.geometry.y);
  });

  it('places an assistant on an offset side branch beside its manager under tree, and below it under layered', async () => {
    const tree = await layoutDocument(orgChart(), { algorithm: 'tree' });
    const byId = new Map(tree.document.nodes.map((n) => [n.id, n]));
    const boss = byId.get('n_ceo')!.geometry;
    const assistant = byId.get('n_ea')!.geometry;
    const report = byId.get('n_cto')!.geometry;
    // The assistant sits beside its manager, top-aligned and clear to the right of the manager's box...
    expect(assistant.y).toBe(boss.y);
    expect(assistant.x).toBeGreaterThanOrEqual(boss.x + boss.w);
    // ...while a plain report is placed below, as the tree draws every other child. Two-sided: the offset is the
    // assistant's alone, not a property of the whole layout.
    expect(report.y).toBeGreaterThan(boss.y);

    // Layered does not offset the assistant: it sits below its manager like any other child.
    const layered = await layoutDocument(orgChart(), { algorithm: 'layered' });
    const byIdLayered = new Map(layered.document.nodes.map((n) => [n.id, n]));
    expect(byIdLayered.get('n_ea')!.geometry.y).toBeGreaterThan(byIdLayered.get('n_ceo')!.geometry.y);
  });

  it('validates params through the RPC method', async () => {
    await expect(layoutElk({ document: exampleWithoutGeometry(), options: { algorithm: 'circular' } })).rejects.toMatchObject({ code: ErrorCodes.invalidParams });
    await expect(layoutElk({ document: exampleWithoutGeometry(), options: { algorithm: 'Layered' } })).rejects.toMatchObject({ code: ErrorCodes.invalidParams });
    await expect(layoutElk({ document: exampleWithoutGeometry(), options: { direction: 'sideways' } })).rejects.toMatchObject({ code: ErrorCodes.invalidParams });
    await expect(layoutElk({ document: { nodes: 'x', edges: [] } })).rejects.toMatchObject({ code: ErrorCodes.invalidDocument });
    await expect(layoutElk({ document: { nodes: [{ id: 'a' }], edges: [{ id: 'e', from: {}, to: { node: 'a' } }] } })).rejects.toBeInstanceOf(RpcError);
    await expect(layoutElk('nope')).rejects.toMatchObject({ code: ErrorCodes.invalidParams });

    const result = await layoutElk({ document: exampleWithoutGeometry(), options: { algorithm: 'layered', direction: 'right' } });
    expect(result.document.nodes.every((n) => n.geometry.w > 0)).toBe(true);

    const tree = await layoutElk({ document: orgChart(), options: { algorithm: 'tree' } });
    expect(tree.document.nodes.every((n) => n.geometry.w > 0)).toBe(true);
  });
});

/** A small org chart: a chief executive, two reports below (one of them a manager), and an assistant beside the chief. */
function orgChart(): Document {
  return {
    id: 'doc_org',
    schemaVersion: 1,
    title: 'Org',
    type: 'architecture',
    nodes: [
      { id: 'n_ceo', kind: 'executive', label: 'Chief Executive\nName', shape: 'custom:org/executive' },
      { id: 'n_ea', kind: 'assistant', label: 'Assistant\nName', shape: 'custom:org/assistant' },
      { id: 'n_cto', kind: 'role', label: 'CTO\nName', shape: 'custom:org/role' },
      { id: 'n_cfo', kind: 'role', label: 'CFO\nName', shape: 'custom:org/role' },
      { id: 'n_dev', kind: 'role', label: 'Developer\nName', shape: 'custom:org/role' },
    ],
    edges: [
      { id: 'e_ea', kind: 'dotted-report', from: { node: 'n_ceo' }, to: { node: 'n_ea' } },
      { id: 'e_cto', kind: 'dotted-report', from: { node: 'n_ceo' }, to: { node: 'n_cto' } },
      { id: 'e_cfo', kind: 'dotted-report', from: { node: 'n_ceo' }, to: { node: 'n_cfo' } },
      { id: 'e_dev', kind: 'dotted-report', from: { node: 'n_cto' }, to: { node: 'n_dev' } },
    ],
    meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'test' },
  };
}
