import { documentsEqual } from './documentEquality.ts';
import { checkoutSequence } from './sequenceFixture.ts';

describe('documentsEqual', () => {
  it('is true for a copy, whatever the key order', () => {
    const document = checkoutSequence();
    const copy = structuredClone(document);
    expect(documentsEqual(document, copy)).toBe(true);
    const first = copy.nodes[0];
    if (first !== undefined) {
      copy.nodes[0] = Object.fromEntries(Object.entries(first).reverse()) as typeof first;
    }
    expect(documentsEqual(document, copy)).toBe(true);
  });

  it('treats a key whose value is undefined as absent', () => {
    const document = checkoutSequence();
    const copy = structuredClone(document);
    const first = copy.nodes[0];
    if (first !== undefined) {
      copy.nodes[0] = { ...first, geometry: undefined } as unknown as typeof first;
    }
    expect(documentsEqual(document, copy)).toBe(true);
  });

  it('is false for a changed label, a moved element, a missing element and a different order', () => {
    const document = checkoutSequence();
    const relabelled = structuredClone(document);
    const first = relabelled.nodes[0];
    if (first !== undefined) {
      relabelled.nodes[0] = { ...first, label: `${first.label}!` };
    }
    expect(documentsEqual(document, relabelled)).toBe(false);
    expect(documentsEqual(document, { ...document, edges: document.edges.slice(1) })).toBe(false);
    expect(documentsEqual(document, { ...document, nodes: [...document.nodes].reverse() })).toBe(false);
    expect(documentsEqual(document, { ...document, title: 'Another' })).toBe(false);
  });
});
