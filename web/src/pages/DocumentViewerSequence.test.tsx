// @vitest-environment jsdom
/**
 * The sequence editor through the whole viewer (design/atlas/sequence-editor.md): the palette group, the connector
 * gesture, the connector panel, the Arrange commands and delete, each driven from the DOM and read back from what is
 * drawn, so the pieces (canvas, hook, palette) are tested as they are wired together.
 */
import './loadTolerance.ts';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { checkoutSequence, must } from '../model/sequenceFixture.ts';
import { sequenceScene } from '../model/sequenceScene.ts';
import type { Document } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };
const scene = sequenceScene(checkoutSequence());

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

function renderSequence(document: Document = checkoutSequence()) {
  const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} />);
  const canvas = must(view.container.querySelector<HTMLElement>('.diagram-canvas'));
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  /** A world point as the client point the canvas draws it at (the canvas fits the diagram on open). */
  const at = (world: { x: number; y: number }) => {
    const match = /translate\(([-\d.e]+)[ ,]+([-\d.e]+)\) scale\(([-\d.e]+)\)/.exec(view.container.querySelector('g.world')?.getAttribute('transform') ?? '');
    const [x, y, zoom] = [Number(match?.[1]), Number(match?.[2]), Number(match?.[3])];
    return { clientX: x + world.x * zoom, clientY: y + world.y * zoom };
  };
  const ids = (selector: string) => [...view.container.querySelectorAll(selector)].map((element) => element.getAttribute('data-id'));
  return { ...view, canvas, at, nodeIds: () => ids('g.nodes > g[data-id]'), edgeIds: () => ids('g.edges > g[data-id]') };
}

function openPalette(query: string): string[] {
  fireEvent.keyDown(window, { key: 'k', metaKey: true });
  fireEvent.change(screen.getByRole('combobox', { name: 'Search commands' }), { target: { value: query } });
  // The empty result is itself an option without a label, so only a command counts.
  return within(screen.getByRole('listbox', { name: 'Commands' })).queryAllByRole('option').flatMap((option) => option.getAttribute('aria-label') ?? []);
}

describe('DocumentViewer on a sequence diagram', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('offers one palette group, Sequence, with Participant and Actor and no free shapes', () => {
    renderSequence();
    fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
    const panel = screen.getByRole('region', { name: 'Shapes' });
    expect(within(panel).getAllByRole('button', { expanded: true }).map((header) => header.textContent)).toEqual(['Sequence']);
    expect(within(panel).getAllByRole('group').map((group) => group.getAttribute('aria-label'))).toEqual(['Sequence', 'Fragments and notes']);
    expect(within(panel).getAllByRole('button').filter((button) => button.classList.contains('shape-panel-glyph')).map((button) => button.getAttribute('aria-label'))).toEqual(['Participant', 'Actor']);
  });

  it('appends a lifeline at the right end on a palette click and opens its label for editing', () => {
    const view = renderSequence();
    fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
    fireEvent.click(within(screen.getByRole('region', { name: 'Shapes' })).getByRole('button', { name: 'Actor' }));
    const ids = view.nodeIds();
    expect(ids).toHaveLength(5);
    expect(ids.slice(0, 4)).toEqual(['p_user', 'p_web', 'p_api', 'p_pay']);
    const added = must(ids[4]);
    // The lifeline is drawn right of the last one, with no geometry stored for it.
    const lifelineX = (id: string) => Number(view.container.querySelector(`line.lifeline[data-lifeline="${id}"]`)?.getAttribute('x1'));
    expect(lifelineX(added)).toBeGreaterThan(lifelineX('p_pay'));
    expect(view.container.querySelector(`g[data-id="${added}"]`)?.getAttribute('data-kind')).toBe('actor');
    const field = must(view.container.querySelector('.label-editor'));
    field.textContent = 'Bank';
    // Plain Enter is a line break in a node label; Ctrl+Enter commits it.
    fireEvent.keyDown(field, { key: 'Enter', metaKey: true });
    expect(view.container.querySelector(`g[data-id="${added}"] .label`)?.textContent).toBe('Bank');
    expect(view.container.querySelector('.label-editor')).toBeNull();
  });

  // P3: a participant dragged out of the palette lands in the slot among the lifelines that the drop falls in.
  it('drops a dragged participant between the lifelines it lands between, as one undo step, and opens its label', () => {
    const view = renderSequence();
    fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
    const glyph = within(screen.getByRole('region', { name: 'Shapes' })).getByRole('button', { name: 'Participant' });
    const centre = (id: string): number => Number(scene.headers.find((header) => header.id === id)?.cx);
    const between = (centre('p_web') + centre('p_api')) / 2;
    fireEvent.pointerDown(glyph, { ...mouse, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: between, y: 200 }) });

    const ids = view.nodeIds();
    expect(ids).toHaveLength(5);
    const added = must(ids[2]);
    expect(ids.filter((id) => id !== added)).toEqual(['p_user', 'p_web', 'p_api', 'p_pay']);
    expect(view.container.querySelector('.label-editor')).not.toBeNull();

    // Nothing was stored for its position, and one undo takes the whole drop away.
    fireEvent.keyDown(must(view.container.querySelector('.label-editor')), { key: 'Escape' });
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(view.nodeIds()).toEqual(['p_user', 'p_web', 'p_api', 'p_pay']);
  });

  it('appends a participant dropped past the last lifeline, and one dropped before the first goes first', () => {
    for (const [x, expected] of [[scene.headers[3] === undefined ? 0 : scene.headers[3].cx + 400, 4], [-200, 0]] as const) {
      const view = renderSequence();
      fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
      fireEvent.pointerDown(within(screen.getByRole('region', { name: 'Shapes' })).getByRole('button', { name: 'Participant' }), { ...mouse, clientX: 10, clientY: 10 });
      fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x, y: 200 }) });
      const ids = view.nodeIds();
      expect(ids.indexOf(must(ids.find((id) => id !== null && !['p_user', 'p_web', 'p_api', 'p_pay'].includes(id))))).toBe(expected);
      view.unmount();
    }
  });

  it('lists Add participant and Add actor in the palette, and no align, distribute, tidy or routing command', () => {
    renderSequence();
    expect(openPalette('add')).toEqual(expect.arrayContaining(['Add participant', 'Add actor']));
    expect(openPalette('align')).toEqual([]);
    expect(openPalette('distribute')).toEqual([]);
    expect(openPalette('tidy')).toEqual([]);
    expect(openPalette('routing')).toEqual([]);
    // The canvas lists Duplicate for every document, but it cannot run here.
    expect(openPalette('duplicate')).toEqual(['Duplicate, Ctrl+D']);
    expect(screen.getByRole('option', { name: 'Duplicate, Ctrl+D' }).getAttribute('aria-disabled')).toBe('true');
    // Group and Ungroup, and the class and table member editors, are left out of a sequence diagram's registry altogether.
    expect(openPalette('group')).toEqual([]);
    expect(openPalette('edit attributes')).toEqual([]);
    expect(openPalette('edit operations')).toEqual([]);
    expect(openPalette('edit columns')).toEqual([]);
  });

  it('runs Add participant from the palette: a new lifeline, its label open', () => {
    const view = renderSequence();
    fireEvent.keyDown(window, { key: 'k', metaKey: true });
    const search = screen.getByRole('combobox', { name: 'Search commands' });
    fireEvent.change(search, { target: { value: 'add participant' } });
    fireEvent.keyDown(search, { key: 'Enter' });
    expect(view.nodeIds()).toHaveLength(5);
    expect(view.container.querySelector('.label-editor')).not.toBeNull();
  });

  it('inserts a sync message where the connector drag is released, selects it and opens its label above the line', () => {
    const view = renderSequence();
    fireEvent.click(screen.getByRole('button', { name: 'Connector' }));
    const web = must(scene.headers.find((header) => header.id === 'p_web')).cx;
    const pay = must(scene.headers.find((header) => header.id === 'p_pay')).cx;
    fireEvent.pointerDown(view.canvas, { ...mouse, ...view.at({ x: web, y: 200 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: pay, y: 250 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: pay, y: 250 }) });
    const ids = view.edgeIds();
    expect(ids).toHaveLength(8);
    // Released between m_4 (216) and m_5 (252): the fifth message.
    const created = must(ids[4]);
    expect(ids.filter((id) => id !== created)).toEqual(['m_1', 'm_2', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
    expect(view.container.querySelector(`g[data-id="${created}"]`)?.getAttribute('data-kind')).toBe('sync');
    expect(view.container.querySelector(`.selection-edge[data-selection-id="${created}"]`)).not.toBeNull();
    const field = must(view.container.querySelector('.edge-label-editor'));
    expect(field.getAttribute('data-editing-id')).toBe(created);
    // Later rows shift on the next render: the old fifth message is now drawn one row lower.
    const shape = view.container.querySelector(`g[data-id="m_5"] polyline:not(.hit-area), g[data-id="m_5"] path:not(.hit-area)`);
    expect(/(?:M |^)[-\d.]+,([-\d.]+)/.exec(shape?.getAttribute('d') ?? shape?.getAttribute('points') ?? '')?.[1]).toBe('288');
    field.textContent = 'ping';
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(view.container.querySelector(`g[data-id="${created}"] .edge-label`)?.textContent).toBe('ping');
  });

  it('makes a self message when the drag ends on the lifeline it began on', () => {
    const view = renderSequence();
    fireEvent.click(screen.getByRole('button', { name: 'Connector' }));
    const api = must(scene.headers.find((header) => header.id === 'p_api')).cx;
    fireEvent.pointerDown(view.canvas, { ...mouse, ...view.at({ x: api, y: 380 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: api, y: 380 }) });
    // A press and release in place is a self message at the end.
    const ids = view.edgeIds();
    expect(ids).toHaveLength(8);
    const created = must(ids[7]);
    const from = view.container.querySelector(`g[data-id="${created}"] polyline:not(.hit-area), g[data-id="${created}"] path:not(.hit-area)`);
    expect(from).not.toBeNull();
  });

  it('changes a message’s type from the Connector panel and offers no routing there', () => {
    const view = renderSequence();
    fireEvent.pointerDown(must(view.container.querySelector('g[data-id="m_5"]')), { ...mouse, ...view.at({ x: 400, y: 252 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 400, y: 252 }) });
    const panel = screen.getByRole('region', { name: /connector/i });
    const kind = within(panel).getByRole<HTMLSelectElement>('combobox', { name: 'Kind' });
    expect([...kind.options].map((option) => option.value)).toEqual(['sync', 'async', 'return']);
    expect(within(panel).queryByRole('combobox', { name: 'Routing' })).toBeNull();
    fireEvent.change(kind, { target: { value: 'return' } });
    expect(view.container.querySelector('g[data-id="m_5"]')?.getAttribute('data-kind')).toBe('return');
  });

  it('moves a selected message later and earlier with the Arrange keys, and the menu says so', () => {
    const view = renderSequence();
    fireEvent.pointerDown(must(view.container.querySelector('g[data-id="m_2"]')), { ...mouse, ...view.at({ x: 300, y: 144 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 300, y: 144 }) });
    fireEvent.keyDown(view.canvas, { key: ']', metaKey: true });
    expect(view.edgeIds()).toEqual(['m_1', 'm_3', 'm_2', 'm_4', 'm_5', 'm_6', 'm_7']);
    fireEvent.keyDown(view.canvas, { key: '[', metaKey: true });
    fireEvent.keyDown(view.canvas, { key: '[', metaKey: true });
    expect(view.edgeIds()).toEqual(['m_2', 'm_1', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
    const commands = openPalette('move');
    expect(commands.some((label) => label.startsWith('Move later'))).toBe(true);
    expect(commands.some((label) => label.startsWith('Move earlier'))).toBe(true);
    expect(openPalette('bring')).toEqual([]);
  });

  it('reorders a message by dragging it to a slot and a participant by dragging it sideways, each in one gesture', () => {
    const view = renderSequence();
    const m1 = must(view.container.querySelector('g[data-id="m_1"]'));
    fireEvent.pointerDown(m1, { ...mouse, ...view.at({ x: 150, y: 108 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 150, y: 260 }) });
    flushFrame();
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 150, y: 260 }) });
    flushFrame();
    expect(view.edgeIds()).toEqual(['m_2', 'm_3', 'm_4', 'm_5', 'm_1', 'm_6', 'm_7']);
    const user = must(view.container.querySelector('g[data-id="p_user"]'));
    fireEvent.pointerDown(user, { ...mouse, ...view.at({ x: 80, y: 40 }) });
    fireEvent.pointerMove(view.canvas, { ...mouse, ...view.at({ x: 300, y: 40 }) });
    flushFrame();
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 300, y: 40 }) });
    flushFrame();
    expect(view.nodeIds()).toEqual(['p_web', 'p_user', 'p_api', 'p_pay']);
    // One undo per gesture: the participant move first, then the message move.
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(view.nodeIds()).toEqual(['p_user', 'p_web', 'p_api', 'p_pay']);
    expect(view.edgeIds()).toEqual(['m_2', 'm_3', 'm_4', 'm_5', 'm_1', 'm_6', 'm_7']);
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(view.edgeIds()).toEqual(['m_1', 'm_2', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
  });

  it('deleting a participant removes its messages, and one undo brings them all back', () => {
    const view = renderSequence();
    fireEvent.pointerDown(must(view.container.querySelector('g[data-id="p_api"]')), { ...mouse, ...view.at({ x: 360, y: 40 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 360, y: 40 }) });
    fireEvent.keyDown(view.canvas, { key: 'Delete' });
    expect(view.nodeIds()).toEqual(['p_user', 'p_web', 'p_pay']);
    // Only m_1 (user to web) did not touch the API.
    expect(view.edgeIds()).toEqual(['m_1']);
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(view.nodeIds()).toHaveLength(4);
    expect(view.edgeIds()).toHaveLength(7);
  });

  it('shows the properties of a selected participant without the align and distribute controls or the container panel', () => {
    const view = renderSequence();
    fireEvent.pointerDown(must(view.container.querySelector('g[data-id="p_api"]')), { ...mouse, ...view.at({ x: 360, y: 40 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 360, y: 40 }) });
    expect(screen.queryByRole('group', { name: 'Align' })).toBeNull();
    expect(screen.queryByRole('group', { name: 'Distribute' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Move later' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /group/i })).toBeNull();
  });
});
