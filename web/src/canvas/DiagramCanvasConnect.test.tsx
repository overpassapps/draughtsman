// @vitest-environment jsdom
import { createRef, useState } from 'react';
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Connection } from '../model/connect.ts';
import type { Document, Edge, Geometry, Id, Node, Point } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import type { Rect } from './geometry.ts';
import { handleRouteBatch } from './routingProtocol.ts';
import { RoutingClient } from './routingWorker.ts';
import { worldToScreen } from './viewport.ts';

const size = { width: 800, height: 600 };
const meta = { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' };

function node(id: string, geometry: Geometry, extra: Partial<Node> = {}): Node {
  return { id, kind: 'service', label: id, shape: 'rectangle', geometry, ...extra };
}

function doc(nodes: Node[], edges: Edge[] = []): Document {
  return parseDocument({ id: 'doc_connect', schemaVersion: 1, title: 'Connect', type: 'architecture', nodes, edges, meta });
}

// Two portless nodes a clear gap apart: each offers the four default side ports.
const pair = doc([node('n_a', { x: 0, y: 0, w: 100, h: 50 }), node('n_b', { x: 300, y: 0, w: 100, h: 50 })]);

/** The real RoutingClient over a port that runs the worker's handler in-process and answers on a microtask. */
function inProcessRouting(): RoutingClient {
  const client: RoutingClient = new RoutingClient({
    post: (message) => {
      queueMicrotask(() => {
        client.receive(handleRouteBatch(message));
      });
    },
    terminate: () => undefined,
  });
  return client;
}

const noRouting = (): undefined => undefined;

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

async function flushRoutes(): Promise<void> {
  await act(async () => {
    for (let i = 0; i < 10; i++) {
      await Promise.resolve();
    }
  });
}

function setup(props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}, document: Document = pair) {
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
  // jsdom lays nothing out, so a client coordinate is a canvas coordinate: map world points through the viewport.
  const at = (world: Point) => {
    const screen = worldToScreen(current.getViewport(), world);
    return { clientX: screen.x, clientY: screen.y };
  };
  return { ...result, canvas, at, handle: current };
}

const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };

function portsShown(container: HTMLElement, nodeId: string): string[] {
  return [...container.querySelectorAll(`.connector-ports[data-connector-node="${nodeId}"] .connector-port`)].map((port) => port.getAttribute('data-port-id') ?? '');
}

function activePort(container: HTMLElement): string | null | undefined {
  return container.querySelector('.connector-port.is-active')?.getAttribute('data-port-id');
}

function points(container: HTMLElement, edgeId: string): Point[] {
  const line = container.querySelector(`g[data-id="${edgeId}"] polyline:not(.hit-area)`);
  return (line?.getAttribute('points') ?? '')
    .split(' ')
    .filter(Boolean)
    .map((pair) => {
      const [x, y] = pair.split(',').map(Number);
      return { x: x ?? NaN, y: y ?? NaN };
    });
}

function crossesInterior(points: Point[], r: Rect): boolean {
  return points.some((q, i) => {
    const p = points[i - 1];
    if (p === undefined) {
      return false;
    }
    if (p.y === q.y) {
      return r.y < p.y && p.y < r.y + r.h && Math.max(p.x, q.x) > r.x && Math.min(p.x, q.x) < r.x + r.w;
    }
    return r.x < p.x && p.x < r.x + r.w && Math.max(p.y, q.y) > r.y && Math.min(p.y, q.y) < r.y + r.h;
  });
}

describe('DiagramCanvas connector tool (checklist item 8)', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows the ports of the node under the pointer, marks the one under it, and hides them off the node', () => {
    const { container, canvas, at } = setup({ tool: 'connector' });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    expect(portsShown(container, 'n_a')).toEqual(['top', 'right', 'bottom', 'left']);
    expect(activePort(container)).toBeUndefined();

    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 101, y: 26 }) });
    expect(activePort(container)).toBe('right');

    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 200, y: 200 }) });
    expect(container.querySelector('.connector-port')).toBeNull();
  });

  it('shows no ports and connects nothing in the select tool', () => {
    const onConnect = vi.fn();
    const { container, canvas, at } = setup({ tool: 'select', onConnect });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    expect(container.querySelector('.connector-port')).toBeNull();
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 300, y: 25 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 300, y: 25 }) });
    expect(onConnect).not.toHaveBeenCalled();
  });

  it('connects a port pressed on one node to the port released on on another', () => {
    const onConnect = vi.fn();
    const { container, canvas, at } = setup({ tool: 'connector', onConnect });
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 50, y: 52 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 350, y: -2 }) });
    // Mid-gesture: a preview line, the source port marked, and the target's ports with the one under the pointer.
    expect(container.querySelector('.connector-preview')).not.toBeNull();
    expect(portsShown(container, 'n_b')).toEqual(['top', 'right', 'bottom', 'left']);
    expect(container.querySelector('.connector-ports-target .is-active')?.getAttribute('data-port-id')).toBe('top');
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 350, y: -2 }) });

    expect(onConnect).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 'n_a', port: { id: 'bottom', x: 0.5, y: 1 } },
      to: { node: 'n_b', port: { id: 'top', x: 0.5, y: 0 } },
    });
    expect(container.querySelector('.connector-preview')).toBeNull();
  });

  it('pins the port a release lands on inside its ring, even when another port would give a cleaner route', () => {
    const onConnect = vi.fn();
    const { canvas, at } = setup({ tool: 'connector', onConnect });
    // Press on n_a's right port and release 5 px from n_b's bottom port: both are chosen, so both are kept.
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 352, y: 54 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 352, y: 54 }) });
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 'n_a', port: { id: 'right', x: 1, y: 0.5 } },
      to: { node: 'n_b', port: { id: 'bottom', x: 0.5, y: 1 } },
    });
  });

  it('attaches a release on the body of a node to the port facing a pinned source, not the one nearest the pointer', () => {
    const onConnect = vi.fn();
    const { canvas, at } = setup({ tool: 'connector', onConnect });
    // Pressed exactly on n_a's right port, released on n_b's body near its bottom-right: the facing side is left.
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 340, y: 38 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 340, y: 38 }) });
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 'n_a', port: { id: 'right', x: 1, y: 0.5 } },
      to: { node: 'n_b', port: { id: 'left', x: 0, y: 0.5 } },
    });
  });

  // S1: a drag from the middle of one shape to the middle of another, from every relative position, leaves by the side
  // facing the target and arrives at the side facing the source.
  it.each([
    ['below', { x: 0, y: 200 }, 'bottom', 'top'],
    ['above', { x: 0, y: -200 }, 'top', 'bottom'],
    ['to the right', { x: 300, y: 0 }, 'right', 'left'],
    ['to the left', { x: -300, y: 0 }, 'left', 'right'],
  ])('connects body to body facing each other when the target is %s', (_where, offset, fromPort, toPort) => {
    const onConnect = vi.fn();
    const document = doc([node('n_a', { x: 0, y: 0, w: 100, h: 50 }), node('n_b', { x: offset.x, y: offset.y, w: 100, h: 50 })]);
    // The mover is n_a, so put the target where `offset` says relative to it: n_a at the origin, n_b displaced.
    const { canvas, at } = setup({ tool: 'connector', onConnect }, document);
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: offset.x + 50, y: offset.y + 25 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: offset.x + 50, y: offset.y + 25 }) });
    expect(onConnect).toHaveBeenCalledTimes(1);
    const connection = onConnect.mock.calls[0]?.[0] as Connection;
    expect([connection.from.port.id, connection.to.port.id]).toEqual([fromPort, toPort]);
  });

  it('previews the facing ports while dragging body to body', () => {
    const document = doc([node('n_a', { x: 0, y: 0, w: 100, h: 50 }), node('n_b', { x: 0, y: 200, w: 100, h: 50 })]);
    const { container, canvas, at } = setup({ tool: 'connector' }, document);
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 50, y: 225 }) });
    expect(container.querySelector('.connector-ports-target .is-active')?.getAttribute('data-port-id')).toBe('top');
    expect(container.querySelector('.connector-ports-source .is-active, .connector-ports:not(.connector-ports-target) .is-active')?.getAttribute('data-port-id')).toBe('bottom');
  });

  it('uses a node\'s own ports when it declares them', () => {
    const onConnect = vi.fn();
    const document = doc([node('n_a', { x: 0, y: 0, w: 100, h: 50 }), node('n_b', { x: 300, y: 0, w: 100, h: 50 }, { ports: [{ id: 'in', x: 0.25, y: 1 }] })]);
    const { container, canvas, at } = setup({ tool: 'connector', onConnect }, document);
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 390, y: 10 }) });
    expect(portsShown(container, 'n_b')).toEqual(['in']);
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 390, y: 10 }) });
    expect(onConnect).toHaveBeenCalledWith({ from: { node: 'n_a', port: { id: 'right', x: 1, y: 0.5 } }, to: { node: 'n_b', port: { id: 'in', x: 0.25, y: 1 } } });
  });

  it('creates nothing on a release over empty canvas, back on the source node, or after Escape', () => {
    const onConnect = vi.fn();
    const { container, canvas, at } = setup({ tool: 'connector', onConnect });
    const gesture = (release: Point, escape = false) => {
      fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }) });
      fireEvent.pointerMove(canvas, { ...mouse, ...at(release) });
      if (escape) {
        fireEvent.keyDown(canvas, { key: 'Escape' });
        expect(container.querySelector('.connector-preview')).toBeNull();
      }
      fireEvent.pointerUp(canvas, { ...mouse, ...at(release) });
    };
    gesture({ x: 200, y: 200 });
    gesture({ x: 10, y: 10 });
    gesture({ x: 350, y: 25 }, true);
    expect(onConnect).not.toHaveBeenCalled();
    // The same press released on the other node does connect, so the three above failed for their own reasons.
    gesture({ x: 350, y: 25 });
    expect(onConnect).toHaveBeenCalledTimes(1);
  });

  it('abandons a connection with no edge when the tool leaves connector mid-drag', () => {
    const onConnect = vi.fn();
    const { canvas, at, rerender } = setup({ tool: 'connector', onConnect });
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 100, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 300, y: 25 }) });
    rerender(<DiagramCanvas document={pair} theme={diagramThemes.overpass} size={size} routingBackend={noRouting} tool="select" onConnect={onConnect} />);
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 300, y: 25 }) });
    expect(onConnect).not.toHaveBeenCalled();
  });

  it('connects rather than resizes from a selected node, whose middle handles sit on its default ports', () => {
    const onConnect = vi.fn();
    const onMoveNodes = vi.fn();
    const { container, canvas, at, rerender } = setup({ onMoveNodes });
    const shape = container.querySelector('g[data-id="n_a"] .shape');
    if (!shape) {
      throw new Error('n_a not drawn');
    }
    fireEvent.pointerDown(shape, { ...mouse, ...at({ x: 50, y: 25 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 50, y: 25 }) });
    expect(container.querySelector('.selection-node[data-selection-id="n_a"]')).not.toBeNull();
    rerender(<DiagramCanvas document={pair} theme={diagramThemes.overpass} size={size} routingBackend={noRouting} onMoveNodes={onMoveNodes} tool="connector" onConnect={onConnect} />);

    const handle = container.querySelector('.selection-node[data-selection-id="n_a"] [data-handle="3"]');
    if (!handle) {
      throw new Error('right-middle handle not drawn');
    }
    fireEvent.pointerDown(handle, { ...mouse, ...at({ x: 100, y: 25 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 300, y: 25 }) });
    flushFrame();
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 300, y: 25 }) });
    expect(onMoveNodes).not.toHaveBeenCalled();
    expect(onConnect).toHaveBeenCalledWith({ from: { node: 'n_a', port: { id: 'right', x: 1, y: 0.5 } }, to: { node: 'n_b', port: { id: 'left', x: 0, y: 0.5 } } });
  });

  function pointerDragOnNode(tool: 'select' | 'connector', pointerType: 'touch' | 'pen', press: Point, release: Point) {
    const onConnect = vi.fn();
    const onMoveNodes = vi.fn();
    const { container, canvas, at } = setup({ tool, onConnect, onMoveNodes });
    const shape = container.querySelector('g[data-id="n_a"] .shape');
    if (!shape) {
      throw new Error('n_a not drawn');
    }
    const before = container.querySelector('g.world')?.getAttribute('transform');
    const pointer = { pointerId: 5, button: 0, pointerType };
    fireEvent.pointerDown(shape, { ...pointer, ...at(press) });
    fireEvent.pointerMove(canvas, { ...pointer, ...at(release) });
    flushFrame();
    fireEvent.pointerUp(canvas, { ...pointer, ...at(release) });
    flushFrame();
    return { onConnect, onMoveNodes, before, after: container.querySelector('g.world')?.getAttribute('transform') };
  }

  function touchDragOnNode(tool: 'select' | 'connector') {
    return pointerDragOnNode(tool, 'touch', { x: 50, y: 25 }, { x: 350, y: 25 });
  }

  it('connects a port pressed on one node to the port released on another with a touch press-drag, without panning', () => {
    const { onConnect, onMoveNodes, before, after } = pointerDragOnNode('connector', 'touch', { x: 50, y: 52 }, { x: 350, y: -2 });
    expect(onConnect).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 'n_a', port: { id: 'bottom', x: 0.5, y: 1 } },
      to: { node: 'n_b', port: { id: 'top', x: 0.5, y: 0 } },
    });
    expect(onMoveNodes).not.toHaveBeenCalled();
    expect(after).toBe(before);
  });

  it('connects a port pressed on one node to the port released on another with a pen press-drag, without panning', () => {
    const { onConnect, onMoveNodes, before, after } = pointerDragOnNode('connector', 'pen', { x: 50, y: 52 }, { x: 350, y: -2 });
    expect(onConnect).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 'n_a', port: { id: 'bottom', x: 0.5, y: 1 } },
      to: { node: 'n_b', port: { id: 'top', x: 0.5, y: 0 } },
    });
    expect(onMoveNodes).not.toHaveBeenCalled();
    expect(after).toBe(before);
  });

  it('zooms on a two-finger pinch with one finger starting on a node', () => {
    const onConnect = vi.fn();
    const onMoveNodes = vi.fn();
    const { container, canvas, at, handle } = setup({ tool: 'connector', onConnect, onMoveNodes });
    const shape = container.querySelector('g[data-id="n_a"] .shape');
    if (!shape) {
      throw new Error('n_a not drawn');
    }
    const before = handle.getViewport().zoom;
    const first = { pointerId: 5, button: 0, pointerType: 'touch' };
    const second = { pointerId: 6, button: 0, pointerType: 'touch' };
    const onNode = at({ x: 50, y: 25 });
    const empty = at({ x: 200, y: 200 });
    fireEvent.pointerDown(shape, { ...first, ...onNode });
    fireEvent.pointerDown(canvas, { ...second, ...empty });
    fireEvent.pointerMove(canvas, { ...second, clientX: empty.clientX + (empty.clientX - onNode.clientX), clientY: empty.clientY + (empty.clientY - onNode.clientY) });
    flushFrame();
    fireEvent.pointerUp(canvas, { ...second });
    fireEvent.pointerUp(canvas, { ...first });
    flushFrame();
    expect(handle.getViewport().zoom).toBeCloseTo(before * 2);
    expect(onConnect).not.toHaveBeenCalled();
    expect(onMoveNodes).not.toHaveBeenCalled();
  });

  it('leaves the select tool panning on a touch press-drag on a node', () => {
    const { onConnect, onMoveNodes, before, after } = touchDragOnNode('select');
    expect(onConnect).not.toHaveBeenCalled();
    expect(onMoveNodes).not.toHaveBeenCalled();
    expect(after).not.toBe(before);
  });

  it('still pans from a press on empty canvas', () => {
    const { container, canvas, at } = setup({ tool: 'connector' });
    const before = container.querySelector('g.world')?.getAttribute('transform');
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 200, y: 200 }) });
    fireEvent.pointerMove(canvas, { ...mouse, clientX: at({ x: 200, y: 200 }).clientX + 40, clientY: at({ x: 200, y: 200 }).clientY });
    flushFrame();
    fireEvent.pointerUp(canvas, { ...mouse });
    expect(container.querySelector('g.world')?.getAttribute('transform')).not.toBe(before);
  });
});

describe('DiagramCanvas orthogonal routing (checklist items 9 and 10)', () => {
  // A and C in a row with B between them; A to C is orthogonal, A to B is left as the document gives it.
  const a = node('n_a', { x: 0, y: 100, w: 100, h: 50 }, { ports: [{ id: 'out', x: 1, y: 0.5 }] });
  const b = node('n_b', { x: 200, y: 90, w: 100, h: 70 });
  const c = node('n_c', { x: 400, y: 100, w: 100, h: 50 }, { ports: [{ id: 'in', x: 0, y: 0.5 }] });
  const routed: Edge = { id: 'e_ac', kind: 'flow', from: { node: 'n_a', port: 'out' }, to: { node: 'n_c', port: 'in' }, routing: { mode: 'orthogonal' } };
  const plain: Edge = { id: 'e_ab', kind: 'data', from: { node: 'n_a' }, to: { node: 'n_b' } };
  const row = doc([a, b, c], [routed, plain]);

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('draws an orthogonal edge around the node between its ends, and every other edge exactly as before', async () => {
    const without = setup({}, row);
    const straightThrough = points(without.container, 'e_ac');
    const plainBefore = without.container.querySelector('g[data-id="e_ab"]')?.outerHTML;
    without.unmount();

    const { container } = setup({ routingBackend: inProcessRouting }, row);
    await flushRoutes();
    const route = points(container, 'e_ac');
    expect(container.querySelector('g[data-id="e_ac"]')?.getAttribute('data-route')).toBe('routed');
    expect(crossesInterior(straightThrough, b.geometry as Rect)).toBe(true);
    expect(crossesInterior(route, b.geometry as Rect)).toBe(false);
    expect(route[0]).toEqual({ x: 100, y: 125 });
    expect(route.at(-1)).toEqual({ x: 400, y: 125 });
    expect(container.querySelector('g[data-id="e_ab"]')?.outerHTML).toBe(plainBefore);
  });

  it('renders a document with no orthogonal edges identically with the router running', async () => {
    const flat = doc([a, b, c], [{ ...routed, routing: { mode: 'straight' } }, plain]);
    const without = setup({}, flat);
    const before = without.container.innerHTML;
    without.unmount();
    const { container } = setup({ routingBackend: inProcessRouting }, flat);
    await flushRoutes();
    expect(container.innerHTML).toBe(before);
  });

  it('re-attaches and re-routes when an endpoint is dragged, without touching the edge in the model', async () => {
    let current = row;
    function Host() {
      const [document, setDocument] = useState(row);
      current = document;
      return (
        <DiagramCanvas
          document={document}
          theme={diagramThemes.overpass}
          size={size}
          fitKey={row}
          routingBackend={inProcessRouting}
          onMoveNodes={(moves) => {
            setDocument((previous) => ({ ...previous, nodes: previous.nodes.map((n) => ({ ...n, geometry: moves.get(n.id) ?? n.geometry })) as Node[] }));
          }}
        />
      );
    }
    const { container } = render(<Host />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    const shape = container.querySelector('g[data-id="n_c"] .shape');
    if (!canvas || !shape) {
      throw new Error('canvas not rendered');
    }
    canvas.setPointerCapture = () => undefined;
    canvas.releasePointerCapture = () => undefined;
    canvas.hasPointerCapture = () => false;
    await flushRoutes();
    expect(points(container, 'e_ac').at(-1)).toEqual({ x: 400, y: 125 });

    // Drag C down by 200 in two frames; the route follows during the drag and after it.
    fireEvent.pointerDown(shape, { ...mouse, clientX: 0, clientY: 0 });
    fireEvent.pointerMove(canvas, { ...mouse, clientX: 0, clientY: 100 });
    flushFrame();
    await flushRoutes();
    expect(points(container, 'e_ac').at(-1)).toEqual({ x: 400, y: 225 });
    fireEvent.pointerMove(canvas, { ...mouse, clientX: 0, clientY: 200 });
    flushFrame();
    fireEvent.pointerUp(canvas, { ...mouse });
    await flushRoutes();

    const route = points(container, 'e_ac');
    expect(route[0]).toEqual({ x: 100, y: 125 });
    expect(route.at(-1)).toEqual({ x: 400, y: 325 });
    expect(route.slice(1).every((q, i) => q.x === route[i]?.x || q.y === route[i]?.y)).toBe(true);
    expect(current.nodes.find((n) => n.id === 'n_c')?.geometry).toEqual({ x: 400, y: 300, w: 100, h: 50 });
    expect(current.edges).toEqual(row.edges);
  });

  it('re-routes when an obstacle is dragged into the path', async () => {
    const clear = doc([a, node('n_b', { x: 200, y: 400, w: 100, h: 70 }), c], [routed]);
    function Host() {
      const [document, setDocument] = useState(clear);
      return (
        <DiagramCanvas
          document={document}
          theme={diagramThemes.overpass}
          size={size}
          fitKey={clear}
          routingBackend={inProcessRouting}
          onMoveNodes={(moves) => {
            setDocument((previous) => ({ ...previous, nodes: previous.nodes.map((n) => ({ ...n, geometry: moves.get(n.id) ?? n.geometry })) as Node[] }));
          }}
        />
      );
    }
    const { container } = render(<Host />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    const shape = container.querySelector('g[data-id="n_b"] .shape');
    if (!canvas || !shape) {
      throw new Error('canvas not rendered');
    }
    canvas.setPointerCapture = () => undefined;
    canvas.releasePointerCapture = () => undefined;
    canvas.hasPointerCapture = () => false;
    await flushRoutes();
    expect(points(container, 'e_ac')).toEqual([
      { x: 100, y: 125 },
      { x: 400, y: 125 },
    ]);

    fireEvent.pointerDown(shape, { ...mouse, clientX: 0, clientY: 0 });
    fireEvent.pointerMove(canvas, { ...mouse, clientX: 0, clientY: -310 });
    flushFrame();
    fireEvent.pointerUp(canvas, { ...mouse });
    await flushRoutes();
    const moved = { x: 200, y: 90, w: 100, h: 70 };
    expect(crossesInterior(points(container, 'e_ac'), moved)).toBe(false);
    expect(points(container, 'e_ac').length).toBeGreaterThan(2);
  });
});

describe('DiagramCanvas segment dragging (routing decision D4)', () => {
  // A pinned orthogonal route with one middle segment, between n_a and n_b: exactly segmentHandles.test.tsx's e_ortho.
  const bent = doc(
    [node('n_a', { x: 0, y: 0, w: 100, h: 50 }), node('n_b', { x: 300, y: 0, w: 100, h: 50 })],
    [{ id: 'e_bend', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' }, routing: { mode: 'orthogonal', waypoints: [{ x: 150, y: 0 }, { x: 150, y: 150 }] } }],
  );

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  function dragMiddleSegment(pointerType: 'mouse' | 'touch' | 'pen') {
    const onMoveNodes = vi.fn<(moves: ReadonlyMap<Id, Geometry>, waypoints?: ReadonlyMap<Id, Point[]>) => void>();
    const onMoveEnd = vi.fn();
    const { container, canvas, at, handle } = setup({ onMoveNodes, onMoveEnd }, bent);
    act(() => {
      handle.select(new Set(['e_bend']));
    });
    const route = container.querySelector('g[data-id="e_bend"].edge polyline.hit-area');
    if (!route) {
      throw new Error('e_bend not drawn');
    }
    const pointer = { pointerId: 3, button: 0, pointerType };
    // The middle segment runs from (150,0) to (150,150); press its midpoint and drag it sideways to pin it.
    fireEvent.pointerDown(route, { ...pointer, ...at({ x: 150, y: 75 }) });
    fireEvent.pointerMove(canvas, { ...pointer, ...at({ x: 200, y: 75 }) });
    flushFrame();
    fireEvent.pointerUp(canvas, { ...pointer, ...at({ x: 200, y: 75 }) });
    return { onMoveNodes, onMoveEnd };
  }

  it('pins an orthogonal connector\'s route on a mouse drag of its middle segment', () => {
    const { onMoveNodes, onMoveEnd } = dragMiddleSegment('mouse');
    expect(onMoveEnd).toHaveBeenCalledTimes(1);
    const [, waypoints] = onMoveNodes.mock.calls.at(-1) ?? [];
    expect(waypoints?.get('e_bend')).toEqual([
      { x: 200, y: 0 },
      { x: 200, y: 150 },
    ]);
  });

  it('pins an orthogonal connector\'s route on a touch drag of its middle segment, the same as a mouse', () => {
    const { onMoveNodes, onMoveEnd } = dragMiddleSegment('touch');
    expect(onMoveEnd).toHaveBeenCalledTimes(1);
    const [, waypoints] = onMoveNodes.mock.calls.at(-1) ?? [];
    expect(waypoints?.get('e_bend')).toEqual([
      { x: 200, y: 0 },
      { x: 200, y: 150 },
    ]);
  });

  it('pins an orthogonal connector\'s route on a pen drag of its middle segment, the same as a mouse', () => {
    const { onMoveNodes, onMoveEnd } = dragMiddleSegment('pen');
    expect(onMoveEnd).toHaveBeenCalledTimes(1);
    const [, waypoints] = onMoveNodes.mock.calls.at(-1) ?? [];
    expect(waypoints?.get('e_bend')).toEqual([
      { x: 200, y: 0 },
      { x: 200, y: 150 },
    ]);
  });
});
