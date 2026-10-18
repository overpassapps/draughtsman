// @vitest-environment jsdom
import { createRef } from 'react';
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Edge, Geometry, Node, Point } from '../model/types.ts';
import { CONNECT_HANDLE_OFFSET, CONNECT_HIT_RADIUS, EDGE_END_HANDLE_INSET } from './connectHandles.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import { worldToScreen } from './viewport.ts';

const size = { width: 800, height: 600 };
const meta = { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' };

function node(id: string, geometry: Geometry, extra: Partial<Node> = {}): Node {
  return { id, kind: 'service', label: id, shape: 'rectangle', geometry, ...extra };
}

function doc(nodes: Node[], edges: Edge[] = []): Document {
  return parseDocument({ id: 'doc_handles', schemaVersion: 1, title: 'Handles', type: 'architecture', nodes, edges, meta });
}

// Three portless nodes: n_a and n_b side by side, n_c below n_b. Each offers the four default side ports.
const nodes = [node('n_a', { x: 0, y: 0, w: 100, h: 50 }), node('n_b', { x: 300, y: 0, w: 100, h: 50 }), node('n_c', { x: 300, y: 200, w: 100, h: 50 })];
const trio = doc(nodes);
// A straight edge from n_a's right port to n_b's left port: its drawn ends are exactly those two points.
const linked = doc(
  nodes.map((n) => (n.id === 'n_c' ? n : { ...n, ports: [{ id: 'right', x: 1, y: 0.5 }, { id: 'left', x: 0, y: 0.5 }] })),
  [{ id: 'e_1', kind: 'flow', from: { node: 'n_a', port: 'right' }, to: { node: 'n_b', port: 'left' }, routing: { mode: 'straight' } }],
);

const noRouting = (): undefined => undefined;
const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };

function setup(props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}, document: Document = trio) {
  const handle = createRef<DiagramCanvasHandle>();
  const result = render(<DiagramCanvas ref={handle} document={document} theme={diagramThemes.overpass} size={size} routingBackend={noRouting} {...props} />);
  const canvas = result.container.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas || !handle.current) {
    throw new Error('canvas not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  const current = handle.current;
  // jsdom lays nothing out, so a client coordinate is a canvas coordinate: map world points through the viewport, then
  // shift by screen pixels, which is how far a hand is off a target at any zoom.
  const at = (world: Point, dx = 0, dy = 0) => {
    const screen = worldToScreen(current.getViewport(), world);
    return { clientX: screen.x + dx, clientY: screen.y + dy };
  };
  return { ...result, canvas, at, handle: current };
}

function handlesShown(container: HTMLElement, nodeId: string): string[] {
  return [...container.querySelectorAll(`.connect-handles[data-connector-node="${nodeId}"] .connect-handle`)].map((h) => h.getAttribute('data-port-id') ?? '');
}

const right = { id: 'right', x: 1, y: 0.5 };
const left = { id: 'left', x: 0, y: 0.5 };
const top = { id: 'top', x: 0.5, y: 0 };

describe('DiagramCanvas select-tool connection handles', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows handles on the hovered node, keeps them while the pointer travels out to one, and hides them past it', () => {
    const { container, canvas, at } = setup({ onConnect: vi.fn() });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    expect(handlesShown(container, 'n_a')).toEqual(['top', 'right', 'bottom', 'left']);
    expect(container.querySelector('.connect-handle.is-active')).toBeNull();

    // On the right handle, set out from the port: still hovered, and that handle is marked.
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 100, y: 25 }, CONNECT_HANDLE_OFFSET) });
    expect(handlesShown(container, 'n_a')).toHaveLength(4);
    expect(container.querySelector('.connect-handle.is-active')?.getAttribute('data-port-id')).toBe('right');

    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 200, y: 200 }) });
    expect(container.querySelector('.connect-handle')).toBeNull();
  });

  it('shows a lone selected node\'s handles wherever the pointer is, and none for a multi-selection', () => {
    const { container, canvas, handle } = setup({ onConnect: vi.fn() });
    fireEvent.pointerMove(canvas, { ...mouse, clientX: 790, clientY: 590 });
    expect(container.querySelector('.connect-handle')).toBeNull();
    act(() => {
      handle.select(new Set(['n_b']));
    });
    expect(handlesShown(container, 'n_b')).toEqual(['top', 'right', 'bottom', 'left']);
    act(() => {
      handle.select(new Set(['n_a', 'n_b']));
    });
    expect(container.querySelector('.connect-handle')).toBeNull();
  });

  it('shows no handles read-only, in the connector tool, or without onConnect', () => {
    for (const props of [{ readOnly: true, onConnect: vi.fn() }, { tool: 'connector' as const, onConnect: vi.fn() }, {}]) {
      const { container, canvas, at, unmount } = setup(props);
      fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
      expect(container.querySelector('.connect-handle')).toBeNull();
      unmount();
    }
  });

  it('connects from a handle without switching tool, with the target outlined and its port ringed mid-drag', () => {
    const onConnect = vi.fn();
    const onMoveNodes = vi.fn();
    const { container, canvas, at } = setup({ onConnect, onMoveNodes });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }, CONNECT_HANDLE_OFFSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 310, y: 30 }) });
    expect(container.querySelector('.connector-preview')).not.toBeNull();
    expect(container.querySelector('.connector-target-outline')?.getAttribute('data-connector-node')).toBe('n_b');
    expect(container.querySelector('.connector-snap-ring')).not.toBeNull();
    // Handles are for choosing where to start; mid-drag only the ports being joined show.
    expect(container.querySelector('.connect-handle')).toBeNull();
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 310, y: 30 }) });

    expect(onConnect).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenCalledWith({ from: { node: 'n_a', port: right }, to: { node: 'n_b', port: left } });
    expect(onMoveNodes).not.toHaveBeenCalled();
    expect(container.querySelector('.connector-preview')).toBeNull();
  });

  it('connects from a handle with a touch or pen drag once the node is selected, since touch has no hover', () => {
    for (const pointerType of ['touch', 'pen'] as const) {
      const onConnect = vi.fn();
      const onMoveNodes = vi.fn();
      const { container, canvas, at, handle, unmount } = setup({ onConnect, onMoveNodes });
      // No hover for touch or pen: the handle only shows, and only takes a press, once the node is the lone selection.
      expect(handlesShown(container, 'n_a')).toEqual([]);
      act(() => {
        handle.select(new Set(['n_a']));
      });
      expect(handlesShown(container, 'n_a')).toEqual(['top', 'right', 'bottom', 'left']);
      const pointer = { pointerId: 7, button: 0, pointerType };
      fireEvent.pointerDown(canvas, { ...pointer, ...at({ x: 100, y: 25 }, CONNECT_HANDLE_OFFSET) });
      fireEvent.pointerMove(canvas, { ...pointer, ...at({ x: 310, y: 30 }) });
      fireEvent.pointerUp(canvas, { ...pointer, ...at({ x: 310, y: 30 }) });
      expect(onConnect).toHaveBeenCalledTimes(1);
      expect(onConnect).toHaveBeenCalledWith({ from: { node: 'n_a', port: right }, to: { node: 'n_b', port: left } });
      expect(onMoveNodes).not.toHaveBeenCalled();
      unmount();
    }
  });

  it('keeps a child\'s handles over its container while the pointer travels out to one, and connects from the child there', () => {
    const onConnect = vi.fn();
    const grouped = doc([
      node('c_box', { x: 0, y: 0, w: 260, h: 200 }, { kind: 'container' }),
      node('n_in', { x: 40, y: 60, w: 100, h: 50 }, { parent: 'c_box' }),
      node('n_b', { x: 400, y: 60, w: 100, h: 50 }),
    ]);
    const { container, canvas, at } = setup({ onConnect }, grouped);
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 90, y: 85 }) });
    expect(handlesShown(container, 'n_in')).toHaveLength(4);
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 140, y: 85 }, CONNECT_HANDLE_OFFSET / 2) });
    expect(handlesShown(container, 'n_in')).toHaveLength(4);
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 140, y: 85 }, CONNECT_HANDLE_OFFSET) });
    expect(container.querySelector('.connect-handle.is-active')?.getAttribute('data-port-id')).toBe('right');
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 140, y: 85 }, CONNECT_HANDLE_OFFSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 410, y: 90 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 410, y: 90 }) });
    expect(onConnect).toHaveBeenCalledWith({ from: { node: 'n_in', port: right }, to: { node: 'n_b', port: left } });
  });

  it('shows no outline or ring over empty canvas, and connects nothing there, back on the source, or after Escape', () => {
    const onConnect = vi.fn();
    const { container, canvas, at } = setup({ onConnect });
    const gesture = (release: Point, escape = false) => {
      fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
      fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }, CONNECT_HANDLE_OFFSET) });
      fireEvent.pointerMove(canvas, { ...mouse, ...at(release) });
      if (escape) {
        fireEvent.keyDown(canvas, { key: 'Escape' });
        expect(container.querySelector('.connector-preview')).toBeNull();
      }
      fireEvent.pointerUp(canvas, { ...mouse, ...at(release) });
    };
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }, CONNECT_HANDLE_OFFSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 200, y: 150 }) });
    expect(container.querySelector('.connector-preview')).not.toBeNull();
    expect(container.querySelector('.connector-target-outline')).toBeNull();
    expect(container.querySelector('.connector-snap-ring')).toBeNull();
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 200, y: 150 }) });
    gesture({ x: 10, y: 10 });
    gesture({ x: 350, y: 25 }, true);
    expect(onConnect).not.toHaveBeenCalled();
    // The same press released on the other node does connect, so the three above failed for their own reasons.
    gesture({ x: 350, y: 25 });
    expect(onConnect).toHaveBeenCalledTimes(1);
  });

  it('takes a press just inside the hit radius and misses one just outside it, at every zoom', () => {
    for (const zoomSteps of [0, 2, -2]) {
      const onConnect = vi.fn();
      const { canvas, at, handle, unmount } = setup({ onConnect });
      act(() => {
        for (let i = 0; i < Math.abs(zoomSteps); i++) {
          if (zoomSteps > 0) {
            handle.zoomIn();
          } else {
            handle.zoomOut();
          }
        }
      });
      flush();
      const drag = (offset: number) => {
        fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
        fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 50, y: 0 }, 0, -CONNECT_HANDLE_OFFSET + offset) });
        fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 25 }) });
        fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 25 }) });
      };
      drag(-(CONNECT_HIT_RADIUS - 1));
      expect(onConnect).toHaveBeenCalledTimes(1);
      expect(onConnect).toHaveBeenLastCalledWith({ from: { node: 'n_a', port: top }, to: { node: 'n_b', port: expect.anything() as unknown } });
      drag(-(CONNECT_HIT_RADIUS + 1));
      expect(onConnect).toHaveBeenCalledTimes(1);
      unmount();
    }
  });

  it('still resizes from a selected node\'s middle handle, which sits on the port rather than on the connection handle', () => {
    const onConnect = vi.fn();
    const onMoveNodes = vi.fn<(moves: ReadonlyMap<string, Geometry>) => void>();
    const { container, canvas, at, handle } = setup({ onConnect, onMoveNodes, grid: false });
    act(() => {
      handle.select(new Set(['n_a']));
    });
    const resize = container.querySelector('.selection-node[data-selection-id="n_a"] [data-handle="3"]');
    if (!resize) {
      throw new Error('right-middle handle not drawn');
    }
    fireEvent.pointerDown(resize, { ...mouse, ...at({ x: 100, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 140, y: 25 }) });
    flush();
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 140, y: 25 }) });
    expect(onConnect).not.toHaveBeenCalled();
    expect(onMoveNodes).toHaveBeenCalled();
    expect(onMoveNodes.mock.lastCall?.[0].get('n_a')?.w).toBeGreaterThan(100);
  });

  it('keeps a lone selected node\'s rotate handle clear of its top connection handle, so each takes its own press', () => {
    const onConnect = vi.fn();
    const onMoveNodes = vi.fn<(moves: ReadonlyMap<string, Geometry>) => void>();
    const onMoveEnd = vi.fn();
    const document = doc([node('n_a', { x: 0, y: 0, w: 100, h: 50 }), node('n_b', { x: 0, y: -300, w: 100, h: 50 })]);
    const { container, canvas, handle } = setup({ onConnect, onMoveNodes, onMoveEnd, grid: false }, document);
    act(() => {
      handle.select(new Set(['n_a']));
    });
    const rotator = container.querySelector('.selection-node[data-selection-id="n_a"] [data-rotate-handle]');
    if (!rotator) {
      throw new Error('rotate handle not drawn');
    }
    const r = { x: Number(rotator.getAttribute('cx')), y: Number(rotator.getAttribute('cy')) };
    const topHandle = container.querySelector('.connect-handles[data-connector-node="n_a"] .connect-handle[data-port-id="top"]');
    if (!topHandle) {
      throw new Error('top connection handle not drawn');
    }
    const t = { x: Number(topHandle.getAttribute('cx')), y: Number(topHandle.getAttribute('cy')) };
    // Clear of the rotate handle's disc by more than a connection handle's whole reach.
    expect(Math.hypot(t.x - r.x, t.y - r.y)).toBeGreaterThan(CONNECT_HIT_RADIUS + Number(rotator.getAttribute('r')));

    // A press on the rotate handle rotates, one undo step, and starts no connector.
    fireEvent.pointerDown(rotator, { ...mouse, clientX: r.x, clientY: r.y });
    fireEvent.pointerMove(canvas, { ...mouse, clientX: r.x + 40, clientY: r.y + 10 });
    flush();
    fireEvent.pointerUp(canvas, { ...mouse, clientX: r.x + 40, clientY: r.y + 10 });
    expect(onMoveNodes.mock.lastCall?.[0].get('n_a')?.rotation).toBeGreaterThan(0);
    expect(onMoveEnd).toHaveBeenCalledTimes(1);
    expect(onConnect).not.toHaveBeenCalled();

    // The top connection handle, where it is drawn, still connects from the top port.
    const freshRender = setup({ onConnect }, document);
    act(() => {
      freshRender.handle.select(new Set(['n_a']));
    });
    const drawnTop = freshRender.container.querySelector('.connect-handles[data-connector-node="n_a"] .connect-handle[data-port-id="top"]');
    fireEvent.pointerDown(freshRender.canvas, { ...mouse, clientX: Number(drawnTop?.getAttribute('cx')), clientY: Number(drawnTop?.getAttribute('cy')) });
    fireEvent.pointerMove(freshRender.canvas, { ...mouse, ...freshRender.at({ x: 50, y: -280 }) });
    fireEvent.pointerUp(freshRender.canvas, { ...mouse, ...freshRender.at({ x: 50, y: -280 }) });
    expect(onConnect).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenLastCalledWith({ from: { node: 'n_a', port: top }, to: { node: 'n_b', port: expect.anything() as unknown } });
  });
});

describe('DiagramCanvas re-attaching a selected edge\'s end', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  function selected(props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}) {
    const result = setup({ onConnect: vi.fn(), ...props }, linked);
    act(() => {
      result.handle.select(new Set(['e_1']));
    });
    return result;
  }

  // The end handles sit in along e_1 from its ports: the tail right of n_a's right port, the arrow left of n_b's left.
  const tail = { x: 100, y: 25 };
  const head = { x: 300, y: 25 };
  const ends = (container: HTMLElement) => [...container.querySelectorAll('.edge-endpoint[data-edge-id="e_1"]')].map((e) => e.getAttribute('data-edge-end'));

  it('draws a handle on each end of a selected edge only when the edge can be re-attached', () => {
    const { container, at } = selected({ onReconnect: vi.fn() });
    expect(ends(container)).toEqual(['from', 'to']);
    const drawn = [...container.querySelectorAll('.edge-endpoint[data-edge-id="e_1"]')].map((e) => ({ clientX: Number(e.getAttribute('cx')), clientY: Number(e.getAttribute('cy')) }));
    expect(drawn).toEqual([at(tail, EDGE_END_HANDLE_INSET), at(head, -EDGE_END_HANDLE_INSET)]);
  });

  it('draws no end handles, and re-attaches nothing, in the connector tool', () => {
    const onReconnect = vi.fn();
    const { container, canvas, at } = selected({ onReconnect, tool: 'connector' });
    expect(ends(container)).toEqual([]);
    fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    expect(onReconnect).not.toHaveBeenCalled();
  });

  it('starts a new connector from a port in the connector tool even where the selected edge ends there', () => {
    const onReconnect = vi.fn();
    const onConnect = vi.fn();
    const { canvas, at } = selected({ onReconnect, onConnect, tool: 'connector' });
    fireEvent.pointerDown(canvas, { ...mouse, ...at(tail) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    expect(onReconnect).not.toHaveBeenCalled();
    expect(onConnect).toHaveBeenCalledWith({ from: { node: 'n_a', port: right }, to: { node: 'n_c', port: top } });
  });

  it('starts a new connector from a shape\'s connection handle in the select tool even where the selected edge ends', () => {
    const onReconnect = vi.fn();
    const onConnect = vi.fn();
    const { canvas, at } = selected({ onReconnect, onConnect });
    // Inside the handle's reach on the port side, which is also within a port's reach of the edge's tail.
    fireEvent.pointerDown(canvas, { ...mouse, ...at(tail, CONNECT_HANDLE_OFFSET - (CONNECT_HIT_RADIUS - 2)) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    expect(onReconnect).not.toHaveBeenCalled();
    expect(onConnect).toHaveBeenCalledWith({ from: { node: 'n_a', port: right }, to: { node: 'n_c', port: top } });
  });

  it('fans out from either end\'s connection handle of a selected 60 px connector in the select tool at 100%', () => {
    const near = doc(
      [node('n_a', { x: 0, y: 0, w: 100, h: 50 }, { ports: [right, left] }), node('n_b', { x: 160, y: 0, w: 100, h: 50 }, { ports: [right, left] }), node('n_c', { x: 300, y: 200, w: 100, h: 50 })],
      [{ id: 'e_1', kind: 'flow', from: { node: 'n_a', port: 'right' }, to: { node: 'n_b', port: 'left' }, routing: { mode: 'straight' } }],
    );
    const cases = [
      { handleAt: { x: 100 + CONNECT_HANDLE_OFFSET, y: 25 }, from: { node: 'n_a', port: right } },
      { handleAt: { x: 160 - CONNECT_HANDLE_OFFSET, y: 25 }, from: { node: 'n_b', port: left } },
    ];
    for (const { handleAt, from } of cases) {
      const onReconnect = vi.fn();
      const onConnect = vi.fn();
      const { container, canvas, at, handle, unmount } = setup({ onConnect, onReconnect }, near);
      expect(handle.getViewport().zoom).toBe(1);
      act(() => {
        handle.select(new Set(['e_1']));
      });
      // Too short to fit end handles clear of the connection handles, so none show.
      expect(ends(container)).toEqual([]);
      for (const dx of [0, 4, -4]) {
        fireEvent.pointerDown(canvas, { ...mouse, ...at(handleAt, dx) });
        fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
        fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
      }
      expect(onReconnect).not.toHaveBeenCalled();
      expect(onConnect).toHaveBeenCalledTimes(3);
      expect(onConnect).toHaveBeenCalledWith({ from, to: { node: 'n_c', port: top } });
      unmount();
    }
  });

  it('keeps an end handle off a neighbouring shape\'s connection handle, so dragging the drawn handle re-attaches', () => {
    // e_2 runs straight from n_a's right port past n_d, whose left connection handle sits 4 px from where the fixed
    // inset would put the tail's handle. The drawn handle must be clear of it, and a drag on it must move the tail.
    const ported = { ports: [right, left] };
    const neighbour = doc(
      [node('n_a', { x: 0, y: 0, w: 100, h: 50 }, ported), node('n_d', { x: 160, y: 0, w: 100, h: 50 }), node('n_g', { x: 500, y: 0, w: 100, h: 50 }, ported), node('n_e', { x: 300, y: 200, w: 100, h: 50 })],
      [{ id: 'e_2', kind: 'flow', from: { node: 'n_a', port: 'right' }, to: { node: 'n_g', port: 'left' }, routing: { mode: 'straight' } }],
    );
    const onReconnect = vi.fn();
    const onConnect = vi.fn();
    const { container, canvas, at, handle } = setup({ onConnect, onReconnect }, neighbour);
    act(() => {
      handle.select(new Set(['e_2']));
    });
    const drawn = container.querySelector('.edge-endpoint[data-edge-id="e_2"][data-edge-end="from"]');
    expect(drawn).not.toBeNull();
    const press = { clientX: Number(drawn?.getAttribute('cx')), clientY: Number(drawn?.getAttribute('cy')) };
    const dLeftHandle = at({ x: 160 - CONNECT_HANDLE_OFFSET, y: 25 });
    expect(Math.hypot(press.clientX - dLeftHandle.clientX, press.clientY - dLeftHandle.clientY)).toBeGreaterThanOrEqual(2 * CONNECT_HIT_RADIUS);
    fireEvent.pointerDown(canvas, { ...mouse, ...press });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    expect(onConnect).not.toHaveBeenCalled();
    expect(onReconnect).toHaveBeenCalledWith({ edge: 'e_2', end: 'from', to: { node: 'n_e', port: top } });
  });

  it('re-attaches nothing when the edge is gone before the release, as after an undo mid-drag', () => {
    const onReconnect = vi.fn();
    const { canvas, at, rerender } = selected({ onReconnect });
    fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    rerender(<DiagramCanvas document={{ ...linked, edges: [] }} theme={diagramThemes.overpass} size={size} routingBackend={noRouting} onConnect={vi.fn()} onReconnect={onReconnect} />);
    expect(() => fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) })).not.toThrow();
    expect(onReconnect).not.toHaveBeenCalled();
  });

  it('draws no end handles, and re-attaches nothing, without onReconnect', () => {
    const onConnect = vi.fn();
    const { container, canvas, at } = selected({ onConnect });
    expect(ends(container)).toEqual([]);
    fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 210 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 210 }) });
    expect(container.querySelector('.connector-preview')).toBeNull();
    expect(onConnect).not.toHaveBeenCalled();
  });

  it('moves the arrow end onto another node, snapping to the port nearest the release, with the target shown mid-drag', () => {
    const onReconnect = vi.fn();
    const onConnect = vi.fn();
    const { container, canvas, at } = selected({ onReconnect, onConnect });
    fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET - (CONNECT_HIT_RADIUS - 2)) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    expect(container.querySelector('.connector-preview')).not.toBeNull();
    expect(container.querySelector('.connector-target-outline')?.getAttribute('data-connector-node')).toBe('n_c');
    expect(container.querySelector('.connector-ports-target .is-active')?.getAttribute('data-port-id')).toBe('top');
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
    expect(onReconnect).toHaveBeenCalledTimes(1);
    expect(onReconnect).toHaveBeenCalledWith({ edge: 'e_1', end: 'to', to: { node: 'n_c', port: top } });
    expect(onConnect).not.toHaveBeenCalled();
    expect(container.querySelector('.connector-preview')).toBeNull();
  });

  it('re-attaches the arrow end onto another node with a touch or pen drag, the same as a mouse', () => {
    for (const pointerType of ['touch', 'pen'] as const) {
      const onReconnect = vi.fn();
      const onConnect = vi.fn();
      const { canvas, at, unmount } = selected({ onReconnect, onConnect });
      const pointer = { pointerId: 9, button: 0, pointerType };
      fireEvent.pointerDown(canvas, { ...pointer, ...at(head, -EDGE_END_HANDLE_INSET - (CONNECT_HIT_RADIUS - 2)) });
      fireEvent.pointerMove(canvas, { ...pointer, ...at({ x: 350, y: 205 }) });
      fireEvent.pointerUp(canvas, { ...pointer, ...at({ x: 350, y: 205 }) });
      expect(onReconnect).toHaveBeenCalledTimes(1);
      expect(onReconnect).toHaveBeenCalledWith({ edge: 'e_1', end: 'to', to: { node: 'n_c', port: top } });
      expect(onConnect).not.toHaveBeenCalled();
      unmount();
    }
  });

  it('re-attaches an end dropped on a node\'s body to the port facing the end that stays put, and pins one dropped on a port', () => {
    const onReconnect = vi.fn();
    const { canvas, at } = selected({ onReconnect });
    // (345, 215) is on n_c's body, nearest its top port, but the tail stays at n_a's right port up-left of n_c: left faces it.
    fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 345, y: 215 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 345, y: 215 }) });
    expect(onReconnect).toHaveBeenLastCalledWith({ edge: 'e_1', end: 'to', to: { node: 'n_c', port: left } });
    // Released within the top port's ring: that port was chosen, so it is kept.
    fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 347, y: 204 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 347, y: 204 }) });
    expect(onReconnect).toHaveBeenLastCalledWith({ edge: 'e_1', end: 'to', to: { node: 'n_c', port: top } });
  });

  it('moves the tail when its end is dragged, to another port of the same node', () => {
    const onReconnect = vi.fn();
    const { canvas, at } = selected({ onReconnect });
    fireEvent.pointerDown(canvas, { ...mouse, ...at(tail, EDGE_END_HANDLE_INSET) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 5, y: 25 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 5, y: 25 }) });
    expect(onReconnect).toHaveBeenCalledWith({ edge: 'e_1', end: 'from', to: { node: 'n_a', port: left } });
  });

  it('changes nothing released on empty canvas, on the other end\'s node, back on the same port, or after Escape', () => {
    const onReconnect = vi.fn();
    const { container, canvas, at } = selected({ onReconnect });
    const gesture = (release: Point, escape = false) => {
      fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET) });
      fireEvent.pointerMove(canvas, { ...mouse, ...at(release) });
      if (escape) {
        fireEvent.keyDown(canvas, { key: 'Escape' });
        expect(container.querySelector('.connector-preview')).toBeNull();
      }
      fireEvent.pointerUp(canvas, { ...mouse, ...at(release) });
    };
    gesture({ x: 200, y: 400 });
    gesture({ x: 50, y: 25 });
    gesture({ x: 302, y: 25 });
    gesture({ x: 350, y: 205 }, true);
    expect(onReconnect).not.toHaveBeenCalled();
    // The same press released on n_c does re-attach, so the four above failed for their own reasons.
    gesture({ x: 350, y: 205 });
    expect(onReconnect).toHaveBeenCalledTimes(1);
  });

  it('takes an end handle within the hit radius and leaves a press beyond it to select as before, at any zoom', () => {
    for (const zoomSteps of [0, 3]) {
      const onReconnect = vi.fn();
      const { canvas, at, handle, unmount } = selected({ onReconnect });
      act(() => {
        for (let i = 0; i < zoomSteps; i++) {
          handle.zoomIn();
        }
      });
      flush();
      const drag = (offset: number) => {
        fireEvent.pointerDown(canvas, { ...mouse, ...at(head, -EDGE_END_HANDLE_INSET, offset) });
        fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
        fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: 205 }) });
      };
      drag(CONNECT_HIT_RADIUS + 1);
      expect(onReconnect).not.toHaveBeenCalled();
      act(() => {
        handle.select(new Set(['e_1']));
      });
      drag(CONNECT_HIT_RADIUS - 1);
      expect(onReconnect).toHaveBeenCalledTimes(1);
      unmount();
    }
  });
});

function flush(): void {
  act(() => {
    vi.runAllTimers();
  });
}
