// @vitest-environment jsdom
/**
 * The sequence editor's canvas (design/atlas/sequence-editor.md). Two halves. The gate: for a document whose type
 * derives its geometry, every free-geometry gesture is inert. Each is proved against a control, the same gesture on a
 * freeform document, where it must write; a gate test that could not tell "inert" from "never wired up" would pass
 * with the gate removed. The editing: what a drag, a connector gesture and the keyboard do instead.
 */
import { createRef } from 'react';
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { messageLabelAnchor, messageSlot, slotLineX, slotLineY } from '../model/sequenceEdit.ts';
import { checkoutSequence, must } from '../model/sequenceFixture.ts';
import { sequenceScene } from '../model/sequenceScene.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Geometry, Id, Point } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import { pointFrameTransform } from './labelEditing.ts';
import { worldToScreen } from './viewport.ts';

const size = { width: 800, height: 600 };
const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };
const meta = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' };

/** The freeform control: two nodes and an orthogonal and a curved edge between them, all with stored geometry. */
function freeform(): Document {
  return parseDocument({
    id: 'doc_control',
    schemaVersion: 1,
    title: 'Control',
    type: 'freeform',
    nodes: [
      { id: 'p_web', kind: 'shape', label: 'Web', shape: 'rectangle', geometry: { x: 100, y: 20, w: 120, h: 40 } },
      { id: 'p_api', kind: 'shape', label: 'API', shape: 'rectangle', geometry: { x: 400, y: 20, w: 120, h: 40 } },
    ],
    edges: [
      { id: 'm_orth', kind: 'flow', from: { node: 'p_web' }, to: { node: 'p_api' }, routing: { mode: 'orthogonal', waypoints: [{ x: 300, y: 200 }] } },
      { id: 'm_curve', kind: 'flow', from: { node: 'p_web' }, to: { node: 'p_api' }, routing: { mode: 'curved', waypoints: [{ x: 300, y: 100 }] } },
    ],
    meta,
  });
}

/** The checkout sequence with two of its messages carrying stored routing, which the scene must ignore. */
function sequence(): Document {
  const document = checkoutSequence();
  document.edges[0] = { ...must(document.edges[0]), routing: { mode: 'orthogonal', waypoints: [{ x: 300, y: 200 }] } };
  document.edges[1] = { ...must(document.edges[1]), routing: { mode: 'curved', waypoints: [{ x: 300, y: 100 }] } };
  return document;
}

const scene = sequenceScene(checkoutSequence());

function setup(document: Document, props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}) {
  const ref = createRef<DiagramCanvasHandle>();
  const result = render(<DiagramCanvas ref={ref} document={document} theme={diagramThemes.overpass} size={size} routingBackend={() => undefined} {...props} />);
  const canvas = result.container.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas || !ref.current) {
    throw new Error('canvas not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  const handle = ref.current;
  const at = (world: Point) => {
    const screen = worldToScreen(handle.getViewport(), world);
    return { clientX: screen.x, clientY: screen.y };
  };
  const element = (id: Id): Element => {
    const found = result.container.querySelector(`g[data-id="${id}"]`);
    if (!found) {
      throw new Error(`no element ${id}`);
    }
    return found;
  };
  return { ...result, canvas, handle, at, element };
}

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

/** A press on `target`, a drag through `to`, and the release, with the frame each move schedules flushed. */
function drag(canvas: HTMLElement, target: Element, from: object, to: object, init: object = mouse): void {
  fireEvent.pointerDown(target, { ...init, ...from });
  fireEvent.pointerMove(canvas, { ...init, ...to });
  flushFrame();
  fireEvent.pointerUp(canvas, { ...init, ...to });
  flushFrame();
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
});
afterEach(() => {
  vi.useRealTimers();
});

describe('the derived-geometry gate: each free-geometry gesture writes on a freeform document and nothing on a sequence one', () => {
  it('free move: dragging a node calls onMoveNodes on the control, and reorders instead on a sequence', () => {
    const control = vi.fn<(moves: ReadonlyMap<Id, Geometry>) => void>();
    const a = setup(freeform(), { onMoveNodes: control, onMoveEnd: vi.fn() });
    drag(a.canvas, a.element('p_web'), a.at({ x: 160, y: 40 }), a.at({ x: 200, y: 140 }));
    expect(control).toHaveBeenCalled();
    a.unmount();

    const moves = vi.fn();
    const onMoveEnd = vi.fn();
    const onReorder = vi.fn();
    const b = setup(sequence(), { onMoveNodes: moves, onMoveEnd, onReorder });
    drag(b.canvas, b.element('p_web'), b.at({ x: 220, y: 40 }), b.at({ x: 300, y: 300 }));
    expect(moves).not.toHaveBeenCalled();
    expect(onMoveEnd).not.toHaveBeenCalled();
    expect(onReorder).toHaveBeenCalledTimes(1);
  });

  it('snap: a drag shows no snap guide of the grid, an edge or a centre, only the insertion line, and nothing is written', () => {
    const moves = vi.fn();
    const a = setup(freeform(), { onMoveNodes: moves, grid: true });
    fireEvent.pointerDown(a.element('p_web'), { ...mouse, ...a.at({ x: 160, y: 40 }) });
    fireEvent.pointerMove(a.canvas, { ...mouse, ...a.at({ x: 163, y: 47 }) });
    flushFrame();
    expect(moves).toHaveBeenCalled();
    expect(a.container.querySelector('.snap-guide')).not.toBeNull();
    a.unmount();

    const onMoveNodes = vi.fn();
    const b = setup(sequence(), { onMoveNodes, grid: true, onReorder: vi.fn() });
    fireEvent.pointerDown(b.element('p_web'), { ...mouse, ...b.at({ x: 220, y: 40 }) });
    fireEvent.pointerMove(b.canvas, { ...mouse, ...b.at({ x: 223, y: 47 }) });
    flushFrame();
    expect(onMoveNodes).not.toHaveBeenCalled();
    expect([...b.container.querySelectorAll('.snap-guide')].map((guide) => guide.getAttribute('data-guide'))).toEqual(['edge']);
  });

  it('resize: a press on a resize handle resizes the control and does nothing on a sequence, which draws no handles', () => {
    for (const [document, expectWrite] of [[freeform(), true], [sequence(), false]] as const) {
      const moves = vi.fn();
      const view = setup(document, { onMoveNodes: moves, onMoveEnd: vi.fn(), onReorder: vi.fn() });
      // Select through a real press, then press where the overlay puts a handle (or would).
      fireEvent.pointerDown(view.element('p_web'), { ...mouse, ...view.at({ x: 160, y: 40 }) });
      fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 160, y: 40 }) });
      expect(view.container.querySelectorAll('[data-handle]').length > 0).toBe(expectWrite);
      // A press on a handle element, present or not: a stand-in carries the attributes the handler reads.
      const overlay = view.container.querySelector('[data-selection-id="p_web"]') ?? view.container.querySelector('svg');
      const handle = overlay?.ownerDocument.createElementNS('http://www.w3.org/2000/svg', 'rect');
      if (!overlay || !handle) {
        throw new Error('no overlay');
      }
      handle.setAttribute('data-handle', '4');
      const holder = view.container.querySelector('[data-selection-id="p_web"]') ?? overlay.appendChild(overlay.ownerDocument.createElementNS('http://www.w3.org/2000/svg', 'g'));
      holder.setAttribute('data-selection-id', 'p_web');
      holder.appendChild(handle);
      fireEvent.pointerDown(handle, { ...mouse, ...view.at({ x: 220, y: 60 }) });
      fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 260, y: 100 }) });
      flushFrame();
      fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 260, y: 100 }) });
      flushFrame();
      expect(moves.mock.calls.length > 0).toBe(expectWrite);
      view.unmount();
    }
  });

  it('rotate: a press on the rotate handle rotates the control and does nothing on a sequence', () => {
    for (const [document, expectWrite] of [[freeform(), true], [sequence(), false]] as const) {
      const moves = vi.fn();
      const view = setup(document, { onMoveNodes: moves, onMoveEnd: vi.fn(), onReorder: vi.fn() });
      fireEvent.pointerDown(view.element('p_web'), { ...mouse, ...view.at({ x: 160, y: 40 }) });
      fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 160, y: 40 }) });
      expect(view.container.querySelector('[data-rotate-handle]') !== null).toBe(expectWrite);
      const svg = view.container.querySelector('svg');
      const holder = svg?.ownerDocument.createElementNS('http://www.w3.org/2000/svg', 'g');
      const handle = svg?.ownerDocument.createElementNS('http://www.w3.org/2000/svg', 'circle');
      if (!svg || !holder || !handle) {
        throw new Error('no svg');
      }
      holder.setAttribute('data-selection-id', 'p_web');
      handle.setAttribute('data-rotate-handle', 'true');
      holder.appendChild(handle);
      svg.appendChild(holder);
      fireEvent.pointerDown(handle, { ...mouse, ...view.at({ x: 160, y: 0 }) });
      fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 260, y: 30 }) });
      flushFrame();
      fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 260, y: 30 }) });
      flushFrame();
      expect(moves.mock.calls.length > 0).toBe(expectWrite);
      view.unmount();
    }
  });

  it('nudge: an arrow key moves the selected control node and does nothing to a sequence participant', () => {
    for (const [document, expectWrite] of [[freeform(), true], [sequence(), false]] as const) {
      const moves = vi.fn();
      const view = setup(document, { onMoveNodes: moves, onMoveEnd: vi.fn(), onReorder: vi.fn() });
      act(() => {
      view.handle.select(new Set(['p_web']));
    });
      fireEvent.keyDown(view.canvas, { key: 'ArrowRight' });
      expect(moves.mock.calls.length > 0).toBe(expectWrite);
      expect(view.handle.actions.find((action) => action.id === 'canvas.nudge.right')?.enabled()).toBe(expectWrite);
      view.unmount();
    }
  });

  it('segment and waypoint drag: a press on a segment handle, or on the selected connector, pins the control and does nothing on a sequence', () => {
    for (const [document, expectWrite] of [[freeform(), true], [sequence(), false]] as const) {
      for (const [edge, mode] of [['m_orth', 'orthogonal'], ['m_curve', 'curved']] as const) {
        const ids: Record<string, string> = expectWrite ? { m_orth: 'm_orth', m_curve: 'm_curve' } : { m_orth: 'm_1', m_curve: 'm_2' };
        const id = must(ids[edge]);
        const moves = vi.fn();
        const view = setup(document, { onMoveNodes: moves, onMoveEnd: vi.fn(), onReorder: vi.fn() });
        const routed = view.container.querySelector(`g[data-id="${id}"]`);
        expect(routed).not.toBeNull();
        act(() => {
      view.handle.select(new Set([id]));
    });
        // A stand-in for the handle a selected connector shows, carrying the attributes the press reads.
        const svg = view.container.querySelector('svg');
        const handle = svg?.ownerDocument.createElementNS('http://www.w3.org/2000/svg', 'rect');
        if (!svg || !handle) {
          throw new Error('no svg');
        }
        handle.setAttribute('data-segment-handle', 'true');
        handle.setAttribute('data-edge-id', id);
        handle.setAttribute('data-segment-kind', mode);
        handle.setAttribute('data-segment-index', mode === 'orthogonal' ? '1' : '0');
        svg.appendChild(handle);
        const from = expectWrite ? { x: 300, y: 200 } : { x: 300, y: 100 };
        fireEvent.pointerDown(handle, { ...mouse, ...view.at(from) });
        fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: from.x + 30, y: from.y + 50 }) });
        flushFrame();
        fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: from.x + 30, y: from.y + 50 }) });
        flushFrame();
        expect(moves.mock.calls.length > 0, `${document.type} ${mode}`).toBe(expectWrite);
        view.unmount();
      }
    }
  });

  it('curved routing: a stored curved or waypointed message is still drawn as the derived straight line', () => {
    const control = setup(freeform());
    expect(control.container.querySelector('g[data-id="m_curve"] path:not(.hit-area)')?.getAttribute('d')).toMatch(/[CQ]/);
    control.unmount();
    const view = setup(sequence());
    // m_1 names orthogonal routing with a waypoint and m_2 curved with one; both are the scene's two-point line.
    expect(view.container.querySelector('g[data-id="m_1"] polyline:not(.hit-area)')?.getAttribute('points')).toBe('80,108 220,108');
    expect(view.container.querySelector('g[data-id="m_2"] path:not(.hit-area)')?.getAttribute('d')).toBe('M 220,144 L 355,144');
  });

  it('connection handles and end re-attach: the select tool offers them on the control and never on a sequence', () => {
    const control = setup(freeform(), { onConnect: vi.fn(), onReconnect: vi.fn() });
    fireEvent.pointerMove(control.canvas, { ...mouse, ...control.at({ x: 160, y: 40 }) });
    expect(control.container.querySelectorAll('.connect-handle').length).toBeGreaterThan(0);
    control.unmount();

    const onConnect = vi.fn();
    const view = setup(sequence(), { onConnect, onReconnect: vi.fn(), onInsertMessage: vi.fn() });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 220, y: 40 }) });
    act(() => {
      view.handle.select(new Set(['p_web']));
    });
    expect(view.container.querySelector('.connect-handle')).toBeNull();
    // A press where a handle would be starts nothing.
    drag(view.canvas, view.canvas, view.at({ x: 280, y: 40 }), view.at({ x: 500, y: 40 }));
    expect(onConnect).not.toHaveBeenCalled();
  });

  it('group, duplicate and the canvas actions that write geometry are disabled', () => {
    const view = setup(sequence(), { onGroup: vi.fn(), onClipboard: vi.fn(), onMoveNodes: vi.fn() });
    act(() => {
      view.handle.select(new Set(['p_web']));
    });
    const enabled = (id: string) => view.handle.actions.find((action) => action.id === id)?.enabled();
    // Group and Ungroup, and the class and table member editors, are not registered at all for a sequence diagram: the menu
    // lists only what can apply.
    expect(enabled('canvas.group')).toBeUndefined();
    expect(view.handle.actions.some((action) => ['canvas.ungroup', 'canvas.editAttributes', 'canvas.editOperations', 'canvas.editColumns'].includes(action.id))).toBe(false);
    expect(enabled('canvas.duplicate')).toBe(false);
    expect(enabled('canvas.nudge.left')).toBe(false);
    // The commands that mean something here stay on.
    expect(enabled('canvas.delete')).toBe(false);
    expect(view.handle.actions.find((action) => action.id === 'canvas.selectNext')?.enabled()).toBe(true);
  });
});

describe('reordering by dragging', () => {
  it('drags a message vertically: an insertion line shows the slot, and the release reports the drop point', () => {
    const onReorder = vi.fn();
    const view = setup(sequence(), { onReorder });
    // m_1 (line at y = 108) is pressed, then dragged to y = 250.
    fireEvent.pointerDown(view.element('m_1'), { ...mouse, ...view.at({ x: 150, y: 108 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 150, y: 250 }) });
    flushFrame();
    const line = must(view.container.querySelector('.snap-guide-edge .snap-guide-line'));
    const expectedY = worldToScreen(view.handle.getViewport(), { x: 0, y: slotLineY(scene, messageSlot(scene, 250, 'm_1'), 'm_1') }).y;
    expect(Number(line.getAttribute('y1'))).toBeCloseTo(expectedY, 3);
    expect(line.getAttribute('y1')).toBe(line.getAttribute('y2'));
    expect(onReorder).not.toHaveBeenCalled();
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 150, y: 250 }) });
    flushFrame();
    expect(onReorder).toHaveBeenCalledTimes(1);
    const [id, point] = must(onReorder.mock.calls[0]) as [string, Point];
    expect(id).toBe('m_1');
    expect(point.y).toBeCloseTo(250, 3);
    expect(view.container.querySelector('.snap-guide')).toBeNull();
    // The message is selected by the press, as any element is.
    expect(view.container.querySelector('.selection-edge[data-selection-id="m_1"]')).not.toBeNull();
  });

  it('drags a participant horizontally only: a vertical insertion line, and the y of the drop is ignored by the editor', () => {
    const onReorder = vi.fn();
    const view = setup(sequence(), { onReorder });
    fireEvent.pointerDown(view.element('p_user'), { ...mouse, ...view.at({ x: 80, y: 40 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 300, y: 500 }) });
    flushFrame();
    const line = must(view.container.querySelector('.snap-guide-edge .snap-guide-line'));
    expect(line.getAttribute('x1')).toBe(line.getAttribute('x2'));
    const expectedX = worldToScreen(view.handle.getViewport(), { x: slotLineX(scene, 1, 'p_user'), y: 0 }).x;
    expect(Number(line.getAttribute('x1'))).toBeCloseTo(expectedX, 3);
    // The header itself has not moved: nothing is drawn away from its column.
    expect(view.element('p_user').querySelector('.shape')?.getAttribute('x') ?? '0').toBe(view.element('p_user').querySelector('.shape')?.getAttribute('x') ?? '0');
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 300, y: 500 }) });
    flushFrame();
    expect(onReorder).toHaveBeenCalledTimes(1);
    expect(must(onReorder.mock.calls[0])[0]).toBe('p_user');
  });

  it('a click, or a drag shorter than the threshold, reorders nothing', () => {
    const onReorder = vi.fn();
    const view = setup(sequence(), { onReorder });
    fireEvent.pointerDown(view.element('m_1'), { ...mouse, ...view.at({ x: 150, y: 108 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 151, y: 110 }) });
    flushFrame();
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 151, y: 110 }) });
    expect(onReorder).not.toHaveBeenCalled();
    expect(view.container.querySelector('.snap-guide')).toBeNull();
  });

  it('a cancelled gesture (pointercancel) reorders nothing', () => {
    const onReorder = vi.fn();
    const view = setup(sequence(), { onReorder });
    fireEvent.pointerDown(view.element('m_1'), { ...mouse, ...view.at({ x: 150, y: 108 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 150, y: 250 }) });
    flushFrame();
    fireEvent.pointerCancel(view.canvas, { ...mouse, ...view.at({ x: 150, y: 250 }) });
    flushFrame();
    expect(onReorder).not.toHaveBeenCalled();
  });

  it('is read-only when the view is: no reorder from a read-only canvas', () => {
    const onReorder = vi.fn();
    const view = setup(sequence(), { onReorder, readOnly: true });
    drag(view.canvas, view.element('m_1'), view.at({ x: 150, y: 108 }), view.at({ x: 150, y: 250 }));
    expect(onReorder).not.toHaveBeenCalled();
  });
});

describe('the connector tool on a sequence diagram', () => {
  const lifeline = (id: string): number => must(scene.headers.find((header) => header.id === id)).cx;

  it('drags from one lifeline to another; the release y picks the slot', () => {
    const onInsertMessage = vi.fn();
    const view = setup(sequence(), { tool: 'connector', onInsertMessage });
    fireEvent.pointerDown(view.canvas, { ...mouse, ...view.at({ x: lifeline('p_web'), y: 200 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: lifeline('p_pay'), y: 250 }) });
    // While it is drawn the preview runs along the slot the release would pick.
    const preview = must(view.container.querySelector('.connector-preview'));
    const y = worldToScreen(view.handle.getViewport(), { x: 0, y: slotLineY(scene, messageSlot(scene, 250)) }).y;
    expect(Number(preview.getAttribute('y1'))).toBeCloseTo(y, 3);
    expect(Number(preview.getAttribute('y2'))).toBeCloseTo(y, 3);
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: lifeline('p_pay'), y: 250 }) });
    // Mids above 250 are m_1 to m_4 (108, 144, 180, 216), so it goes fifth: index 4.
    expect(onInsertMessage).toHaveBeenCalledExactlyOnceWith({ from: 'p_web', to: 'p_pay', index: 4 });
    expect(view.container.querySelector('.connector-preview')).toBeNull();
  });

  it('picks the first and last slots at the extremes', () => {
    const onInsertMessage = vi.fn();
    const view = setup(sequence(), { tool: 'connector', onInsertMessage });
    drag(view.canvas, view.canvas, view.at({ x: lifeline('p_user'), y: 300 }), view.at({ x: lifeline('p_web'), y: 70 }));
    drag(view.canvas, view.canvas, view.at({ x: lifeline('p_user'), y: 300 }), view.at({ x: lifeline('p_web'), y: 372 }));
    expect(onInsertMessage.mock.calls.map(([insert]) => (insert as { index: number }).index)).toEqual([0, 7]);
  });

  it('starts from a header as well as a lifeline, and drops on the same lifeline for a self message', () => {
    const onInsertMessage = vi.fn();
    const view = setup(sequence(), { tool: 'connector', onInsertMessage });
    drag(view.canvas, view.element('p_api'), view.at({ x: lifeline('p_api'), y: 40 }), view.at({ x: lifeline('p_api') + 3, y: 200 }));
    expect(onInsertMessage).toHaveBeenCalledExactlyOnceWith({ from: 'p_api', to: 'p_api', index: 3 });
  });

  it('creates nothing when released off every lifeline, or when cancelled with Escape', () => {
    const onInsertMessage = vi.fn();
    const view = setup(sequence(), { tool: 'connector', onInsertMessage });
    drag(view.canvas, view.canvas, view.at({ x: lifeline('p_web'), y: 200 }), view.at({ x: lifeline('p_web') + 60, y: 200 }));
    expect(onInsertMessage).not.toHaveBeenCalled();
    fireEvent.pointerDown(view.canvas, { ...mouse, ...view.at({ x: lifeline('p_web'), y: 200 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: lifeline('p_pay'), y: 200 }) });
    fireEvent.keyDown(view.canvas, { key: 'Escape' });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: lifeline('p_pay'), y: 200 }) });
    expect(onInsertMessage).not.toHaveBeenCalled();
    expect(view.container.querySelector('.connector-preview')).toBeNull();
  });

  it('does not start a gesture on empty canvas, where it pans', () => {
    const onInsertMessage = vi.fn();
    const view = setup(sequence(), { tool: 'connector', onInsertMessage });
    drag(view.canvas, view.canvas, view.at({ x: 300, y: 250 }), view.at({ x: lifeline('p_pay'), y: 250 }));
    expect(onInsertMessage).not.toHaveBeenCalled();
  });

  it('works with touch, whose lifeline target is wider than a mouse’s', () => {
    const onInsertMessage = vi.fn();
    const view = setup(sequence(), { tool: 'connector', onInsertMessage });
    const off = 11;
    // A mouse 11 px from a lifeline misses it (the mouse radius is 10); a finger reaches it (12 and up).
    drag(view.canvas, view.canvas, view.at({ x: lifeline('p_web') + off, y: 200 }), view.at({ x: lifeline('p_pay'), y: 200 }));
    expect(onInsertMessage).not.toHaveBeenCalled();
    const touch = { pointerId: 2, button: 0, pointerType: 'touch' };
    drag(view.canvas, view.canvas, view.at({ x: lifeline('p_web') + off, y: 200 }), view.at({ x: lifeline('p_pay') - off, y: 200 }), touch);
    expect(onInsertMessage).toHaveBeenCalledTimes(1);
  });

  it('shows no port markers, which a lifeline has none of', () => {
    const view = setup(sequence(), { tool: 'connector', onInsertMessage: vi.fn() });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: lifeline('p_web'), y: 200 }) });
    expect(view.container.querySelector('.connector-port')).toBeNull();
  });
});

describe('label editing, the handle and the keyboard', () => {
  it('opens the message label editor above its line, where the label is drawn', () => {
    const onLabelChange = vi.fn();
    const document = checkoutSequence();
    const view = setup(document, { onLabelChange });
    const message = must(sequenceScene(document).messages.find((candidate) => candidate.id === 'm_2'));
    fireEvent.doubleClick(view.element('m_2'));
    const frame = must(view.container.querySelector('.edge-label-editor')?.parentElement);
    expect(frame.style.transform).toBe(pointFrameTransform(view.handle.getViewport(), messageLabelAnchor(message)));
    // Above the line (y < the line's y), not on it as a free connector's editor would be.
    expect(messageLabelAnchor(message).y).toBeLessThan(message.y);
  });

  it('opens the editor above the line for a message with no label yet, and for a self message', () => {
    const document = checkoutSequence();
    document.edges[0] = { ...must(document.edges[0]), label: '' };
    const view = setup(document, { onLabelChange: vi.fn() });
    const bare = must(sequenceScene(document).messages.find((candidate) => candidate.id === 'm_1'));
    expect(bare.labelAt).toBeUndefined();
    fireEvent.doubleClick(view.element('m_1'));
    expect(must(view.container.querySelector('.edge-label-editor')?.parentElement).style.transform).toBe(pointFrameTransform(view.handle.getViewport(), { x: (must(bare.points[0]).x + must(bare.points[1]).x) / 2, y: bare.y - 14 }));
  });

  it('opens the editor over the bracket of a self message that has no label', () => {
    const document = checkoutSequence();
    document.edges[6] = { ...must(document.edges[6]), label: '' };
    const view = setup(document, { onLabelChange: vi.fn() });
    const bare = must(sequenceScene(document).messages.find((candidate) => candidate.id === 'm_7'));
    expect(bare.self).toBe(true);
    fireEvent.doubleClick(view.element('m_7'));
    // Above the bracket's top segment, 30 right of the lifeline's edge where the bracket turns.
    expect(must(view.container.querySelector('.edge-label-editor')?.parentElement).style.transform).toBe(pointFrameTransform(view.handle.getViewport(), { x: must(bare.points[0]).x + 30, y: bare.y - 14 }));
  });

  it('edit(id) opens the editor on an element that is drawn in the same render as the call', () => {
    const view = setup(checkoutSequence(), { onLabelChange: vi.fn() });
    act(() => {
      view.handle.edit('p_pay');
    });
    expect(view.container.querySelector('.label-editor')).not.toBeNull();
    view.unmount();
    // An id that never appears closes by itself rather than leaving an editor over nothing.
    const other = setup(checkoutSequence(), { onLabelChange: vi.fn() });
    act(() => {
      other.handle.edit('nobody');
    });
    expect(other.container.querySelector('.label-editor')).toBeNull();
  });

  it('edit(id) does nothing on a read-only canvas', () => {
    const view = setup(checkoutSequence(), { readOnly: true, onLabelChange: vi.fn() });
    act(() => {
      view.handle.edit('p_pay');
    });
    expect(view.container.querySelector('.label-editor')).toBeNull();
  });

  it('names the Arrange commands for an order: later and earlier, not forward and backward', () => {
    const view = setup(checkoutSequence(), { onArrange: vi.fn() });
    const labels = view.handle.actions.filter((action) => action.id.startsWith('canvas.arrange.')).map((action) => [action.id, action.label]);
    expect(labels).toEqual([
      ['canvas.arrange.bringToFront', 'Move to end'],
      ['canvas.arrange.bringForward', 'Move later'],
      ['canvas.arrange.sendBackward', 'Move earlier'],
      ['canvas.arrange.sendToBack', 'Move to start'],
    ]);
    view.unmount();
    const control = setup(freeform(), { onArrange: vi.fn() });
    expect(control.handle.actions.find((action) => action.id === 'canvas.arrange.bringForward')?.label).toBe('Bring forward');
  });

  it('Cmd+] on a selected message hands it to onArrange, which the editor reads as move later', () => {
    const onArrange = vi.fn();
    const view = setup(checkoutSequence(), { onArrange });
    act(() => {
      view.handle.select(new Set(['m_2']));
    });
    fireEvent.keyDown(view.canvas, { key: ']', metaKey: true });
    expect(onArrange).toHaveBeenCalledExactlyOnceWith(new Set(['m_2']), 'bringForward');
  });

  it('Tab walks the participants left to right and then the messages top to bottom, and announces each', () => {
    const onSelectionChange = vi.fn();
    const view = setup(checkoutSequence(), { onSelectionChange, onDeleteElements: vi.fn() });
    const selected: string[] = [];
    act(() => {
      view.handle.select(new Set(['p_pay']));
    });
    for (let i = 0; i < 3; i++) {
      fireEvent.keyDown(view.canvas, { key: 'Tab' });
      selected.push([...must(onSelectionChange.mock.calls.at(-1))[0] as Set<string>][0] ?? '');
    }
    expect(selected).toEqual(['m_1', 'm_2', 'm_3']);
    expect(view.container.parentElement?.querySelector('[role="status"]')?.textContent).toBe('charge(total), sync');
    fireEvent.keyDown(view.canvas, { key: 'Tab', shiftKey: true });
    fireEvent.keyDown(view.canvas, { key: 'Tab', shiftKey: true });
    fireEvent.keyDown(view.canvas, { key: 'Tab', shiftKey: true });
    expect([...must(onSelectionChange.mock.calls.at(-1))[0] as Set<string>]).toEqual(['p_pay']);
  });

  it('Tab still walks nodes only on a document that is not derived', () => {
    const onSelectionChange = vi.fn();
    const view = setup(freeform(), { onSelectionChange, onDeleteElements: vi.fn() });
    act(() => {
      view.handle.select(new Set(['p_api']));
    });
    fireEvent.keyDown(view.canvas, { key: 'Tab' });
    expect([...must(onSelectionChange.mock.calls.at(-1))[0] as Set<string>]).toEqual(['p_api']);
  });

  it('Delete removes a selected message or participant through onDeleteElements', () => {
    const onDeleteElements = vi.fn();
    const view = setup(checkoutSequence(), { onDeleteElements });
    act(() => {
      view.handle.select(new Set(['p_api']));
    });
    fireEvent.keyDown(view.canvas, { key: 'Delete' });
    expect(onDeleteElements).toHaveBeenCalledExactlyOnceWith(new Set(['p_api']));
  });
});
