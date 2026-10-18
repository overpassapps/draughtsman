// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { SaveConflict } from '../model/documentClient.ts';
import type { CompareMarks } from '../model/compare.ts';
import type { Document } from '../model/types.ts';
import { HistoryPanel } from './HistoryPanel.tsx';
import { actorLabel, formatSize, readableReason } from './historyFormat.ts';

const current: Document = {
  id: 'doc_h',
  schemaVersion: 1,
  title: 'Order flow',
  type: 'flowchart',
  nodes: [{ id: 'n_a', kind: 'process', label: 'Receive', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 40 } }, { id: 'n_b', kind: 'process', label: 'Ship', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 40 } }],
  edges: [],
  meta: { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' },
};

const recent = new Date(Date.now() - 5 * 60_000).toISOString();
const versions = [
  { id: 'v_auto', documentId: 'doc_h', name: 'checkpoint', createdAt: recent, actor: 'agent:claude-code/claude-fable-5-1', reason: 'Before replace by agent:claude-code/claude-fable-5-1', size: 2400 },
  { id: 'v_named', documentId: 'doc_h', name: 'Before the rework', createdAt: recent, actor: 'user:Alice', size: 640 },
];

const diffBody = {
  fromVersion: 'v_named',
  toVersion: 'current',
  diff: {
    addedNodes: [{ id: 'n_b', kind: 'process', label: 'Ship', shape: 'rectangle' }],
    removedNodes: [{ id: 'n_old', kind: 'process', label: 'Archive', shape: 'rectangle', geometry: { x: 10, y: 10, w: 80, h: 30 } }],
    changedNodes: [{ id: 'n_a', fields: [{ field: 'label' }] }],
    addedEdges: [], removedEdges: [], changedEdges: [],
  },
};

function serve(overrides: Record<string, () => Response> = {}) {
  const calls: { url: string; method: string }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const method = init?.method ?? 'GET';
      calls.push({ url, method });
      for (const [match, respond] of Object.entries(overrides)) {
        if (`${method} ${url}`.includes(match)) {
          return Promise.resolve(respond());
        }
      }
      if (url.includes('/versions?')) {
        return Promise.resolve(new Response(JSON.stringify({ items: versions, total: versions.length, offset: 0, limit: 50 }), { status: 200 }));
      }
      if (url.includes('/diff?')) {
        return Promise.resolve(new Response(JSON.stringify(diffBody), { status: 200 }));
      }
      return Promise.resolve(new Response(JSON.stringify({ id: 'v_new', name: 'x' }), { status: 201 }));
    }),
  );
  return calls;
}

// What the canvas would be told to draw; module state so the harness never writes to its own props.
const shown: { marks: CompareMarks | undefined } = { marks: undefined };

function Harness(props: { flush?: () => Promise<boolean>; onRestore?: (id: string) => Promise<void>; onReveal?: (id: string) => void }) {
  const [active, setActive] = useState(true);
  return (
    <>
      <button type="button" onClick={() => { setActive((now) => !now); }}>toggle tab</button>
      <HistoryPanel
        document={current}
        active={active}
        flush={props.flush ?? (() => Promise.resolve(true))}
        onRestore={props.onRestore ?? (() => Promise.resolve())}
        onMarks={(marks) => { shown.marks = marks; }}
        onReveal={props.onReveal ?? (() => undefined)}
        onEscape={() => undefined}
      />
    </>
  );
}

describe('HistoryPanel', () => {
  afterEach(() => {
    shown.marks = undefined;
    cleanup();
    vi.unstubAllGlobals();
  });

  it('lists versions with their time, actor and size, calling an automatic checkpoint by that name with its reason', async () => {
    serve();
    render(<Harness />);

    expect(await screen.findByText('Before the rework')).toBeTruthy();
    const items = within(screen.getByRole('list', { name: 'Versions, newest first' })).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]?.textContent).toContain('Automatic checkpoint');
    expect(items[0]?.textContent).toContain('claude-code (agent)');
    expect(items[0]?.textContent).toContain('2.3 KB');
    expect(items[0]?.textContent).toContain('Before replace by agent:claude-code');
    expect(items[1]?.textContent).toContain('Alice');
    expect(items[1]?.textContent).toContain('640 B');
    expect(items[1]?.textContent).toContain('5 min ago');
    expect(formatSize(12 * 1024)).toBe('12 KB');
    expect(actorLabel('user:Alice')).toBe('Alice');
  });

  it('names a version after saving what is unsaved, and refuses to if the save fails', async () => {
    const calls = serve();
    const flush = vi.fn<() => Promise<boolean>>().mockResolvedValue(true);
    render(<Harness flush={flush} />);
    await screen.findByText('Before the rework');

    fireEvent.click(screen.getByRole('button', { name: 'Name this version' }));
    const save = screen.getByRole('button', { name: 'Save version' });
    expect((save as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Version name'), { target: { value: 'Release 1' } });
    fireEvent.click(save);

    await waitFor(() => {
      expect(calls.some((call) => call.method === 'POST' && call.url === '/api/documents/doc_h/versions')).toBe(true);
    });
    expect(flush).toHaveBeenCalled();

    // A failed save means the snapshot would not match the canvas: nothing is named.
    flush.mockResolvedValue(false);
    const before = calls.filter((call) => call.method === 'POST').length;
    fireEvent.click(screen.getByRole('button', { name: 'Name this version' }));
    fireEvent.change(screen.getByLabelText('Version name'), { target: { value: 'Release 2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save version' }));
    expect((await screen.findByRole('alert')).textContent).toContain('could not be saved');
    expect(calls.filter((call) => call.method === 'POST')).toHaveLength(before);
  });

  it('compares with the saved document: summary, grouped entries, marks for the canvas, and stops when asked', async () => {
    serve();
    const reveal = vi.fn<(id: string) => void>();
    render(<Harness onReveal={reveal} />);
    await screen.findByText('Before the rework');

    fireEvent.click(screen.getByRole('button', { name: 'Compare with Before the rework' }));

    const region = await screen.findByRole('region', { name: 'Comparison with Before the rework' });
    expect(region.textContent).toContain('1 added, 1 removed, 1 changed');
    expect(within(region).getByRole('heading', { name: /Added since then/ })).toBeTruthy();
    expect(within(region).getByRole('heading', { name: /Removed since then/ })).toBeTruthy();
    expect(within(region).getByRole('heading', { name: /Changed since then/ })).toBeTruthy();
    expect([...(shown.marks?.added ?? [])]).toEqual(['n_b']);
    expect([...(shown.marks?.changed ?? [])]).toEqual(['n_a']);
    expect(shown.marks?.removed.map((r) => r.id)).toEqual(['n_old']);

    // Present elements can be shown; one that is gone cannot.
    fireEvent.click(within(region).getByRole('button', { name: /Ship/ }));
    expect(reveal).toHaveBeenCalledWith('n_b');
    expect(within(region).getByRole('button', { name: /Archive/ }).hasAttribute('disabled')).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: 'Stop comparing' }));
    expect(screen.queryByRole('region', { name: /Comparison with/ })).toBeNull();
    expect(shown.marks).toBeUndefined();
  });

  it('takes the marks off the canvas while another tab shows, and puts them back with the tab', async () => {
    serve();
    render(<Harness />);
    await screen.findByText('Before the rework');
    fireEvent.click(screen.getByRole('button', { name: 'Compare with Before the rework' }));
    await screen.findByRole('region', { name: /Comparison with/ });
    expect(shown.marks).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'toggle tab' }));
    expect(shown.marks).toBeUndefined();
    fireEvent.click(screen.getByRole('button', { name: 'toggle tab' }));
    await waitFor(() => {
      expect(shown.marks).toBeDefined();
    });
  });

  it('says so when the comparison cannot be made and leaves the canvas unmarked', async () => {
    serve({ '/diff?': () => new Response(JSON.stringify({ title: 'Cannot compare', detail: 'two elements share an id' }), { status: 422 }) });
    render(<Harness />);
    await screen.findByText('Before the rework');

    fireEvent.click(screen.getByRole('button', { name: 'Compare with Before the rework' }));

    expect((await screen.findByRole('alert')).textContent).toContain('two elements share an id');
    expect(shown.marks).toBeUndefined();
  });

  it('asks before restoring, names what happens, and restores only on confirmation', async () => {
    serve();
    const onRestore = vi.fn<(id: string) => Promise<void>>().mockResolvedValue(undefined);
    render(<Harness onRestore={onRestore} />);
    await screen.findByText('Before the rework');

    fireEvent.click(screen.getByRole('button', { name: 'Restore Before the rework' }));

    const dialog = screen.getByRole('dialog');
    expect(dialog.textContent).toContain('Before the rework');
    expect(dialog.textContent).toContain('saved first as an automatic checkpoint');
    expect(onRestore).not.toHaveBeenCalled();
    // Focus starts on the safe button, and cancelling restores nothing.
    expect(window.document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Cancel' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(onRestore).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Restore Before the rework' }));
    await act(async () => {
      fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Restore' }));
      await Promise.resolve();
    });
    expect(onRestore).toHaveBeenCalledWith('v_named');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
  });

  it('explains a restore refused because the document moved on, and changes nothing', async () => {
    serve();
    const onRestore = vi.fn<(id: string) => Promise<void>>().mockRejectedValue(new SaveConflict(412, 'stale'));
    render(<Harness onRestore={onRestore} />);
    await screen.findByText('Before the rework');

    fireEvent.click(screen.getByRole('button', { name: 'Restore Before the rework' }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Restore' }));

    expect((await screen.findByRole('alert')).textContent).toContain('changed somewhere else');
  });

  it('shows a helpful line when there is no history yet', async () => {
    serve({ '/versions?': () => new Response(JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }), { status: 200 }) });
    render(<Harness />);

    expect(await screen.findByText(/No saved versions yet/)).toBeTruthy();
  });
});

describe('readableReason (P11)', () => {
  it('shows the UTC stamp the server wrote into a restore checkpoint as a readable date and time, not the raw stamp', () => {
    const shown = readableReason('Before restoring "v1 baseline" (2026-10-01 01:47:49Z)');
    expect(shown).not.toContain('2026-10-01 01:47:49Z');
    // Shown in the viewer's own time zone, so 01:47Z is still 30 Sep west of UTC-2 (and 1 Oct everywhere else, up to UTC+14).
    expect(shown).toMatch(/^Before restoring "v1 baseline" \((30 Sep|1 Oct) 2026, \d{2}:\d{2}\)$/);
  });

  it('leaves a reason with no stamp, or one that is not a date, as it was written', () => {
    expect(readableReason('Saved before the agent edit')).toBe('Saved before the agent edit');
    expect(readableReason('Ticket 2026-13-45 99:99:99Z')).toBe('Ticket 2026-13-45 99:99:99Z');
  });
});
