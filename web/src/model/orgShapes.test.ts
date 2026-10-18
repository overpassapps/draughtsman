import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import schema from '../../../docs/schema/document.schema.json';
import { ORG_OPTIONS, ORG_SHAPES, ORG_SHAPE_PREFIX } from './orgShapes.ts';

/** True when the committed schema's shape pattern admits `shape`. */
function schemaAdmits(shape: string): boolean {
  return new RegExp(schema.$defs.node.properties.shape.pattern).test(shape);
}

describe('org shape palette options', () => {
  it('lists every box once, each shape admitted by the schema', () => {
    expect(ORG_OPTIONS.map((option) => option.name)).toEqual([...new Set(ORG_OPTIONS.map((option) => option.name))]);
    expect(ORG_OPTIONS.length).toBe(ORG_SHAPES.length);
    expect(ORG_OPTIONS.length).toBeGreaterThan(0);
    for (const option of ORG_OPTIONS) {
      expect(option.shape).toBe(ORG_SHAPE_PREFIX + option.name);
      expect(schemaAdmits(option.shape), `${option.shape} is not in the schema`).toBe(true);
    }
  });

  it('places every box with a real architecture kind whose own default shape is the box', () => {
    const vocabulary = new Map(architecture.nodeKinds.map((kind) => [kind.name, kind]));
    for (const option of ORG_OPTIONS) {
      const kind = vocabulary.get(option.kind);
      expect(kind, `architecture has no kind ${option.kind}`).toBeDefined();
      expect(kind?.defaultShape).toBe(option.shape);
      expect(option.kind).not.toBe('shape');
    }
  });

  it('matches the architecture vocabulary both ways, so no org kind and no kind default drift apart', () => {
    const declared = new Set(ORG_OPTIONS.map((option) => option.kind));
    const vocabulary = architecture.nodeKinds.filter((kind) => kind.defaultShape.startsWith(ORG_SHAPE_PREFIX));

    // Equal sets: a vocabulary kind with an org default and no palette entry fails, and so does the reverse.
    expect(new Set(vocabulary.map((kind) => kind.name))).toEqual(declared);
    for (const kind of vocabulary) {
      const option = ORG_OPTIONS.find((candidate) => candidate.kind === kind.name);
      expect(option?.shape).toBe(kind.defaultShape);
      expect(option?.defaultLabel).toContain('\n');
    }
  });

  it('gives every box a two-line label (name above title), and no box a single-line default', () => {
    for (const box of ORG_SHAPES) {
      expect(box.defaultLabel.split('\n').length, `${box.name} defaultLabel is not two lines`).toBeGreaterThanOrEqual(2);
    }
  });
});
