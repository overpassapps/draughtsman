import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import schema from '../../../docs/schema/document.schema.json';
import { GENERIC_ICONS, GENERIC_ICON_PREFIX, ICON_OPTIONS } from './icons.ts';

/** True when the committed schema's shape pattern admits `shape`. */
function schemaAdmits(shape: string): boolean {
  return new RegExp(schema.$defs.node.properties.shape.pattern).test(shape);
}

describe('generic icon palette options', () => {
  it('lists every icon once, each shape admitted by the schema', () => {
    expect(ICON_OPTIONS.map((option) => option.name)).toEqual([...new Set(ICON_OPTIONS.map((option) => option.name))]);
    expect(ICON_OPTIONS.length).toBe(GENERIC_ICONS.length);
    for (const option of ICON_OPTIONS) {
      expect(option.shape).toBe(GENERIC_ICON_PREFIX + option.name);
      expect(schemaAdmits(option.shape), `${option.shape} is not in the schema`).toBe(true);
    }
  });

  it('places every icon with a real architecture kind, never the unknown shape placeholder', () => {
    const known = new Set(architecture.nodeKinds.map((kind) => kind.name));
    for (const option of ICON_OPTIONS) {
      const icon = GENERIC_ICONS.find((candidate) => candidate.name === option.name);
      expect(option.kind).toBe(icon?.paletteKind);
      expect(known.has(option.kind), `${option.name} places unknown kind ${option.kind}`).toBe(true);
      expect(option.kind).not.toBe('shape');
    }

    // Two-sided: some icons place a distinct new kind of their own, others an everyday vocabulary kind.
    expect(ICON_OPTIONS.some((option) => option.name === option.kind)).toBe(true);
    expect(ICON_OPTIONS.some((option) => option.name !== option.kind)).toBe(true);
  });

  it('matches the architecture vocabulary both ways, so no icon kind and no kind default drift apart', () => {
    const declared = new Set(ICON_OPTIONS.filter((option) => option.name === option.kind).map((option) => option.name));
    const vocabulary = architecture.nodeKinds.filter((kind) => kind.defaultShape.startsWith(GENERIC_ICON_PREFIX));

    // Equal sets: a vocabulary kind with an icon default and no palette entry fails, and so does the reverse.
    expect(new Set(vocabulary.map((kind) => kind.name))).toEqual(declared);
    for (const kind of vocabulary) {
      const option = ICON_OPTIONS.find((candidate) => candidate.name === kind.name);
      expect(option?.shape).toBe(kind.defaultShape);
      expect(option?.kind).toBe(kind.name);
      expect(kind.description.length).toBeGreaterThan(0);
    }
  });
});
