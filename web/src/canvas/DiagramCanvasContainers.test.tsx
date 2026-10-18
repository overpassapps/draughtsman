// @vitest-environment jsdom
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import type { Document, Geometry, Id, Point } from '../model/types.ts';
import { DiagramCanvas } from './DiagramCanvas.tsx';

const size = { width: 800, height: 600 };

// Fits at zoom 1 (fit never zooms in past 1), so a screen delta is the world delta. c_box (0,0 300x200) holds n_in
// (20,20 40x40) and n_in2 (200,120 40x40), joined by e_in with one waypoint; n_out (400,0 40x40) is top level.
const document: Document = {
  id: 'doc_containers',
  schemaVersion: 1,
  title: 'Containers',
  type: 'architecture',
  nodes: [
    { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 300, h: 200 } },
    { id: 'n_in', kind: 'service', label: 'In', shape: 'rectangle', parent: 'c_box', geometry: { x: 20, y: 20, w: 40, h: 40 } },
    { id: 'n_in2', kind: 'service', label: 'In 2', shape: 'rectangle', parent: 'c_box', geometry: { x: 200, y: 120, w: 40, h: 40 } },
    { id: 'n_out', kind: 'service', label: 'Out', shape: 'rectangle', geometry: { x: 400, y: 0, w: 40, h: 40 } },
  ],
  edges: [{ id: 'e_in', kind: 'flow', from: { node: 'n_in' }, to: { node: 'n_in2' }, routing: { mode: 'straight', waypoints: [{ x: 120, y: 40 }] } }],
  meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
};

function renderCanvas(props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}) {
  const result = render(<DiagramCanvas document={document} theme={diagramThemes.overpass} size={size} grid={false} {...props} />);
  const canvas = result.container.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas) {
    throw new Error('canvas not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  return { ...result, canvas };
}

const shapeOf = (root: HTMLElement, id: string): SVGElement => {
  const shape = root.querySelector<SVGElement>(`g[data-id="${id}"] .shape`);
  if (!shape) {
    throw new Error(`no shape for ${id}`);
  }
  return shape;
};

const highlighted = (root: HTMLElement): (string | null)[] => [...root.querySelectorAll('.drop-target')].map((rect) => rect.getAttribute('data-drop-target'));

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

/** Presses on `id`, drags by (dx, dy) in one move, flushes the frame, and returns a function that releases. */
function drag(root: HTMLElement, canvas: HTMLElement, id: string, dx: number, dy: number): () => void {
  fireEvent.pointerDown(shapeOf(root, id), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
  fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 100 + dx, clientY: 100 + dy });
  flushFrame();
  return () => {
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100 + dx, clientY: 100 + dy });
  };
}

describe('DiagramCanvas containers', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('moves a dragged container’s children and the waypoints of the edge inside it by the same delta', () => {
    const onMoveNodes = vi.fn();
    const { container, canvas } = renderCanvas({ onMoveNodes, onMoveEnd: vi.fn() });
    drag(container, canvas, 'c_box', 30, 50)();
    expect(onMoveNodes).toHaveBeenLastCalledWith(
      new Map([
        ['c_box', { x: 30, y: 50, w: 300, h: 200 }],
        ['n_in', { x: 50, y: 70, w: 40, h: 40 }],
        ['n_in2', { x: 230, y: 170, w: 40, h: 40 }],
      ]),
      new Map([['e_in', [{ x: 150, y: 90 }]]]),
    );
  });

  it('leaves the waypoints alone when only one end of the edge moves', () => {
    const onMoveNodes = vi.fn();
    const { container, canvas } = renderCanvas({ onMoveNodes, onMoveEnd: vi.fn() });
    drag(container, canvas, 'n_in', 30, 30)();
    expect(onMoveNodes).toHaveBeenLastCalledWith(new Map([['n_in', { x: 50, y: 50, w: 40, h: 40 }]]));
  });

  it('outlines the container a drag would drop into, then reparents into it on release before the gesture ends', () => {
    const calls: string[] = [];
    const onReparentNodes = vi.fn((parents: ReadonlyMap<Id, Id | null>) => {
      calls.push(`reparent ${JSON.stringify([...parents])}`);
    });
    const { container, canvas } = renderCanvas({
      onMoveNodes: () => calls.push('move'),
      onMoveEnd: () => calls.push('end'),
      onReparentNodes,
    });
    expect(highlighted(container)).toEqual([]);
    // n_out from (400,0) to (100,50): fully inside c_box.
    const release = drag(container, canvas, 'n_out', -300, 50);
    expect(highlighted(container)).toEqual(['c_box']);
    release();
    expect(onReparentNodes).toHaveBeenCalledTimes(1);
    expect(calls.slice(-2)).toEqual(['reparent [["n_out","c_box"]]', 'end']);
    expect(highlighted(container)).toEqual([]);
  });

  it('clears the parent of a node dragged out of its container', () => {
    const onReparentNodes = vi.fn();
    const { container, canvas } = renderCanvas({ onMoveNodes: vi.fn(), onMoveEnd: vi.fn(), onReparentNodes });
    // n_in from (20,20) to (20,320): below c_box.
    const release = drag(container, canvas, 'n_in', 0, 300);
    expect(highlighted(container)).toEqual([]);
    release();
    expect(onReparentNodes).toHaveBeenCalledWith(new Map([['n_in', null]]));
  });

  it('keeps the parent for a move within the container, and neither shows nor drops anything without a handler', () => {
    const onReparentNodes = vi.fn();
    const first = renderCanvas({ onMoveNodes: vi.fn(), onMoveEnd: vi.fn(), onReparentNodes });
    const release = drag(first.container, first.canvas, 'n_in', 50, 50);
    // Still inside c_box, which is outlined, but nothing changes on release.
    expect(highlighted(first.container)).toEqual(['c_box']);
    release();
    expect(onReparentNodes).not.toHaveBeenCalled();
    first.unmount();

    const second = renderCanvas({ onMoveNodes: vi.fn(), onMoveEnd: vi.fn() });
    const releaseOut = drag(second.container, second.canvas, 'n_out', -300, 50);
    expect(highlighted(second.container)).toEqual([]);
    releaseOut();
  });

  it('nudges a container with its children and inner waypoints', () => {
    const onMoveNodes = vi.fn<(moves: ReadonlyMap<Id, Geometry>, waypoints?: ReadonlyMap<Id, Point[]>) => void>();
    const { container, canvas } = renderCanvas({ onMoveNodes, onMoveEnd: vi.fn() });
    fireEvent.pointerDown(shapeOf(container, 'c_box'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    fireEvent.keyDown(canvas, { key: 'ArrowRight', shiftKey: true });
    expect(onMoveNodes).toHaveBeenCalledTimes(1);
    expect(onMoveNodes.mock.calls[0]?.[1]).toEqual(new Map([['e_in', [{ x: 130, y: 40 }]]]));
    expect(onMoveNodes.mock.calls[0]?.[0].get('n_in2')).toEqual({ x: 210, y: 120, w: 40, h: 40 });
  });

  it('groups on Cmd/Ctrl+G and ungroups on Cmd/Ctrl+Shift+G, selecting what the handler returns', () => {
    const onGroup = vi.fn((_ids: ReadonlySet<Id>, command: string) => (command === 'group' ? new Set(['c_new']) : new Set(['n_in', 'n_in2'])));
    const onSelectionChange = vi.fn();
    const { container, canvas } = renderCanvas({ onGroup, onSelectionChange });
    // Nothing selected: the key is held back from the browser but nothing is grouped.
    const idle = new KeyboardEvent('keydown', { key: 'g', metaKey: true, bubbles: true, cancelable: true });
    canvas.dispatchEvent(idle);
    expect(idle.defaultPrevented).toBe(true);
    expect(onGroup).not.toHaveBeenCalled();

    fireEvent.pointerDown(shapeOf(container, 'n_out'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    fireEvent.keyDown(canvas, { key: 'g', ctrlKey: true });
    expect(onGroup).toHaveBeenLastCalledWith(new Set(['n_out']), 'group');
    expect(onSelectionChange).toHaveBeenLastCalledWith(new Set(['c_new']));

    fireEvent.keyDown(canvas, { key: 'G', metaKey: true, shiftKey: true });
    expect(onGroup).toHaveBeenLastCalledWith(new Set(['c_new']), 'ungroup');
    expect(onSelectionChange).toHaveBeenLastCalledWith(new Set(['n_in', 'n_in2']));
    // Alt is not a group chord.
    fireEvent.keyDown(canvas, { key: 'g', ctrlKey: true, altKey: true });
    expect(onGroup).toHaveBeenCalledTimes(2);
  });
});
