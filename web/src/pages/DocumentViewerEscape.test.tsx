// @vitest-environment jsdom
/**
 * Escape cancels a drag in progress (canvas/DiagramCanvas.tsx module comment): a node move, a resize, a rotate, and a
 * sequence participant or message reorder each end with the model as the press found it, pointer capture released, the
 * insertion line and snap guides gone, no undo entry, no redo entry, nothing unsaved, and the release that follows
 * doing nothing. Each is driven through the whole viewer from the DOM and read back from what is drawn.
 */
import './loadTolerance.ts';
import { act, fireEvent, render, screen } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { parseDocument } from '../model/parse.ts';
import { checkoutSequence, must } from '../model/sequenceFixture.ts';
import type { Document } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };
const architecture = parseDocument(orderService);

// Two ER entities, the shape the Escape rule must hold for as it does for a plain box (a table is one node with rows).
const entities: Document = parseDocument({
  id: 'doc_er',
  schemaVersion: 1,
  title: 'Shop',
  type: 'architecture',
  nodes: [
    { id: 't_users', kind: 'entity', label: 'Users', shape: 'custom:er/entity', geometry: { x: 40, y: 40, w: 264, h: 80 }, props: { columns: ['PK Id int not null', 'Name text'] } },
    { id: 't_orders', kind: 'entity', label: 'Orders', shape: 'custom:er/entity', geometry: { x: 500, y: 40, w: 264, h: 80 }, props: { columns: ['PK Id int not null', 'FK UserId int'] } },
  ],
  edges: [],
  meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
});

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

function renderEditor(document: Document) {
  const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} />);
  const canvas = must(view.container.querySelector<HTMLElement>('.diagram-canvas'));
  // A pointer capture that is really held, so the release Escape must make is observable.
  const released: number[] = [];
  let held = false;
  canvas.setPointerCapture = () => { held = true; };
  canvas.hasPointerCapture = () => held;
  canvas.releasePointerCapture = (id: number) => { held = false; released.push(id); };
  const at = (world: { x: number; y: number }) => {
    const match = /translate\(([-\d.e]+)[ ,]+([-\d.e]+)\) scale\(([-\d.e]+)\)/.exec(view.container.querySelector('g.world')?.getAttribute('transform') ?? '');
    const [x, y, zoom] = [Number(match?.[1]), Number(match?.[2]), Number(match?.[3])];
    return { clientX: x + world.x * zoom, clientY: y + world.y * zoom };
  };
  const ids = (selector: string) => [...view.container.querySelectorAll(selector)].map((element) => element.getAttribute('data-id'));
  return { ...view, canvas, at, released, nodeIds: () => ids('g.nodes > g[data-id]'), edgeIds: () => ids('g.edges > g[data-id]') };
}

const status = () => screen.getByText(/^(Saved|Saving…|Unsaved changes|Not saved)$/).textContent;
const undoDisabled = () => screen.getByRole('button', { name: 'Undo' }).getAttribute('aria-disabled');
const redoDisabled = () => screen.getByRole('button', { name: 'Redo' }).getAttribute('aria-disabled');
const drawing = (container: HTMLElement, id: string) => container.querySelector(`g[data-id="${id}"]`)?.outerHTML;

/** What "nothing happened" is: saved, and neither Undo nor Redo has anything. */
function expectUntouched(): void {
  expect(status()).toBe('Saved');
  expect(undoDisabled()).toBe('true');
  expect(redoDisabled()).toBe('true');
}

describe('Escape cancels a drag in progress', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('a node move: the node goes back, and releasing afterwards commits nothing', () => {
    const view = renderEditor(architecture);
    const shape = must(view.container.querySelector('g[data-id="n_web"] .shape'));
    const before = drawing(view.container, 'n_web');
    fireEvent.pointerDown(shape, { ...mouse, clientX: 100, clientY: 100 });
    for (let i = 1; i <= 4; i++) {
      fireEvent.pointerMove(view.canvas, { pointerId: 1, clientX: 100 + i * 10, clientY: 100 + i * 5 });
      flushFrame();
    }
    // Mid-drag the node has moved and the document is dirty.
    expect(drawing(view.container, 'n_web')).not.toBe(before);
    expect(status()).toBe('Unsaved changes');

    fireEvent.keyDown(view.canvas, { key: 'Escape' });
    expect(drawing(view.container, 'n_web')).toBe(before);
    expect(view.released).toEqual([1]);
    // The pointer keeps moving and then lets go: neither writes anything.
    fireEvent.pointerMove(view.canvas, { pointerId: 1, clientX: 300, clientY: 300 });
    flushFrame();
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    flushFrame();
    expect(drawing(view.container, 'n_web')).toBe(before);
    expect(view.container.querySelector('.snap-guide')).toBeNull();
    expectUntouched();
  });

  it('a move that already has an undo step behind it takes back only itself', () => {
    const view = renderEditor(architecture);
    const shape = must(view.container.querySelector('g[data-id="n_web"] .shape'));
    fireEvent.pointerDown(shape, { ...mouse, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    fireEvent.keyDown(view.canvas, { key: 'ArrowRight' });
    const nudged = drawing(view.container, 'n_web');

    fireEvent.pointerDown(shape, { ...mouse, clientX: 100, clientY: 100 });
    for (let i = 1; i <= 3; i++) {
      fireEvent.pointerMove(view.canvas, { pointerId: 1, clientX: 100 + i * 20, clientY: 100 });
      flushFrame();
    }
    fireEvent.keyDown(view.canvas, { key: 'Escape' });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    expect(drawing(view.container, 'n_web')).toBe(nudged);
    // The nudge is still one undo step and there is nothing to redo.
    expect(undoDisabled()).toBe('false');
    expect(redoDisabled()).toBe('true');
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(view.container.querySelector('g[data-id="n_web"] > g')?.getAttribute('transform')).toBe('translate(40 40)');
    expect(undoDisabled()).toBe('true');
  });

  it('an ER table move', () => {
    const view = renderEditor(entities);
    const shape = must(view.container.querySelector('g[data-id="t_users"] .shape'));
    const before = drawing(view.container, 't_users');
    fireEvent.pointerDown(shape, { ...mouse, clientX: 100, clientY: 100 });
    for (let i = 1; i <= 4; i++) {
      fireEvent.pointerMove(view.canvas, { pointerId: 1, clientX: 100 + i * 25, clientY: 100 + i * 10 });
      flushFrame();
    }
    expect(drawing(view.container, 't_users')).not.toBe(before);
    fireEvent.keyDown(view.canvas, { key: 'Escape' });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    flushFrame();
    expect(drawing(view.container, 't_users')).toBe(before);
    expectUntouched();
  });

  it('a resize', () => {
    const view = renderEditor(architecture);
    fireEvent.pointerDown(must(view.container.querySelector('g[data-id="n_web"] .shape')), { ...mouse, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    const before = drawing(view.container, 'n_web');
    view.released.length = 0; // the selecting click released its own capture
    const corner = must(view.container.querySelector('.selection-node[data-selection-id="n_web"] [data-handle="4"]'));
    fireEvent.pointerDown(corner, { ...mouse, clientX: 500, clientY: 500 });
    for (let i = 1; i <= 4; i++) {
      fireEvent.pointerMove(view.canvas, { pointerId: 1, clientX: 500 + i * 10, clientY: 500 + i * 10 });
      flushFrame();
    }
    expect(drawing(view.container, 'n_web')).not.toBe(before);
    fireEvent.keyDown(view.canvas, { key: 'Escape' });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    flushFrame();
    expect(drawing(view.container, 'n_web')).toBe(before);
    expect(view.released).toEqual([1]);
    expectUntouched();
  });

  it('a rotate, taking the angle readout away with it', () => {
    const view = renderEditor(architecture);
    fireEvent.pointerDown(must(view.container.querySelector('g[data-id="n_web"] .shape')), { ...mouse, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    const before = drawing(view.container, 'n_web');
    const outline = must(view.container.querySelector('.selection-node[data-selection-id="n_web"] .selection-outline'));
    const handle = must(view.container.querySelector('.selection-node[data-selection-id="n_web"] [data-rotate-handle]'));
    const centreX = Number(outline.getAttribute('x')) + Number(outline.getAttribute('width')) / 2;
    const centreY = Number(outline.getAttribute('y')) + Number(outline.getAttribute('height')) / 2;
    const handleY = Number(handle.getAttribute('cy'));
    fireEvent.pointerDown(handle, { ...mouse, clientX: centreX, clientY: handleY });
    for (let i = 1; i <= 4; i++) {
      const angle = (i / 4) * (Math.PI / 2);
      const r = centreY - handleY;
      fireEvent.pointerMove(view.canvas, { pointerId: 1, clientX: centreX + r * Math.sin(angle), clientY: centreY - r * Math.cos(angle) });
      flushFrame();
    }
    expect(drawing(view.container, 'n_web')).toContain('rotate(90');
    fireEvent.keyDown(view.canvas, { key: 'Escape' });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    flushFrame();
    expect(drawing(view.container, 'n_web')).toBe(before);
    expectUntouched();
  });

  it('a press that has not travelled yet ends with nothing to undo and the release does not click', () => {
    const view = renderEditor(architecture);
    const shape = must(view.container.querySelector('g[data-id="n_web"] .shape'));
    fireEvent.pointerDown(shape, { ...mouse, clientX: 100, clientY: 100 });
    fireEvent.keyDown(view.canvas, { key: 'Escape' });
    fireEvent.pointerUp(view.canvas, { pointerId: 1 });
    expect(view.released).toEqual([1]);
    expectUntouched();
  });

  describe('in a sequence diagram, where a drag writes nothing until release', () => {
    it('a participant reorder: the insertion line goes and the release does not reorder', () => {
      const view = renderEditor(checkoutSequence());
      const before = view.nodeIds();
      const user = must(view.container.querySelector('g[data-id="p_user"]'));
      fireEvent.pointerDown(user, { ...mouse, ...view.at({ x: 80, y: 40 }) });
      fireEvent.pointerMove(view.canvas, { pointerId: 1, ...view.at({ x: 300, y: 40 }) });
      flushFrame();
      expect(view.container.querySelector('[data-guide="edge"]')).not.toBeNull();

      fireEvent.keyDown(view.canvas, { key: 'Escape' });
      expect(view.container.querySelector('[data-guide="edge"]')).toBeNull();
      expect(view.released).toEqual([1]);
      fireEvent.pointerUp(view.canvas, { pointerId: 1, ...view.at({ x: 300, y: 40 }) });
      flushFrame();
      expect(view.nodeIds()).toEqual(before);
      expectUntouched();
    });

    it('a message reorder: the insertion line goes and the release does not reorder', () => {
      const view = renderEditor(checkoutSequence());
      const before = view.edgeIds();
      const m1 = must(view.container.querySelector('g[data-id="m_1"]'));
      fireEvent.pointerDown(m1, { ...mouse, ...view.at({ x: 150, y: 108 }) });
      fireEvent.pointerMove(view.canvas, { pointerId: 1, ...view.at({ x: 150, y: 260 }) });
      flushFrame();
      expect(view.container.querySelector('[data-guide="edge"]')).not.toBeNull();

      fireEvent.keyDown(view.canvas, { key: 'Escape' });
      expect(view.container.querySelector('[data-guide="edge"]')).toBeNull();
      fireEvent.pointerUp(view.canvas, { pointerId: 1, ...view.at({ x: 150, y: 260 }) });
      flushFrame();
      expect(view.edgeIds()).toEqual(before);
      expectUntouched();
    });

    it('still reorders when released without Escape (the control for the two above)', () => {
      const view = renderEditor(checkoutSequence());
      const m1 = must(view.container.querySelector('g[data-id="m_1"]'));
      fireEvent.pointerDown(m1, { ...mouse, ...view.at({ x: 150, y: 108 }) });
      fireEvent.pointerMove(view.canvas, { pointerId: 1, ...view.at({ x: 150, y: 260 }) });
      flushFrame();
      fireEvent.pointerUp(view.canvas, { pointerId: 1, ...view.at({ x: 150, y: 260 }) });
      flushFrame();
      expect(view.edgeIds()).toEqual(['m_2', 'm_3', 'm_4', 'm_5', 'm_1', 'm_6', 'm_7']);
      expect(status()).toBe('Unsaved changes');
    });
  });
});
