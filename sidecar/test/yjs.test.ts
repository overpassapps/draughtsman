import * as Y from 'yjs';
import { describe, expect, it } from 'vitest';
import { ErrorCodes, RpcError } from '../src/rpc.ts';
import { decodeBase64, yjsApplyUpdates, yjsFromJson, yjsToJson } from '../src/yjs/methods.ts';
import { EDGES_MAP, HEADER_MAP, NODES_MAP, ORDER_FIELD, documentToYDoc, yDocToDocument } from '../src/yjs/shape.ts';
import type { Flythrough } from '../src/model/types.ts';
import { loadExample } from './helpers.ts';

describe('Yjs document shape', () => {
  it.each(['document', 'predefined-process', 'manual-input', 'off-page-connector'])('preserves %s through binary Yjs materialisation', (shape) => {
    const original = { ...loadExample(), nodes: [{ id: 'n_shape', kind: 'process', label: 'Shape', shape }], edges: [] };
    const { state } = yjsFromJson({ document: original });
    const restored = yjsToJson({ state }).document as { nodes: Array<{ shape: string }> };
    expect(restored.nodes[0]?.shape).toBe(shape);
  });

  it('round-trips the order-service example json -> state -> json byte for byte', () => {
    const original = loadExample();

    const { state } = yjsFromJson({ document: original });
    const { document } = yjsToJson({ state });

    expect(document).toEqual(original);
    // Member order matters: the C# serialiser writes the same order, so both sides produce identical bytes.
    expect(JSON.stringify(document)).toBe(JSON.stringify(original));
  });

  it('stores a Y.Map per node and edge keyed by id with the array position as order', () => {
    const doc = documentToYDoc(loadExample());

    const nodes = doc.getMap<Y.Map<unknown>>(NODES_MAP);
    expect([...nodes.keys()].sort()).toEqual(['c_vpc', 'n_api', 'n_cache', 'n_db', 'n_web']);
    expect(nodes.get('n_api')).toBeInstanceOf(Y.Map);
    expect(nodes.get('n_api')?.get('label')).toBe('Orders API');
    expect(nodes.get('n_api')?.get(ORDER_FIELD)).toBe(2);
    expect(nodes.get('n_api')?.get('geometry')).toEqual({ x: 40, y: 200, w: 320, h: 80 });
    expect(doc.getMap(EDGES_MAP).get('e_3')).toBeInstanceOf(Y.Map);
    expect(doc.getMap(HEADER_MAP).get('type')).toBe('architecture');
  });

  it('materialises an edit made by another client through applyUpdates', () => {
    const original = loadExample();
    const { state } = yjsFromJson({ document: original });

    // A browser holding the same state changes a label and adds a node; only its delta crosses the wire.
    const client = new Y.Doc();
    Y.applyUpdate(client, decodeBase64(state, 'state'));
    const before = Y.encodeStateVector(client);
    client.transact(() => {
      const nodes = client.getMap<Y.Map<unknown>>(NODES_MAP);
      nodes.get('n_db')?.set('label', 'Postgres 17');
      const added = new Y.Map<unknown>();
      nodes.set('n_new', added);
      added.set('id', 'n_new');
      added.set('kind', 'queue');
      added.set('label', 'Outbox');
      added.set('shape', 'rectangle');
      added.set('geometry', { x: 1, y: 2, w: 3, h: 4 });
      added.set(ORDER_FIELD, 99);
    });
    const delta = Buffer.from(Y.encodeStateAsUpdate(client, before)).toString('base64');

    const applied = yjsApplyUpdates({ state, updates: [delta] });
    const { document } = yjsToJson({ state: applied.state });
    expect(document).toEqual(yjsToJson({ state, updates: [delta] }).document);

    const materialised = document as { nodes: Array<{ id: string; label: string }> };
    expect(materialised.nodes.find((n) => n.id === 'n_db')?.label).toBe('Postgres 17');
    expect(materialised.nodes.map((n) => n.id)).toEqual(['n_web', 'c_vpc', 'n_api', 'n_db', 'n_cache', 'n_new']);
  });

  it('applyUpdates from no state builds the document from the updates alone', () => {
    const doc = documentToYDoc(loadExample());
    const full = Buffer.from(Y.encodeStateAsUpdate(doc)).toString('base64');

    const result = yjsApplyUpdates({ updates: [full] });
    expect(yjsToJson({ updates: [result.state] }).document).toEqual(loadExample());
  });

  it('rejects states that do not hold a document and malformed input', () => {
    const empty = Buffer.from(Y.encodeStateAsUpdate(new Y.Doc())).toString('base64');
    expect(() => yjsToJson({ state: empty })).toThrowError(RpcError);
    try {
      yjsToJson({ state: empty });
    } catch (error) {
      expect((error as RpcError).code).toBe(ErrorCodes.invalidDocument);
    }

    expect(() => yjsToJson({})).toThrowError(/needs state, updates or both/);
    expect(() => yjsToJson({ state: '!!!' })).toThrowError(/not valid base64/);
    expect(() => yjsFromJson({ document: { id: 'x' } })).toThrowError(RpcError);
    expect(() => yjsApplyUpdates({ updates: 'nope' })).toThrowError(/must be an array/);
  });

  it('refuses to materialise an element that is missing a required field', () => {
    for (const field of ['kind', 'label', 'shape']) {
      const doc = documentToYDoc(loadExample());
      doc.getMap<Y.Map<unknown>>(NODES_MAP).get('n_api')?.delete(field);

      expect(() => yDocToDocument(doc)).toThrowError(new RegExp(`nodes.n_api.${field}`));
      try {
        yDocToDocument(doc);
      } catch (error) {
        expect((error as RpcError).code).toBe(ErrorCodes.invalidDocument);
      }
    }

    const missingEndpoint = documentToYDoc(loadExample());
    missingEndpoint.getMap<Y.Map<unknown>>(EDGES_MAP).get('e_1')?.delete('to');
    expect(() => yDocToDocument(missingEndpoint)).toThrowError(/edges.e_1.to.node/);
  });

  it('refuses a state whose element id disagrees with the key it is stored under', () => {
    const doc = documentToYDoc(loadExample());
    doc.getMap<Y.Map<unknown>>(NODES_MAP).get('n_api')?.set('id', 'n_other');

    expect(() => yDocToDocument(doc)).toThrowError(/carrying id n_other/);
    try {
      yDocToDocument(doc);
    } catch (error) {
      expect((error as RpcError).code).toBe(ErrorCodes.invalidDocument);
    }
  });

  it('emits theme between type and nodes, where every other copy of the model puts it', () => {
    // The spread leaves theme last in the input; the materialised document must still be canonical.
    const themed = { ...loadExample(), theme: { 'node.fill': '#101418' } };

    const { state } = yjsFromJson({ document: themed });
    const { document } = yjsToJson({ state });

    // The example carries a tour, which is always last.
    expect(Object.keys(document as object)).toEqual(['id', 'schemaVersion', 'title', 'type', 'theme', 'nodes', 'edges', 'meta', 'flythrough']);
    expect((document as { theme: Record<string, string> }).theme).toEqual({ 'node.fill': '#101418' });
  });

  describe('the flythrough, the camera path Present plays', () => {
    const tour: Flythrough = {
      keyframes: [
        { id: 'k_1', view: { x: 0, y: 0, w: 800, h: 500 }, caption: 'All of it' },
        { id: 'k_2', view: { x: 40, y: 200, w: 320, h: 200 }, durationMs: 900, holdMs: 0, easing: 'easeOut' },
      ],
      loop: true,
    };

    it('keeps the tour as one plain value in the header map, the way theme is kept', () => {
      const doc = documentToYDoc({ ...loadExample(), flythrough: tour });

      expect(doc.getMap(HEADER_MAP).get('flythrough')).toEqual(tour);
    });

    it('emits the tour after meta, where every other copy of the model puts it, however the input ordered it', () => {
      // The spread puts flythrough before meta in the input object; the materialised document must still be canonical.
      const { meta, ...rest } = loadExample();
      const scrambled = { ...rest, flythrough: tour, meta };

      const { state } = yjsFromJson({ document: scrambled });
      const { document } = yjsToJson({ state });

      expect(Object.keys(document as object)).toEqual(['id', 'schemaVersion', 'title', 'type', 'nodes', 'edges', 'meta', 'flythrough']);
      expect(document).toEqual({ ...loadExample(), flythrough: tour });
      expect(JSON.stringify((document as { flythrough: unknown }).flythrough)).toBe(JSON.stringify(tour));
    });

    it('emits no member at all for a document with no tour, and does not invent one', () => {
      const { flythrough: _dropped, ...without } = loadExample() as ReturnType<typeof loadExample> & { flythrough?: unknown };
      void _dropped;

      const { state } = yjsFromJson({ document: without });
      const { document } = yjsToJson({ state });

      expect(Object.keys(document as object)).not.toContain('flythrough');
      expect(Object.keys(document as object)).toEqual(['id', 'schemaVersion', 'title', 'type', 'nodes', 'edges', 'meta']);
    });

    it('hands out a copy, so a caller mutating the materialised tour cannot change the state', () => {
      const doc = documentToYDoc({ ...loadExample(), flythrough: tour });

      const first = yDocToDocument(doc);
      first.flythrough!.keyframes[0]!.caption = 'changed by a caller';

      expect(yDocToDocument(doc).flythrough!.keyframes[0]!.caption).toBe('All of it');
    });

    it('survives a binary Yjs round trip through applyUpdates', () => {
      const { state } = yjsFromJson({ document: { ...loadExample(), flythrough: tour } });
      const client = new Y.Doc();
      Y.applyUpdate(client, decodeBase64(state, 'state'));
      const before = Y.encodeStateVector(client);
      client.transact(() => {
        client.getMap(HEADER_MAP).set('flythrough', { keyframes: [{ id: 'k_only', view: { x: 1, y: 2, w: 3, h: 4 } }] });
      });
      const delta = Buffer.from(Y.encodeStateAsUpdate(client, before)).toString('base64');

      const applied = yjsApplyUpdates({ state, updates: [delta] });
      const { document } = yjsToJson({ state: applied.state });

      expect((document as { flythrough: unknown }).flythrough).toEqual({ keyframes: [{ id: 'k_only', view: { x: 1, y: 2, w: 3, h: 4 } }] });
    });
  });

  it('refuses a state that holds an element as a plain value instead of a Y.Map', () => {
    const doc = documentToYDoc(loadExample());
    doc.getMap<unknown>(NODES_MAP).set('n_plain', { id: 'n_plain', kind: 'service', label: 'Queue', shape: 'rectangle', order: 5 });

    expect(() => yDocToDocument(doc)).toThrowError(/nodes.n_plain/);
    try {
      yDocToDocument(doc);
    } catch (error) {
      expect((error as RpcError).code).toBe(ErrorCodes.invalidDocument);
    }
  });

  it('materialises a node that has not been laid out with no geometry rather than an empty box', () => {
    const doc = documentToYDoc(loadExample());
    doc.getMap<Y.Map<unknown>>(NODES_MAP).get('n_api')?.delete('geometry');

    const node = yDocToDocument(doc).nodes.find((n) => n.id === 'n_api')!;
    expect(node.geometry).toBeUndefined();
    expect(Object.keys(node)).toEqual(['id', 'kind', 'label', 'shape', 'props', 'parent', 'ports']);
  });

  it('orders elements without an order field after those with one, by id', () => {
    const doc = documentToYDoc(loadExample());
    const nodes = doc.getMap<Y.Map<unknown>>(NODES_MAP);
    nodes.get('n_web')?.delete(ORDER_FIELD);
    nodes.get('c_vpc')?.delete(ORDER_FIELD);

    expect(yDocToDocument(doc).nodes.map((n) => n.id)).toEqual(['n_api', 'n_db', 'n_cache', 'c_vpc', 'n_web']);
  });
});
