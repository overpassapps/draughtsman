// @vitest-environment jsdom
/**
 * Restoring a version from the History panel through the editor hook (design/atlas/version-history.md): it stores what is
 * unsaved first, restores over the version the server really holds, and adopts the result as the saved document. The stand-in
 * server checks every If-Match exactly, so a restore quoting a stale version is a 412 here as it is for real.
 */
import { act, renderHook } from '@testing-library/react';
import { SaveConflict } from '../model/documentClient.ts';
import type { Document } from '../model/types.ts';
import { useDocumentEditor } from './useDocumentEditor.ts';

const initial: Document = {
  id: 'doc_r',
  schemaVersion: 1,
  title: 'Restore me',
  type: 'flowchart',
  nodes: [{ id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } }],
  edges: [],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
};

const restoredContent: Document = { ...initial, title: 'As it was', nodes: [{ id: 'n_old', kind: 'service', label: 'Old box', shape: 'rectangle', geometry: { x: 5, y: 5, w: 100, h: 50 } }] };

function server(options: { putStatus?: number; restoreStatus?: number } = {}) {
  let stored = structuredClone(initial);
  let version = 1;
  const log: string[] = [];
  const etag = (): string => `"dv1:${String(version)}"`;
  const fetchMock = vi.fn<typeof fetch>((input, init) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const method = init?.method ?? 'GET';
    const ifMatch = new Headers(init?.headers).get('If-Match');
    if (method === 'PUT') {
      log.push(`PUT ${String(ifMatch)}`);
      if (options.putStatus !== undefined) {
        return Promise.resolve(new Response('{}', { status: options.putStatus }));
      }
      if (ifMatch !== etag()) {
        return Promise.resolve(new Response('{"title":"Document has changed"}', { status: 412 }));
      }
      stored = JSON.parse(init?.body as string) as Document;
      version += 1;
      return Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: etag() } }));
    }
    if (method === 'POST' && url.endsWith('/restore')) {
      log.push(`RESTORE ${String(ifMatch)}`);
      if (options.restoreStatus !== undefined) {
        return Promise.resolve(new Response('{"title":"Document has changed"}', { status: options.restoreStatus }));
      }
      if (ifMatch !== etag()) {
        return Promise.resolve(new Response('{"title":"Document has changed"}', { status: 412 }));
      }
      stored = structuredClone(restoredContent);
      version += 1;
      return Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: etag() } }));
    }
    log.push('GET');
    return Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: etag() } }));
  });
  vi.stubGlobal('fetch', fetchMock);
  return { log, stored: () => stored };
}

describe('useDocumentEditor restoreVersion', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('saves the unsaved edit first, restores over the version that save produced, and shows the restored document as saved', async () => {
    const stub = server();
    const { result } = renderHook(() => useDocumentEditor('doc_r', initial, '"dv1:1"'));
    act(() => {
      result.current.setLabel('n_a', 'Edited, not yet saved');
    });
    expect(result.current.saveStatus).toBe('unsaved');

    await act(() => result.current.restoreVersion('v_1'));

    // The unsaved edit was stored (so the server checkpoints it), and the restore quoted the version that store made, not the stale one.
    expect(stub.log).toEqual(['PUT "dv1:1"', 'RESTORE "dv1:2"', 'GET']);
    expect(result.current.document.title).toBe('As it was');
    expect(result.current.document.nodes.map((node) => node.label)).toEqual(['Old box']);
    expect(result.current.saveStatus).toBe('saved');
    // The restored version is now the base a later save quotes.
    await act(async () => {
      result.current.setLabel('n_old', 'Next edit');
      await result.current.save();
    });
    expect(stub.log.at(-1)).toBe('PUT "dv1:3"');
  });

  it('does not restore, and keeps the local edit, when the unsaved edit cannot be stored first', async () => {
    const stub = server({ putStatus: 500 });
    const { result } = renderHook(() => useDocumentEditor('doc_r', initial, '"dv1:1"'));
    act(() => {
      result.current.setLabel('n_a', 'Edit that cannot be saved');
    });

    await expect(act(() => result.current.restoreVersion('v_1'))).rejects.toThrow('could not be saved');

    expect(stub.log.some((entry) => entry.startsWith('RESTORE'))).toBe(false);
    expect(result.current.document.nodes[0]?.label).toBe('Edit that cannot be saved');
  });

  it('raises a SaveConflict and leaves the document alone when the server refuses the restore as stale', async () => {
    server({ restoreStatus: 412 });
    const { result } = renderHook(() => useDocumentEditor('doc_r', initial, '"dv1:1"'));

    await expect(act(() => result.current.restoreVersion('v_1'))).rejects.toBeInstanceOf(SaveConflict);

    expect(result.current.document.title).toBe('Restore me');
    expect(result.current.document.nodes[0]?.label).toBe('A');
  });
});
