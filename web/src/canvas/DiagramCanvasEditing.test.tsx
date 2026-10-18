// @vitest-environment jsdom
import { useState } from 'react';
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import type { Document, Geometry } from '../model/types.ts';
import { parseDocument } from '../model/parse.ts';
import { DiagramCanvas } from './DiagramCanvas.tsx';

const size = { width: 800, height: 600 };

// A small document whose content fits at zoom 1 (fit never zooms in past 1), so a screen delta is the world delta.
const document: Document = parseDocument({
  id: 'doc_edit',
  schemaVersion: 1,
  title: 'Edit',
  type: 'flowchart',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
  ],
  edges: [],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
});

function stubPointerCapture(canvas: HTMLElement) {
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
}

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

function renderCanvas(props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}, doc: Document = document) {
  const result = render(<DiagramCanvas document={doc} theme={diagramThemes.overpass} size={size} {...props} />);
  const canvas = result.container.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas) {
    throw new Error('canvas not rendered');
  }
  stubPointerCapture(canvas);
  return { ...result, canvas };
}

const shapeOf = (root: HTMLElement, id: string): SVGElement => {
  const shape = root.querySelector<SVGElement>(`g[data-id="${id}"] .shape`);
  if (!shape) {
    throw new Error(`no shape for ${id}`);
  }
  return shape;
};

describe('DiagramCanvas editing', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it.each([false, true])('keeps model-writing drags alive across frames (nested: %s)', (nested) => {
    const initial: Document = { ...document, nodes: [
      { id: 'box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 150, h: 100 } },
      { id: 'child', kind: 'service', label: 'Child', shape: 'rectangle', geometry: { x: 10, y: 10, w: 50, h: 30 }, ...(nested ? { parent: 'box' } : {}) },
    ] };
    let current = initial;
    const ended = vi.fn();
    function Host() {
      const [doc, setDoc] = useState(initial);
      current = doc;
      return <DiagramCanvas document={doc} theme={diagramThemes.overpass} size={size} fitKey={initial}
        onMoveNodes={(moves) => { setDoc((previous) => ({ ...previous, nodes: previous.nodes.map((node) => { const geometry = moves.get(node.id); return geometry ? { ...node, geometry } : node; }) })); }}
        onMoveEnd={ended} />;
    }
    const { container } = render(<Host />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) throw new Error('canvas missing');
    stubPointerCapture(canvas);
    fireEvent.pointerDown(shapeOf(container, 'child'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    // Each delta lands the child's edges on grid lines and clear of the box's edges and centre, so snapping keeps it.
    for (const delta of [30, 90, 190]) {
      fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 10 + delta, clientY: 10 });
      flushFrame();
      expect(current.nodes[1]?.geometry?.x).toBe(10 + delta);
      const child = container.querySelector('g[data-id="child"]');
      expect(child).not.toBeNull();
      expect(child?.closest('[clip-path]')).toBeNull();
      expect(child?.closest('.drag-layer')).not.toBeNull();
      expect(container.querySelectorAll('g[data-id="child"]')).toHaveLength(1);
    }
    expect(current.nodes[1]?.parent).toBe(nested ? 'box' : undefined);
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(ended).toHaveBeenCalledTimes(1);
    expect(current.nodes[1]?.geometry).toEqual({ x: 200, y: 10, w: 50, h: 30 });
    expect(container.querySelector('.drag-layer')).toBeNull();
  });

  it('selects a node on a plain press and shows eight handles', () => {
    const onSelection = vi.fn();
    const { container, canvas } = renderCanvas({ onSelectionChange: onSelection });
    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    const box = container.querySelector('.selection-node[data-selection-id="n_a"]');
    expect(box).not.toBeNull();
    expect(box?.querySelectorAll('.selection-handle')).toHaveLength(8);
    expect(container.querySelector('.selection-node[data-selection-id="n_b"]')).toBeNull();
    expect(onSelection).toHaveBeenLastCalledWith(new Set(['n_a']));
  });

  it('selects a node and an edge by pressing them far zoomed out, where they are drawn simplified', () => {
    // 20,000 units apart, the fitted zoom is about 0.04, far below FULL_DETAIL_ZOOM.
    const far = parseDocument({
      ...document,
      nodes: [...document.nodes, { id: 'n_far', kind: 'service', label: 'Far', shape: 'rectangle', geometry: { x: 20000, y: 0, w: 100, h: 50 } }],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_far' } }],
    });
    const onSelection = vi.fn();
    const { container, canvas } = renderCanvas({ onSelectionChange: onSelection }, far);
    const box = shapeOf(container, 'n_a');
    expect(box.parentElement?.getAttribute('data-id')).toBe('n_a');
    expect(container.querySelector('marker')).toBeNull();

    fireEvent.pointerDown(box, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(onSelection).toHaveBeenLastCalledWith(new Set(['n_a']));
    expect(container.querySelector('.selection-node[data-selection-id="n_a"]')).not.toBeNull();

    const hit = container.querySelector('g[data-id="e_1"] polyline.hit-area');
    if (!hit) {
      throw new Error('edge hit stroke not rendered');
    }
    fireEvent.pointerDown(hit, { pointerId: 2, button: 0, pointerType: 'mouse', clientX: 300, clientY: 300 });
    fireEvent.pointerUp(canvas, { pointerId: 2 });
    expect(onSelection).toHaveBeenLastCalledWith(new Set(['e_1']));
  });

  it('Shift-click adds to and removes from the selection', () => {
    const onSelection = vi.fn();
    const { container, canvas } = renderCanvas({ onSelectionChange: onSelection });
    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    fireEvent.pointerDown(shapeOf(container, 'n_b'), { pointerId: 2, button: 0, pointerType: 'mouse', shiftKey: true, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 2 });
    expect(container.querySelectorAll('.selection-node')).toHaveLength(2);

    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 3, button: 0, pointerType: 'mouse', shiftKey: true, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 3 });
    expect(container.querySelectorAll('.selection-node')).toHaveLength(1);
    expect(onSelection).toHaveBeenLastCalledWith(new Set(['n_b']));
  });

  it('clears the selection on a press on empty canvas', () => {
    const { container, canvas } = renderCanvas();
    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(container.querySelectorAll('.selection-node')).toHaveLength(1);

    fireEvent.pointerDown(canvas, { pointerId: 2, button: 0, pointerType: 'mouse', clientX: 500, clientY: 500 });
    fireEvent.pointerUp(canvas, { pointerId: 2 });
    expect(container.querySelectorAll('.selection-node')).toHaveLength(0);
  });

  it('narrows a multi-selection to the clicked node on a plain click, without moving anything', () => {
    const onMove = vi.fn();
    const onMoveEnd = vi.fn();
    const { container, canvas } = renderCanvas({ onMoveNodes: onMove, onMoveEnd });
    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    fireEvent.pointerDown(shapeOf(container, 'n_b'), { pointerId: 2, button: 0, pointerType: 'mouse', shiftKey: true, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 2 });
    expect(container.querySelectorAll('.selection-node')).toHaveLength(2);

    // A 2px wobble is under the drag threshold: still a click.
    fireEvent.pointerDown(shapeOf(container, 'n_b'), { pointerId: 3, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 12, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 3 });
    flushFrame();
    expect([...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'))).toEqual(['n_b']);
    expect(onMove).not.toHaveBeenCalled();
    expect(onMoveEnd).not.toHaveBeenCalled();
  });

  it('selects an edge on a press and moves nothing when that press is dragged', () => {
    const onMove = vi.fn();
    const withEdge = parseDocument({ ...document, edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }] });
    const { container, canvas } = renderCanvas({ onMoveNodes: onMove }, withEdge);
    const hit = container.querySelector('g[data-id="e_1"] polyline');
    if (!hit) {
      throw new Error('edge not rendered');
    }
    fireEvent.pointerDown(hit, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 60, clientY: 60 });
    flushFrame();
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    expect(container.querySelector('.selection-edge[data-selection-id="e_1"]')).not.toBeNull();
    expect(onMove).not.toHaveBeenCalled();
  });

  it('writes dragged geometry throttled to one write per animation frame', () => {
    const onMove = vi.fn();
    const { container, canvas } = renderCanvas({ onMoveNodes: onMove });
    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
    // Two moves before the frame commits coalesce into a single write carrying the latest delta.
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 130, clientY: 120 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 160, clientY: 140 });
    flushFrame();
    expect(onMove).toHaveBeenCalledTimes(1);
    expect(onMove).toHaveBeenCalledWith(new Map([['n_a', { x: 60, y: 40, w: 100, h: 50 }]]));
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
  });

  it('writes the last position and ends the move exactly once on release', () => {
    const calls: string[] = [];
    // Grid off, and n_a stays clear of n_b's edges and centre, so nothing snaps and each write is the raw delta.
    const { container, canvas } = renderCanvas({ grid: false, onMoveNodes: (moves) => calls.push(`move ${String(moves.get('n_a')?.x)}`), onMoveEnd: () => calls.push('end') });
    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 120, clientY: 100 });
    flushFrame();
    // Released before the next frame: the pending position is written first, then the move ends.
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 150, clientY: 100 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    expect(calls).toEqual(['move 20', 'move 50', 'end']);
  });

  it('moves every selected node together on a drag', () => {
    const onMove = vi.fn();
    // Grid off: with both nodes moving there is nothing else to snap to, so the raw delta is written.
    const { container, canvas } = renderCanvas({ grid: false, onMoveNodes: onMove });
    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    fireEvent.pointerDown(shapeOf(container, 'n_b'), { pointerId: 2, button: 0, pointerType: 'mouse', shiftKey: true, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 2 });

    fireEvent.pointerDown(shapeOf(container, 'n_a'), { pointerId: 3, button: 0, pointerType: 'mouse', clientX: 50, clientY: 50 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 60, clientY: 55 });
    flushFrame();
    expect(onMove).toHaveBeenCalledWith(
      new Map<string, Geometry>([
        ['n_a', { x: 10, y: 5, w: 100, h: 50 }],
        ['n_b', { x: 210, y: 5, w: 100, h: 50 }],
      ]),
    );
    fireEvent.pointerUp(canvas, { pointerId: 3 });
    flushFrame();
  });

  function selectNode(container: HTMLElement, canvas: HTMLElement, id: string): void {
    fireEvent.pointerDown(shapeOf(container, id), { pointerId: 9, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 9 });
  }

  function handleOf(container: HTMLElement, id: string, index: number): Element {
    const handle = container.querySelector(`.selection-node[data-selection-id="${id}"] [data-handle="${String(index)}"]`);
    if (!handle) {
      throw new Error(`no handle ${String(index)} on ${id}`);
    }
    return handle;
  }

  it('resizes from the bottom-right handle with the top-left held, as one gesture ending on release', () => {
    const calls: string[] = [];
    const moves: ReadonlyMap<string, Geometry>[] = [];
    const { container, canvas } = renderCanvas({ grid: false, onMoveNodes: (m) => { moves.push(m); calls.push('move'); }, onMoveEnd: () => calls.push('end') });
    selectNode(container, canvas, 'n_a');
    // Handle 4 is the bottom-right corner; zoom is 1, so screen deltas are world deltas.
    fireEvent.pointerDown(handleOf(container, 'n_a', 4), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 300, clientY: 300 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 330, clientY: 310 });
    flushFrame();
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 350, clientY: 320 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    expect(calls).toEqual(['move', 'move', 'end']);
    expect(moves.at(-1)).toEqual(new Map([['n_a', { x: 0, y: 0, w: 150, h: 70 }]]));
    // The press on the handle neither moved n_a as a drag would nor changed the selection.
    expect([...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'))).toEqual(['n_a']);
  });

  it('keeps the aspect ratio while Shift is held during a resize', () => {
    const onMove = vi.fn();
    const { container, canvas } = renderCanvas({ grid: false, onMoveNodes: onMove });
    selectNode(container, canvas, 'n_a');
    fireEvent.pointerDown(handleOf(container, 'n_a', 4), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 300, clientY: 300 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 320, shiftKey: true });
    flushFrame();
    expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 0, y: 0, w: 200, h: 100 }]]));
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 320, shiftKey: false });
    flushFrame();
    expect(onMove).toHaveBeenLastCalledWith(new Map([['n_a', { x: 0, y: 0, w: 200, h: 70 }]]));
    fireEvent.pointerUp(canvas, { pointerId: 1 });
  });

  it('writes nothing for a click on a handle that never travels the drag threshold', () => {
    const onMove = vi.fn();
    const onMoveEnd = vi.fn();
    const { container, canvas } = renderCanvas({ onMoveNodes: onMove, onMoveEnd });
    selectNode(container, canvas, 'n_a');
    fireEvent.pointerDown(handleOf(container, 'n_a', 0), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 300, clientY: 300 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 302, clientY: 300 });
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    expect(onMove).not.toHaveBeenCalled();
    expect(onMoveEnd).not.toHaveBeenCalled();
  });

  it('nudges the selection 1px per arrow and 10px with Shift, ending a step on every press', () => {
    const calls: string[] = [];
    const { container, canvas } = renderCanvas({ onMoveNodes: (m) => calls.push(`move ${JSON.stringify([...m])}`), onMoveEnd: () => calls.push('end') });
    selectNode(container, canvas, 'n_b');
    expect(fireEvent.keyDown(canvas, { key: 'ArrowRight' })).toBe(false);
    fireEvent.keyDown(canvas, { key: 'ArrowUp', shiftKey: true });
    expect(calls).toEqual([
      `move ${JSON.stringify([['n_b', { x: 201, y: 0, w: 100, h: 50 }]])}`,
      'end',
      `move ${JSON.stringify([['n_b', { x: 200, y: -10, w: 100, h: 50 }]])}`,
      'end',
    ]);
  });

  it('leaves arrows alone with nothing selected, in a text field inside the canvas, and read-only', () => {
    const onMove = vi.fn();
    const { container, canvas } = renderCanvas({ onMoveNodes: onMove });
    expect(fireEvent.keyDown(canvas, { key: 'ArrowRight' })).toBe(true);
    selectNode(container, canvas, 'n_a');
    const field = globalThis.document.createElement('input');
    canvas.append(field);
    expect(fireEvent.keyDown(field, { key: 'ArrowRight' })).toBe(true);
    expect(onMove).not.toHaveBeenCalled();
    // The same press on the canvas itself does nudge, so the guard above is what held it back.
    fireEvent.keyDown(canvas, { key: 'ArrowRight' });
    expect(onMove).toHaveBeenCalledTimes(1);

    const readOnly = renderCanvas({ onMoveNodes: onMove, readOnly: true });
    fireEvent.keyDown(readOnly.canvas, { key: 'ArrowRight' });
    expect(onMove).toHaveBeenCalledTimes(1);
  });

  it('hands the selection to onDeleteElements on Delete or Backspace and clears it', () => {
    const onDelete = vi.fn();
    const onSelection = vi.fn();
    const { container, canvas } = renderCanvas({ onDeleteElements: onDelete, onSelectionChange: onSelection });
    selectNode(container, canvas, 'n_a');
    expect(fireEvent.keyDown(canvas, { key: 'Delete' })).toBe(false);
    expect(onDelete).toHaveBeenLastCalledWith(new Set(['n_a']));
    expect(onSelection).toHaveBeenLastCalledWith(new Set());
    expect(container.querySelectorAll('.selection-node')).toHaveLength(0);

    selectNode(container, canvas, 'n_b');
    fireEvent.keyDown(canvas, { key: 'Backspace' });
    expect(onDelete).toHaveBeenLastCalledWith(new Set(['n_b']));
    expect(onDelete).toHaveBeenCalledTimes(2);
  });

  it('does not delete from a text field inside the canvas, with nothing selected, or read-only', () => {
    const onDelete = vi.fn();
    const { container, canvas } = renderCanvas({ onDeleteElements: onDelete });
    fireEvent.keyDown(canvas, { key: 'Delete' });
    selectNode(container, canvas, 'n_a');
    const editor = globalThis.document.createElement('div');
    editor.setAttribute('contenteditable', 'true');
    canvas.append(editor);
    expect(fireEvent.keyDown(editor, { key: 'Backspace' })).toBe(true);
    expect(onDelete).not.toHaveBeenCalled();
    expect(container.querySelectorAll('.selection-node')).toHaveLength(1);

    const readOnly = renderCanvas({ onDeleteElements: onDelete, readOnly: true });
    fireEvent.keyDown(readOnly.canvas, { key: 'Delete' });
    expect(onDelete).not.toHaveBeenCalled();
  });

  describe('Tab walk (checklist item 21)', () => {
    // Document order deliberately differs from id order, so a walk by id would fail.
    const walked = parseDocument({ ...document, nodes: [
      { id: 'n_z', kind: 'service', label: 'Zed', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
      { id: 'n_a', kind: 'database', label: '', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
      { id: 'n_m', kind: 'service', label: 'Em', shape: 'rectangle', geometry: { x: 400, y: 0, w: 100, h: 50 } },
    ] });
    const selected = (container: HTMLElement) => [...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'));
    const status = (container: HTMLElement) => container.querySelector('[role="status"]')?.textContent;

    it('selects the next node in document order on Tab and the previous on Shift+Tab, and announces it', () => {
      const onSelection = vi.fn();
      const { container, canvas } = renderCanvas({ onSelectionChange: onSelection }, walked);
      expect(fireEvent.keyDown(canvas, { key: 'Tab' })).toBe(false);
      expect(selected(container)).toEqual(['n_z']);
      expect(status(container)).toBe('Zed, service');
      expect(fireEvent.keyDown(canvas, { key: 'Tab' })).toBe(false);
      expect(selected(container)).toEqual(['n_a']);
      // A node with no label is announced by its id.
      expect(status(container)).toBe('n_a, database');
      expect(fireEvent.keyDown(canvas, { key: 'Tab' })).toBe(false);
      expect(selected(container)).toEqual(['n_m']);
      expect(fireEvent.keyDown(canvas, { key: 'Tab', shiftKey: true })).toBe(false);
      expect(selected(container)).toEqual(['n_a']);
      expect(onSelection).toHaveBeenLastCalledWith(new Set(['n_a']));
    });

    it('lets Tab on the last node and Shift+Tab on the first leave the canvas, selection unchanged, rather than wrap', () => {
      const onSelection = vi.fn();
      const { container, canvas } = renderCanvas({ onSelectionChange: onSelection }, walked);
      expect(fireEvent.keyDown(canvas, { key: 'Tab', shiftKey: true })).toBe(false);
      expect(selected(container)).toEqual(['n_m']);
      onSelection.mockClear();
      expect(fireEvent.keyDown(canvas, { key: 'Tab' })).toBe(true);
      expect(selected(container)).toEqual(['n_m']);
      expect(onSelection).not.toHaveBeenCalled();

      fireEvent.keyDown(canvas, { key: 'Tab', shiftKey: true });
      fireEvent.keyDown(canvas, { key: 'Tab', shiftKey: true });
      expect(selected(container)).toEqual(['n_z']);
      onSelection.mockClear();
      expect(fireEvent.keyDown(canvas, { key: 'Tab', shiftKey: true })).toBe(true);
      expect(selected(container)).toEqual(['n_z']);
      expect(onSelection).not.toHaveBeenCalled();
    });

    it('leaves Tab to move focus when read-only, the document is empty, or a text field inside the canvas has focus', () => {
      const readOnly = renderCanvas({ readOnly: true }, walked);
      expect(fireEvent.keyDown(readOnly.canvas, { key: 'Tab' })).toBe(true);
      const empty = renderCanvas({}, { ...walked, nodes: [] });
      expect(fireEvent.keyDown(empty.canvas, { key: 'Tab' })).toBe(true);
      const { container, canvas } = renderCanvas({}, walked);
      const field = globalThis.document.createElement('input');
      canvas.append(field);
      expect(fireEvent.keyDown(field, { key: 'Tab' })).toBe(true);
      expect(selected(container)).toEqual([]);
    });
  });

  describe('clipboard keys (checklist items 18, 19 and 21)', () => {
    /** A DataTransfer as a clipboard event carries one: text/plain only. */
    const transfer = (text = '') => {
      const data = { text };
      return { getData: (type: string) => (type === 'text/plain' ? data.text : ''), setData: (type: string, value: string) => { if (type === 'text/plain') data.text = value; } };
    };
    const pasteEvent = (target: Element, text: string) => fireEvent.paste(target, { clipboardData: transfer(text) });
    const nextTask = () => act(() => new Promise((resolve) => setTimeout(resolve, 0)));

    it('hands duplicate, and the copy and cut events the browser fires, with the selection to onClipboard, and only with one', async () => {
      const onClipboard = vi.fn((command: string, _selection: unknown, data?: { setData: (type: string, value: string) => void }) => {
        if (command === 'copy') {
          data?.setData('text/plain', 'payload');
        }
      });
      const { container, canvas } = renderCanvas({ onClipboard });
      // Nothing selected: copy, cut and duplicate have nothing to act on. Copy and cut keep the browser's default;
      // Cmd/Ctrl+D is still held back so it cannot open the browser's bookmark dialog.
      expect(fireEvent.keyDown(canvas, { key: 'c', metaKey: true })).toBe(true);
      expect(fireEvent.copy(canvas, { clipboardData: transfer() })).toBe(true);
      expect(fireEvent.keyDown(canvas, { key: 'x', metaKey: true })).toBe(true);
      expect(fireEvent.cut(canvas, { clipboardData: transfer() })).toBe(true);
      expect(fireEvent.keyDown(canvas, { key: 'd', ctrlKey: true })).toBe(false);
      expect(fireEvent.keyDown(canvas, { key: 'd', metaKey: true })).toBe(false);
      await nextTask();
      expect(onClipboard).not.toHaveBeenCalled();

      selectNode(container, canvas, 'n_b');
      expect(fireEvent.keyDown(canvas, { key: 'd', ctrlKey: true })).toBe(false);
      expect(onClipboard).toHaveBeenLastCalledWith('duplicate', new Set(['n_b']));
      // Not prevented: preventing the key would stop the browser firing its copy or cut event.
      expect(fireEvent.keyDown(canvas, { key: 'c', ctrlKey: true })).toBe(true);
      const copied = transfer();
      // Prevented once onClipboard has written the event's DataTransfer, so the browser puts that text on the clipboard.
      expect(fireEvent.copy(canvas, { clipboardData: copied })).toBe(false);
      expect(onClipboard).toHaveBeenLastCalledWith('copy', new Set(['n_b']), copied);
      expect(copied.getData('text/plain')).toBe('payload');
      expect(fireEvent.keyDown(canvas, { key: 'x', ctrlKey: true })).toBe(true);
      const cut = transfer();
      // A cut that wrote nothing keeps the browser's default, which leaves the system clipboard alone.
      expect(fireEvent.cut(canvas, { clipboardData: cut })).toBe(true);
      expect(onClipboard).toHaveBeenLastCalledWith('cut', new Set(['n_b']), cut);
      await nextTask();
      expect(onClipboard).toHaveBeenCalledTimes(3);
    });

    it('ignores copy, cut and paste events and holds back Cmd/Ctrl+D without duplicating during a marquee drag', async () => {
      const onClipboard = vi.fn();
      const { container, canvas } = renderCanvas({ onClipboard });
      selectNode(container, canvas, 'n_b');
      fireEvent.pointerDown(canvas, { pointerId: 1, button: 0, pointerType: 'mouse', shiftKey: true, clientX: 790, clientY: 590 });
      fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 700, clientY: 500 });
      flushFrame();
      expect(container.querySelector('.marquee')).not.toBeNull();
      expect(fireEvent.copy(canvas, { clipboardData: transfer() })).toBe(true);
      expect(fireEvent.cut(canvas, { clipboardData: transfer() })).toBe(true);
      expect(pasteEvent(canvas, 'clipboard text')).toBe(true);
      expect(fireEvent.keyDown(canvas, { key: 'd', metaKey: true })).toBe(false);
      await nextTask();
      expect(onClipboard).not.toHaveBeenCalled();

      fireEvent.pointerUp(canvas, { pointerId: 1, shiftKey: true, clientX: 700, clientY: 500 });
      expect(pasteEvent(canvas, 'clipboard text')).toBe(false);
      expect(onClipboard).toHaveBeenCalledTimes(1);
    });

    it('asks onClipboard to write for itself when no copy or cut event follows the key', async () => {
      const onClipboard = vi.fn();
      const { container, canvas } = renderCanvas({ onClipboard });
      selectNode(container, canvas, 'n_b');
      fireEvent.keyDown(canvas, { key: 'c', metaKey: true });
      fireEvent.keyDown(canvas, { key: 'x', metaKey: true });
      expect(onClipboard).not.toHaveBeenCalled();
      await nextTask();
      expect(onClipboard).toHaveBeenCalledTimes(1);
      expect(onClipboard).toHaveBeenLastCalledWith('cut', new Set(['n_b']));
    });

    it('lets Cmd/Ctrl+V through and pastes the text of the paste event the browser fires, once', async () => {
      const onClipboard = vi.fn();
      const { container, canvas } = renderCanvas({ onClipboard });
      selectNode(container, canvas, 'n_a');
      // Not prevented: preventing the key would stop the browser firing its paste event.
      expect(fireEvent.keyDown(canvas, { key: 'v', metaKey: true })).toBe(true);
      expect(pasteEvent(canvas, 'clipboard text')).toBe(false);
      await nextTask();
      expect(onClipboard).toHaveBeenCalledTimes(1);
      expect(onClipboard.mock.lastCall?.slice(0, 2)).toEqual(['paste', new Set(['n_a'])]);
      expect((onClipboard.mock.lastCall?.[2] as DataTransfer | undefined)?.getData('text/plain')).toBe('clipboard text');
      // A paste from the browser's menu, with no key before it, pastes too.
      pasteEvent(canvas, 'menu paste');
      expect((onClipboard.mock.lastCall?.[2] as DataTransfer | undefined)?.getData('text/plain')).toBe('menu paste');
    });

    it('takes a paste the browser fires at a caret left in page text while the canvas has focus, and none without focus', async () => {
      const onClipboard = vi.fn();
      const { container, canvas } = renderCanvas({ onClipboard });
      const page = globalThis.document.createElement('p');
      page.textContent = 'Document title';
      globalThis.document.body.append(page);
      const field = globalThis.document.createElement('input');
      globalThis.document.body.append(field);
      try {
        selectNode(container, canvas, 'n_a');
        canvas.focus();
        const text = page.firstChild ?? page;
        globalThis.document.getSelection()?.collapse(text, 3);
        fireEvent.keyDown(canvas, { key: 'v', metaKey: true });
        expect(pasteEvent(page, 'clipboard text')).toBe(false);
        await nextTask();
        expect(onClipboard).toHaveBeenCalledTimes(1);
        expect((onClipboard.mock.lastCall?.[2] as DataTransfer | undefined)?.getData('text/plain')).toBe('clipboard text');

        field.focus();
        expect(fireEvent.copy(field, { clipboardData: transfer() })).toBe(true);
        expect(pasteEvent(page, 'elsewhere')).toBe(true);
        expect(pasteEvent(field, 'into the input')).toBe(true);
        await nextTask();
        expect(onClipboard).toHaveBeenCalledTimes(1);
      } finally {
        page.remove();
        field.remove();
      }
    });

    it('asks onClipboard to read for itself when no paste event follows the key', async () => {
      const onClipboard = vi.fn();
      const { canvas } = renderCanvas({ onClipboard });
      fireEvent.keyDown(canvas, { key: 'v', ctrlKey: true });
      expect(onClipboard).not.toHaveBeenCalled();
      await nextTask();
      expect(onClipboard).toHaveBeenCalledTimes(1);
      expect(onClipboard).toHaveBeenLastCalledWith('paste', new Set());
    });

    it('sends nothing from a text field or the label editor inside the canvas, or read-only', async () => {
      const onClipboard = vi.fn();
      const onSelection = vi.fn();
      const { container, canvas } = renderCanvas({ onClipboard, onSelectionChange: onSelection, onLabelChange: vi.fn() });
      selectNode(container, canvas, 'n_a');
      const field = globalThis.document.createElement('textarea');
      canvas.append(field);
      fireEvent.keyDown(field, { key: 'v', metaKey: true });
      expect(pasteEvent(field, 'into the field')).toBe(true);
      field.remove();
      fireEvent.keyDown(canvas, { key: 'Enter' });
      const labelEditor = container.querySelector<HTMLElement>('.label-editor');
      expect(labelEditor).not.toBeNull();
      onSelection.mockClear();
      for (const key of ['c', 'x', 'v', 'd']) {
        fireEvent.keyDown(labelEditor ?? canvas, { key, metaKey: true });
      }
      // The label editor handles its own clipboard; the canvas must not also copy or paste elements.
      fireEvent.copy(labelEditor ?? canvas, { clipboardData: transfer() });
      fireEvent.cut(labelEditor ?? canvas, { clipboardData: transfer() });
      pasteEvent(labelEditor ?? canvas, 'into the label');
      fireEvent.keyDown(labelEditor ?? canvas, { key: 'Tab' });
      await nextTask();
      expect(onClipboard).not.toHaveBeenCalled();
      expect(onSelection).not.toHaveBeenCalled();

      const readOnly = renderCanvas({ onClipboard, readOnly: true });
      fireEvent.keyDown(readOnly.canvas, { key: 'v', metaKey: true });
      pasteEvent(readOnly.canvas, 'read-only');
      await nextTask();
      expect(onClipboard).not.toHaveBeenCalled();
    });
  });

  it('draws a rotated node\'s handles turned with it', () => {
    const rotated = parseDocument({ ...document, nodes: [{ id: 'n_r', kind: 'service', label: 'R', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50, rotation: 30 } }] });
    const { container, canvas } = renderCanvas({}, rotated);
    selectNode(container, canvas, 'n_r');
    expect(container.querySelector('.selection-node[data-selection-id="n_r"]')?.getAttribute('transform')).toMatch(/^rotate\(30 /);
  });

  it('moves a node without geometry from its provisional spot, giving it real geometry', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const unplaced: Document = parseDocument({
      id: 'doc_unplaced',
      schemaVersion: 1,
      title: 'Unplaced',
      type: 'flowchart',
      nodes: [
        { id: 'n_p', kind: 'service', label: 'P', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
        { id: 'n_u', kind: 'service', label: 'U', shape: 'rectangle' },
      ],
      edges: [],
      meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
    });
    const onMove = vi.fn();
    const { container, canvas } = renderCanvas({ grid: false, onMoveNodes: onMove }, unplaced);
    fireEvent.pointerDown(shapeOf(container, 'n_u'), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 20, clientY: 15 });
    flushFrame();
    // The provisional spot sits below the placed node (y = 50 + 16 = 66) at the provisional size 160x48.
    expect(onMove).toHaveBeenCalledWith(new Map([['n_u', { x: 10, y: 71, w: 160, h: 48 }]]));
    fireEvent.pointerUp(canvas, { pointerId: 1 });
    flushFrame();
    warn.mockRestore();
  });
});