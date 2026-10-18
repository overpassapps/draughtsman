// @vitest-environment jsdom
/**
 * Autosave and stale-save recovery in the editor hook (design/atlas/editor-safety.md, "Autosave"): when a save is made,
 * that it is never an older snapshot over a newer one, what happens when the server is down or refuses, and the three
 * answers to a save refused as stale. Timers are fake, so "2 seconds after the last change" is asserted at 1999 and 2000.
 */
import { act, renderHook } from '@testing-library/react';
import { StrictMode } from 'react';
import type { Document, Geometry, Id } from '../model/types.ts';
import { readLocalCopies, storeLocalCopy } from './localCopy.ts';
import { AUTOSAVE_DELAY_MS, AUTOSAVE_MAX_WAIT_MS, useDocumentEditor } from './useDocumentEditor.ts';

const initial: Document = {
  id: 'doc_edit',
  schemaVersion: 1,
  title: 'Edit',
  type: 'flowchart',
  nodes: [{ id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } }],
  edges: [],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
};
const at = (x: number): Geometry => ({ x, y: 0, w: 100, h: 50 });
const xOf = (document: Document): number | undefined => document.nodes[0]?.geometry?.x;
const labelOf = (document: Document): string | undefined => document.nodes[0]?.label;

interface Put {
  ifMatch: string | null;
  body: Document;
  keepalive: boolean | undefined;
  at: number;
}

/** A stand-in for the document endpoints with the failure modes autosave has to survive. */
function server(start: Document) {
  let stored = structuredClone(start);
  let version = 1;
  let mode: { kind: 'ok' } | { kind: 'status'; status: number } | { kind: 'network' } = { kind: 'ok' };
  let held: ((response: Response) => void)[] | undefined;
  const puts: Put[] = [];
  const posts: Document[] = [];
  const etag = (): string => `"dv1:${String(version)}"`;
  const respondPut = (init: RequestInit | undefined): Response => {
    const body = JSON.parse(init?.body as string) as Document;
    if (new Headers(init?.headers).get('If-Match') !== etag()) {
      return new Response('{"title":"Document has changed"}', { status: 412 });
    }
    stored = body;
    version += 1;
    return new Response(JSON.stringify(stored), { status: 200, headers: { ETag: etag() } });
  };
  const fetchMock = vi.fn<typeof fetch>((_input, init) => {
    const method = init?.method ?? 'GET';
    if (mode.kind === 'network') {
      const refused = new TypeError('Failed to fetch');
      if (method === 'PUT') {
        puts.push({ ifMatch: new Headers(init?.headers).get('If-Match'), body: JSON.parse(init?.body as string) as Document, keepalive: init?.keepalive, at: Date.now() });
      }
      return Promise.reject(refused);
    }
    if (method === 'PUT') {
      puts.push({ ifMatch: new Headers(init?.headers).get('If-Match'), body: JSON.parse(init?.body as string) as Document, keepalive: init?.keepalive, at: Date.now() });
      if (mode.kind === 'status') {
        return Promise.resolve(new Response('{}', { status: mode.status }));
      }
      if (held !== undefined) {
        return new Promise<Response>((resolve) => {
          held?.push(() => {
            resolve(respondPut(init));
          });
        });
      }
      return Promise.resolve(respondPut(init));
    }
    if (method === 'POST') {
      posts.push(JSON.parse(init?.body as string) as Document);
      return Promise.resolve(new Response('{"id":"doc_copy"}', { status: 201 }));
    }
    if (new Headers(init?.headers).get('If-None-Match') === etag()) {
      return Promise.resolve(new Response(null, { status: 304, headers: { ETag: etag() } }));
    }
    return Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: etag() } }));
  });
  vi.stubGlobal('fetch', fetchMock);
  return {
    puts,
    posts,
    get stored(): Document {
      return stored;
    },
    get etag(): string {
      return etag();
    },
    /** Another writer saves: the stored document changes and the version moves on. */
    otherWriterChanges(change: (document: Document) => void): void {
      change(stored);
      version += 1;
    },
    failWith(status: number): void {
      mode = { kind: 'status', status };
    },
    goOffline(): void {
      mode = { kind: 'network' };
    },
    recover(): void {
      mode = { kind: 'ok' };
    },
    /** Holds every PUT until `release`, to edit while a save is in flight. */
    hold(): void {
      held = [];
    },
    release(): void {
      const waiting = held ?? [];
      held = undefined;
      for (const resolve of waiting) {
        resolve(new Response());
      }
    },
  };
}

function open(options: { liveSyncIntervalMs?: number } = {}) {
  return renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:1"', { autosaveDelayMs: AUTOSAVE_DELAY_MS, ...options }), { wrapper: StrictMode });
}
type Editor = ReturnType<typeof open>['result']['current'];

const edit = (result: { current: Editor }, x: number): void => {
  act(() => {
    result.current.moveNodes(new Map<Id, Geometry>([['n_a', at(x)]]));
    result.current.moveEnd();
  });
};
const wait = (ms: number): Promise<void> =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
const canUndo = (result: { current: Editor }): boolean => result.current.actions.find((action) => action.id === 'document.undo')?.enabled() ?? false;

beforeEach(() => {
  localStorage.clear();
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('autosave timing', () => {
  it('saves AUTOSAVE_DELAY_MS after the last change, not before', async () => {
    const s = server(initial);
    const { result } = open();
    edit(result, 70);
    expect(result.current.saveStatus).toBe('unsaved');

    await wait(AUTOSAVE_DELAY_MS - 1);
    expect(s.puts).toHaveLength(0);
    expect(result.current.saveStatus).toBe('unsaved');

    await wait(1);
    expect(s.puts).toHaveLength(1);
    expect(s.puts[0]?.ifMatch).toBe('"dv1:1"');
    expect(xOf(s.puts[0]?.body ?? initial)).toBe(70);
    expect(result.current.saveStatus).toBe('saved');
  });

  it('restarts the wait on each change, so a burst of edits is one save of the last one', async () => {
    const s = server(initial);
    const { result } = open();
    edit(result, 10);
    await wait(1500);
    edit(result, 20);
    await wait(1500);
    // Three seconds in, more than the delay since the first change but not since the last.
    expect(s.puts).toHaveLength(0);
    await wait(500);
    expect(s.puts).toHaveLength(1);
    expect(xOf(s.puts[0]?.body ?? initial)).toBe(20);
  });

  it('still saves under continuous editing, AUTOSAVE_MAX_WAIT_MS after the first unsaved change', async () => {
    const s = server(initial);
    const { result } = open();
    // An edit every 1.5s never leaves the 2s gap a pure debounce needs: edits at 0, 1.5, ... 9s.
    for (let i = 0; i < 6; i += 1) {
      edit(result, i + 1);
      await wait(1500);
    }
    edit(result, 7);
    expect(s.puts).toHaveLength(0);
    expect(AUTOSAVE_MAX_WAIT_MS).toBe(10_000);
    await wait(AUTOSAVE_MAX_WAIT_MS - 9000 - 1);
    expect(s.puts).toHaveLength(0);
    await wait(1);
    expect(s.puts).toHaveLength(1);
    expect(xOf(s.puts[0]?.body ?? initial)).toBe(7);
  });

  it('does nothing for a document nobody edited, or when autosave is not switched on', async () => {
    const s = server(initial);
    const untouched = open();
    await wait(60_000);
    expect(s.puts).toHaveLength(0);
    expect(untouched.result.current.saveStatus).toBe('saved');

    const off = renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:1"'), { wrapper: StrictMode });
    edit(off.result, 70);
    await wait(60_000);
    expect(s.puts).toHaveLength(0);
    expect(off.result.current.saveStatus).toBe('unsaved');
  });

  it('never saves a document with no id, which is a read-only preview', async () => {
    const s = server(initial);
    const preview = renderHook(() => useDocumentEditor(undefined, initial, undefined, { autosaveDelayMs: AUTOSAVE_DELAY_MS }), { wrapper: StrictMode });
    edit(preview.result, 70);
    await wait(60_000);
    expect(s.puts).toHaveLength(0);
  });
});

describe('autosave ordering', () => {
  it('sends one save at a time and never puts an older snapshot over a newer one', async () => {
    const s = server(initial);
    s.hold();
    const { result } = open();
    edit(result, 10);
    await wait(AUTOSAVE_DELAY_MS);
    expect(s.puts).toHaveLength(1);
    expect(result.current.saveStatus).toBe('saving');

    // The user keeps working while the first request is out. Nothing else is sent until it comes back.
    edit(result, 20);
    await wait(30_000);
    expect(s.puts).toHaveLength(1);
    expect(xOf(s.puts[0]?.body ?? initial)).toBe(10);
    // A manual save in that window is refused rather than racing it.
    let manual: boolean | undefined;
    await act(async () => { manual = await result.current.save(); });
    expect(manual).toBe(false);
    expect(s.puts).toHaveLength(1);

    s.release();
    await wait(0);
    expect(result.current.saveStatus).toBe('unsaved');
    await wait(AUTOSAVE_DELAY_MS);
    expect(s.puts).toHaveLength(2);
    // The second save was made from the version the first returned, and carries the newer edit.
    expect(s.puts[1]?.ifMatch).toBe('"dv1:2"');
    expect(xOf(s.puts[1]?.body ?? initial)).toBe(20);
    expect(xOf(s.stored)).toBe(20);
    expect(result.current.saveStatus).toBe('saved');
  });

  it('flush waits for the save in flight and then saves what is still unsaved, resolving true only when all of it is stored', async () => {
    const s = server(initial);
    s.hold();
    const { result } = open();
    edit(result, 10);
    await wait(AUTOSAVE_DELAY_MS);
    edit(result, 20);

    let flushed: Promise<boolean> | undefined;
    act(() => { flushed = result.current.flush(); });
    s.release();
    await wait(0);
    // The first save's release is what let the flush go on to the second.
    await act(async () => { await flushed; });
    expect(s.puts.map((put) => xOf(put.body))).toEqual([10, 20]);
    await expect(flushed).resolves.toBe(true);
    expect(xOf(s.stored)).toBe(20);
  });

  it('flush resolves true at once for a document that is already stored, and false while the server refuses', async () => {
    const s = server(initial);
    const { result } = open();
    await expect(result.current.flush()).resolves.toBe(true);
    expect(s.puts).toHaveLength(0);

    s.failWith(500);
    edit(result, 10);
    let flushed = true;
    await act(async () => { flushed = await result.current.flush(); });
    expect(flushed).toBe(false);
    expect(result.current.saveStatus).toBe('error');
  });
});

describe('undo and redo around an autosave', () => {
  it('keeps the undo history through a save, and saves what undo and redo leave', async () => {
    const s = server(initial);
    const { result } = open();
    edit(result, 70);
    await wait(AUTOSAVE_DELAY_MS);
    expect(result.current.saveStatus).toBe('saved');
    expect(canUndo(result)).toBe(true);

    act(() => { result.current.undo(); });
    expect(xOf(result.current.document)).toBe(0);
    expect(result.current.saveStatus).toBe('unsaved');
    await wait(AUTOSAVE_DELAY_MS);
    expect(xOf(s.stored)).toBe(0);
    expect(result.current.saveStatus).toBe('saved');

    act(() => { result.current.redo(); });
    await wait(AUTOSAVE_DELAY_MS);
    expect(xOf(s.stored)).toBe(70);
    expect(s.puts).toHaveLength(3);
  });
});

describe('autosave when the save fails', () => {
  it('stays unsaved, retries with a doubling delay, and saves once the server is back', async () => {
    const s = server(initial);
    s.failWith(503);
    const { result } = open();
    edit(result, 70);
    const start = Date.now();

    await wait(AUTOSAVE_DELAY_MS);
    expect(s.puts).toHaveLength(1);
    expect(result.current.saveStatus).toBe('error');
    expect(result.current.retrying).toBe(true);
    expect(result.current.saveError).toContain('503');

    // First retry after the base delay, the next after twice that.
    await wait(AUTOSAVE_DELAY_MS - 1);
    expect(s.puts).toHaveLength(1);
    await wait(1);
    expect(s.puts).toHaveLength(2);
    await wait(2 * AUTOSAVE_DELAY_MS - 1);
    expect(s.puts).toHaveLength(2);
    expect(result.current.saveStatus).toBe('error');

    s.recover();
    await wait(1);
    expect(s.puts).toHaveLength(3);
    expect(s.puts.map((put) => put.at - start)).toEqual([2000, 4000, 8000]);
    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.retrying).toBe(false);
    expect(result.current.saveError).toBeUndefined();
    expect(xOf(s.stored)).toBe(70);
  });

  it('treats a server that cannot be reached the same way, and retries at once when the browser comes back online', async () => {
    const s = server(initial);
    s.goOffline();
    const { result } = open();
    edit(result, 70);
    await wait(AUTOSAVE_DELAY_MS);
    expect(result.current.saveStatus).toBe('error');
    expect(result.current.retrying).toBe(true);
    expect(result.current.saveError).toMatch(/cannot reach the server/i);

    s.recover();
    act(() => { window.dispatchEvent(new Event('online')); });
    await wait(0);
    expect(result.current.saveStatus).toBe('saved');
    expect(xOf(s.stored)).toBe(70);
  });

  it('does not retry what cannot succeed (the document is gone), but tries again after the next edit', async () => {
    const s = server(initial);
    s.failWith(404);
    const { result } = open();
    edit(result, 70);
    await wait(AUTOSAVE_DELAY_MS);
    expect(result.current.saveStatus).toBe('error');
    expect(result.current.retrying).toBe(false);
    await wait(120_000);
    expect(s.puts).toHaveLength(1);

    edit(result, 80);
    await wait(AUTOSAVE_DELAY_MS);
    expect(s.puts).toHaveLength(2);
  });

  it('keeps the unsaved edits as a local copy while it fails, follows later edits, and drops it once the server has them', async () => {
    const s = server(initial);
    s.failWith(503);
    const { result } = open();
    edit(result, 70);
    await wait(AUTOSAVE_DELAY_MS);
    expect(readLocalCopies('doc_edit').map((copy) => xOf(copy.document))).toEqual([70]);
    edit(result, 90);
    expect(readLocalCopies('doc_edit').map((copy) => xOf(copy.document))).toEqual([90]);

    s.recover();
    await wait(AUTOSAVE_DELAY_MS);
    expect(result.current.saveStatus).toBe('saved');
    expect(readLocalCopies('doc_edit')).toEqual([]);
    expect(result.current.localCopies).toEqual([]);
  });
});

describe('leaving', () => {
  const setHidden = (hidden: boolean): void => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
    document.dispatchEvent(new Event('visibilitychange'));
  };
  afterEach(() => {
    Reflect.deleteProperty(document, 'hidden');
  });

  it('saves at once when the tab is hidden with unsaved changes, asking the browser to finish the request', async () => {
    const s = server(initial);
    const { result } = open();
    edit(result, 70);
    act(() => { setHidden(true); });
    await wait(0);
    expect(s.puts).toHaveLength(1);
    expect(s.puts[0]?.keepalive).toBe(true);
    expect(result.current.saveStatus).toBe('saved');
  });

  it('sends nothing when the tab is hidden or the page goes away with everything already saved', async () => {
    const s = server(initial);
    const { result } = open();
    edit(result, 70);
    await wait(AUTOSAVE_DELAY_MS);
    expect(s.puts).toHaveLength(1);

    act(() => { setHidden(true); });
    act(() => { window.dispatchEvent(new Event('pagehide')); });
    await wait(0);
    expect(s.puts).toHaveLength(1);
  });

  it('flushes on pagehide too', async () => {
    const s = server(initial);
    const { result } = open();
    edit(result, 70);
    act(() => { window.dispatchEvent(new Event('pagehide')); });
    await wait(0);
    expect(s.puts).toHaveLength(1);
  });
});

describe('a save refused as stale', () => {
  /** An unsaved edit of the label, refused because another writer saved first. */
  async function refused(options: { liveSyncIntervalMs?: number } = {}) {
    const s = server(initial);
    const view = open(options);
    act(() => { view.result.current.setLabel('n_a', 'A (mine)'); });
    // The other writer lands just before autosave fires, after any poll that would have merged it first.
    await wait(AUTOSAVE_DELAY_MS - 100);
    s.otherWriterChanges((document) => {
      const node = document.nodes[0];
      if (node) {
        node.label = 'A (theirs)';
        node.geometry = at(300);
      }
    });
    await wait(100);
    return { s, ...view };
  }

  it('stops autosaving, keeps the user’s version on the canvas and as a local copy, and changes nothing on the server', async () => {
    const { s, result } = await refused();
    expect(result.current.saveStatus).toBe('conflict');
    expect(result.current.stale).toEqual({ busy: false, error: undefined });
    expect(labelOf(result.current.document)).toBe('A (mine)');
    expect(labelOf(s.stored)).toBe('A (theirs)');
    expect(readLocalCopies('doc_edit').map((copy) => copy.document.nodes[0]?.label)).toEqual(['A (mine)']);

    // Nothing more is sent by itself, however long it waits, and Save does not quote the stale version again.
    await wait(120_000);
    expect(s.puts).toHaveLength(1);
    let manual: boolean | undefined;
    await act(async () => { manual = await result.current.save(); });
    expect(manual).toBe(false);
    expect(s.puts).toHaveLength(1);
    // A further edit does not reopen autosave either.
    edit(result, 20);
    await wait(120_000);
    expect(s.puts).toHaveLength(1);
    expect(result.current.saveStatus).toBe('conflict');
    // ...but the local copy follows it.
    expect(readLocalCopies('doc_edit').map((copy) => xOf(copy.document))).toEqual([20]);
  });

  it('reloads theirs: the editor shows the server’s version, undo cannot bring the user’s back onto the canvas, and their version stays as a local copy', async () => {
    const { s, result } = await refused();
    await act(() => result.current.reloadTheirs());

    expect(labelOf(result.current.document)).toBe('A (theirs)');
    expect(xOf(result.current.document)).toBe(300);
    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.stale).toBeUndefined();
    expect(canUndo(result)).toBe(false);
    act(() => { result.current.undo(); });
    expect(labelOf(result.current.document)).toBe('A (theirs)');
    // Nothing of theirs was overwritten and mine is not lost.
    expect(labelOf(s.stored)).toBe('A (theirs)');
    expect(result.current.localCopies.map((copy) => copy.document.nodes[0]?.label)).toEqual(['A (mine)']);

    // The next edit saves from the server’s version, and that save does not throw the set-aside copy away.
    const serverVersion = s.etag;
    edit(result, 20);
    await wait(AUTOSAVE_DELAY_MS);
    expect(s.puts.at(-1)?.ifMatch).toBe(serverVersion);
    expect(xOf(s.stored)).toBe(20);
    expect(labelOf(s.stored)).toBe('A (theirs)');
    expect(readLocalCopies('doc_edit').map((copy) => copy.document.nodes[0]?.label)).toEqual(['A (mine)']);
  });

  it('keeps mine: overwrites the server’s current version, quoting it, then autosave carries on', async () => {
    const { s, result } = await refused();
    // It moved again while the question was open: keeping mine must quote this one, not the one the refusal saw.
    s.otherWriterChanges((document) => {
      const node = document.nodes[0];
      if (node) {
        node.label = 'A (theirs, again)';
      }
    });
    const quoted = s.etag;
    await act(() => result.current.keepMine());

    expect(s.puts.at(-1)?.ifMatch).toBe(quoted);
    expect(labelOf(s.stored)).toBe('A (mine)');
    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.stale).toBeUndefined();
    expect(readLocalCopies('doc_edit')).toEqual([]);

    edit(result, 20);
    await wait(AUTOSAVE_DELAY_MS);
    expect(xOf(s.stored)).toBe(20);
  });

  it('keeps mine only as far as the server allows: if it changed yet again during the overwrite, nothing is overwritten and the question stays', async () => {
    const { s, result } = await refused();
    // Every PUT is now refused as stale, as if a writer kept winning the race.
    s.otherWriterChanges(() => undefined);
    const realFetch = globalThis.fetch;
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((input, init) => (init?.method === 'PUT' ? Promise.resolve(new Response('{}', { status: 412 })) : realFetch(input, init))));
    await act(() => result.current.keepMine());

    expect(result.current.saveStatus).toBe('conflict');
    expect(result.current.stale?.error).toMatch(/changed again/i);
    expect(result.current.stale?.busy).toBe(false);
    expect(labelOf(s.stored)).toBe('A (theirs)');
    expect(labelOf(result.current.document)).toBe('A (mine)');
  });

  it('saves mine as a new document (no id, no meta, marked as a copy), then shows the server’s version here', async () => {
    const { s, result } = await refused();
    let created: string | undefined;
    await act(async () => { created = await result.current.saveMineAsNew(); });

    expect(created).toBe('doc_copy');
    expect(s.posts).toHaveLength(1);
    expect(s.posts[0]?.title).toBe('Edit (my copy)');
    expect(s.posts[0]?.nodes[0]?.label).toBe('A (mine)');
    expect(s.posts[0]).not.toHaveProperty('id');
    expect(s.posts[0]).not.toHaveProperty('meta');
    // The copy now lives in a document of its own, so it is not also kept aside; the server’s document was left alone.
    expect(readLocalCopies('doc_edit')).toEqual([]);
    expect(labelOf(s.stored)).toBe('A (theirs)');
    expect(labelOf(result.current.document)).toBe('A (theirs)');
    expect(result.current.saveStatus).toBe('saved');
  });

  it('leaves the question open, with the reason, when the server cannot be reached to answer it', async () => {
    const { s, result } = await refused();
    s.goOffline();
    await act(() => result.current.reloadTheirs());
    expect(result.current.saveStatus).toBe('conflict');
    expect(result.current.stale?.error).toMatch(/cannot reach the server/i);
    expect(labelOf(result.current.document)).toBe('A (mine)');
  });

  it('does not merge another writer’s change while the question is open, so "mine" and "theirs" stay what the user was shown', async () => {
    // One poll a second: the one at 1s finds nothing, the writer lands at 1.9s, autosave at 2s is refused, and the polls
    // from then on would merge it if they were allowed to.
    const { s, result } = await refused({ liveSyncIntervalMs: 1000 });
    expect(result.current.saveStatus).toBe('conflict');
    await wait(5000);
    expect(labelOf(result.current.document)).toBe('A (mine)');
    expect(xOf(result.current.document)).toBe(0);
    expect(result.current.remote).toBeUndefined();
    expect(labelOf(s.stored)).toBe('A (theirs)');
  });
});

describe('local copies from an earlier visit', () => {
  const copyOf = (copyId: string, label: string): Parameters<typeof storeLocalCopy>[0] => ({
    copyId,
    documentId: 'doc_edit',
    document: { ...initial, nodes: [{ ...(initial.nodes[0] ?? { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle' }), label }] },
    keptAt: '2026-09-29T10:00:00.000Z',
  });

  it('lists a copy that differs from what was loaded, and forgets one that matches it', () => {
    storeLocalCopy(copyOf('differs', 'A (unsaved last time)'));
    storeLocalCopy(copyOf('same', 'A'));
    const { result } = open();
    expect(result.current.localCopies.map((copy) => copy.copyId)).toEqual(['differs']);
    expect(readLocalCopies('doc_edit').map((copy) => copy.copyId)).toEqual(['differs']);
  });

  it('saves one as a new document and drops it; discarding drops it without a request', async () => {
    const s = server(initial);
    storeLocalCopy(copyOf('one', 'A (one)'));
    storeLocalCopy(copyOf('two', 'A (two)'));
    const { result } = open();
    let created: string | undefined;
    await act(async () => { created = await result.current.saveCopyAsNew('one'); });
    expect(created).toBe('doc_copy');
    expect(s.posts[0]?.title).toBe('Edit (my copy)');
    expect(s.posts[0]?.nodes[0]?.label).toBe('A (one)');
    expect(result.current.localCopies.map((copy) => copy.copyId)).toEqual(['two']);

    act(() => { result.current.discardLocalCopy('two'); });
    expect(result.current.localCopies).toEqual([]);
    expect(s.posts).toHaveLength(1);
  });

  it('is not thrown away by this visit’s own saves', async () => {
    const s = server(initial);
    storeLocalCopy(copyOf('earlier', 'A (earlier)'));
    const { result } = open();
    edit(result, 70);
    await wait(AUTOSAVE_DELAY_MS);
    expect(xOf(s.stored)).toBe(70);
    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.localCopies.map((copy) => copy.copyId)).toEqual(['earlier']);
  });

  it('survives a later refusal: the new copy is added beside it, not over it', async () => {
    const s = server(initial);
    storeLocalCopy(copyOf('earlier', 'A (earlier)'));
    s.failWith(503);
    const { result } = open();
    edit(result, 70);
    await wait(AUTOSAVE_DELAY_MS);
    expect(readLocalCopies('doc_edit').map((copy) => copy.copyId)).toHaveLength(2);
    expect(readLocalCopies('doc_edit')[0]?.copyId).toBe('earlier');
  });
});
