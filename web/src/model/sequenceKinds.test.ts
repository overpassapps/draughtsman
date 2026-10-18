import sequence from '../../../src/Draughtsman.Core/Schemas/sequence.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { checkoutSequence } from './sequenceFixture.ts';

// The server renderer reads a message kind's `defaultStyle` from sequence.json (it does not resolve theme tokens); the
// web canvas reads the same look from the theme files. This keeps the two from drifting apart.
describe('the sequence vocabulary and the diagram themes agree', () => {
  const themes = Object.entries(diagramThemes);

  it('ships three themes to check', () => {
    expect(themes.map(([name]) => name).sort()).toEqual(['mono', 'neutral', 'overpass']);
  });

  it.each(sequence.edgeKinds.map((kind) => [kind.name, kind.defaultStyle] as const))('draws %s in every theme with the vocabulary’s dash and arrowhead', (kind, defaultStyle) => {
    for (const [name, theme] of themes) {
      const styled = theme.edges[kind];
      expect(styled, `${name} has no edge kind ${kind}`).toBeDefined();
      expect([name, styled?.dash, styled?.arrowEnd, styled?.arrowStart]).toEqual([name, defaultStyle.dash, defaultStyle.arrowEnd, 'none']);
    }
  });

  it('gives the return message a different look from the sync message, or the check above proves nothing', () => {
    const kinds = Object.fromEntries(sequence.edgeKinds.map((kind) => [kind.name, kind.defaultStyle]));
    expect(kinds.return?.dash).toBe('dashed');
    expect(kinds.sync?.dash).toBe('solid');
    expect(kinds.sync?.arrowEnd).not.toBe(kinds.async?.arrowEnd);
  });

  it.each(sequence.nodeKinds.map((kind) => kind.name))('styles the %s participant kind in every theme', (kind) => {
    for (const [name, theme] of themes) {
      expect(theme.nodes[kind], `${name} has no node kind ${kind}`).toBeDefined();
    }
  });

  it('teaches the rules an agent must know: edge order is time, the activation props, and how fragment membership is written', () => {
    expect(sequence.description).toContain('ORDER OF THE EDGES ARRAY IS TIME ORDER');
    for (const kind of sequence.edgeKinds.filter((edge) => ['sync', 'async', 'return'].includes(edge.name))) {
      expect(Object.keys(kind.props)).toEqual(['activate', 'deactivate', 'in', 'operand']);
    }
  });

  it('teaches fragments, notes, create and destroy through get_schema', () => {
    expect(sequence.nodeKinds.map((kind) => kind.name)).toContain('fragment');
    expect(sequence.edgeKinds.map((kind) => kind.name)).toEqual(expect.arrayContaining(['note', 'create', 'destroy']));
    // Membership is written on the message, never on the fragment, and the vocabulary must say so where an agent reads it.
    expect(sequence.description).toContain('in="<fragment id>"');
    const fragment = sequence.nodeKinds.find((kind) => kind.name === 'fragment');
    expect(Object.keys(fragment?.props ?? {})).toEqual(['operator', 'operands', 'operand']);
    expect(fragment?.description).toContain('never here');
  });

  it('is drawn with kinds the vocabulary names', () => {
    const known = new Set(sequence.edgeKinds.map((kind) => kind.name));
    const nodes = new Set(sequence.nodeKinds.map((kind) => kind.name));
    const document = checkoutSequence();
    expect(document.edges.every((edge) => known.has(edge.kind))).toBe(true);
    expect(document.nodes.every((node) => nodes.has(node.kind))).toBe(true);
  });
});
