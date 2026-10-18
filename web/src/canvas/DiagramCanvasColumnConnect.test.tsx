// @vitest-environment jsdom
/**
 * Connecting to a column row of a database table (model/columnPorts.ts): in the connector tool a pointer over an
 * entity's rows highlights the row under it, and a press or release there attaches to that row instead of the table;
 * dragging a selected connector's end onto another row re-attaches it. A touch or pen press takes the same path.
 */
import { createRef } from 'react';
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Node, Point } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import { worldToScreen } from './viewport.ts';

const size = { width: 1000, height: 600 };
const meta = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' };

// Row centres are 42, 60 and 78 below a table's top; the name band is the top 28.
const order: Node = { id: 't_order', kind: 'entity', label: 'Order', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'FK CustomerId int not null', 'Placed datetime2 not null'] } };
const customer: Node = { id: 't_customer', kind: 'entity', label: 'Customer', shape: 'custom:er/entity', geometry: { x: 500, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'Name nvarchar(100) not null', 'Email nvarchar(100) null'] } };
const tables: Document = parseDocument({ id: 'doc_cols', schemaVersion: 1, title: 'Cols', type: 'architecture', nodes: [order, customer], edges: [], meta });
const linked: Document = { ...tables, edges: [{ id: 'e_fk', kind: 'association', from: { node: 't_order', port: 'col:CustomerId' }, to: { node: 't_customer', port: 'col:Id' }, routing: { mode: 'straight' } }] };

const noRouting = (): undefined => undefined;
const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };
const touch = { pointerId: 2, button: 0, pointerType: 'touch', isPrimary: true };

function setup(props: Partial<Parameters<typeof DiagramCanvas>[0]> = {}, document: Document = tables) {
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
  const at = (world: Point) => {
    const screen = worldToScreen(current.getViewport(), world);
    return { clientX: screen.x, clientY: screen.y };
  };
  return { ...result, canvas, at, handle: current };
}

const highlighted = (container: HTMLElement): (string | null)[] => [...container.querySelectorAll('.connector-column-row')].map((row) => row.getAttribute('data-port-id'));

describe('DiagramCanvas column connectors', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('highlights the row under the pointer in the connector tool, and none over the name band', () => {
    const { container, canvas, at } = setup({ tool: 'connector' });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 100, y: 60 }) });
    expect(highlighted(container)).toEqual(['col:CustomerId']);
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 100, y: 78 }) });
    expect(highlighted(container)).toEqual(['col:Placed']);
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 100, y: 10 }) });
    expect(highlighted(container)).toEqual([]);
  });

  it('connects the FK row of one table to the key row of another', () => {
    const onConnect = vi.fn();
    const { container, canvas, at } = setup({ tool: 'connector', onConnect });
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 200, y: 60 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 600, y: 42 }) });
    // Mid-gesture the target row is the one highlighted.
    expect(highlighted(container)).toContain('col:Id');
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 600, y: 42 }) });
    expect(onConnect).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 't_order', port: { id: 'col:CustomerId', x: 1, y: 60 / 92 } },
      to: { node: 't_customer', port: { id: 'col:Id', x: 0, y: 42 / 92 } },
    });
  });

  it('connects the same way for a touch press', () => {
    const onConnect = vi.fn();
    const { canvas, at } = setup({ tool: 'connector', onConnect });
    fireEvent.pointerDown(canvas, { ...touch, ...at({ x: 200, y: 60 }) });
    fireEvent.pointerMove(canvas, { ...touch, ...at({ x: 600, y: 78 }) });
    fireEvent.pointerUp(canvas, { ...touch, ...at({ x: 600, y: 78 }) });
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 't_order', port: expect.objectContaining({ id: 'col:CustomerId' }) as unknown },
      to: { node: 't_customer', port: expect.objectContaining({ id: 'col:Email' }) as unknown },
    });
  });

  it('attaches a release over the name band to a table port, not a row', () => {
    const onConnect = vi.fn();
    const { canvas, at } = setup({ tool: 'connector', onConnect });
    fireEvent.pointerDown(canvas, { ...mouse, ...at({ x: 200, y: 60 }) });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 635, y: 5 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 635, y: 5 }) });
    expect(onConnect).toHaveBeenCalledWith({
      from: { node: 't_order', port: expect.objectContaining({ id: 'col:CustomerId' }) as unknown },
      to: { node: 't_customer', port: { id: 'top', x: 0.5, y: 0 } },
    });
  });

  it('re-attaches a selected connector’s end onto another row', () => {
    const onReconnect = vi.fn();
    const { container, canvas, at, handle } = setup({ onConnect: vi.fn(), onReconnect }, linked);
    act(() => {
      handle.select(new Set(['e_fk']));
    });
    const drawn = container.querySelector('.edge-endpoint[data-edge-id="e_fk"][data-edge-end="to"]');
    expect(drawn).not.toBeNull();
    const press = { clientX: Number(drawn?.getAttribute('cx')), clientY: Number(drawn?.getAttribute('cy')) };
    fireEvent.pointerDown(canvas, { ...mouse, ...press });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 600, y: 78 }) });
    expect(highlighted(container)).toContain('col:Email');
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 600, y: 78 }) });
    expect(onReconnect).toHaveBeenCalledWith({ edge: 'e_fk', end: 'to', to: { node: 't_customer', port: expect.objectContaining({ id: 'col:Email' }) as unknown } });
  });

  it('re-attaches nothing when the end is released back on the row it is already on', () => {
    const onReconnect = vi.fn();
    const { container, canvas, at, handle } = setup({ onConnect: vi.fn(), onReconnect }, linked);
    act(() => {
      handle.select(new Set(['e_fk']));
    });
    const drawn = container.querySelector('.edge-endpoint[data-edge-id="e_fk"][data-edge-end="to"]');
    const press = { clientX: Number(drawn?.getAttribute('cx')), clientY: Number(drawn?.getAttribute('cy')) };
    fireEvent.pointerDown(canvas, { ...mouse, ...press });
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 600, y: 42 }) });
    fireEvent.pointerUp(canvas, { ...mouse, ...at({ x: 600, y: 42 }) });
    expect(onReconnect).not.toHaveBeenCalled();
  });

  it('never offers a row on a node that is not a table', () => {
    const plain = parseDocument({ id: 'doc_plain', schemaVersion: 1, title: 'Plain', type: 'architecture', nodes: [{ ...order, shape: 'rectangle' }], edges: [], meta });
    const { container, canvas, at } = setup({ tool: 'connector' }, plain);
    fireEvent.pointerMove(canvas, { ...mouse, ...at({ x: 100, y: 60 }) });
    expect(highlighted(container)).toEqual([]);
  });
});
