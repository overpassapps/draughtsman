import { createImportedDocument, exportSql, hasEntities, previewSqlImport, SqlImportFailed } from './sqlImport.ts';
import type { Document } from './types.ts';

const imported = {
  id: 'doc_importer',
  schemaVersion: 1,
  title: 'Imported schema',
  type: 'architecture',
  nodes: [
    { id: 't_Customers', kind: 'entity', label: 'Customers', shape: 'custom:er/entity', props: { columns: ['PK Id int not null'] } },
    { id: 't_Orders', kind: 'entity', label: 'Orders', shape: 'custom:er/entity', props: { columns: ['PK Id int not null', 'FK CustomerId int not null'] } },
  ],
  edges: [{ id: 'e_1', kind: 'association', from: { node: 't_Orders' }, to: { node: 't_Customers' }, label: 'CustomerId', routing: { mode: 'orthogonal' } }],
  meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'sql-import' },
};

function laidOut() {
  return {
    ...imported,
    nodes: imported.nodes.map((node, index) => ({ ...node, geometry: { x: index * 300, y: 0, w: 270, h: 92 } })),
  };
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('previewSqlImport', () => {
  it('imports, then lays the document out, and reports what it found', async () => {
    const calls: { url: string; body: string; method?: string }[] = [];
    const fetchImpl = vi.fn<typeof fetch>((url, init) => {
      calls.push({ url: urlOf(url), body: init?.body as string, ...(init?.method === undefined ? {} : { method: init.method }) });
      return Promise.resolve(urlOf(url).startsWith('/api/import/sql')
        ? json(200, { document: imported, warnings: [{ code: 'unsupported-syntax', message: 'Skipped "SET ANSI_NULLS ON"', line: 2 }] })
        : json(200, { document: laidOut(), warnings: [] }));
    });

    const preview = await previewSqlImport('create table t (a int)', { dialect: 'tsql', type: 'architecture', title: ' Shop ' }, fetchImpl);

    expect(calls.map((call) => call.url)).toEqual(['/api/import/sql?type=architecture&dialect=tsql&title=Shop', '/api/layout?algorithm=layered']);
    expect(calls[0]?.body).toBe('create table t (a int)');
    expect(preview.tables).toBe(2);
    expect(preview.relationships).toBe(1);
    expect(preview.document.nodes[0]?.geometry?.w).toBe(270);
    expect(preview.warnings).toEqual([{ code: 'unsupported-syntax', message: 'Skipped "SET ANSI_NULLS ON"', line: 2 }]);
  });

  it('keeps the laid-out relationships derived: ELK\'s bend points are not stored on them (S7)', async () => {
    const pinned = () => ({ ...laidOut(), edges: imported.edges.map((edge) => ({ ...edge, routing: { mode: 'orthogonal', waypoints: [{ x: 280, y: 40 }] } })) });
    const fetchImpl = vi.fn<typeof fetch>((url) => Promise.resolve(urlOf(url).startsWith('/api/import/sql') ? json(200, { document: imported }) : json(200, { document: pinned() })));

    const preview = await previewSqlImport('x', { dialect: 'auto', type: 'architecture', title: '' }, fetchImpl);

    expect(preview.document.edges[0]?.routing).toEqual({ mode: 'orthogonal' });
  });

  it('leaves the dialect and title out of the request when they are automatic or blank', async () => {
    const fetchImpl = vi.fn<typeof fetch>((url) => Promise.resolve(urlOf(url).startsWith('/api/import/sql') ? json(200, { document: imported }) : json(200, { document: laidOut() })));

    await previewSqlImport('x', { dialect: 'auto', type: 'freeform', title: '  ' }, fetchImpl);

    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/import/sql?type=freeform');
  });

  it('asks the server to skip tag colours only when tags are switched off', async () => {
    const fetchImpl = vi.fn<typeof fetch>((url) => Promise.resolve(urlOf(url).startsWith('/api/import/sql') ? json(200, { document: imported }) : json(200, { document: laidOut() })));

    await previewSqlImport('x', { dialect: 'auto', type: 'freeform', tags: false }, fetchImpl);
    await previewSqlImport('x', { dialect: 'auto', type: 'freeform', tags: true }, fetchImpl);

    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/import/sql?type=freeform&tags=off');
    expect(fetchImpl.mock.calls[2]?.[0]).toBe('/api/import/sql?type=freeform');
  });

  it('asks the server to skip relationships noted in comments only when implied is switched off', async () => {
    const fetchImpl = vi.fn<typeof fetch>((url) => Promise.resolve(urlOf(url).startsWith('/api/import/sql') ? json(200, { document: imported }) : json(200, { document: laidOut() })));

    await previewSqlImport('x', { dialect: 'auto', type: 'freeform', implied: false }, fetchImpl);
    await previewSqlImport('x', { dialect: 'auto', type: 'freeform', implied: true }, fetchImpl);

    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/import/sql?type=freeform&implied=off');
    expect(fetchImpl.mock.calls[2]?.[0]).toBe('/api/import/sql?type=freeform');
  });

  it('counts a weak entity as a table', async () => {
    const withWeak = { ...imported, nodes: [...imported.nodes, { id: 't_Lines', kind: 'weak-entity', label: 'Lines', shape: 'custom:er/weak-entity', props: { columns: ['PK FK OrderId int not null', 'PK Line int not null'] } }] };
    const fetchImpl = vi.fn<typeof fetch>((url) => Promise.resolve(urlOf(url).startsWith('/api/import/sql') ? json(200, { document: withWeak }) : json(200, { document: withWeak })));

    const preview = await previewSqlImport('x', { dialect: 'auto', type: 'architecture' }, fetchImpl);

    expect(preview.tables).toBe(3);
  });

  it('keeps the imported document and adds a warning when the layout engine is unavailable', async () => {
    const fetchImpl = vi.fn<typeof fetch>((url) => Promise.resolve(urlOf(url).startsWith('/api/import/sql')
      ? json(200, { document: imported, warnings: [] })
      : json(503, { title: 'The layout engine is not available.', detail: 'The sidecar is not running.' })));

    const preview = await previewSqlImport('x', { dialect: 'auto', type: 'architecture' }, fetchImpl);

    expect(preview.tables).toBe(2);
    expect(preview.document.nodes[0]?.geometry).toBeUndefined();
    expect(preview.warnings).toHaveLength(1);
    expect(preview.warnings[0]?.code).toBe('layout-unavailable');
    expect(preview.warnings[0]?.message).toContain('The sidecar is not running.');
  });

  it('does not ask for a layout when nothing was imported', async () => {
    const empty = { ...imported, nodes: [], edges: [] };
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(json(200, { document: empty, warnings: [{ code: 'empty-diagram', message: 'No CREATE TABLE statement was found in the source.' }] })));

    const preview = await previewSqlImport('select 1', { dialect: 'auto', type: 'architecture' }, fetchImpl);

    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(preview.tables).toBe(0);
  });

  it('throws the server message when the request is refused', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(json(400, { title: 'Unknown dialect', detail: "'oracle' is not a dialect." })));

    await expect(previewSqlImport('x', { dialect: 'auto', type: 'architecture' }, fetchImpl)).rejects.toThrow(SqlImportFailed);
    await expect(previewSqlImport('x', { dialect: 'auto', type: 'architecture' }, fetchImpl)).rejects.toThrow("'oracle' is not a dialect.");
  });
});

describe('createImportedDocument', () => {
  it('posts the document without the importer id and header so the server assigns its own, and returns the new id', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(json(201, { id: 'doc_new' })));

    const id = await createImportedDocument(laidOut() as unknown as Document, fetchImpl);

    expect(id).toBe('doc_new');
    const [url, init] = fetchImpl.mock.calls[0] ?? [];
    expect(url).toBe('/api/documents');
    expect(init?.method).toBe('POST');
    const sent = JSON.parse(init?.body as string) as Record<string, unknown>;
    expect(sent).not.toHaveProperty('id');
    expect(sent).not.toHaveProperty('meta');
    expect(sent.title).toBe('Imported schema');
    expect(sent.nodes).toHaveLength(2);
  });

  it('throws the server message when the document is refused', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(json(400, { title: 'Document is not valid', detail: 'nope' })));

    await expect(createImportedDocument(laidOut() as unknown as Document, fetchImpl)).rejects.toThrow('nope');
  });
});

describe('exportSql', () => {
  it('posts the document to the dialect and returns the script', async () => {
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response('create table [dbo].[Customers]', { status: 200 })));

    const sql = await exportSql(laidOut() as unknown as Document, 'postgres', fetchImpl);

    expect(sql).toBe('create table [dbo].[Customers]');
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/api/export/sql?dialect=postgres');
  });
});

describe('hasEntities', () => {
  it('is true for a diagram with a table and false for one without', () => {
    expect(hasEntities(laidOut() as unknown as Document)).toBe(true);
    expect(hasEntities({ ...imported, nodes: [{ id: 'n', kind: 'service', label: 'S', shape: 'rectangle' }] } as unknown as Document)).toBe(false);
    expect(hasEntities({ ...imported, nodes: [{ id: 'n', kind: 'shape', label: 'S', shape: 'custom:er/entity' }] } as unknown as Document)).toBe(true);
  });
});
