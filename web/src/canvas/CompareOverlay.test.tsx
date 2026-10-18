// @vitest-environment jsdom
import { render } from '@testing-library/react';
import type { CompareMarks } from '../model/compare.ts';
import type { Document } from '../model/types.ts';
import { CompareOverlay } from './CompareOverlay.tsx';
import { indexDocument } from './documentIndex.ts';
import { IDENTITY_VIEWPORT } from './viewport.ts';

const doc: Document = {
  id: 'doc_o',
  schemaVersion: 1,
  title: 'O',
  type: 'flowchart',
  nodes: [
    { id: 'n_a', kind: 'process', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 40 } },
    { id: 'n_b', kind: 'process', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 40 } },
  ],
  edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
  meta: { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' },
};

const draw = (marks: CompareMarks, viewport = IDENTITY_VIEWPORT) =>
  render(<svg><CompareOverlay marks={marks} index={indexDocument(doc)} viewport={viewport} /></svg>).container;

describe('CompareOverlay', () => {
  it('draws added, changed and removed marks differently, each with its own badge, so colour is never the only cue', () => {
    const container = draw({ added: new Set(['n_a']), changed: new Set(['n_b', 'e_1']), removed: [{ id: 'n_gone', geometry: { x: 10, y: 100, w: 80, h: 30 } }] });

    expect(container.querySelector('[data-compare="added"][data-compare-id="n_a"] rect.compare-added')).not.toBeNull();
    expect(container.querySelector('[data-compare="changed"][data-compare-id="n_b"] rect.compare-changed')).not.toBeNull();
    expect(container.querySelector('polyline[data-compare="changed"][data-compare-id="e_1"]')).not.toBeNull();
    expect(container.querySelector('[data-compare="removed"][data-compare-id="n_gone"] rect.compare-removed')).not.toBeNull();
    const badges = [...container.querySelectorAll('.compare-badge text')].map((text) => text.textContent);
    expect(badges.sort()).toEqual(['+', '~', '−'].sort());
  });

  it('puts a removed node where the old version had it, scaled and moved with the view', () => {
    const container = draw({ added: new Set(), changed: new Set(), removed: [{ id: 'n_gone', geometry: { x: 10, y: 100, w: 80, h: 30 } }] }, { x: 5, y: 7, zoom: 2 });

    const rect = container.querySelector('rect.compare-removed');
    expect(Number(rect?.getAttribute('x'))).toBe(25);
    expect(Number(rect?.getAttribute('y'))).toBe(207);
    expect(Number(rect?.getAttribute('width'))).toBe(160);
    expect(Number(rect?.getAttribute('height'))).toBe(60);
  });

  it('never takes a press and is hidden from assistive technology', () => {
    const container = draw({ added: new Set(['n_a']), changed: new Set(), removed: [] });

    const group = container.querySelector('.compare-marks');
    expect(group?.getAttribute('pointer-events')).toBe('none');
    expect(group?.getAttribute('aria-hidden')).toBe('true');
  });

  it('draws nothing for an id the document does not have, and nothing at all when there is nothing to mark', () => {
    expect(draw({ added: new Set(['n_missing']), changed: new Set(), removed: [] }).querySelector('.compare-marks')).toBeNull();
    expect(draw({ added: new Set(), changed: new Set(), removed: [] }).querySelector('.compare-marks')).toBeNull();
  });
});
