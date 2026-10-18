// @vitest-environment jsdom
/**
 * The Text pane through the whole editor (design/atlas/properties-panel.md, "Text pane"): the toolbar button and View menu
 * entry that open it, the open state remembered per viewer, a line of the text marked for the canvas selection and a click on
 * a line selecting its element, Apply as one undo step, and unapplied text surviving a change made on the canvas meanwhile.
 */
import './loadTolerance.ts';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import dsl from '../../../docs/examples/order-service.dsl?raw';
import { TEXT_PANE_KEY } from '../editor/textPanePrefs.ts';
import { parseDocument } from '../model/parse.ts';
import { must } from '../model/sequenceFixture.ts';
import type { Document } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const populated = parseDocument(JSON.parse(JSON.stringify(orderService)));
const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };

function json(body: unknown, status = 200): Response { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }

/** Answers the export call with the example's own text, and the import call with whatever `parsed` says the text means. */
function stubServer(parsed: () => Document = () => populated): ReturnType<typeof vi.fn<typeof fetch>> {
  const mock = vi.fn<typeof fetch>((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    if (url === '/api/export/dsl') return Promise.resolve(new Response(dsl));
    if (url === '/api/import/dsl') return Promise.resolve(json({ document: parsed(), warnings: [] }));
    if (url === '/api/export/mermaid') return Promise.resolve(new Response('flowchart TD\n'));
    return Promise.resolve(new Response('{}', { status: 200 }));
  });
  vi.stubGlobal('fetch', mock);
  return mock;
}

function mount(document: Document = populated) {
  const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} />);
  const canvas = must(view.container.querySelector<HTMLElement>('.diagram-canvas'));
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  return { ...view, canvas };
}
const flushFrame = (): void => { act(() => { vi.runAllTimers(); }); };
const toggle = () => screen.getByRole<HTMLButtonElement>('button', { name: 'Text pane' });
const field = () => screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Document DSL' });
const bands = (container: HTMLElement) => [...container.querySelectorAll<HTMLElement>('.dsl-band-selected')].map((band) => band.style.transform);
const lineOf = (id: string) => dsl.split('\n').findIndex((line) => new RegExp(`^(node|container|edge) ${id} `).test(line)) + 1;

function select(view: ReturnType<typeof mount>, id: string): void {
  fireEvent.pointerDown(must(view.container.querySelector(`g[data-id="${id}"] .shape`)), { ...mouse, clientX: 100, clientY: 100 });
  fireEvent.pointerUp(view.canvas, { pointerId: 1 });
  flushFrame();
}

describe('the Text pane in the editor', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    window.localStorage.clear();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    window.localStorage.clear();
  });

  it('opens and closes from the toolbar button, and shows the document as DSL by default', async () => {
    stubServer();
    mount();
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    expect(screen.queryByRole('textbox', { name: 'Document DSL' })).toBeNull();
    fireEvent.click(toggle());
    expect(toggle().getAttribute('aria-pressed')).toBe('true');
    await waitFor(() => { expect(field().value).toBe(dsl); });
    expect(screen.getByRole('button', { name: 'DSL' }).getAttribute('aria-pressed')).toBe('true');
    expect(field().closest('[hidden]')).toBeNull();
    fireEvent.click(toggle());
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    expect(screen.queryByRole('textbox', { name: 'Document DSL' })).toBeNull();
  });

  it('opens and closes from the View menu as well', () => {
    stubServer();
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Menu' }));
    const item = within(screen.getByRole('group', { name: 'View' })).getByRole('menuitem', { name: /Toggle Text/ });
    fireEvent.click(item);
    expect(toggle().getAttribute('aria-pressed')).toBe('true');
  });

  it('remembers that it was open, and which text, for the next diagram this viewer opens, without taking the keyboard', async () => {
    stubServer();
    const first = mount();
    fireEvent.click(toggle());
    await waitFor(() => { expect(field().value).toBe(dsl); });
    fireEvent.click(screen.getByRole('button', { name: 'Mermaid' }));
    expect(JSON.parse(window.localStorage.getItem(TEXT_PANE_KEY) ?? '{}')).toEqual({ open: true, format: 'mermaid' });
    first.unmount();

    mount();
    expect(toggle().getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByRole('button', { name: 'Mermaid' }).getAttribute('aria-pressed')).toBe('true');
    // Restored, not asked for: the canvas keeps the keyboard.
    expect(document.activeElement).not.toBe(document.querySelector('#document-dsl'));
    fireEvent.click(toggle());
    expect(JSON.parse(window.localStorage.getItem(TEXT_PANE_KEY) ?? '{}')).toEqual({ open: false, format: 'mermaid' });
  });

  it('opens on the Properties panel when it was last closed', () => {
    stubServer();
    window.localStorage.setItem(TEXT_PANE_KEY, JSON.stringify({ open: false, format: 'dsl' }));
    mount();
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
  });

  it('works when the browser will not let it remember anything', async () => {
    stubServer();
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('blocked', 'SecurityError'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('blocked', 'SecurityError'); });
    mount();
    fireEvent.click(toggle());
    await waitFor(() => { expect(field().value).toBe(dsl); });
    expect(toggle().getAttribute('aria-pressed')).toBe('true');
  });

  it('is not offered where there is nothing to save to: a preview has no panel', () => {
    stubServer();
    render(<DocumentViewer document={populated} />);
    expect(screen.queryByRole('button', { name: 'Text pane' })).toBeNull();
  });

  it('marks the line of what is selected on the canvas, and selects the element whose line is clicked', async () => {
    stubServer();
    const view = mount();
    fireEvent.click(toggle());
    await waitFor(() => { expect(field().value).toBe(dsl); });
    expect(bands(view.container)).toEqual([]);

    select(view, 'n_web');
    expect(bands(view.container)).toEqual([`translateY(${String(8 + (lineOf('n_web') - 1) * 21)}px)`]);

    const offset = dsl.indexOf('node n_db ') + 6;
    field().setSelectionRange(offset, offset);
    fireEvent.click(field());
    flushFrame();
    expect(view.container.querySelector('.selection-node[data-selection-id="n_db"]')).not.toBeNull();
    expect(view.container.querySelector('.selection-node[data-selection-id="n_web"]')).toBeNull();
    expect(bands(view.container)).toEqual([`translateY(${String(8 + (lineOf('n_db') - 1) * 21)}px)`]);
  });

  it('applies the text as one undo step on the canvas, with Ctrl+Enter, and Undo takes it back', async () => {
    const edited: Document = { ...populated, title: 'Typed title', nodes: populated.nodes.map((node) => (node.id === 'n_web' ? { ...node, label: 'Typed label' } : node)) };
    let parsed = populated;
    stubServer(() => parsed);
    const view = mount();
    fireEvent.click(toggle());
    await waitFor(() => { expect(field().value).toBe(dsl); });
    parsed = edited;
    fireEvent.change(field(), { target: { value: dsl.replace('"Web app"', '"Typed label"') } });
    fireEvent.keyDown(field(), { key: 'Enter', ctrlKey: true });
    await waitFor(() => { expect(view.container.querySelector('g[data-id="n_web"]')?.textContent).toContain('Typed label'); });
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Typed title');
    // The text stays as typed.
    expect(field().value).toContain('"Typed label"');
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    flushFrame();
    expect(view.container.querySelector('g[data-id="n_web"]')?.textContent).toContain('Web app');
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Order service');
    expect(screen.getByRole('button', { name: 'Undo' }).getAttribute('aria-disabled')).toBe('true');
  });

  it('keeps unapplied text when the canvas changes, and Keep mine then folds the text in without undoing the canvas change', async () => {
    const edited: Document = { ...populated, title: 'Typed title' };
    let parsed = populated;
    stubServer(() => parsed);
    const view = mount();
    fireEvent.click(toggle());
    await waitFor(() => { expect(field().value).toBe(dsl); });
    fireEvent.change(field(), { target: { value: `${dsl}\n# typed` } });

    // A change made on the canvas meanwhile: the selected node is deleted.
    select(view, 'n_web');
    fireEvent.keyDown(view.canvas, { key: 'Delete' });
    flushFrame();
    expect(view.container.querySelector('g[data-id="n_web"]')).toBeNull();
    const notice = await screen.findByText('Diagram changed. Your unapplied text is kept here.');
    expect(field().value).toBe(`${dsl}\n# typed`);
    expect(within(must(notice.parentElement)).getByRole('button', { name: 'Reload text' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Keep mine' }));
    parsed = edited;
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => { expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Typed title'); });
    // The text said nothing about n_web, so the deletion stands; the typed title is in.
    expect(view.container.querySelector('g[data-id="n_web"]')).toBeNull();
    expect(view.container.querySelector('g[data-id="n_api"]')).not.toBeNull();

    // One Undo takes the merge back and leaves the canvas deletion.
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    flushFrame();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Order service');
    expect(view.container.querySelector('g[data-id="n_web"]')).toBeNull();
  });
});
