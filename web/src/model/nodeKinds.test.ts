import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json';
import sequence from '../../../src/Draughtsman.Core/Schemas/sequence.json';
import schema from '../../../docs/schema/document.schema.json';
import { GENERIC_SHAPE_KIND, MORE_FLOWCHART_SHAPES, NODE_KINDS, nodeKindOptions, SHAPE_OPTIONS, SWIMLANE_OPTIONS } from './nodeKinds.ts';

/** True when the committed schema's shape pattern admits `shape`. */
function schemaAdmits(shape: string): boolean {
  return new RegExp(schema.$defs.node.properties.shape.pattern).test(shape);
}

describe('node kinds', () => {
  it.each(['architecture', 'flowchart', 'sequence'] as const)("every %s entry names a real vocabulary kind with that kind's own default shape", (type) => {
    // The vocabulary the server loads, imported rather than restated, so the two cannot drift apart unnoticed.
    const vocabulary = { architecture, flowchart, sequence }[type];
    for (const option of NODE_KINDS[type]) {
      const kind = vocabulary.nodeKinds.find((k) => k.name === option.kind);
      expect(kind, `${type} has no kind ${option.kind}`).toBeDefined();
      expect(option.defaultShape).toBe(kind?.defaultShape);
    }
  });

  it('lists each kind at most once per type', () => {
    for (const options of Object.values(NODE_KINDS)) {
      expect(options.map((o) => o.kind)).toEqual([...new Set(options.map((o) => o.kind))]);
    }
  });

  it('offers a kind group for architecture and flowchart, and none for freeform', () => {
    expect(nodeKindOptions('architecture')).toBe(NODE_KINDS.architecture);
    expect(nodeKindOptions('flowchart')).toBe(NODE_KINDS.flowchart);
    expect(nodeKindOptions('sequence')).toBe(NODE_KINDS.sequence);
    expect(nodeKindOptions('freeform')).toEqual([]);
  });

  it('offers the presentation shapes once each, none of them the generic shape kind', () => {
    expect(SHAPE_OPTIONS.map((o) => o.shape)).toEqual([...new Set(SHAPE_OPTIONS.map((o) => o.shape))]);
    expect(SHAPE_OPTIONS.length).toBeGreaterThan(0);
    expect(GENERIC_SHAPE_KIND).not.toBe('');
  });

  it('lists the wider ISO 5807 symbols once each, all admitted by the schema', () => {
    expect(MORE_FLOWCHART_SHAPES.map((t) => t.shape)).toEqual([...new Set(MORE_FLOWCHART_SHAPES.map((t) => t.shape))]);
    for (const template of MORE_FLOWCHART_SHAPES) {
      expect(schemaAdmits(template.shape), `${template.shape} is not in the schema`).toBe(true);
    }
    // The shape sweep in shapes.test.tsx proves each of these renders; this proves the palette offers the whole set.
    expect(MORE_FLOWCHART_SHAPES.length).toBe(15);
  });

  it('gives a symbol with its own vocabulary kind that kind and the kind\u2019s own default shape', () => {
    const semantic = MORE_FLOWCHART_SHAPES.filter((template) => template.kind !== GENERIC_SHAPE_KIND);
    expect(semantic.length).toBeGreaterThan(0);
    for (const template of semantic) {
      const kind = flowchart.nodeKinds.find((k) => k.name === template.kind);
      expect(kind, `flowchart has no kind ${template.kind}`).toBeDefined();
      expect(kind?.defaultShape).toBe(template.shape);
    }
  });

  it('offers the two swimlane containers as real flowchart kinds with their own shapes', () => {
    expect(SWIMLANE_OPTIONS.map((template) => template.shape)).toEqual([...new Set(SWIMLANE_OPTIONS.map((template) => template.shape))]);
    expect(SWIMLANE_OPTIONS.map((template) => template.kind)).toEqual(['pool', 'lane']);
    for (const template of SWIMLANE_OPTIONS) {
      const kind = flowchart.nodeKinds.find((k) => k.name === template.kind);
      expect(kind, `flowchart has no kind ${template.kind}`).toBeDefined();
      expect(kind?.defaultShape).toBe(template.shape);
    }
  });
});
