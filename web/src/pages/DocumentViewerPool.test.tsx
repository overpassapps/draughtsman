// @vitest-environment jsdom
/** A pool dragged out of the palette lands with its lanes, at a working size, as one undo step (QA P4). */
import './loadTolerance.ts';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { parseDocument } from '../model/parse.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };
const empty = parseDocument({ id: 'doc_pool', schemaVersion: 1, title: 'Process', type: 'flowchart', nodes: [], edges: [], meta: { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' } });

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

describe('dropping a pool', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('places a sized pool with two lanes inside it, selects the pool, and takes all of it away with one undo', () => {
    const { container } = render(<DocumentViewer id={empty.id} document={empty} etag={'"dv1:loaded"'} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
    fireEvent.click(screen.getByRole('button', { name: 'Swimlane' }));
    const glyph = within(screen.getByRole('region', { name: 'Shapes' })).getByRole('button', { name: 'Pool' });
    fireEvent.pointerDown(glyph, { ...mouse, clientX: 5, clientY: 5 });
    fireEvent.pointerUp(canvas, { ...mouse, clientX: 600, clientY: 400 });
    flushFrame();

    const kinds = [...container.querySelectorAll('g.nodes g[data-id]')].map((g) => g.getAttribute('data-kind'));
    expect(kinds.filter((kind) => kind === 'pool')).toHaveLength(1);
    expect(kinds.filter((kind) => kind === 'lane')).toHaveLength(2);
    // The pool is a working size: its shape is wider than the 160 px box a plain node gets.
    const pool = container.querySelector('g[data-kind="pool"] .shape');
    expect(Number(pool?.getAttribute('width'))).toBeGreaterThanOrEqual(480);
    expect(container.querySelectorAll('.selection-node')).toHaveLength(1);

    fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
    expect(container.querySelectorAll('g.nodes g[data-id]')).toHaveLength(0);
  });
});
