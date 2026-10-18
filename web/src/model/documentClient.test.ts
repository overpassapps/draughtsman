import { createDocument, createDocumentFrom, deleteDocument, fetchDocumentEtag, fetchDocumentIfChanged, generateFromText, GenerateFromTextFailed, listDocuments, putDocument, renameDocument, SaveConflict, SaveFailed } from './documentClient.ts';
import type { Document } from './types.ts';

const document: Document = {
  id: 'doc_save',
  schemaVersion: 1,
  title: 'Save',
  type: 'flowchart',
  nodes: [],
  edges: [],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
};

function respond(status: number, headers: Record<string, string> = {}): typeof fetch {
  return vi.fn(() => Promise.resolve(new Response(status === 200 ? '{}' : 'refused', { status, headers })));
}

describe('putDocument', () => {
  it('sends the base version as If-Match and returns the new version from the ETag', async () => {
    const fetchImpl = respond(200, { ETag: '"dv1:new"' });
    await expect(putDocument('doc_save', document, '"dv1:old"', fetchImpl)).resolves.toBe('"dv1:new"');
    const [url, init] = vi.mocked(fetchImpl).mock.calls[0] ?? [];
    expect(url).toBe('/api/documents/doc_save');
    expect(init?.method).toBe('PUT');
    expect(new Headers(init?.headers).get('If-Match')).toBe('"dv1:old"');
  });

  it('tries once more when a 403 says it carries a fresh CSRF pair, and gives up on a plain 403', async () => {
    // A tab that outlived a server upgrade: the first refusal reissues the pair, the second attempt (which reads the cookie again) is accepted.
    const accepted = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(new Response('refused', { status: 403, headers: { 'X-Draughtsman-Csrf': 'reissued' } }))
      .mockResolvedValueOnce(new Response('{}', { status: 200, headers: { ETag: '"dv1:new"' } }));
    await expect(putDocument('doc_save', document, '"dv1:old"', accepted)).resolves.toBe('"dv1:new"');
    expect(accepted).toHaveBeenCalledTimes(2);

    // Only the marked refusal is retried, and only once: a 403 that is about permission, or one that keeps refusing, is not looped on.
    const plain = respond(403);
    await expect(putDocument('doc_save', document, '"dv1:old"', plain)).rejects.toMatchObject({ name: 'SaveFailed', status: 403 });
    expect(plain).toHaveBeenCalledTimes(1);
    const stuck = respond(403, { 'X-Draughtsman-Csrf': 'reissued' });
    await expect(putDocument('doc_save', document, '"dv1:old"', stuck)).rejects.toMatchObject({ name: 'SaveFailed', status: 403 });
    expect(stuck).toHaveBeenCalledTimes(2);
  });

  it('raises a SaveConflict for 412 and 428, so the caller can tell a refusal from a failure', async () => {
    await expect(putDocument('doc_save', document, '"dv1:old"', respond(412))).rejects.toMatchObject({ name: 'SaveConflict', status: 412 });
    await expect(putDocument('doc_save', document, undefined, respond(428))).rejects.toBeInstanceOf(SaveConflict);
  });

  it('raises a plain error for any other failure', async () => {
    const failure = putDocument('doc_save', document, '"dv1:old"', respond(500));
    await expect(failure).rejects.toThrow('500');
    await expect(failure).rejects.not.toBeInstanceOf(SaveConflict);
  });

  it('carries the status of a failure, so autosave can tell a server that is down from one that refuses', async () => {
    await expect(putDocument('doc_save', document, '"dv1:old"', respond(503))).rejects.toMatchObject({ name: 'SaveFailed', status: 503 });
    await expect(putDocument('doc_save', document, '"dv1:old"', respond(403))).rejects.toBeInstanceOf(SaveFailed);
  });

  it('asks the browser to finish a flush as the page goes away, but only where the body fits a keepalive request', async () => {
    const small = respond(200, { ETag: '"dv1:new"' });
    await putDocument('doc_save', document, '"dv1:old"', small, { keepalive: true });
    expect(vi.mocked(small).mock.calls[0]?.[1]?.keepalive).toBe(true);

    const plain = respond(200, { ETag: '"dv1:new"' });
    await putDocument('doc_save', document, '"dv1:old"', plain);
    expect(vi.mocked(plain).mock.calls[0]?.[1]?.keepalive).toBeUndefined();

    // Chrome rejects a keepalive body over 64 KiB outright, which would fail the flush instead of making it.
    const big = respond(200, { ETag: '"dv1:new"' });
    const large = { ...document, title: 'x'.repeat(70_000) };
    await putDocument('doc_save', large, '"dv1:old"', big, { keepalive: true });
    expect(vi.mocked(big).mock.calls[0]?.[1]?.keepalive).toBeUndefined();
  });
});

describe('createDocumentFrom', () => {
  it('posts the document without its id and meta, which belong to the one it was copied from, and returns the new id', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{"id":"doc_new"}', { status: 201 })));
    await expect(createDocumentFrom({ ...document, title: 'Save (my copy)' }, fetchImpl)).resolves.toBe('doc_new');
    const [url, init] = fetchImpl.mock.calls[0] ?? [];
    expect(url).toBe('/api/documents');
    expect(init?.method).toBe('POST');
    const body = JSON.parse(init?.body as string) as Record<string, unknown>;
    expect(body).toMatchObject({ title: 'Save (my copy)', type: 'flowchart', schemaVersion: 1 });
    expect(body).not.toHaveProperty('id');
    expect(body).not.toHaveProperty('meta');
  });

  it('throws when the server refuses', async () => {
    await expect(createDocumentFrom(document, respond(400))).rejects.toThrow('400');
  });
});

describe('fetchDocumentIfChanged', () => {
  const stored = { ...document, title: 'Stored' };

  it('asks with the version it has and resolves undefined while the server says 304', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response(null, { status: 304, headers: { ETag: '"dv1:have"' } })));
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', fetchImpl)).resolves.toBeUndefined();
    const [url, init] = fetchImpl.mock.calls[0] ?? [];
    expect(url).toBe('/api/documents/doc_save');
    expect(new Headers(init?.headers).get('If-None-Match')).toBe('"dv1:have"');
    expect(init?.cache).toBe('no-store');
  });

  it('returns the newer document with its version, and nothing for the version it already has', async () => {
    const answer = (etag: string) => vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: etag } })));
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', answer('"dv1:next"'))).resolves.toEqual({ document: stored, etag: '"dv1:next"' });
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', answer('"dv1:have"'))).resolves.toBeUndefined();
  });

  it('throws for a failure, a missing ETag or a body that is not a document, so nothing half-read is merged', async () => {
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', respond(500))).rejects.toThrow('500');
    const untagged = vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify(stored), { status: 200 })));
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', untagged)).rejects.toThrow('ETag');
    const junk = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{"title":1}', { status: 200, headers: { ETag: '"dv1:next"' } })));
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', junk)).rejects.toThrow();
  });
});

describe('listDocuments', () => {
  it('returns the page items the server lists', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() =>
      Promise.resolve(new Response(JSON.stringify({ items: [{ id: 'doc_a', title: 'A', type: 'flowchart' }], total: 1 }), { status: 200 })),
    );
    await expect(listDocuments(fetchImpl)).resolves.toEqual([{ id: 'doc_a', title: 'A', type: 'flowchart' }]);
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/documents');
  });
});

describe('createDocument', () => {
  it('posts a blank document of the type and returns its id', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{"id":"doc_new"}', { status: 201 })));
    await expect(createDocument('New', 'architecture', undefined, fetchImpl)).resolves.toBe('doc_new');
    const [url, init] = fetchImpl.mock.calls[0] ?? [];
    expect(url).toBe('/api/documents');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(init?.body as string)).toEqual({ title: 'New', type: 'architecture', nodes: [], edges: [] });
  });
});

describe('generateFromText', () => {
  it('posts the description, diagram type and document id, and parses the returned document and provenance', async () => {
    const responseBody = {
      document,
      warnings: ['edge missing a label'],
      addedNodeIds: ['n_a'],
      addedEdgeIds: [],
      changedElementCount: 2,
      removedElementCount: 1,
      provenance: { provider: 'ollama', model: 'llama3.1' },
    };
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify(responseBody), { status: 200 })));

    const result = await generateFromText('a login flow', 'flowchart', 'doc_save', fetchImpl);

    const [url, init] = fetchImpl.mock.calls[0] ?? [];
    expect(url).toBe('/api/ai/generate-from-text');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(init?.body as string)).toEqual({ description: 'a login flow', diagramType: 'flowchart', documentId: 'doc_save' });
    expect(result).toEqual({
      document,
      warnings: ['edge missing a label'],
      addedNodeIds: ['n_a'],
      addedEdgeIds: [],
      changedElementCount: 2,
      removedElementCount: 1,
      provenance: { provider: 'ollama', model: 'llama3.1' },
    });
  });

  it('omits documentId when generating a new document rather than patching one', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() =>
      Promise.resolve(new Response(JSON.stringify({ document, warnings: [], addedNodeIds: [], addedEdgeIds: [], changedElementCount: 0, removedElementCount: 0, provenance: { provider: 'ollama', model: 'llama3.1' } }), { status: 200 })),
    );

    await generateFromText('a login flow', 'flowchart', undefined, fetchImpl);

    // Two-sided against the previous test: the same call, minus documentId, must serialise it as absent (undefined),
    // not as the string "undefined" or a leftover value from a prior call.
    const [, init] = fetchImpl.mock.calls[0] ?? [];
    expect(JSON.parse(init?.body as string)).toEqual({ description: 'a login flow', diagramType: 'flowchart', documentId: undefined });
  });

  it('raises GenerateFromTextFailed carrying the status and the problem detail on failure', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() =>
      Promise.resolve(new Response(JSON.stringify({ title: 'The AI provider is unreachable', detail: 'Could not reach Ollama.' }), { status: 502 })),
    );

    const failure = generateFromText('a login flow', 'flowchart', undefined, fetchImpl);

    await expect(failure).rejects.toBeInstanceOf(GenerateFromTextFailed);
    await expect(failure).rejects.toMatchObject({ status: 502, message: 'Could not reach Ollama.' });
  });

  it('is hidden-when-off from the caller\'s point of view: a 404 still raises GenerateFromTextFailed, not a generic throw', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify({ title: 'AI is not configured' }), { status: 404 })));

    await expect(generateFromText('a login flow', 'flowchart', undefined, fetchImpl)).rejects.toMatchObject({ status: 404 });
  });
});

describe('renameDocument', () => {
  it('sends the base version as If-Match and returns the new ETag', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:new"' } })));
    await expect(renameDocument('doc_a', 'Renamed', '"dv1:old"', fetchImpl)).resolves.toBe('"dv1:new"');
    const [url, init] = fetchImpl.mock.calls[0] ?? [];
    expect(url).toBe('/api/documents/doc_a');
    expect(init?.method).toBe('PATCH');
    expect(new Headers(init?.headers).get('If-Match')).toBe('"dv1:old"');
    expect(JSON.parse(init?.body as string)).toEqual({ title: 'Renamed' });
  });

  it('raises a SaveConflict for a stale base version', async () => {
    await expect(renameDocument('doc_a', 'Renamed', '"dv1:stale"', respond(412))).rejects.toMatchObject({ name: 'SaveConflict', status: 412 });
  });
});

describe('deleteDocument', () => {
  it('resolves true on 204 and false on 404', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response(null, { status: 204 })));
    await expect(deleteDocument('doc_a', fetchImpl)).resolves.toBe(true);
    const missing = vi.fn<typeof fetch>(() => Promise.resolve(new Response(null, { status: 404 })));
    await expect(deleteDocument('doc_a', missing)).resolves.toBe(false);
  });
});

describe('fetchDocumentEtag', () => {
  it('returns the ETag and rejects a response that carries none', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:x"' } })));
    await expect(fetchDocumentEtag('doc_a', fetchImpl)).resolves.toBe('"dv1:x"');
    const untagged = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200 })));
    await expect(fetchDocumentEtag('doc_a', untagged)).rejects.toThrow('ETag');
  });
});

describe('the version a response carries (X-Document-Version first, then the ETag)', () => {
  const stored = JSON.stringify(document);

  it('putDocument and renameDocument return X-Document-Version even when a gateway weakened or removed the ETag', async () => {
    const weakened = respond(200, { ETag: 'W/"dv1:gateway"', 'X-Document-Version': 'dv1:new' });
    await expect(putDocument('doc_save', document, '"dv1:old"', weakened)).resolves.toBe('"dv1:new"');
    const stripped = respond(200, { 'X-Document-Version': 'dv1:new' });
    await expect(putDocument('doc_save', document, '"dv1:old"', stripped)).resolves.toBe('"dv1:new"');
    await expect(renameDocument('doc_save', 'New', '"dv1:old"', stripped)).resolves.toBe('"dv1:new"');
  });

  it('putDocument and renameDocument fall back to the ETag, as a strong tag, when the server sends no X-Document-Version', async () => {
    const weak = respond(200, { ETag: 'W/"dv1:new"' });
    await expect(putDocument('doc_save', document, '"dv1:old"', weak)).resolves.toBe('"dv1:new"');
    await expect(renameDocument('doc_save', 'New', '"dv1:old"', weak)).resolves.toBe('"dv1:new"');
  });

  it('fetchDocumentEtag hands back a strong tag a rename can quote, from either header', async () => {
    const own = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200, headers: { 'X-Document-Version': 'dv1:x' } })));
    await expect(fetchDocumentEtag('doc_a', own)).resolves.toBe('"dv1:x"');
    const weak = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: 'W/"dv1:x"' } })));
    await expect(fetchDocumentEtag('doc_a', weak)).resolves.toBe('"dv1:x"');
  });

  it('fetchDocumentIfChanged reads the version from X-Document-Version, and treats a weakened copy of the version it has as unchanged', async () => {
    const answer = (headers: Record<string, string>) => vi.fn<typeof fetch>(() => Promise.resolve(new Response(stored, { status: 200, headers })));
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', answer({ 'X-Document-Version': 'dv1:next' }))).resolves.toMatchObject({ etag: '"dv1:next"' });
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', answer({ ETag: 'W/"dv1:next"' }))).resolves.toMatchObject({ etag: '"dv1:next"' });
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', answer({ ETag: 'W/"dv1:have"' }))).resolves.toBeUndefined();
    await expect(fetchDocumentIfChanged('doc_save', '"dv1:have"', answer({ ETag: 'W/"dv1:other"', 'X-Document-Version': 'dv1:have' }))).resolves.toBeUndefined();
  });
});
