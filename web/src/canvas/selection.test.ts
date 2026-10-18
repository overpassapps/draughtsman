import type { Document } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';
import { dragStarts, selectOnPress, selectOnRelease, stepSelection } from './selection.ts';

describe('selectOnPress', () => {
  it('selects only the pressed element on a plain click', () => {
    expect(selectOnPress(new Set(['a', 'b']), 'c', false)).toEqual(new Set(['c']));
    expect(selectOnPress(new Set(), 'a', false)).toEqual(new Set(['a']));
  });

  it('keeps a multi-selection when the pressed node is already in it, so a drag moves them all', () => {
    const before = new Set(['a', 'b']);
    expect(selectOnPress(before, 'a', false)).toBe(before);
  });

  it('adds and removes an element on a Shift-click', () => {
    expect(selectOnPress(new Set(['a']), 'b', true)).toEqual(new Set(['a', 'b']));
    expect(selectOnPress(new Set(['a', 'b']), 'a', true)).toEqual(new Set(['b']));
  });

  it('does not mutate the selection it was given', () => {
    const before = new Set(['a']);
    const after = selectOnPress(before, 'b', true);
    expect(after).toEqual(new Set(['a', 'b']));
    expect(before).toEqual(new Set(['a']));
  });
});

describe('selectOnRelease', () => {
  it('narrows a multi-selection to the element a plain click landed on', () => {
    expect(selectOnRelease(new Set(['a', 'b', 'c']), 'b', false, false)).toEqual(new Set(['b']));
  });

  it('keeps the multi-selection after a drag, which moved all of it', () => {
    const current = new Set(['a', 'b']);
    expect(selectOnRelease(current, 'a', false, true)).toBe(current);
  });

  it('keeps what a Shift-click left, even when it removed the clicked element', () => {
    const current = new Set(['b', 'c']);
    expect(selectOnRelease(current, 'a', true, false)).toBe(current);
    expect(selectOnRelease(current, 'b', true, false)).toBe(current);
  });
});

describe('dragStarts', () => {
  const document: Document = {
    id: 'doc_drag',
    schemaVersion: 1,
    title: 'Drag',
    type: 'architecture',
    nodes: [
      { id: 'n_box', kind: 'container', label: 'Box', shape: 'container', geometry: { x: 0, y: 0, w: 400, h: 300 } },
      { id: 'n_in', kind: 'service', label: 'In', shape: 'rectangle', geometry: { x: 20, y: 40, w: 100, h: 50 }, parent: 'n_box' },
      { id: 'n_out', kind: 'service', label: 'Out', shape: 'rectangle', geometry: { x: 600, y: 0, w: 100, h: 50 } },
      { id: 'n_loose', kind: 'service', label: 'Loose', shape: 'rectangle' },
    ],
    edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_in' }, to: { node: 'n_out' } }],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
  };

  it('moves a container together with the nodes drawn inside it, and nothing else', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const starts = dragStarts(indexDocument(document), new Set(['n_box']));
    warn.mockRestore();
    expect(starts).toEqual(
      new Map([
        ['n_box', { x: 0, y: 0, w: 400, h: 300 }],
        ['n_in', { x: 20, y: 40, w: 100, h: 50 }],
      ]),
    );
  });

  it('leaves edges out and starts an unplaced node from its provisional spot', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const starts = dragStarts(indexDocument(document), new Set(['e_1', 'n_out', 'n_loose']));
    warn.mockRestore();
    // The provisional stack starts at the left of the placed content, one 16px gap below its bottom (300), 160x48.
    expect(starts).toEqual(
      new Map([
        ['n_out', { x: 600, y: 0, w: 100, h: 50 }],
        ['n_loose', { x: 0, y: 316, w: 160, h: 48 }],
      ]),
    );
  });
});

describe('stepSelection', () => {
  const order = ['a', 'b', 'c'];

  it('moves a single selection to the next node on Tab and the previous on Shift+Tab', () => {
    expect(stepSelection(order, new Set(['b']), 1)).toEqual(new Set(['c']));
    expect(stepSelection(order, new Set(['b']), -1)).toEqual(new Set(['a']));
  });

  it('does not wrap: past the last node, or before the first, there is no next selection', () => {
    expect(stepSelection(order, new Set(['c']), 1)).toBeUndefined();
    expect(stepSelection(order, new Set(['a']), -1)).toBeUndefined();
    expect(stepSelection(order, new Set(['b', 'c']), 1)).toBeUndefined();
    expect(stepSelection(order, new Set(['a', 'b']), -1)).toBeUndefined();
  });

  it('starts at the first node, or the last for Shift+Tab, when no node is selected', () => {
    expect(stepSelection(order, new Set(), 1)).toEqual(new Set(['a']));
    expect(stepSelection(order, new Set(), -1)).toEqual(new Set(['c']));
    // An edge is not in the walk: selecting one starts the walk over.
    expect(stepSelection(order, new Set(['e_1']), 1)).toEqual(new Set(['a']));
  });

  it('walks a multi-selection on from its last node in document order and back from its first', () => {
    // Selection order is deliberately the reverse of document order.
    expect(stepSelection(['a', 'b', 'c', 'd'], new Set(['c', 'b']), 1)).toEqual(new Set(['d']));
    expect(stepSelection(['a', 'b', 'c', 'd'], new Set(['c', 'b']), -1)).toEqual(new Set(['a']));
  });

  it('has no next selection when there are no nodes to walk', () => {
    expect(stepSelection([], new Set(['e_1']), 1)).toBeUndefined();
    expect(stepSelection([], new Set(), -1)).toBeUndefined();
  });
});
