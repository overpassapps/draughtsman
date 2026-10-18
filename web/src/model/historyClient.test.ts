import { SaveConflict } from './documentClient.ts';
import { critiqueDocument, diffRevisions, isCheckpoint, listVersions, nameSnapshot, restoreVersion } from './historyClient.ts';
import { daysLeft, listBin, purgeDocument, restoreDocument, retentionLine, type BinEntry } from './binClient.ts';
import type { Document } from './types.ts';

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers });
}

const document: Document = { id: 'doc_h', schemaVersion: 1, title: 'H', type: 'flowchart', nodes: [], edges: [], meta: { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' } };

describe('historyClient', () => {
  it('lists versions with their size and tells an automatic checkpoint from a named one', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(json({ items: [{ id: 'v_1', documentId: 'doc_h', name: 'checkpoint', createdAt: '2026-10-01T00:00:00.000Z', actor: 'agent:a/b', size: 12 }], total: 1, offset: 0, limit: 50 })));
    const page = await listVersions('doc_h', 0, 50, fetchImpl);
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/documents/doc_h/versions?offset=0&limit=50');
    expect(page.items[0]?.size).toBe(12);
    const first = page.items[0];
    expect(first === undefined ? undefined : isCheckpoint(first)).toBe(true);
    expect(first === undefined ? undefined : isCheckpoint({ ...first, name: 'Release 1' })).toBe(false);
  });

  it('names a snapshot with the note only when there is one, and surfaces the server reason for a refusal', async () => {
    const ok = vi.fn<typeof fetch>(() => Promise.resolve(json({ id: 'v_2' }, 201)));
    await nameSnapshot('doc_h', 'Release 1', '  ', ok);
    expect(JSON.parse(ok.mock.calls[0]?.[1]?.body as string)).toEqual({ name: 'Release 1' });
    expect(ok.mock.calls[0]?.[1]?.method).toBe('POST');
    await nameSnapshot('doc_h', 'Release 1', 'for review', ok);
    expect(JSON.parse(ok.mock.calls[1]?.[1]?.body as string)).toEqual({ name: 'Release 1', reason: 'for review' });

    const refused = vi.fn<typeof fetch>(() => Promise.resolve(json({ title: 'Reserved name', detail: 'checkpoint is reserved' }, 400)));
    await expect(nameSnapshot('doc_h', 'checkpoint', undefined, refused)).rejects.toThrow('checkpoint is reserved');
  });

  it('restores over the version the caller holds and raises SaveConflict when it is stale', async () => {
    const ok = vi.fn<typeof fetch>(() => Promise.resolve(json({})));
    await restoreVersion('doc_h', 'v_1', '"dv1:x"', ok);
    const [url, init] = ok.mock.calls[0] ?? [];
    expect(url).toBe('/api/documents/doc_h/versions/v_1/restore');
    expect(init?.method).toBe('POST');
    expect(new Headers(init?.headers).get('If-Match')).toBe('"dv1:x"');

    for (const status of [412, 428]) {
      await expect(restoreVersion('doc_h', 'v_1', '"dv1:x"', () => Promise.resolve(json({ title: 'Document has changed' }, status)))).rejects.toBeInstanceOf(SaveConflict);
    }
    await expect(restoreVersion('doc_h', 'v_missing', '"dv1:x"', () => Promise.resolve(json({ title: 'Version not found', detail: 'No version' }, 404)))).rejects.toThrow('No version');
  });

  it('asks for the diff of two revisions by name', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(json({ fromVersion: 'v_1', toVersion: 'current', diff: { addedNodes: [], removedNodes: [], changedNodes: [], addedEdges: [], removedEdges: [], changedEdges: [] } })));
    const result = await diffRevisions('doc_h', 'v_1', 'current', fetchImpl);
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/documents/doc_h/diff?from=v_1&to=current');
    expect(result.diff.addedNodes).toEqual([]);
    await expect(diffRevisions('doc_h', 'v_x', 'current', () => Promise.resolve(json({ detail: 'No version v_x' }, 404)))).rejects.toThrow('No version v_x');
  });

  it('posts the document as it stands for the critique and reads severities as words', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(json({ findings: [{ rule: 'dangling-edge', severity: 'error', message: 'm', nodeIds: ['n_1'], edgeIds: [] }, { rule: 'isolated-node', severity: 'warning', message: 'w', nodeIds: [], edgeIds: ['e_1'] }] })));
    const findings = await critiqueDocument(document, fetchImpl);
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/critique');
    expect((JSON.parse(fetchImpl.mock.calls[0]?.[1]?.body as string) as { id: string }).id).toBe('doc_h');
    expect(findings.map((f) => f.severity)).toEqual(['error', 'warning']);
    await expect(critiqueDocument(document, () => Promise.resolve(json({ detail: 'nope' }, 400)))).rejects.toThrow('nope');
  });
});

describe('binClient', () => {
  const entry = (extra: Partial<BinEntry> = {}): BinEntry => ({ id: 'doc_b', title: 'B', type: 'flowchart', createdAt: '', updatedAt: '', deletedAt: '2026-10-01T00:00:00.000Z', canPurge: true, ...extra });

  it('lists, restores and removes for good through the bin routes, and reports a missing entry as false, not an error', async () => {
    const fetchImpl = vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      if (url.startsWith('/api/bin?')) return Promise.resolve(json({ items: [entry()], total: 1, offset: 0, limit: 200, retentionDays: 30 }));
      if (url.endsWith('/restore')) return Promise.resolve(url.includes('gone') ? json({}, 404) : json({ id: 'doc_b' }));
      return Promise.resolve(init?.method === 'DELETE' && !url.includes('gone') ? new Response(null, { status: 204 }) : json({}, 404));
    });
    expect((await listBin(fetchImpl)).items).toHaveLength(1);
    expect(await restoreDocument('doc_b', fetchImpl)).toBe(true);
    expect(await restoreDocument('gone', fetchImpl)).toBe(false);
    expect(await purgeDocument('doc_b', fetchImpl)).toBe(true);
    expect(await purgeDocument('gone', fetchImpl)).toBe(false);
    await expect(purgeDocument('doc_b', () => Promise.resolve(json({ title: 'Not yours', detail: 'Only an administrator' }, 403)))).rejects.toThrow('Only an administrator');
  });

  it('counts whole days left, rounding up, and none when nothing is scheduled', () => {
    const now = Date.parse('2026-10-01T00:00:00.000Z');
    expect(daysLeft(entry({ purgesAt: '2026-10-31T00:00:00.000Z' }), now)).toBe(30);
    expect(daysLeft(entry({ purgesAt: '2026-10-01T06:00:00.000Z' }), now)).toBe(1);
    expect(daysLeft(entry({ purgesAt: '2026-09-01T00:00:00.000Z' }), now)).toBe(0);
    expect(daysLeft(entry(), now)).toBeUndefined();
    expect(retentionLine(1)).toContain('1 day,');
    expect(retentionLine(0)).toContain('until someone deletes them permanently');
  });
});
