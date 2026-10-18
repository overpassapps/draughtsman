// @vitest-environment jsdom
import { act, fireEvent, render } from '@testing-library/react';
import { createRef } from 'react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Point } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import { worldToScreen } from './viewport.ts';

const size = { width: 800, height: 600 };

// n_a and n_b share a row 100 apart; n_c sits below n_a. The content fits at zoom 1, centred.
const document: Document = parseDocument({
  id: 'doc_marquee',
  schemaVersion: 1,
  title: 'Marquee',
  type: 'flowchart',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
    { id: 'n_c', kind: 'service', label: 'C', shape: 'rectangle', geometry: { x: 0, y: 200, w: 100, h: 50 } },
  ],
  edges: [],
  meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
});

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

function renderCanvas(props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}) {
  const ref = createRef<DiagramCanvasHandle>();
  const result = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} {...props} />);
  const canvas = result.container.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas) {
    throw new Error('canvas not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  /** The client position of a world point; jsdom puts the canvas box at the page origin. */
  const at = (world: Point): { clientX: number; clientY: number } => {
    const viewport = ref.current?.getViewport();
    if (!viewport) {
      throw new Error('no viewport');
    }
    const screen = worldToScreen(viewport, world);
    return { clientX: screen.x, clientY: screen.y };
  };
  return { ...result, canvas, ref, at };
}

function selected(container: HTMLElement): string[] {
  return [...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id') ?? '').sort();
}

function press(target: Element, pointerId: number, point: { clientX: number; clientY: number }, init: Record<string, unknown> = {}): void {
  fireEvent.pointerDown(target, { pointerId, button: 0, pointerType: 'mouse', ...point, ...init });
}

describe('DiagramCanvas marquee', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('draws a marquee from an empty-canvas drag and selects every node it intersects on release, without panning', () => {
    const onSelection = vi.fn();
    const { container, canvas, ref, at } = renderCanvas({ onSelectionChange: onSelection });
    const before = ref.current?.getViewport();
    // From above-left of n_a to the middle of n_b: catches half of each, misses n_c below.
    press(canvas, 1, at({ x: -20, y: -20 }));
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 250, y: 25 }) });
    flushFrame();
    const box = container.querySelector('.marquee');
    expect(box?.getAttribute('width')).toBe('270');
    expect(box?.getAttribute('height')).toBe('45');
    // Nothing is selected until release.
    expect(selected(container)).toEqual([]);
    fireEvent.pointerUp(canvas, { pointerId: 1, ...at({ x: 250, y: 25 }) });
    flushFrame();
    expect(selected(container)).toEqual(['n_a', 'n_b']);
    expect(onSelection).toHaveBeenLastCalledWith(new Set(['n_a', 'n_b']));
    expect(container.querySelector('.marquee')).toBeNull();
    expect(ref.current?.getViewport()).toEqual(before);
  });

  it('with Alt held on release selects only the nodes the marquee fully encloses', () => {
    const { container, canvas, at } = renderCanvas();
    press(canvas, 1, at({ x: -20, y: -20 }));
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 250, y: 60 }) });
    fireEvent.pointerUp(canvas, { pointerId: 1, altKey: true, ...at({ x: 250, y: 60 }) });
    expect(selected(container)).toEqual(['n_a']);
  });

  it('with Shift adds what it catches to the selection; without Shift it replaces the selection', () => {
    const { container, canvas, at } = renderCanvas();
    const nodeC = container.querySelector('g[data-id="n_c"] .shape');
    if (!nodeC) {
      throw new Error('n_c not rendered');
    }
    press(nodeC, 1, at({ x: 50, y: 225 }));
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(selected(container)).toEqual(['n_c']);

    press(canvas, 2, at({ x: 150, y: -20 }), { shiftKey: true });
    fireEvent.pointerMove(canvas, { pointerId: 2, ...at({ x: 250, y: 25 }) });
    fireEvent.pointerUp(canvas, { pointerId: 2, shiftKey: true, ...at({ x: 250, y: 25 }) });
    expect(selected(container)).toEqual(['n_b', 'n_c']);

    press(canvas, 3, at({ x: -20, y: -20 }));
    fireEvent.pointerMove(canvas, { pointerId: 3, ...at({ x: 50, y: 25 }) });
    fireEvent.pointerUp(canvas, { pointerId: 3, ...at({ x: 50, y: 25 }) });
    expect(selected(container)).toEqual(['n_a']);
  });

  it('abandons the marquee on Escape, selecting nothing on the release that follows', () => {
    const { container, canvas, at } = renderCanvas();
    press(canvas, 1, at({ x: -20, y: -20 }));
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 250, y: 25 }) });
    expect(container.querySelector('.marquee')).not.toBeNull();
    fireEvent.keyDown(canvas, { key: 'Escape' });
    expect(container.querySelector('.marquee')).toBeNull();
    fireEvent.pointerUp(canvas, { pointerId: 1, ...at({ x: 250, y: 25 }) });
    expect(selected(container)).toEqual([]);
  });

  it('draws nothing for a press that never travels the drag threshold', () => {
    const { container, canvas, at } = renderCanvas();
    press(canvas, 1, at({ x: 150, y: 100 }));
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 152, y: 100 }) });
    expect(container.querySelector('.marquee')).toBeNull();
    fireEvent.pointerUp(canvas, { pointerId: 1, ...at({ x: 152, y: 100 }) });
    expect(selected(container)).toEqual([]);
  });

  it('pans instead in the connector tool', () => {
    const { container, canvas, ref, at } = renderCanvas({ tool: 'connector' });
    const before = ref.current?.getViewport();
    press(canvas, 1, at({ x: 150, y: 100 }));
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 180, y: 115 }) });
    flushFrame();
    expect(container.querySelector('.marquee')).toBeNull();
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    expect(ref.current?.getViewport().x).toBeCloseTo((before?.x ?? 0) + 30);
  });
});

describe('DiagramCanvas snapping', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  function shape(container: HTMLElement, id: string): Element {
    const found = container.querySelector(`g[data-id="${id}"] .shape`);
    if (!found) {
      throw new Error(`no shape for ${id}`);
    }
    return found;
  }

  it('snaps a dragged node to a neighbour edge within 6 px, shows the guide across both nodes, and clears it on release', () => {
    const onMove = vi.fn();
    const onMoveEnd = vi.fn();
    const { container, canvas, at } = renderCanvas({ grid: false, onMoveNodes: onMove, onMoveEnd });
    press(shape(container, 'n_a'), 1, at({ x: 50, y: 25 }));
    // 95 right and 40 down: n_a's right edge would sit at 195, 5 px short of n_b's left edge.
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 145, y: 65 }) });
    flushFrame();
    expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 100, y: 40, w: 100, h: 50 }]]));
    const guides = container.querySelectorAll('.snap-guide');
    expect(guides).toHaveLength(1);
    expect(guides[0]?.getAttribute('data-guide')).toBe('edge');
    const line = guides[0]?.querySelector('line');
    // Vertical at n_b's left edge, from n_b's top (0) to the moved n_a's bottom (90).
    expect([line?.getAttribute('x1'), line?.getAttribute('y1'), line?.getAttribute('y2')]).toEqual([String(at({ x: 200, y: 0 }).clientX), String(at({ x: 200, y: 0 }).clientY), String(at({ x: 200, y: 90 }).clientY)]);

    // 12 px short is out of the magnet: the node follows the pointer and the guide goes.
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 138, y: 65 }) });
    flushFrame();
    expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 88, y: 40, w: 100, h: 50 }]]));
    expect(container.querySelectorAll('.snap-guide')).toHaveLength(0);

    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 145, y: 65 }) });
    flushFrame();
    expect(container.querySelectorAll('.snap-guide')).toHaveLength(1);
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 100, y: 40, w: 100, h: 50 }]]));
    expect(onMoveEnd).toHaveBeenCalledTimes(1);
    expect(container.querySelectorAll('.snap-guide')).toHaveLength(0);
  });

  it('jumps a dragged node between grid lines while the grid is on, and not while it is off', () => {
    for (const grid of [true, false]) {
      const onMove = vi.fn();
      const { container, canvas, at, unmount } = renderCanvas({ grid, onMoveNodes: onMove });
      // Down 103: n_c's top would sit at 303, 3 from a grid line (its bottom is 7 from one); the grid pulls it to 300.
      press(shape(container, 'n_c'), 1, at({ x: 50, y: 225 }));
      fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 50, y: 328 }) });
      flushFrame();
      expect(onMove).toHaveBeenLastCalledWith(new Map([['n_c', { x: 0, y: grid ? 300 : 303, w: 100, h: 50 }]]));
      expect(container.querySelectorAll('.snap-guide-grid').length > 0).toBe(grid);
      expect(container.querySelector('.canvas-grid') !== null).toBe(grid);
      fireEvent.pointerUp(canvas, { pointerId: 1 });
      unmount();
    }
  });

  it('snaps a resized edge to a neighbour and shows the guide', () => {
    const onMove = vi.fn();
    const { container, canvas, at } = renderCanvas({ grid: false, onMoveNodes: onMove });
    press(shape(container, 'n_a'), 9, at({ x: 50, y: 25 }));
    fireEvent.pointerUp(canvas, { pointerId: 9 });
    // Handle 3 is the right-edge midpoint; drag it 96 right, 4 px short of n_b's left edge.
    const handle = container.querySelector('.selection-node[data-selection-id="n_a"] [data-handle="3"]');
    if (!handle) {
      throw new Error('no handle');
    }
    press(handle, 1, at({ x: 100, y: 25 }));
    fireEvent.pointerMove(canvas, { pointerId: 1, ...at({ x: 196, y: 25 }) });
    flushFrame();
    expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 0, y: 0, w: 200, h: 50 }]]));
    expect(container.querySelector('.snap-guide')?.getAttribute('data-guide')).toBe('edge');
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(container.querySelectorAll('.snap-guide')).toHaveLength(0);
  });
});
