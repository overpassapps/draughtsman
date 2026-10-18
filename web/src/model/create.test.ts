import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json';
import type { IdGenerator } from './clipboard.ts';
import { createNode, DEFAULT_NODE, NEW_NODE_SIZE } from './create.ts';
import type { Document, DocumentType } from './types.ts';

function empty(type: DocumentType, nodes: Document['nodes'] = []): Document {
  return {
    id: 'doc_c',
    schemaVersion: 1,
    title: 'Create',
    type,
    nodes,
    edges: [],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
  };
}

/** Proposes the given ids in turn, so a test decides exactly which proposals collide. */
function proposing(...ids: string[]): IdGenerator {
  return () => ids.shift() ?? 'n_exhausted';
}

describe('createNode', () => {
  it.each(['architecture', 'flowchart'] as const)('places a %s kind with the default shape its vocabulary names', (type) => {
    // The vocabulary the server loads, imported rather than restated, so the two cannot drift apart unnoticed.
    const vocabulary = { architecture, flowchart }[type];
    const kind = vocabulary.nodeKinds.find((k) => k.name === DEFAULT_NODE[type].kind);
    expect(kind, `${type} has no kind ${DEFAULT_NODE[type].kind}`).toBeDefined();
    const node = createNode(empty(type), { x: 0, y: 0 }, 16);
    expect(node.kind).toBe(kind?.name);
    expect(node.shape).toBe(kind?.defaultShape);
  });

  it('centres the node on the view centre, to the nearest canvas unit', () => {
    const node = createNode(empty('flowchart'), { x: 500.4, y: 300.6 }, 16, proposing('n_1'));
    expect(node).toEqual({ id: 'n_1', kind: 'process', label: 'Process', shape: 'rounded', geometry: { x: 420, y: 261, w: 160, h: 80 } });
    expect(NEW_NODE_SIZE).toEqual({ w: 160, h: 80 });
  });

  it('steps one grid step down and right for each node already at the spot, and not when the spot is free', () => {
    const atCentre = { id: 'n_a', kind: 'process', label: 'A', shape: 'rounded', geometry: { x: 420, y: 260, w: 160, h: 80 } };
    const stepped = { id: 'n_b', kind: 'process', label: 'B', shape: 'rounded', geometry: { x: 436, y: 276, w: 160, h: 80 } };
    const centre = { x: 500, y: 300 };
    expect(createNode(empty('flowchart', [atCentre, stepped]), centre, 16).geometry).toEqual({ x: 452, y: 292, w: 160, h: 80 });
    expect(createNode(empty('flowchart', [stepped]), centre, 16).geometry).toEqual({ x: 420, y: 260, w: 160, h: 80 });
  });

  it('never reuses an id the document already has', () => {
    const existing = { id: 'n_1', kind: 'process', label: 'A', shape: 'rounded' };
    const node = createNode(empty('flowchart', [existing]), { x: 0, y: 0 }, 16, proposing('n_1', 'n_2'));
    expect(node.id).toBe('n_2');
  });

  it('places a plain rectangle in a freeform document, which has no vocabulary', () => {
    const node = createNode(empty('freeform'), { x: 0, y: 0 }, 16);
    expect(node.shape).toBe('rectangle');
    expect(node.id).toMatch(/^n_[a-z0-9]+$/);
  });

  it('places the given template instead of the document type default when one is passed', () => {
    const node = createNode(empty('architecture'), { x: 0, y: 0 }, 16, proposing('n_1'), { kind: 'database', shape: 'cylinder', label: 'Database' });
    expect(node.kind).toBe('database');
    expect(node.shape).toBe('cylinder');
    expect(node.label).toBe('Database');
    // Never the type default the template overrides.
    expect(node.kind).not.toBe(DEFAULT_NODE.architecture.kind);
  });
  it('places a template at its own size and with a copy of its props, centred the same way', () => {
    const template = { kind: 'class', shape: 'custom:uml/class', label: 'ClassName', size: { w: 156, h: 84 }, props: { attributes: ['- a'] } };
    const node = createNode(empty('freeform'), { x: 500, y: 300 }, 16, proposing('n_1'), template);
    expect(node).toEqual({
      id: 'n_1',
      kind: 'class',
      label: 'ClassName',
      shape: 'custom:uml/class',
      geometry: { x: 422, y: 258, w: 156, h: 84 },
      props: { attributes: ['- a'] },
    });
    // The node owns its props: editing them must not reach back into the palette's template.
    (node.props?.attributes as string[]).push('- b');
    expect(template.props.attributes).toEqual(['- a']);
    // A template with neither keeps the standard size and carries no props key.
    const plain = createNode(empty('freeform'), { x: 500, y: 300 }, 16, proposing('n_2'), { kind: 'shape', shape: 'rectangle', label: 'Shape' });
    expect(plain.geometry).toMatchObject({ w: 160, h: 80 });
    expect('props' in plain).toBe(false);
  });
});
