import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { layoutElk } from '../src/layout/methods.ts';
import { ErrorCodes, RpcError } from '../src/rpc.ts';
import { sequenceSceneMethod } from '../src/sequence/methods.ts';
import { yjsFromJson, yjsToJson } from '../src/yjs/methods.ts';
import { repoRoot } from './helpers.ts';
import type { Document } from '../src/model/types.ts';

/** docs/examples/checkout-sequence.json, shared with the web and .NET tests; the numbers below are worked out by hand in web/src/model/sequenceScene.test.ts. */
function checkout(): Document {
  return JSON.parse(readFileSync(join(repoRoot, 'docs', 'examples', 'checkout-sequence.json'), 'utf8')) as Document;
}

/** docs/examples/checkout-fragments.json (milestone 2), whose numbers web/src/model/sequenceFragments.test.ts works out by hand. */
function fragments(): Document {
  return JSON.parse(readFileSync(join(repoRoot, 'docs', 'examples', 'checkout-fragments.json'), 'utf8')) as Document;
}

describe('sequence.scene', () => {
  it('returns the editor’s derived scene as plain data', () => {
    const { scene } = sequenceSceneMethod({ document: checkout() });
    expect(scene.headers.map((h) => [h.id, h.x, h.cx])).toEqual([['p_user', 20, 80], ['p_web', 160, 220], ['p_api', 300, 360], ['p_pay', 440, 500]]);
    expect(scene.messages.map((m) => [m.id, m.y])).toEqual([['m_1', 108], ['m_2', 144], ['m_3', 180], ['m_4', 216], ['m_5', 252], ['m_6', 288], ['m_7', 326]]);
    expect(scene.bars.map((b) => [b.participant, b.y, b.h])).toEqual([['p_api', 144, 144], ['p_pay', 180, 36]]);
    expect(scene.bounds).toEqual({ x: 0, y: 0, w: 580, h: 378 });
    // It crosses the JSON-RPC boundary as JSON, so it must survive a round trip.
    expect(JSON.parse(JSON.stringify(scene))).toEqual(scene);
  });

  it('carries the frames, note boxes, destroy marks and a created participant’s header of a milestone two document', () => {
    const { scene } = sequenceSceneMethod({ document: fragments() });
    expect(scene.fragments.map((f) => [f.id, f.operator, f.x, f.y, f.w, f.h, f.empty])).toEqual([['f_1', 'alt', 200, 198, 315, 160, false]]);
    expect(scene.fragments[0]?.sections.map((s) => [s.operand, s.guard, s.y, s.guardAt])).toEqual([[0, '[card ok]', 198, { x: 239, y: 207 }], [1, '[declined]', 292, { x: 208, y: 301 }]]);
    expect(scene.messages.find((m) => m.id === 'n_1')?.note).toEqual({ x: 210, y: 166, w: 160, h: 28 });
    expect(scene.destroys).toEqual([{ participant: 'p_mail', x: 640, y: 506 }]);
    expect(scene.headers.find((h) => h.id === 'p_mail')?.y).toBe(408);
    expect(scene.bounds).toEqual({ x: 0, y: 0, w: 720, h: 544 });
    expect(JSON.parse(JSON.stringify(scene))).toEqual(scene);
  });

  it('refuses a document of another type, and params that are not an object', () => {
    const wrongType = { ...checkout(), type: 'architecture' };
    expect(() => sequenceSceneMethod({ document: wrongType })).toThrow(RpcError);
    try {
      sequenceSceneMethod({ document: wrongType });
    } catch (e) {
      expect((e as RpcError).code).toBe(ErrorCodes.invalidParams);
      expect((e as RpcError).message).toContain("'sequence'");
    }
    expect(() => sequenceSceneMethod('nope')).toThrow(RpcError);
  });
});

describe('layout.elk for a sequence document', () => {
  it('hands the document back unchanged, with no warning and no geometry invented', async () => {
    const document = checkout();
    const before = structuredClone(document);
    const result = await layoutElk({ document });
    expect(result.warnings).toEqual([]);
    expect(result.document).toEqual(before);
    expect(result.document.nodes.every((n) => 'geometry' in n)).toBe(false);
  });
});

describe('the Yjs shape for a sequence document', () => {
  it('round-trips the checkout example and keeps the edge order, which is time', () => {
    const original = checkout();
    const { state } = yjsFromJson({ document: original });
    const { document } = yjsToJson({ state });

    expect(JSON.stringify(document)).toBe(JSON.stringify(original));
    expect((document as Document).edges.map((edge) => edge.id)).toEqual(['m_1', 'm_2', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
  });

  it('round-trips a fragment’s operand list, its parent and a message’s in and operand props', () => {
    const original = fragments();
    original.nodes.push({ id: 'f_2', kind: 'fragment', label: 'loop', shape: 'rectangle', parent: 'f_1', props: { operator: 'loop', operands: ['[n times]'], operand: 1 } });
    const { document } = yjsToJson({ state: yjsFromJson({ document: original }).state });

    // The Yjs shape orders a node's keys its own way (parent after props), so compare the values, not the text.
    expect(document).toEqual(original);
    expect((document as Document).nodes.find((n) => n.id === 'f_2')).toMatchObject({ parent: 'f_1', props: { operands: ['[n times]'], operand: 1 } });
    expect((document as Document).edges.find((e) => e.id === 'm_5')?.props).toMatchObject({ in: 'f_1', operand: 1 });
  });

  it('keeps a reordered edge list reordered, so a reorder is a real change in the stored state', () => {
    const original = checkout();
    const [first, second, ...rest] = original.edges;
    const reordered = { ...original, edges: [second, first, ...rest] };

    const { document } = yjsToJson({ state: yjsFromJson({ document: reordered }).state });

    expect((document as Document).edges.map((edge) => edge.id)).toEqual(['m_2', 'm_1', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
    expect(JSON.stringify(document)).not.toBe(JSON.stringify(original));
  });
});
