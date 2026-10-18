// @vitest-environment jsdom
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { useDocumentEditor, type DocumentEditor } from '../editor/useDocumentEditor.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Geometry, Id, Point } from '../model/types.ts';
import { DiagramCanvas } from './DiagramCanvas.tsx';
import { boxAt, boxesOverlap } from './labelLayout.ts';
import { handleRouteBatch } from './routingProtocol.ts';
import { RoutingClient } from './routingWorker.ts';

// Checklist item 11: double-clicking an edge opens a label editor at the midpoint; the committed label follows the
// edge when it re-routes and is written to edge.label.

const size = { width: 800, height: 600 };
const meta = { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' };

// n_b sits across the straight line from n_a's right port to n_c's left one, so the router bends e_ac around it.
const obstacle: Geometry = { x: 200, y: 90, w: 100, h: 70 };
const document: Document = parseDocument({
  id: 'doc_edge_label',
  schemaVersion: 1,
  title: 'Edge labels',
  type: 'architecture',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'Orders', shape: 'rectangle', geometry: { x: 0, y: 100, w: 100, h: 50 }, ports: [{ id: 'out', x: 1, y: 0.5 }] },
    { id: 'n_b', kind: 'service', label: 'Billing', shape: 'rectangle', geometry: obstacle },
    { id: 'n_c', kind: 'service', label: 'Ledger', shape: 'rectangle', geometry: { x: 400, y: 100, w: 100, h: 50 }, ports: [{ id: 'in', x: 0, y: 0.5 }] },
  ],
  edges: [
    { id: 'e_ac', kind: 'flow', label: 'HTTPS', from: { node: 'n_a', port: 'out' }, to: { node: 'n_c', port: 'in' }, routing: { mode: 'orthogonal' } },
    { id: 'e_ab', kind: 'data', from: { node: 'n_a' }, to: { node: 'n_b' } },
  ],
  meta,
});

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

async function flushRoutes(): Promise<void> {
  await act(async () => {
    for (let i = 0; i < 10; i++) {
      await Promise.resolve();
    }
  });
}

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

/** Halfway along a polyline by length, worked out here rather than by the code under test. */
function halfway(points: Point[]): Point {
  const lengths = points.slice(1).map((p, i) => Math.hypot(p.x - (points[i]?.x ?? 0), p.y - (points[i]?.y ?? 0)));
  let remaining = lengths.reduce((a, b) => a + b, 0) / 2;
  for (const [i, length] of lengths.entries()) {
    const from = points[i];
    const to = points[i + 1];
    if (from && to && remaining <= length) {
      return { x: from.x + ((to.x - from.x) * remaining) / length, y: from.y + ((to.y - from.y) * remaining) / length };
    }
    remaining -= length;
  }
  throw new Error('empty polyline');
}

let editor: DocumentEditor;
const labelChanges: [Id, string][] = [];

function setup() {
  function Editor() {
    editor = useDocumentEditor('doc_edge_label', document, '"dv1:loaded"');
    return (
      <DiagramCanvas
        document={editor.document}
        theme={diagramThemes.overpass}
        size={size}
        fitKey={document}
        routingBackend={inProcessRouting}
        onMoveNodes={editor.moveNodes}
        onMoveEnd={editor.moveEnd}
        onLabelChange={(id, label) => {
          labelChanges.push([id, label]);
          editor.setLabel(id, label);
        }}
      />
    );
  }
  const result = render(<Editor />);
  const root = result.container;
  const canvas = root.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas) {
    throw new Error('canvas not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  const hitArea = (id: string): SVGElement => {
    const found = root.querySelector<SVGElement>(`g[data-id="${id}"] polyline.hit-area`);
    if (!found) {
      throw new Error(`no hit area for ${id}`);
    }
    return found;
  };
  let pointerId = 0;
  const click = (target: Element): void => {
    pointerId += 1;
    fireEvent.pointerDown(target, { pointerId, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId });
  };
  const doubleClick = (id: string): void => {
    click(hitArea(id));
    click(hitArea(id));
    fireEvent.doubleClick(hitArea(id));
  };
  const field = (): HTMLElement | null => root.querySelector<HTMLElement>('.label-editor');
  const openField = (): HTMLElement => {
    const found = field();
    if (!found) {
      throw new Error('label editor not open');
    }
    return found;
  };
  /** The world point the open field is centred on: the last translate of its frame's transform. */
  const fieldAt = (): Point => {
    const transform = openField().parentElement?.style.transform ?? '';
    const match = /translate\(([-\d.e]+)px, ([-\d.e]+)px\)$/.exec(transform);
    if (!match) {
      throw new Error(`unexpected transform ${transform}`);
    }
    return { x: Number(match[1]), y: Number(match[2]) };
  };
  /** The visible line the edge is drawn along now. */
  const drawn = (id: string): Point[] =>
    (root.querySelector(`g[data-id="${id}"] polyline:not(.hit-area)`)?.getAttribute('points') ?? '').split(' ').map((pair) => {
      const [x, y] = pair.split(',').map(Number);
      return { x: x ?? NaN, y: y ?? NaN };
    });
  const renderedLabel = (id: string): { text: string | null; at: Point } | undefined => {
    const text = root.querySelector(`g[data-id="${id}"] .edge-label text`);
    return text === null ? undefined : { text: text.textContent, at: { x: Number(text.getAttribute('x')), y: Number(text.getAttribute('y')) } };
  };
  /** The label's plate rect, in world units, straight from the DOM rather than recomputed. */
  const renderedLabelBox = (id: string): { x: number; y: number; w: number; h: number } | undefined => {
    const rect = root.querySelector(`g[data-id="${id}"] .edge-label rect`);
    return rect === null ? undefined : { x: Number(rect.getAttribute('x')), y: Number(rect.getAttribute('y')), w: Number(rect.getAttribute('width')), h: Number(rect.getAttribute('height')) };
  };
  return { ...result, canvas, hitArea, click, doubleClick, field, openField, fieldAt, drawn, renderedLabel, renderedLabelBox };
}

const edgeOf = (id: Id) => editor.document.edges.find((e) => e.id === id);
const move = (id: Id, geometry: Geometry): void => {
  act(() => {
    editor.moveNodes(new Map([[id, geometry]]));
    editor.moveEnd();
  });
};
const undo = (): void => {
  act(() => {
    editor.undo();
  });
};

describe('edge label editing', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    labelChanges.length = 0;
    // jsdom reports no focus while focus moves between elements; a real window the user is working in has it.
    vi.spyOn(globalThis.document, 'hasFocus').mockReturnValue(true);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('opens on a double-click on an edge with its label selected, centred on the routed midpoint, and hides the drawn label', async () => {
    const { doubleClick, field, openField, fieldAt, drawn, renderedLabel } = setup();
    flushFrame();
    await flushRoutes();
    expect(field()).toBeNull();
    expect(renderedLabel('e_ac')?.text).toBe('HTTPS');

    doubleClick('e_ac');
    const input = openField();
    expect(input.getAttribute('data-editing-id')).toBe('e_ac');
    expect(input.textContent).toBe('HTTPS');
    expect(globalThis.document.activeElement).toBe(input);
    expect(globalThis.getSelection()?.toString()).toBe('HTTPS');
    expect(renderedLabel('e_ac')).toBeUndefined();

    // The route bends around n_b, so its midpoint is off the straight line between the ports; the field is there.
    const route = drawn('e_ac');
    expect(route.length).toBeGreaterThan(2);
    const straightMidpoint = { x: 250, y: 125 };
    expect(fieldAt()).not.toEqual(straightMidpoint);
    expect(fieldAt().x).toBeCloseTo(halfway(route).x, 6);
    expect(fieldAt().y).toBeCloseTo(halfway(route).y, 6);
  });

  it('opens empty on an unlabelled edge and writes the typed label to edge.label as one undo step', async () => {
    const { doubleClick, openField, field, renderedLabel } = setup();
    flushFrame();
    await flushRoutes();
    move('n_a', { x: 0, y: 110, w: 100, h: 50 });
    expect(edgeOf('e_ab')?.label).toBeUndefined();

    doubleClick('e_ab');
    expect(openField().textContent).toBe('');
    openField().textContent = 'read/write';
    fireEvent.keyDown(openField(), { key: 'Enter', ctrlKey: true });
    expect(field()).toBeNull();
    expect(labelChanges).toEqual([['e_ab', 'read/write']]);
    expect(edgeOf('e_ab')?.label).toBe('read/write');
    expect(renderedLabel('e_ab')?.text).toBe('read/write');

    // One undo takes back the label and only the label; the next takes back the move before it.
    undo();
    expect(edgeOf('e_ab')?.label).toBeUndefined();
    expect(editor.document.nodes[0]?.geometry?.y).toBe(110);
    undo();
    expect(editor.document.nodes[0]?.geometry?.y).toBe(100);
  });

  it('opens on Enter with one edge selected and writes the committed label to edge.label', () => {
    const { canvas, click, hitArea, openField, field } = setup();
    click(hitArea('e_ac'));
    expect(field()).toBeNull();
    fireEvent.keyDown(canvas, { key: 'Enter' });
    expect(openField().getAttribute('data-editing-id')).toBe('e_ac');
    expect(openField().textContent).toBe('HTTPS');
    openField().textContent = 'gRPC';
    fireEvent.keyDown(openField(), { key: 'Enter' });
    expect(field()).toBeNull();
    expect(labelChanges).toEqual([['e_ac', 'gRPC']]);
    expect(edgeOf('e_ac')?.label).toBe('gRPC');
  });

  it('commits on Enter alone, since an edge label is drawn on one line', () => {
    const { doubleClick, openField, field } = setup();
    doubleClick('e_ac');
    openField().textContent = 'gRPC';
    fireEvent.keyDown(openField(), { key: 'Enter' });
    expect(field()).toBeNull();
    expect(edgeOf('e_ac')?.label).toBe('gRPC');
  });

  it('commits when focus moves elsewhere in the page', () => {
    const { doubleClick, openField, field } = setup();
    doubleClick('e_ac');
    openField().textContent = 'AMQP';
    fireEvent.blur(openField());
    expect(field()).toBeNull();
    expect(edgeOf('e_ac')?.label).toBe('AMQP');
  });

  it('discards on Escape with nothing written and no undo step', () => {
    const { doubleClick, openField, field, renderedLabel } = setup();
    move('n_a', { x: 0, y: 110, w: 100, h: 50 });
    doubleClick('e_ac');
    openField().textContent = 'Thrown away';
    fireEvent.keyDown(openField(), { key: 'Escape' });
    expect(field()).toBeNull();
    expect(labelChanges).toEqual([]);
    expect(edgeOf('e_ac')?.label).toBe('HTTPS');
    expect(renderedLabel('e_ac')?.text).toBe('HTTPS');
    // The move is still the top of the stack: the discarded edit left nothing above it.
    undo();
    expect(editor.document.nodes[0]?.geometry?.y).toBe(100);
    expect(edgeOf('e_ac')?.label).toBe('HTTPS');
  });

  it('stays open and writes nothing when the window, not the page, takes focus', () => {
    const { doubleClick, openField, field } = setup();
    doubleClick('e_ac');
    const input = openField();
    input.textContent = 'Half typed';
    vi.spyOn(globalThis.document, 'hasFocus').mockReturnValue(false);
    fireEvent.blur(input);
    expect(field()).toBe(input);
    expect(labelChanges).toEqual([]);
    expect(edgeOf('e_ac')?.label).toBe('HTTPS');
    expect(editor.saveStatus).toBe('saved');
  });

  it('removes the label when it is cleared, and writes nothing when it is committed unchanged', () => {
    const { doubleClick, openField, renderedLabel } = setup();
    doubleClick('e_ac');
    fireEvent.keyDown(openField(), { key: 'Enter', metaKey: true });
    expect(labelChanges).toEqual([['e_ac', 'HTTPS']]);
    expect(editor.saveStatus).toBe('saved');

    doubleClick('e_ac');
    openField().textContent = '';
    fireEvent.keyDown(openField(), { key: 'Enter', metaKey: true });
    expect(edgeOf('e_ac')).not.toHaveProperty('label');
    expect(renderedLabel('e_ac')).toBeUndefined();
    expect(editor.saveStatus).toBe('unsaved');
  });

  it('keeps pasted and typed text to one line of plain text', () => {
    const { doubleClick, openField } = setup();
    doubleClick('e_ac');
    const input = openField();
    fireEvent.paste(input, { clipboardData: { getData: (type: string) => (type === 'text/plain' ? 'read\nwrite' : '<b>read</b><br>write') } });
    expect(input.innerHTML).toBe('read write');
    // Markup a browser put in some other way contributes only its text, and a break in it becomes a space.
    input.innerHTML = '<b>Bold</b><div>next</div>';
    fireEvent.keyDown(input, { key: 'Enter', metaKey: true });
    expect(edgeOf('e_ac')?.label).toBe('Bold next');
  });

  it('follows the edge while open, and the committed label follows it when it re-routes', async () => {
    const { doubleClick, openField, fieldAt, drawn, renderedLabel, renderedLabelBox } = setup();
    flushFrame();
    await flushRoutes();
    doubleClick('e_ac');
    const bent = fieldAt();

    // Another writer moves the obstacle out of the way while the editor is open: the edge re-routes straight.
    move('n_b', { ...obstacle, y: 300 });
    await flushRoutes();
    const straight = drawn('e_ac');
    expect(straight).toEqual([
      { x: 100, y: 125 },
      { x: 400, y: 125 },
    ]);
    expect(fieldAt()).toEqual({ x: 250, y: 125 });
    expect(fieldAt()).not.toEqual(bent);

    openField().textContent = 'HTTPS/2';
    fireEvent.keyDown(openField(), { key: 'Enter', metaKey: true });
    expect(renderedLabel('e_ac')).toEqual({ text: 'HTTPS/2', at: { x: 250, y: 125 } });

    // Moving the obstacle back re-routes the edge around it again, and the drawn label goes with the route.
    move('n_b', obstacle);
    await flushRoutes();
    const rerouted = drawn('e_ac');
    expect(rerouted.length).toBeGreaterThan(2);
    const label = renderedLabel('e_ac');
    expect(label?.at).not.toEqual({ x: 250, y: 125 });
    // The route keeps ROUTE_CLEARANCE (12) from n_b, more than half a label plate (10), so its raw arc-length midpoint
    // no longer puts the plate across n_b's box; the plate must still never cross it (M2 final Finding C, whose nudge
    // itself is pinned in labelLayout.test.ts).
    const rawMidpoint = halfway(rerouted);
    const obstacleBox = { x: obstacle.x, y: obstacle.y, w: obstacle.w, h: obstacle.h };
    expect(boxesOverlap(boxAt(rawMidpoint, 60, 20), obstacleBox)).toBe(false);
    const box = renderedLabelBox('e_ac');
    if (box === undefined) {
      throw new Error('label should be rendered');
    }
    expect(boxesOverlap(box, obstacleBox)).toBe(false);
    // Nothing about the route was written: only the label changed on the edge.
    expect(edgeOf('e_ac')).toEqual({ ...document.edges[0], label: 'HTTPS/2' });
  });
});
