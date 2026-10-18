// @vitest-environment jsdom
import { act, fireEvent, render } from '@testing-library/react';
import { createRef, useState } from 'react';
import orderService from '../../../docs/examples/order-service.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import { screenToWorld, type Viewport } from './viewport.ts';

const document = parseDocument(orderService);
const size = { width: 800, height: 600 };

function transformOf(container: HTMLElement): string {
  return container.querySelector('g.world')?.getAttribute('transform') ?? '';
}

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

describe('DiagramCanvas', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('fits the document on first render and exposes the viewport', () => {
    const changes: Viewport[] = [];
    const { container } = render(<DiagramCanvas document={document} theme={diagramThemes.overpass} size={size} onViewportChange={(v) => changes.push(v)} />);
    expect(changes).toHaveLength(1);
    expect(changes[0]?.zoom).toBeGreaterThan(0);
    expect(changes[0]?.zoom).toBeLessThanOrEqual(1);
    expect(transformOf(container)).toContain('scale(');
  });

  it('converts a client point to the same world point screenToWorld gives for it, offset by the canvas box', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas || !ref.current) {
      throw new Error('canvas not rendered');
    }
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue(new DOMRect(50, 20, size.width, size.height));
    const viewport = ref.current.getViewport();
    // A client point offset by the box's own position lands on the same world point local coordinates would.
    expect(ref.current.pointToWorld({ x: 50 + 12, y: 20 + 34 })).toEqual(screenToWorld(viewport, { x: 12, y: 34 }));
    // The two points differ: this is not an identity function that ignores the box position.
    expect(ref.current.pointToWorld({ x: 12, y: 34 })).not.toEqual(ref.current.pointToWorld({ x: 50 + 12, y: 20 + 34 }));
  });

  it('zooms with Cmd/Ctrl and = - 1 0, as the editor-shell atlas specifies', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    const fitted = ref.current?.getViewport();
    const initial = fitted?.zoom ?? 0;

    fireEvent.keyDown(canvas, { key: '=', ctrlKey: true });
    flushFrame();
    expect(ref.current?.getViewport().zoom).toBeCloseTo(initial * 1.25);

    fireEvent.keyDown(canvas, { key: '-', metaKey: true });
    flushFrame();
    expect(ref.current?.getViewport().zoom).toBeCloseTo(initial);

    fireEvent.keyDown(canvas, { key: '1', ctrlKey: true });
    flushFrame();
    expect(ref.current?.getViewport().zoom).toBe(1);
    expect(transformOf(container)).toContain('scale(1)');

    fireEvent.keyDown(canvas, { key: '0', metaKey: true });
    flushFrame();
    expect(ref.current?.getViewport()).toEqual(fitted);
  });

  it('leaves unmodified digit and sign keys alone', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    const before = ref.current?.getViewport();
    for (const key of ['0', '1', '+', '-', '=']) {
      fireEvent.keyDown(canvas, { key });
    }
    flushFrame();
    expect(ref.current?.getViewport()).toEqual(before);
  });

  it('pans on a plain wheel or two-finger scroll', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    const before = ref.current?.getViewport();
    fireEvent.wheel(canvas, { deltaX: 12, deltaY: 40 });
    flushFrame();
    const after = ref.current?.getViewport();
    // Scrolling down and right moves the drawing up and left, as in every scrolling surface.
    expect(after?.x).toBeCloseTo((before?.x ?? 0) - 12);
    expect(after?.y).toBeCloseTo((before?.y ?? 0) - 40);
    expect(after?.zoom).toBe(before?.zoom);
  });

  it('zooms around the cursor on Ctrl/Cmd+wheel and trackpad pinch', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    const before = ref.current?.getViewport();
    fireEvent.wheel(canvas, { deltaY: -100, ctrlKey: true, clientX: 0, clientY: 0 });
    flushFrame();
    const after = ref.current?.getViewport();
    if (!before || !after) {
      throw new Error('viewport not reported');
    }
    expect(after.zoom).toBeGreaterThan(before.zoom);
    // The world point under the cursor (jsdom reports the element at 0,0) stays put.
    const cursor = { x: 0, y: 0 };
    expect(screenToWorld(after, cursor).x).toBeCloseTo(screenToWorld(before, cursor).x);
    expect(screenToWorld(after, cursor).y).toBeCloseTo(screenToWorld(before, cursor).y);

    fireEvent.wheel(canvas, { deltaY: 100, metaKey: true, clientX: 0, clientY: 0 });
    flushFrame();
    expect(ref.current?.getViewport().zoom).toBeCloseTo(before.zoom);
  });

  // An empty-canvas mouse drag in the select tool is a marquee (DiagramCanvasMarquee.test.tsx); panning stays on space,
  // the middle button, one finger, and a read-only view.
  it.each([
    { name: 'space+drag', press: { button: 0, pointerType: 'mouse' }, space: true, readOnly: false },
    { name: 'the middle mouse button', press: { button: 1, pointerType: 'mouse' }, space: false, readOnly: false },
    { name: 'one finger', press: { button: 0, pointerType: 'touch' }, space: false, readOnly: false },
    { name: 'a mouse drag on a read-only view', press: { button: 0, pointerType: 'mouse' }, space: false, readOnly: true },
  ])('pans on $name over empty space', ({ press, space, readOnly }) => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} readOnly={readOnly} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    canvas.setPointerCapture = () => undefined;
    canvas.releasePointerCapture = () => undefined;
    canvas.hasPointerCapture = () => false;
    if (space) {
      fireEvent.keyDown(canvas, { key: ' ' });
    }
    const before = ref.current?.getViewport();
    fireEvent.pointerDown(canvas, { pointerId: 1, ...press, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId: 1, pointerType: press.pointerType, clientX: 40, clientY: 25 });
    fireEvent.pointerUp(canvas, { pointerId: 1, pointerType: press.pointerType });
    flushFrame();
    const after = ref.current?.getViewport();
    expect(after?.x).toBeCloseTo((before?.x ?? 0) + 30);
    expect(after?.y).toBeCloseTo((before?.y ?? 0) + 15);
    expect(after?.zoom).toBe(before?.zoom);
    expect(container.querySelector('.marquee')).toBeNull();
  });

  it('selects an edge on a press instead of panning', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    const edge = container.querySelector<SVGElement>('g.edge[data-id] polyline.hit-area');
    if (!canvas || !edge) {
      throw new Error('canvas or edge not rendered');
    }
    const id = edge.closest('g[data-id]')?.getAttribute('data-id');
    canvas.setPointerCapture = () => undefined;
    canvas.releasePointerCapture = () => undefined;
    canvas.hasPointerCapture = () => false;
    const before = ref.current?.getViewport();
    fireEvent.pointerDown(edge, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 40, clientY: 25 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    const after = ref.current?.getViewport();
    expect(after).toEqual(before);
    expect(globalThis.document.activeElement).toBe(canvas);
    expect(container.querySelector(`.selection-edge[data-selection-id="${id ?? ''}"]`)).not.toBeNull();
  });

  it('leaves a press on a node alone, so selection can claim it', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    const node = container.querySelector<SVGElement>('g.node[data-id] .shape');
    if (!canvas || !node) {
      throw new Error('canvas or node not rendered');
    }
    canvas.setPointerCapture = () => undefined;
    canvas.releasePointerCapture = () => undefined;
    canvas.hasPointerCapture = () => false;
    const before = ref.current?.getViewport();
    fireEvent.pointerDown(node, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 40, clientY: 25 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    expect(ref.current?.getViewport()).toEqual(before);
  });

  it('keeps a drag going when the parent re-renders with a fresh callback', () => {
    const ref = createRef<DiagramCanvasHandle>();
    // A parent that re-renders on every viewport change, handing the canvas a new inline callback each time.
    function Host() {
      const [frames, setFrames] = useState(0);
      return (
        <div data-frames={frames}>
          <DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} onViewportChange={() => { setFrames((n) => n + 1); }} />
        </div>
      );
    }
    const { container } = render(<Host />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    canvas.setPointerCapture = () => undefined;
    canvas.releasePointerCapture = () => undefined;
    canvas.hasPointerCapture = () => false;
    flushFrame();
    const before = ref.current?.getViewport();
    fireEvent.pointerDown(canvas, { pointerId: 1, button: 1, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 40, clientY: 25 });
    flushFrame();
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 60, clientY: 45 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    const after = ref.current?.getViewport();
    expect(after?.x).toBeCloseTo((before?.x ?? 0) + 50);
    expect(after?.y).toBeCloseTo((before?.y ?? 0) + 35);
  });

  it('takes keyboard focus on a press on empty space so the shortcuts work without tabbing', () => {
    // preventDefault on pointerdown cancels the browser's default mousedown focus; jsdom never focuses on
    // mousedown at all, so this only passes when the canvas focuses itself.
    const { container } = render(<DiagramCanvas document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    canvas.setPointerCapture = () => undefined;
    canvas.releasePointerCapture = () => undefined;
    canvas.hasPointerCapture = () => false;
    expect(globalThis.document.activeElement).not.toBe(canvas);
    fireEvent.pointerDown(canvas, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(globalThis.document.activeElement).toBe(canvas);
    // Script focus from a pointer press would otherwise match :focus-visible and paint the keyboard ring around the
    // whole canvas; the marker keeps the ring keyboard-only until the next key press.
    expect(canvas.hasAttribute('data-pointer-focus')).toBe(true);
    fireEvent.keyDown(canvas, { key: '=', ctrlKey: true });
    expect(canvas.hasAttribute('data-pointer-focus')).toBe(false);
  });

  it('focuses through the handle as a pointer press does, so toolbar buttons can hand focus to the canvas', () => {
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    ref.current?.focus();
    expect(globalThis.document.activeElement).toBe(canvas);
    expect(canvas.hasAttribute('data-pointer-focus')).toBe(true);
    fireEvent.blur(canvas);
    expect(canvas.hasAttribute('data-pointer-focus')).toBe(false);
  });

  it('shows a node the agent left without a position, keeps its edge, and says so', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    // One placed node and one the agent added with no position, joined by an edge: the user-facing repro where a
    // node exists in the model but used to vanish from the canvas, taking its edge with it and warning nobody.
    const repro: Document = {
      id: 'doc_repro',
      schemaVersion: 1,
      title: 'Repro',
      type: 'architecture',
      nodes: [
        { id: 'n_placed', kind: 'service', label: 'Placed', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
        { id: 'n_unplaced', kind: 'service', label: 'Unplaced', shape: 'rectangle' },
      ],
      edges: [{ id: 'e_join', kind: 'flow', from: { node: 'n_placed' }, to: { node: 'n_unplaced' } }],
      meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 'test' },
    };

    const { container } = render(<DiagramCanvas document={repro} theme={diagramThemes.overpass} size={size} />);

    expect(container.querySelector('g[data-id="n_placed"]')).not.toBeNull();
    const unplaced = container.querySelector('g[data-id="n_unplaced"]');
    expect(unplaced).not.toBeNull();
    expect(unplaced?.getAttribute('data-unplaced')).toBe('true');
    expect(container.querySelector('g[data-id="e_join"] polyline')).not.toBeNull();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('n_unplaced'));
    warn.mockRestore();
  });

  it('fits an all-unplaced document instead of sitting on a blank canvas', () => {
    const allUnplaced: Document = {
      id: 'doc_blank',
      schemaVersion: 1,
      title: 'Blank',
      type: 'flowchart',
      nodes: [
        { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle' },
        { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle' },
      ],
      edges: [],
      meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 'test' },
    };
    const ref = createRef<DiagramCanvasHandle>();
    const { container } = render(<DiagramCanvas ref={ref} document={allUnplaced} theme={diagramThemes.overpass} size={size} />);

    // Nothing is positioned — the Mermaid-import case — but the provisional stack gives Fit something to frame.
    expect(container.querySelector('g[data-id="n_a"]')).not.toBeNull();
    expect(container.querySelector('g[data-id="n_b"]')).not.toBeNull();
    expect(ref.current?.getViewport().zoom).toBeGreaterThan(0);
  });
});
