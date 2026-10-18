import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json';
import sequence from '../../../src/Draughtsman.Core/Schemas/sequence.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { EDGE_KINDS, edgeKindOptions } from './edgeKinds.ts';

describe('edge kinds', () => {
  it.each(['architecture', 'flowchart', 'sequence'] as const)('offers exactly the %s vocabulary’s edge kinds, in its order', (type) => {
    // The vocabulary the server loads, imported rather than restated, so the two cannot drift apart unnoticed.
    const vocabulary = { architecture, flowchart, sequence }[type];
    expect(EDGE_KINDS[type]).toEqual(vocabulary.edgeKinds.map((k) => k.name));
    if (type !== 'sequence') {
      expect(edgeKindOptions(type, ['themed_only'], vocabulary.edgeKinds[0]?.name ?? '').map((o) => o.kind)).toEqual(vocabulary.edgeKinds.map((k) => k.name));
    }
  });

  it('offers a sequence message only sync, async and return, and shows a row’s own special kind while it is selected', () => {
    expect(edgeKindOptions('sequence', [], 'sync')).toEqual([{ kind: 'sync', unknown: false }, { kind: 'async', unknown: false }, { kind: 'return', unknown: false }]);
    for (const special of ['note', 'interaction-use', 'create', 'destroy']) {
      expect(edgeKindOptions('sequence', [], special)).toEqual([{ kind: 'sync', unknown: false }, { kind: 'async', unknown: false }, { kind: 'return', unknown: false }, { kind: special, unknown: false }]);
    }
    expect(edgeKindOptions('sequence', [], 'bogus').at(-1)).toEqual({ kind: 'bogus', unknown: true });
  });

  it('offers the theme’s styled kinds for freeform, which has no vocabulary', () => {
    const themed = Object.keys(diagramThemes.overpass.edges).filter((k) => k !== 'default');
    expect(themed.length).toBeGreaterThan(0);
    expect(edgeKindOptions('freeform', themed, themed[0] ?? '').map((o) => o.kind)).toEqual(themed);
    expect(edgeKindOptions('freeform', themed, themed[0] ?? '').some((o) => o.unknown)).toBe(false);
  });

  it('keeps a kind the list does not name, flagged as unknown, and flags nothing for a listed kind', () => {
    expect(edgeKindOptions('flowchart', [], 'data')).toEqual([
      { kind: 'flow', unknown: false },
      { kind: 'association', unknown: false },
      { kind: 'message-flow', unknown: false },
      { kind: 'bpmn-association', unknown: false },
      { kind: 'data-association', unknown: false },
      { kind: 'data', unknown: true },
    ]);
    expect(edgeKindOptions('flowchart', [], 'association').filter((o) => o.unknown)).toEqual([]);
  });
});
