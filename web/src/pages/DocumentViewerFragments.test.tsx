// @vitest-environment jsdom
/**
 * Milestone 2 of the sequence editor through the whole viewer (design/atlas/sequence-fragments.md): Wrap in fragment,
 * Add operand, Add note, Toggle activation and the guard editor, each driven from the palette and the DOM and read back
 * from what is drawn, so the canvas, the hook and the registry are tested as they are wired together.
 */
import './loadTolerance.ts';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { checkoutFragments, checkoutSequence, must } from '../model/sequenceFixture.ts';
import type { Document } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };

function renderSequence(document: Document) {
  const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} />);
  const canvas = must(view.container.querySelector<HTMLElement>('.diagram-canvas'));
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  const at = (world: { x: number; y: number }) => {
    const match = /translate\(([-\d.e]+)[ ,]+([-\d.e]+)\) scale\(([-\d.e]+)\)/.exec(view.container.querySelector('g.world')?.getAttribute('transform') ?? '');
    const [x, y, zoom] = [Number(match?.[1]), Number(match?.[2]), Number(match?.[3])];
    return { clientX: x + world.x * zoom, clientY: y + world.y * zoom };
  };
  /** A press and release on the message drawn `id`, which selects it; `shift` adds it to the selection. */
  const select = (id: string, world: { x: number; y: number }, shift = false) => {
    fireEvent.pointerDown(must(view.container.querySelector(`g[data-id="${id}"]`)), { ...mouse, shiftKey: shift, ...at(world) });
    fireEvent.pointerUp(canvas, { ...mouse, shiftKey: shift, ...at(world) });
  };
  return { ...view, canvas, at, select };
}

/** The palette's commands for `query`, with whether each is enabled. */
function palette(query: string): [string, boolean][] {
  fireEvent.keyDown(window, { key: 'k', metaKey: true });
  fireEvent.change(screen.getByRole('combobox', { name: 'Search commands' }), { target: { value: query } });
  return within(screen.getByRole('listbox', { name: 'Commands' }))
    .queryAllByRole('option')
    .flatMap((option) => (option.getAttribute('aria-label') === null ? [] : [[option.getAttribute('aria-label') ?? '', option.getAttribute('aria-disabled') !== 'true'] as [string, boolean]]));
}

function runFromPalette(query: string): void {
  fireEvent.keyDown(window, { key: 'k', metaKey: true });
  const search = screen.getByRole('combobox', { name: 'Search commands' });
  fireEvent.change(search, { target: { value: query } });
  fireEvent.keyDown(search, { key: 'Enter' });
}

/** Whether Wrap in fragment: alt is enabled right now. */
const wrapAlt = (): boolean => {
  const enabled = palette('wrap in fragment: alt').find(([label]) => label === 'Wrap in fragment: alt')?.[1] ?? false;
  fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Escape' });
  return enabled;
};

const guardTexts = (container: HTMLElement, id: string) => [...container.querySelectorAll(`g.node[data-id="${id}"] text.guard`)].map((text) => text.textContent);

describe('DocumentViewer: sequence fragments, notes and activation', () => {
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

  it('lists every command in the palette for a sequence diagram, disabled until a selection makes it apply', () => {
    renderSequence(checkoutSequence());
    // The palette lists a group at a time (Edit before Insert); each of the nine is there and none applies yet.
    expect(palette('fragment').sort()).toEqual([
      ['Add operand to fragment', false],
      ['Set fragment operator: alt', false],
      ['Set fragment operator: loop', false],
      ['Set fragment operator: opt', false],
      ['Set fragment operator: par', false],
      ['Wrap in fragment: alt', false],
      ['Wrap in fragment: loop', false],
      ['Wrap in fragment: opt', false],
      ['Wrap in fragment: par', false],
    ]);
    expect(palette('note')).toEqual([['Add note', true]]);
    expect(palette('activation')).toEqual([['Toggle activation', false]]);
  });

  it('offers none of them for a document that is not a sequence diagram', () => {
    renderSequence({ ...checkoutSequence(), type: 'freeform', nodes: [], edges: [] });
    expect(palette('fragment')).toEqual([]);
    expect(palette('activation')).toEqual([]);
    expect(palette('add note')).toEqual([]);
  });

  it('wraps the selected messages in a frame, selects it and opens its guard, which commits into the frame', () => {
    const view = renderSequence(checkoutSequence());
    view.select('m_3', { x: 430, y: 180 });
    expect(wrapAlt()).toBe(true);
    runFromPalette('wrap in fragment: alt');

    const frame = must(view.container.querySelector('g.node[data-id="f_1"]'));
    expect(frame.querySelector('text.operator')?.textContent).toBe('alt');
    expect(guardTexts(view.container, 'f_1')).toEqual([]);
    const field = must(view.container.querySelector<HTMLElement>('.label-editor[aria-label="Fragment guard"]'));
    expect(field.textContent).toBe('[condition]');
    field.textContent = '[card ok]';
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(guardTexts(view.container, 'f_1')).toEqual(['[card ok]']);
    expect(view.container.querySelector('.label-editor')).toBeNull();
    // m_3 is drawn a guard row lower than it was (180 becomes 202): the frame took 22 above it.
    expect(view.container.querySelector('g.edges > g[data-id="m_3"] polyline')?.getAttribute('points')).toContain(',202');
  });

  it('refuses a selection that is not next to each other, and grows the enabled state with Shift-click', () => {
    const view = renderSequence(checkoutSequence());
    view.select('m_2', { x: 290, y: 144 });
    view.select('m_4', { x: 430, y: 216 }, true);
    expect(wrapAlt()).toBe(false);
    view.select('m_3', { x: 430, y: 180 }, true);
    expect(wrapAlt()).toBe(true);
  });

  it('adds an operand to the alt at the selected message and opens the new guard', () => {
    const view = renderSequence(checkoutFragments());
    view.select('m_4', { x: 430, y: 274 });
    expect(palette('add operand')).toEqual([['Add operand to fragment', true]]);
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Enter' });

    // The alt split at m_4: [card ok], [else], [declined]; the new [else] guard is open.
    const field = must(view.container.querySelector<HTMLElement>('.label-editor[aria-label="Fragment guard"]'));
    expect(field.textContent).toBe('[else]');
    expect(guardTexts(view.container, 'f_1')).toEqual(['[card ok]', '[declined]']);
    field.textContent = '[3-D Secure]';
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(guardTexts(view.container, 'f_1')).toEqual(['[card ok]', '[3-D Secure]', '[declined]']);
    expect(view.container.querySelectorAll('g.node[data-id="f_1"] line.divider')).toHaveLength(2);
  });

  it('adds an operand from the selected frame itself, at its end', () => {
    const document = checkoutFragments();
    const last = must(document.edges.find((edge) => edge.id === 'm_6'));
    last.props = { ...last.props, in: 'f_1', operand: 1 };
    const view = renderSequence(document);
    // Nothing selected: the command does not apply.
    expect(palette('add operand')).toEqual([['Add operand to fragment', false]]);
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Escape' });
    view.select('f_1', { x: 300, y: 240 });
    expect(palette('add operand')).toEqual([['Add operand to fragment', true]]);
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Enter' });
    const field = must(view.container.querySelector<HTMLElement>('.label-editor[aria-label="Fragment guard"]'));
    expect(field.textContent).toBe('[else]');
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(guardTexts(view.container, 'f_1')).toEqual(['[card ok]', '[declined]', '[else]']);
  });

  it('offers the fragment, operand and note commands as labelled buttons in the Shapes panel, enabled like the menu', () => {
    const view = renderSequence(checkoutSequence());
    fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
    const panel = screen.getByRole('region', { name: 'Shapes' });
    const button = (name: RegExp) => within(panel).getByRole<HTMLButtonElement>('button', { name });
    // Visible text, not an icon alone; wrapping needs messages selected, a note does not.
    expect(button(/^Fragment: alt/).textContent).toContain('Select the messages to wrap');
    expect(button(/^Fragment: alt/).disabled).toBe(true);
    expect(button(/^Note/).disabled).toBe(false);
    view.select('m_3', { x: 430, y: 180 });
    expect(button(/^Fragment: alt/).disabled).toBe(false);
    fireEvent.click(button(/^Fragment: alt/));
    expect(view.container.querySelector('g.node[data-id="f_1"] text.operator')?.textContent).toBe('alt');
  });

  it('offers no such buttons in the Shapes panel of a freeform document', () => {
    renderSequence({ ...checkoutSequence(), type: 'freeform', nodes: [], edges: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
    expect(within(screen.getByRole('region', { name: 'Shapes' })).queryByRole('button', { name: /^Fragment: alt/ })).toBeNull();
  });

  it('adds a note after the selected message and opens its text', () => {
    const view = renderSequence(checkoutSequence());
    view.select('m_2', { x: 290, y: 144 });
    runFromPalette('add note');
    const note = must(view.container.querySelector('g.edge.note'));
    expect(note.getAttribute('data-kind')).toBe('note');
    const field = must(view.container.querySelector<HTMLElement>('.label-editor[aria-label="Connector label"]'));
    field.textContent = 'retry safe';
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(view.container.querySelector('g.edge.note')?.textContent).toBe('retry safe');
    // It sits right after m_2: the box is the third row (162-198), 4 down, and m_3 has moved down a row.
    expect(view.container.querySelector('g.edge.note path.note-box')?.getAttribute('d')).toContain('166');
  });

  it('toggles the activation bar a selected message opens, on and off', () => {
    const view = renderSequence(checkoutSequence());
    const bars = () => [...view.container.querySelectorAll('rect.activation')].map((bar) => bar.getAttribute('data-lifeline'));
    expect(bars()).toEqual(['p_api', 'p_pay']);
    view.select('m_1', { x: 150, y: 108 });
    runFromPalette('toggle activation');
    expect(bars().sort()).toEqual(['p_api', 'p_pay', 'p_web']);
    runFromPalette('toggle activation');
    expect(bars()).toEqual(['p_api', 'p_pay']);
  });

  it('opens the guard under the pointer on a double-click on the frame, and Enter opens the first', () => {
    const view = renderSequence(checkoutFragments());
    // The border of the frame is its hit target; a double-click on it (the second operand's row) edits that guard.
    fireEvent.pointerDown(must(view.container.querySelector('g.node[data-id="f_1"]')), { ...mouse, ...view.at({ x: 200, y: 300 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 200, y: 300 }) });
    fireEvent.doubleClick(must(view.container.querySelector('g.node[data-id="f_1"]')), view.at({ x: 200, y: 300 }));
    expect(must(view.container.querySelector<HTMLElement>('.label-editor[aria-label="Fragment guard"]')).textContent).toBe('[declined]');
    fireEvent.keyDown(must(view.container.querySelector('.label-editor')), { key: 'Escape' });
    expect(view.container.querySelector('.label-editor')).toBeNull();
    // Escape discarded it: the guard is as it was.
    expect(guardTexts(view.container, 'f_1')).toEqual(['[card ok]', '[declined]']);
    act(() => {
      view.canvas.focus();
    });
    fireEvent.keyDown(view.canvas, { key: 'Enter' });
    expect(must(view.container.querySelector<HTMLElement>('.label-editor[aria-label="Fragment guard"]')).textContent).toBe('[card ok]');
  });

  it('changes a selected frame’s operator from the palette', () => {
    const view = renderSequence(checkoutFragments());
    fireEvent.pointerDown(must(view.container.querySelector('g.node[data-id="f_1"]')), { ...mouse, ...view.at({ x: 200, y: 300 }) });
    fireEvent.pointerUp(view.canvas, { ...mouse, ...view.at({ x: 200, y: 300 }) });
    // Only the operators it is not already are on offer.
    expect(palette('set fragment operator').sort()).toEqual([
      ['Set fragment operator: alt', false],
      ['Set fragment operator: loop', true],
      ['Set fragment operator: opt', true],
      ['Set fragment operator: par', true],
    ]);
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Escape' });
    palette('set fragment operator');
    fireEvent.click(screen.getByRole('option', { name: 'Set fragment operator: par' }));
    expect(view.container.querySelector('g.node[data-id="f_1"] text.operator')?.textContent).toBe('par');
  });
});
