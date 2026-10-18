// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import example from '../../../docs/examples/order-service.json';
import { parseDocument } from '../model/parse.ts';
import type { MergeConflict } from '../model/remoteMerge.ts';
import { must } from '../model/sequenceFixture.ts';
import type { Document, Id } from '../model/types.ts';
import { DslPanel } from './DslPanel.tsx';
import { TEXT_PANE_KEY } from './textPanePrefs.ts';

const original = parseDocument(example);
// Line 5 declares n_api, line 6 n_db and line 8 e_1: the lines the selection tests expect to be marked.
const source = 'draughtsman 1\ntype architecture\ntitle "Order service"\n\nnode n_api service "Orders API" @ 40,200\nnode n_db database "Postgres" @ 40,360\n\nedge e_1 data n_api -> n_db "read"\n';
const changed = { ...original, title: 'Text edit' };
const conflict: MergeConflict = { kind: 'node', id: 'n_api', reason: 'both-changed', fields: ['label'] };
function json(body: unknown, status = 200): Response { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }

interface Options {
  importer?: () => Promise<Response>;
  readOnly?: boolean;
  selectedIds?: ReadonlySet<Id>;
  merge?: (next: Document, base: Document) => MergeConflict[];
}
function props(options: Options = {}) {
  return {
    active: true,
    onApply: vi.fn(),
    onMerge: vi.fn(options.merge ?? (() => [])),
    onTakeMine: vi.fn(),
    onSelect: vi.fn(),
    onEscape: vi.fn(),
    ...(options.readOnly === undefined ? {} : { readOnly: options.readOnly }),
    ...(options.selectedIds === undefined ? {} : { selectedIds: options.selectedIds }),
  };
}
function setup(options: Options = {}) {
  const importer = options.importer ?? (() => Promise.resolve(json({ document: changed, warnings: [] })));
  const fetchMock = vi.fn<typeof fetch>(async (url) => {
    if (url === '/api/export/dsl') return new Response(source);
    if (url === '/api/export/mermaid') return new Response('flowchart TD\n  n_api["Orders API"]\n', { headers: { 'X-Draughtsman-Export-Warnings': '1', 'X-Draughtsman-Export-Notes': encodeURIComponent(JSON.stringify(['Shape “cylinder” has no Mermaid spelling.'])) } });
    return importer();
  });
  vi.stubGlobal('fetch', fetchMock);
  const handlers = props(options);
  const view = (document: Document, extra: Partial<ReturnType<typeof props>> & { active?: boolean } = {}) => <DslPanel document={document} {...handlers} {...extra} />;
  const rendered = render(view(original));
  return { ...rendered, fetchMock, ...handlers, view, rerenderWith: (document: Document, extra = {}) => { rendered.rerender(view(document, extra)); } };
}
const dslField = () => screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Document DSL' });
async function loaded(): Promise<void> {
  await waitFor(() => { expect(dslField().value).toBe(source); });
}
async function edit(): Promise<void> {
  await loaded();
  fireEvent.change(dslField(), { target: { value: 'edited DSL' } });
}
const calls = (fetchMock: ReturnType<typeof vi.fn<typeof fetch>>, url: string) => fetchMock.mock.calls.filter(([called]) => called === url);
const bands = (kind: string) => [...document.querySelectorAll<HTMLElement>(`.dsl-band-${kind}`)].map((band) => band.style.transform);
afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });

it('loads the live document and applies only on explicit Apply', async () => {
  const { onApply, fetchMock } = setup();
  await edit();
  expect(onApply).not.toHaveBeenCalled();
  expect(calls(fetchMock, '/api/import/dsl')).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
  await waitFor(() => { expect(onApply).toHaveBeenCalledWith(changed, original); });
  expect(calls(fetchMock, '/api/import/dsl')[0]?.[1]?.body).toBe('edited DSL');
});

it('applies on Ctrl+Enter and on Cmd+Enter, and not when nothing was edited', async () => {
  const { onApply, rerenderWith } = setup();
  await loaded();
  fireEvent.keyDown(dslField(), { key: 'Enter', ctrlKey: true });
  await act(async () => { await Promise.resolve(); });
  expect(onApply).not.toHaveBeenCalled();
  fireEvent.change(dslField(), { target: { value: 'edited DSL' } });
  fireEvent.keyDown(dslField(), { key: 'Enter', metaKey: true });
  await waitFor(() => { expect(onApply).toHaveBeenCalledTimes(1); });
  // The editor hands the applied diagram back as the document, which is what the text now is.
  rerenderWith(changed);
  fireEvent.change(dslField(), { target: { value: 'edited again' } });
  fireEvent.keyDown(dslField(), { key: 'Enter' });
  await act(async () => { await Promise.resolve(); });
  expect(onApply).toHaveBeenCalledTimes(1);
  fireEvent.keyDown(dslField(), { key: 'Enter', ctrlKey: true });
  await waitFor(() => { expect(onApply).toHaveBeenCalledTimes(2); });
});

// P7: pasted DSL rarely carries this diagram's id, and the importer slugs one from the title when there is none.
it('keeps the diagram\'s own id when the text names another, applies the rest, and says so', async () => {
  const { onApply } = setup({ importer: () => Promise.resolve(json({ document: { ...changed, id: 'doc_text_edit' }, warnings: [{ message: 'Unknown kind "foo"' }] })) });
  await edit();
  fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
  await waitFor(() => { expect(onApply).toHaveBeenCalledWith(changed, original); });
  const notes = screen.getByRole('list', { name: 'DSL notes and warnings' });
  expect(notes.textContent).toContain(`The text named the id "doc_text_edit"; this diagram keeps its own id, "${original.id}".`);
  expect(notes.textContent).toContain('Unknown kind "foo"');
});

it('does not mention the id when the text carries the same one', async () => {
  const { onApply } = setup();
  await edit();
  fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
  await waitFor(() => { expect(onApply).toHaveBeenCalled(); });
  expect(screen.queryByRole('list', { name: 'DSL notes and warnings' })).toBeNull();
});

it('numbers the lines in a gutter that follows the text', async () => {
  setup();
  await loaded();
  const gutter = document.querySelector('.dsl-gutter pre');
  expect(gutter?.textContent).toBe('1\n2\n3\n4\n5\n6\n7\n8\n9');
  fireEvent.change(dslField(), { target: { value: 'a\nb' } });
  expect(gutter?.textContent).toBe('1\n2');
});

describe('errors and warnings', () => {
  it('keeps invalid text, writes nothing, and says which line and column the parser stopped at', async () => {
    const { onApply, onMerge } = setup({ importer: () => Promise.resolve(json({ detail: 'Expected a node id', line: 7, column: 6 }, 422)) });
    await edit();
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Line 7, column 6: Expected a node id');
    expect(onApply).not.toHaveBeenCalled();
    expect(onMerge).not.toHaveBeenCalled();
    expect(dslField().value).toBe('edited DSL');
    expect(dslField().getAttribute('aria-invalid')).toBe('true');
  });

  it('marks the error line in the text and moves the caret there from the message', async () => {
    setup({ importer: () => Promise.resolve(json({ detail: 'Expected a node id', line: 6, column: 6 }, 422)) });
    await loaded();
    fireEvent.change(dslField(), { target: { value: source + 'x' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    const go = await screen.findByRole('button', { name: 'Line 6, column 6' });
    expect(bands('problem')).toEqual([`translateY(${String(8 + 5 * 21)}px)`]);
    fireEvent.click(go);
    const lineStart = source.split('\n').slice(0, 5).join('\n').length + 1;
    expect(dslField().selectionStart).toBe(lineStart + 5);
    expect(dslField().selectionEnd).toBe(lineStart + 'node n_db database "Postgres" @ 40,360'.length);
    expect(document.activeElement).toBe(dslField());
    // Typing again clears the message and the mark: they described text that is gone.
    fireEvent.change(dslField(), { target: { value: 'fixed' } });
    expect(screen.queryByRole('alert')).toBeNull();
    expect(bands('problem')).toEqual([]);
  });

  it('an error that names no line is shown as it is', async () => {
    setup({ importer: () => Promise.resolve(json({ title: 'Document source too large' }, 413)) });
    await edit();
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Document source too large');
    expect(screen.queryByRole('button', { name: /^Line/ })).toBeNull();
  });

  it('an error body that is not JSON still reads as an error', async () => {
    setup({ importer: () => Promise.resolve(new Response('<html>Bad gateway</html>', { status: 502 })) });
    await edit();
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Request failed (502)');
  });

  it('lists a warning with its line, applies the text anyway, and keeps what was typed', async () => {
    const { onApply } = setup({ importer: () => Promise.resolve(json({ document: changed, warnings: [{ code: 'dsl-lint', message: 'Unknown kind "foo"', line: 5 }, { code: 'dsl-lint', message: 'No line for this one' }] })) });
    await edit();
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => { expect(onApply).toHaveBeenCalledWith(changed, original); });
    const notes = screen.getByRole('list', { name: 'DSL notes and warnings' });
    const items = within(notes).getAllByRole('listitem').map((item) => item.textContent);
    expect(items).toEqual(['Line 5: Unknown kind "foo"', 'No line for this one']);
    expect(bands('problem')).toEqual([`translateY(${String(8 + 4 * 21)}px)`]);
    // What was typed is still there, not reprinted under the cursor: the line numbers above are lines of it.
    expect(dslField().value).toBe('edited DSL');
  });
});

describe('the diagram changing while there is text to keep', () => {
  it('refreshes clean text when the diagram changes', async () => {
    const { fetchMock, rerenderWith } = setup();
    await loaded();
    fetchMock.mockImplementation(() => Promise.resolve(new Response('new canvas DSL')));
    rerenderWith(changed);
    await waitFor(() => { expect(dslField().value).toBe('new canvas DSL'); });
  });

  it('keeps unapplied text, never rewriting it, and offers Reload text or Keep mine without a dialog', async () => {
    const { rerenderWith, onApply, fetchMock } = setup();
    await edit();
    fetchMock.mockClear();
    rerenderWith(changed);
    const notice = await screen.findByRole('status');
    expect(notice.textContent).toContain('Diagram changed');
    expect(within(notice).getByRole('button', { name: 'Reload text' })).toBeTruthy();
    expect(within(notice).getByRole('button', { name: 'Keep mine' })).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 250)); });
    expect(dslField().value).toBe('edited DSL');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onApply).not.toHaveBeenCalled();
  });

  it('Reload text drops the draft for the diagram as it is now', async () => {
    const { rerenderWith, fetchMock } = setup();
    await edit();
    rerenderWith(changed);
    fetchMock.mockImplementation(() => Promise.resolve(new Response('the diagram now')));
    fireEvent.click(await screen.findByRole('button', { name: 'Reload text' }));
    await waitFor(() => { expect(dslField().value).toBe('the diagram now'); });
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('Keep mine keeps the text, drops the notice and lets Apply fold the text into the diagram instead of refusing it', async () => {
    const { rerenderWith, onApply, onMerge, fetchMock } = setup();
    await edit();
    rerenderWith(changed);
    fireEvent.click(await screen.findByRole('button', { name: 'Keep mine' }));
    expect(screen.queryByRole('button', { name: 'Reload text' })).toBeNull();
    expect(screen.getByRole('status').textContent).toContain('Keeping your text');
    expect(dslField().value).toBe('edited DSL');
    const apply = screen.getByRole<HTMLButtonElement>('button', { name: 'Apply' });
    expect(apply.disabled).toBe(false);
    fetchMock.mockImplementation((url) => Promise.resolve(url === '/api/import/dsl' ? json({ document: { ...original, title: 'Typed title' }, warnings: [] }) : new Response('reprinted')));
    fireEvent.click(apply);
    // The text was made from `original`; the diagram is `changed` now.
    await waitFor(() => { expect(onMerge).toHaveBeenCalledWith({ ...original, title: 'Typed title' }, original); });
    expect(onApply).not.toHaveBeenCalled();
    // The diagram now holds the merge, which is not exactly what was typed, so the text is printed again from it.
    await waitFor(() => { expect(dslField().value).toBe('reprinted'); });
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('says what both sides changed, keeps the diagram\'s side, and takes the text\'s on request', async () => {
    const { rerenderWith, onTakeMine, fetchMock } = setup({ merge: () => [conflict] });
    await edit();
    rerenderWith(changed);
    fireEvent.click(await screen.findByRole('button', { name: 'Keep mine' }));
    fetchMock.mockImplementation((url) => Promise.resolve(url === '/api/import/dsl' ? json({ document: { ...original, title: 'Typed title' }, warnings: [] }) : new Response('reprinted')));
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    const notice = await screen.findByText(/the diagram’s version stayed/);
    expect(notice.parentElement?.textContent).toContain('“Orders API” (n_api): label');
    fireEvent.click(screen.getByRole('button', { name: 'Use my text for these' }));
    expect(onTakeMine).toHaveBeenCalledWith({ ...original, title: 'Typed title' }, [conflict]);
    expect(screen.queryByText(/the diagram’s version stayed/)).toBeNull();
  });

  it('a diagram that has not moved on is applied exactly, never merged', async () => {
    const { onApply, onMerge } = setup();
    await edit();
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => { expect(onApply).toHaveBeenCalledOnce(); });
    expect(onMerge).not.toHaveBeenCalled();
  });

  it('leaves text that was just applied alone when the diagram becomes what it applied', async () => {
    const { rerenderWith, fetchMock } = setup();
    await edit();
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => { expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Apply' }).disabled).toBe(true); });
    fetchMock.mockClear();
    rerenderWith(changed);
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 250)); });
    expect(fetchMock).not.toHaveBeenCalled();
    expect(dslField().value).toBe('edited DSL');
    // Then the next change is a change like any other.
    fetchMock.mockImplementation(() => Promise.resolve(new Response('later')));
    rerenderWith({ ...changed, title: 'Elsewhere' });
    await waitFor(() => { expect(dslField().value).toBe('later'); });
  });

  it('ignores an obsolete print response that finishes after typing begins', async () => {
    const { rerenderWith, fetchMock } = setup();
    await loaded();
    let finish: (response: Response) => void = () => undefined;
    fetchMock.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    rerenderWith(changed);
    await waitFor(() => { expect(fetchMock).toHaveBeenCalledTimes(2); });
    fireEvent.change(dslField(), { target: { value: 'draft survives' } });
    await act(async () => { finish(new Response('obsolete print')); await Promise.resolve(); });
    expect(dslField().value).toBe('draft survives');
  });

  it('requests nothing while hidden, keeps a dirty draft, and refreshes clean text when shown again', async () => {
    const { rerenderWith, fetchMock } = setup();
    await edit();
    rerenderWith(changed, { active: false });
    rerenderWith(original, { active: false });
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 200)); });
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(dslField().value).toBe('edited DSL');
    expect(screen.queryByRole('status')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Reset text' }));
    rerenderWith(changed, { active: false });
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 200)); });
    expect(fetchMock).toHaveBeenCalledOnce();
    fetchMock.mockResolvedValue(new Response('shown again'));
    rerenderWith(changed);
    await waitFor(() => { expect(dslField().value).toBe('shown again'); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

it('contains keyboard commands and uses Escape to return focus to the canvas', async () => {
  const { onEscape } = setup();
  await edit();
  const windowKey = vi.fn();
  window.addEventListener('keydown', windowKey);
  fireEvent.keyDown(dslField(), { key: 'z', ctrlKey: true });
  fireEvent.keyDown(dslField(), { key: 'Enter', ctrlKey: true });
  expect(windowKey).not.toHaveBeenCalled();
  fireEvent.keyDown(dslField(), { key: 'Escape' });
  expect(onEscape).toHaveBeenCalledOnce();
  window.removeEventListener('keydown', windowKey);
});

it('asks before the tab is closed or reloaded while there is unapplied text, and not otherwise', async () => {
  const { unmount } = setup();
  await loaded();
  const closing = () => !window.dispatchEvent(new Event('beforeunload', { cancelable: true }));
  expect(closing()).toBe(false);
  fireEvent.change(dslField(), { target: { value: 'edited DSL' } });
  expect(closing()).toBe(true);
  fireEvent.change(dslField(), { target: { value: source } });
  expect(closing()).toBe(false);
  fireEvent.change(dslField(), { target: { value: 'edited again' } });
  expect(closing()).toBe(true);
  unmount();
  expect(closing()).toBe(false);
});

it('takes the keyboard into the text on open, unless it was only restored', async () => {
  setup();
  await loaded();
  expect(document.activeElement).toBe(dslField());
});

describe('read only', () => {
  it('shows and copies the text but offers no way to change the diagram from it', async () => {
    const { onApply, onMerge } = setup({ readOnly: true });
    await loaded();
    expect(dslField().readOnly).toBe(true);
    fireEvent.change(dslField(), { target: { value: 'edited DSL' } });
    expect(screen.queryByRole('button', { name: 'Apply' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Reset text' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeTruthy();
    expect(screen.getByRole('status').textContent).toContain('View only');
    fireEvent.keyDown(dslField(), { key: 'Enter', ctrlKey: true });
    await act(async () => { await Promise.resolve(); });
    expect(onApply).not.toHaveBeenCalled();
    expect(onMerge).not.toHaveBeenCalled();
  });
});

describe('Mermaid', () => {
  it('shows the export read-only, says what Mermaid had to simplify, and remembers the choice', async () => {
    const { fetchMock } = setup();
    await loaded();
    expect(screen.getByRole('button', { name: 'DSL' }).getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'Mermaid' }));
    const field = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Document Mermaid (read only)' });
    await waitFor(() => { expect(field.value).toBe('flowchart TD\n  n_api["Orders API"]\n'); });
    expect(field.readOnly).toBe(true);
    const sent = calls(fetchMock, '/api/export/mermaid')[0]?.[1]?.body;
    expect(JSON.parse(typeof sent === 'string' ? sent : '{}')).toMatchObject({ id: original.id });
    expect(screen.getByRole('list', { name: 'What Mermaid simplified' }).textContent).toContain('has no Mermaid spelling');
    expect(screen.queryByRole('button', { name: 'Apply' })).toBeNull();
    expect(JSON.parse(localStorage.getItem(TEXT_PANE_KEY) ?? '{}')).toMatchObject({ format: 'mermaid' });
  });

  it('opens on the remembered format, and a draft in the DSL survives a look at Mermaid', async () => {
    localStorage.setItem(TEXT_PANE_KEY, JSON.stringify({ open: true, format: 'mermaid' }));
    setup();
    expect(screen.getByRole('button', { name: 'Mermaid' }).getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'DSL' }));
    await edit();
    fireEvent.click(screen.getByRole('button', { name: 'Mermaid' }));
    fireEvent.click(screen.getByRole('button', { name: 'DSL' }));
    expect(dslField().value).toBe('edited DSL');
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Apply' }).disabled).toBe(false);
  });

  it('says so when the Mermaid export fails', async () => {
    const { fetchMock } = setup();
    await loaded();
    fetchMock.mockImplementation(() => Promise.resolve(json({ detail: 'Mermaid is unavailable' }, 500)));
    fireEvent.click(screen.getByRole('button', { name: 'Mermaid' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Mermaid is unavailable');
  });
});

describe('selection', () => {
  it('marks the text line of each selected node and edge', async () => {
    const { rerenderWith } = setup({ selectedIds: new Set(['n_api']) });
    await loaded();
    expect(bands('selected')).toEqual([`translateY(${String(8 + 4 * 21)}px)`]);
    rerenderWith(original, { selectedIds: new Set(['e_1', 'n_db', 'not_in_the_text']) });
    expect(bands('selected')).toEqual([`translateY(${String(8 + 5 * 21)}px)`, `translateY(${String(8 + 7 * 21)}px)`]);
    rerenderWith(original, { selectedIds: new Set<Id>() });
    expect(bands('selected')).toEqual([]);
  });

  it('follows the text as it is scrolled', async () => {
    setup({ selectedIds: new Set(['n_api']) });
    await loaded();
    fireEvent.scroll(dslField(), { target: { scrollTop: 30 } });
    expect(bands('selected')).toEqual([`translateY(${String(8 + 4 * 21 - 30)}px)`]);
    expect(must(document.querySelector<HTMLElement>('.dsl-gutter pre')).style.transform).toBe('translateY(-30px)');
  });

  it('selects the element of a line that is clicked, and nothing for a line that declares none or a drag over text', async () => {
    const { onSelect } = setup();
    await loaded();
    const offset = source.indexOf('node n_db') + 3;
    dslField().setSelectionRange(offset, offset);
    fireEvent.click(dslField());
    expect(onSelect).toHaveBeenCalledWith(new Set(['n_db']));
    onSelect.mockClear();
    dslField().setSelectionRange(2, 2);
    fireEvent.click(dslField());
    dslField().setSelectionRange(offset, offset + 4);
    fireEvent.click(dslField());
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('does not select an id the diagram does not have yet', async () => {
    const { onSelect } = setup();
    await loaded();
    fireEvent.change(dslField(), { target: { value: 'node n_new service "New"' } });
    dslField().setSelectionRange(8, 8);
    fireEvent.click(dslField());
    expect(onSelect).not.toHaveBeenCalled();
  });
});
