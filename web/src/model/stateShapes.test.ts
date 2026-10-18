import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json';
import schema from '../../../docs/schema/document.schema.json';
import { STATE_SHAPE_OPTIONS, STATE_SHAPE_PREFIX, STATE_SHAPES, stateShapeName } from './stateShapes.ts';

/** True when the committed schema's shape pattern admits `shape`. */
function schemaAdmits(shape: string): boolean {
  return new RegExp(schema.$defs.node.properties.shape.pattern).test(shape);
}

describe('state shape palette options', () => {
  it('lists every shape once, each shape admitted by the schema', () => {
    expect(STATE_SHAPE_OPTIONS.map((option) => option.shape)).toEqual([...new Set(STATE_SHAPE_OPTIONS.map((option) => option.shape))]);
    expect(STATE_SHAPE_OPTIONS.length).toBe(STATE_SHAPES.length);
    expect(STATE_SHAPES.length).toBeGreaterThan(0);
    for (const option of STATE_SHAPE_OPTIONS) {
      expect(option.shape.startsWith(STATE_SHAPE_PREFIX)).toBe(true);
      expect(schemaAdmits(option.shape), `${option.shape} is not in the schema`).toBe(true);
    }
  });

  it('matches the flowchart vocabulary both ways, so no state kind and no shape default drift apart', () => {
    const declared = new Set(STATE_SHAPES.map((shape) => shape.kind));
    const vocabulary = flowchart.nodeKinds.filter((kind) => kind.defaultShape.startsWith(STATE_SHAPE_PREFIX));

    // Equal sets: a vocabulary kind with a state shape default and no palette entry fails, and so does the reverse.
    expect(new Set(vocabulary.map((kind) => kind.name))).toEqual(declared);
    for (const kind of vocabulary) {
      const shape = STATE_SHAPES.find((candidate) => candidate.kind === kind.name);
      expect(shape?.paletteKind).toBe(kind.name);
      expect(kind.defaultShape).toBe(stateShapeName(shape?.name ?? ''));
      expect(kind.description.length).toBeGreaterThan(0);
    }
  });

  it('places every shape with its own kind, never the unknown shape placeholder', () => {
    for (const option of STATE_SHAPE_OPTIONS) {
      const shape = STATE_SHAPES.find((candidate) => stateShapeName(candidate.name) === option.shape);
      expect(shape).toBeDefined();
      expect(option.kind).toBe(shape?.paletteKind);
      expect(option.kind).not.toBe('shape');
    }
  });
});
