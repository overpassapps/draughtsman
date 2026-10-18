// @vitest-environment jsdom
import { act, fireEvent, render } from '@testing-library/react';
import { createRef } from 'react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Geometry, Point } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import { worldToScreen } from './viewport.ts';

const size = { width: 800, height: 600 };

function makeDocument(rotation?: number): Document {
  const geometry: Geometry = rotation === undefined ? { x: 0, y: 0, w: 100, h: 50 } : { x: 0, y: 0, w: 100, h: 50, rotation };
  return parseDocument({
    id: 'doc_rotate',
    schemaVersion: 1,
    title: 'Rotate',
    type: 'flowchart',
    nodes: [
      { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry },
      { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } },
    ],
    edges: [],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
  });
}

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

function renderCanvas(document: Document, props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}) {
  const ref = createRef<DiagramCanvasHandle>();
  const result = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} {...props} />);
  const canvas = result.container.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas) {
    throw new Error('canvas not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
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

function press(target: Element, pointerId: number, point: { clientX: number; clientY: number }, init: Record<string, unknown> = {}): void {
  fireEvent.pointerDown(target, { pointerId, button: 0, pointerType: 'mouse', ...point, ...init });
}

function shape(container: HTMLElement, id: string): Element {
  const found = container.querySelector(`g[data-id="${id}"] .shape`);
  if (!found) {
    throw new Error(`no shape for ${id}`);
  }
  return found;
}

function rotateHandle(container: HTMLElement, id: string): Element {
  const found = container.querySelector(`.selection-node[data-selection-id="${id}"] [data-rotate-handle]`);
  if (!found) {
    throw new Error(`no rotate handle for ${id}`);
  }
  return found;
}

/** The centre and the rotate handle's own screen position, read straight from the rendered overlay. */
function rotateGeometryScreen(container: HTMLElement, id: string): { centre: Point; handle: Point } {
  const outline = container.querySelector(`.selection-node[data-selection-id="${id}"] .selection-outline`);
  const handleEl = rotateHandle(container, id);
  if (!outline) {
    throw new Error(`no selection outline for ${id}`);
  }
  const x = Number(outline.getAttribute('x'));
  const y = Number(outline.getAttribute('y'));
  const w = Number(outline.getAttribute('width'));
  const h = Number(outline.getAttribute('height'));
  return {
    centre: { x: x + w / 2, y: y + h / 2 },
    handle: { x: Number(handleEl.getAttribute('cx')), y: Number(handleEl.getAttribute('cy')) },
  };
}

/** `p` turned `degrees` clockwise about `centre`, in the same y-down convention as geometry.rotation. */
function rotateAround(centre: Point, p: Point, degrees: number): Point {
  const radians = (degrees * Math.PI) / 180;
  const cos = Math.cos(radians);
  const sin = Math.sin(radians);
  const dx = p.x - centre.x;
  const dy = p.y - centre.y;
  return { x: centre.x + dx * cos - dy * sin, y: centre.y + dx * sin + dy * cos };
}

describe('DiagramCanvas rotate handle', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows a rotate handle only when a single node is selected, never for none or a multi-selection', () => {
    const document = makeDocument();
    const { container, canvas, at } = renderCanvas(document);
    expect(container.querySelector('[data-rotate-handle]')).toBeNull();

    press(shape(container, 'n_a'), 1, at({ x: 50, y: 25 }));
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(container.querySelectorAll('[data-rotate-handle]')).toHaveLength(1);

    press(shape(container, 'n_b'), 2, at({ x: 350, y: 25 }), { shiftKey: true });
    fireEvent.pointerUp(canvas, { pointerId: 2 });
    expect(container.querySelector('[data-rotate-handle]')).toBeNull();
  });

  it('rotates clockwise and anticlockwise in 1° steps, matching the on-screen drag exactly, as one undo step', () => {
    for (const delta of [40, -40]) {
      const onMove = vi.fn();
      const onMoveEnd = vi.fn();
      const { container, canvas, at, unmount } = renderCanvas(makeDocument(), { onMoveNodes: onMove, onMoveEnd });
      press(shape(container, 'n_a'), 1, at({ x: 50, y: 25 }));
      fireEvent.pointerUp(canvas, { pointerId: 1 });

      const { centre, handle } = rotateGeometryScreen(container, 'n_a');
      press(rotateHandle(container, 'n_a'), 2, { clientX: handle.x, clientY: handle.y });
      const dragged = rotateAround(centre, handle, delta);
      fireEvent.pointerMove(canvas, { pointerId: 2, clientX: dragged.x, clientY: dragged.y });
      flushFrame();

      const expected = delta > 0 ? delta : 360 + delta;
      expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 0, y: 0, w: 100, h: 50, rotation: expected }]]));
      expect(container.querySelector('.selection-rotate-label')?.textContent).toBe(`${String(expected)}°`);

      fireEvent.pointerUp(canvas, { pointerId: 2 });
      expect(onMoveEnd).toHaveBeenCalledTimes(1);
      // The label is view state: it disappears once the gesture ends.
      expect(container.querySelector('.selection-rotate-label')).toBeNull();
      unmount();
    }
  });

  it('quantises to 15° steps with Shift held, in both directions', () => {
    for (const delta of [38, -38]) {
      const onMove = vi.fn();
      const { container, canvas, at, unmount } = renderCanvas(makeDocument(), { onMoveNodes: onMove });
      press(shape(container, 'n_a'), 1, at({ x: 50, y: 25 }));
      fireEvent.pointerUp(canvas, { pointerId: 1 });

      const { centre, handle } = rotateGeometryScreen(container, 'n_a');
      press(rotateHandle(container, 'n_a'), 2, { clientX: handle.x, clientY: handle.y });
      const dragged = rotateAround(centre, handle, delta);
      fireEvent.pointerMove(canvas, { pointerId: 2, clientX: dragged.x, clientY: dragged.y, shiftKey: true });
      flushFrame();

      // 38° (or -38°) rounds to the nearest 15° step: 45 (or 360 - 45 = 315).
      const expected = delta > 0 ? 45 : 315;
      expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 0, y: 0, w: 100, h: 50, rotation: expected }]]));
      fireEvent.pointerUp(canvas, { pointerId: 2 });
      unmount();
    }
  });

  it('normalises the model value to [0, 360) when a drag carries it past a full turn', () => {
    const onMove = vi.fn();
    const { container, canvas, at } = renderCanvas(makeDocument(350), { onMoveNodes: onMove });
    press(shape(container, 'n_a'), 1, at({ x: 50, y: 25 }));
    fireEvent.pointerUp(canvas, { pointerId: 1 });

    const { centre, handle } = rotateGeometryScreen(container, 'n_a');
    press(rotateHandle(container, 'n_a'), 2, { clientX: handle.x, clientY: handle.y });
    // Starting at 350°, 20° further clockwise crosses 360 and lands on 10.
    const dragged = rotateAround(centre, handle, 20);
    fireEvent.pointerMove(canvas, { pointerId: 2, clientX: dragged.x, clientY: dragged.y });
    flushFrame();

    expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 0, y: 0, w: 100, h: 50, rotation: 10 }]]));
    fireEvent.pointerUp(canvas, { pointerId: 2 });
  });

  it('writes nothing for a press that never travels the drag threshold', () => {
    const onMove = vi.fn();
    const onMoveEnd = vi.fn();
    const { container, canvas, at } = renderCanvas(makeDocument(), { onMoveNodes: onMove, onMoveEnd });
    press(shape(container, 'n_a'), 1, at({ x: 50, y: 25 }));
    fireEvent.pointerUp(canvas, { pointerId: 1 });

    const { handle } = rotateGeometryScreen(container, 'n_a');
    press(rotateHandle(container, 'n_a'), 2, { clientX: handle.x, clientY: handle.y });
    fireEvent.pointerMove(canvas, { pointerId: 2, clientX: handle.x + 1, clientY: handle.y });
    flushFrame();
    fireEvent.pointerUp(canvas, { pointerId: 2 });

    expect(onMove).not.toHaveBeenCalled();
    expect(onMoveEnd).not.toHaveBeenCalled();
  });
});
