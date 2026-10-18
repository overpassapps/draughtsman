/**
 * The write half of the document HTTP contract. The browser and an agent both save through `PUT /api/documents/{id}`
 * with the `If-Match` header carrying the version the save was composed from (docs/api.md). A save whose base version
 * is no longer current is refused with 412, and one with no base version with 428; both surface as a typed
 * `SaveConflict` the editor must show rather than silently swallow, because the guarded write leaves the local
 * document untouched and what to do next (merge, reload) is the caller's decision.
 */

import { csrfHeaders } from './csrf.ts';
import { readDocumentVersion } from './documentVersion.ts';
import { parseDocument } from './parse.ts';
import type { Document, DocumentType, Edge, Node } from './types.ts';

/** Raised when the server refused the save because the base version was stale (412) or absent (428). */
export class SaveConflict extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'SaveConflict';
    this.status = status;
  }
}

/** Raised when a save reached the server and was refused for a reason other than a stale base version (`status` says which). */
export class SaveFailed extends Error {
  readonly status: number;

  constructor(status: number) {
    super(`Saving failed: the server returned ${String(status)}`);
    this.name = 'SaveFailed';
    this.status = status;
  }
}

/** Set by the server on a 403 that carries a fresh CSRF pair (src/Draughtsman.Server/Auth/CsrfMiddleware.cs `ReissuedHeaderName`). */
const CSRF_REISSUED_HEADER = 'X-Draughtsman-Csrf';

/** A `keepalive` request body may not exceed 64 KiB in Chrome; past this the flush on leaving falls back to a plain request. */
const KEEPALIVE_LIMIT = 60_000;

export interface PutOptions {
  /** Ask the browser to finish the request even if the page goes away (a flush as the tab closes), where the body is small enough. */
  keepalive?: boolean;
}

/** Replaces the document snapshot, requiring the base version it was composed from, and returns the new version tag. */
export async function putDocument(
  id: string,
  document: Document,
  etag: string | undefined,
  fetchImpl: typeof fetch = fetch,
  options: PutOptions = {},
): Promise<string> {
  const body = JSON.stringify(document);
  const send = (): Promise<Response> =>
    fetchImpl(`/api/documents/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
        ...(etag === undefined ? {} : { 'If-Match': etag }),
        ...csrfHeaders(),
      },
      body,
      ...(options.keepalive === true && body.length < KEEPALIVE_LIMIT ? { keepalive: true } : {}),
    });
  let response = await send();
  // A tab that stayed open across a server upgrade holds a CSRF pair the new server cannot read. Its refusal carries a fresh pair (CsrfMiddleware), so
  // one more attempt, reading the cookie again, saves the edits instead of leaving them stuck behind a 403 until the page is reloaded.
  if (response.status === 403 && response.headers.get(CSRF_REISSUED_HEADER) === 'reissued') {
    response = await send();
  }
  if (response.status === 412 || response.status === 428) {
    let detail = '';
    try {
      detail = await response.text();
    } catch {
      // The status alone is enough to act on; the body is a nicety.
    }
    throw new SaveConflict(response.status, detail);
  }
  if (!response.ok) {
    throw new SaveFailed(response.status);
  }
  return readDocumentVersion(response) ?? etag ?? '';
}
/** A stored document and the version tag it carries. */
export interface VersionedDocument {
  document: Document;
  etag: string;
}

/**
 * The read half of live sync (checklist item 30): a conditional GET that sends the version the caller already has as
 * `If-None-Match`. The server answers 304 with no body while that is still the stored version, so polling an open
 * document costs one small request; any other version comes back whole with its new ETag. Resolves undefined for 304,
 * and throws for anything that is neither, so a caller can keep polling past a failure without applying it.
 */
export async function fetchDocumentIfChanged(
  id: string,
  etag: string | undefined,
  fetchImpl: typeof fetch = fetch,
  signal?: AbortSignal,
): Promise<VersionedDocument | undefined> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(id)}`, {
    headers: { Accept: 'application/json', ...(etag === undefined ? {} : { 'If-None-Match': etag }) },
    cache: 'no-store',
    ...(signal === undefined ? {} : { signal }),
  });
  if (response.status === 304) {
    return undefined;
  }
  if (!response.ok) {
    throw new Error(`Reading the document failed: the server returned ${String(response.status)}`);
  }
  const next = readDocumentVersion(response);
  if (next === undefined) {
    throw new Error('Reading the document failed: the response carried no version (X-Document-Version or ETag)');
  }
  if (next === etag) {
    return undefined;
  }
  return { document: parseDocument(await response.json()), etag: next };
}

/** A document listing row, as `GET /api/documents` returns it (docs/api.md). */
export interface DocumentSummary {
  id: string;
  title: string;
  type: DocumentType;
  workspace: string;
  createdAt: string;
  updatedAt: string;
}

/** A page of document summaries plus the total, mirroring the server's `DocumentPage`. */
export interface DocumentListPage {
  items: DocumentSummary[];
  total: number;
  offset: number;
  limit: number;
}

/** What the list can be ordered by, and in which direction (docs/api.md `GET /api/documents`). */
export type DocumentSort = 'modified' | 'created' | 'title' | 'type';

export interface DocumentListQuery {
  /** Title search; blank or undefined lists everything. */
  q?: string | undefined;
  sort?: DocumentSort | undefined;
  /** The direction as the reader sees it: `asc` is A to Z or oldest first, `desc` Z to A or newest first. */
  dir?: 'asc' | 'desc' | undefined;
  type?: DocumentType | undefined;
  limit?: number | undefined;
}

/** Lists documents (docs/api.md `GET /api/documents`): newest updated first unless the query says otherwise. Whatever is in the bin is not listed. */
export async function listDocumentsPage(query: DocumentListQuery = {}, fetchImpl: typeof fetch = fetch, signal?: AbortSignal): Promise<DocumentListPage> {
  const params = new URLSearchParams();
  if (query.q !== undefined && query.q.trim() !== '') {
    params.set('q', query.q.trim());
  }
  if (query.sort !== undefined) {
    params.set('sort', query.sort);
  }
  if (query.dir !== undefined) {
    params.set('dir', query.dir);
  }
  if (query.type !== undefined) {
    params.set('type', query.type);
  }
  if (query.limit !== undefined) {
    params.set('limit', String(query.limit));
  }
  const suffix = params.size === 0 ? '' : `?${params.toString()}`;
  const response = await fetchImpl(`/api/documents${suffix}`, { headers: { Accept: 'application/json' }, ...(signal === undefined ? {} : { signal }) });
  if (!response.ok) {
    throw new Error(`Listing documents failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as DocumentListPage;
}

/** Lists documents, newest updated first (docs/api.md `GET /api/documents`). */
export async function listDocuments(fetchImpl: typeof fetch = fetch): Promise<DocumentSummary[]> {
  return (await listDocumentsPage({}, fetchImpl)).items;
}

/**
 * Stores a copy of a document under a new id (docs/api.md `POST /api/documents/{id}/duplicate`): the server names it
 * "<title> (copy)" unless `title` says otherwise. Resolves the new document's id; opens nothing.
 */
export async function duplicateDocument(id: string, title?: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(id)}/duplicate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(title === undefined ? {} : { title }),
  });
  if (!response.ok) {
    throw new Error(`Duplicating the document failed: the server returned ${String(response.status)}`);
  }
  return ((await response.json()) as { id: string }).id;
}

/**
 * Creates a document of `type` and returns its id, opening nothing itself (docs/api.md `POST /api/documents`). It is
 * blank unless `starter` names the nodes and edges to begin with (a sequence diagram's two participants and a message).
 */
export async function createDocument(
  title: string,
  type: DocumentType,
  starter: { nodes: Node[]; edges: Edge[] } = { nodes: [], edges: [] },
  fetchImpl: typeof fetch = fetch,
): Promise<string> {
  const body = { title, type, nodes: starter.nodes, edges: starter.edges };
  const response = await fetchImpl('/api/documents', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`Creating the document failed: the server returned ${String(response.status)}`);
  }
  const created = (await response.json()) as { id: string };
  return created.id;
}

/**
 * Stores `document` as a new document of its own (docs/api.md `POST /api/documents`): the id and meta are left for the
 * server to assign, since they belong to the document it was copied from. Resolves the new document's id.
 */
export async function createDocumentFrom(document: Document, fetchImpl: typeof fetch = fetch): Promise<string> {
  const rest = Object.fromEntries(Object.entries(document).filter(([key]) => key !== 'id' && key !== 'meta'));
  const response = await fetchImpl('/api/documents', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(rest),
  });
  if (!response.ok) {
    throw new Error(`Creating the document failed: the server returned ${String(response.status)}`);
  }
  const created = (await response.json()) as { id: string };
  return created.id;
}

/**
 * Renames a document (docs/api.md `PATCH /api/documents/{id}`). Like a save it is guarded, so the caller must pass the
 * base version the rename was composed from; a stale one throws `SaveConflict`, same as a refused save.
 */
export async function renameDocument(id: string, title: string, etag: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'If-Match': etag, ...csrfHeaders() },
    body: JSON.stringify({ title }),
  });
  if (response.status === 412 || response.status === 428) {
    let detail = '';
    try {
      detail = await response.text();
    } catch {
      // The status alone is enough to act on; the body is a nicety.
    }
    throw new SaveConflict(response.status, detail);
  }
  if (!response.ok) {
    throw new Error(`Renaming the document failed: the server returned ${String(response.status)}`);
  }
  return readDocumentVersion(response) ?? etag;
}

/**
 * Moves a document to the bin (docs/api.md `DELETE /api/documents/{id}`); it stays recoverable until the retention
 * period ends (`binClient.ts`). Resolves false when there was no such document.
 */
export async function deleteDocument(id: string, fetchImpl: typeof fetch = fetch): Promise<boolean> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  if (response.status === 404) {
    return false;
  }
  if (!response.ok) {
    throw new Error(`Deleting the document failed: the server returned ${String(response.status)}`);
  }
  return true;
}

/** A diagram type Generate from text can produce; freeform has no kind vocabulary to write valid kinds against. */
export type GeneratableDiagramType = Exclude<DocumentType, 'freeform'>;

/** Who produced a generated draft (build-spec-v0.1.md section 9's aiProvenance), for the panel's provenance line. */
export interface GenerateFromTextProvenance {
  provider: string;
  model: string;
}

/**
 * The full result of a generate-from-text call: the server's new document, what it added, and - for an update to an
 * existing document, where the model could also have changed or removed something already there - how many
 * elements it changed or removed.
 */
export interface GenerateFromTextResult {
  document: Document;
  warnings: string[];
  addedNodeIds: string[];
  addedEdgeIds: string[];
  changedElementCount: number;
  removedElementCount: number;
  provenance: GenerateFromTextProvenance;
}

/** Raised when the server refused or could not complete a generate-from-text request. Carries the HTTP status so the panel can tell "AI off" (404) from a model/parse failure (422/502) apart if it needs to. */
export class GenerateFromTextFailed extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'GenerateFromTextFailed';
    this.status = status;
  }
}

/**
 * "Generate from text" (build-spec-v0.1.md section 9, MVP AI feature 1; docs/api.md, design/atlas/ai-panel.md).
 * With `documentId`, the server patches the generated nodes/edges into that document (checkpointed, aiProvenance);
 * without it, the server creates a new document. Either way the returned document is the server's new truth: the
 * caller applies it locally through the same `applyTextDocument`/`applyDocumentDiff` path the DSL/Text panel uses,
 * so Insert lands as one local undo step.
 */
export async function generateFromText(
  description: string,
  diagramType: GeneratableDiagramType,
  documentId: string | undefined,
  fetchImpl: typeof fetch = fetch,
): Promise<GenerateFromTextResult> {
  const response = await fetchImpl('/api/ai/generate-from-text', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ description, diagramType, documentId }),
  });
  if (!response.ok) {
    let detail = `Generate from text failed: the server returned ${String(response.status)}`;
    try {
      const problem = (await response.json()) as { detail?: string; title?: string };
      detail = problem.detail ?? problem.title ?? detail;
    } catch {
      // The status alone is enough to act on; the body is a nicety.
    }
    throw new GenerateFromTextFailed(response.status, detail);
  }
  const body = (await response.json()) as {
    document: unknown;
    warnings: string[];
    addedNodeIds: string[];
    addedEdgeIds: string[];
    changedElementCount: number;
    removedElementCount: number;
    provenance: GenerateFromTextProvenance;
  };
  return {
    document: parseDocument(body.document),
    warnings: body.warnings,
    addedNodeIds: body.addedNodeIds,
    addedEdgeIds: body.addedEdgeIds,
    changedElementCount: body.changedElementCount,
    removedElementCount: body.removedElementCount,
    provenance: body.provenance,
  };
}

/** The three documents "Diagram to document" can produce (design/atlas/ai-panel.md). */
export type DiagramToDocumentOutputKind = 'design-doc' | 'runbook' | 'explanation';

/** The result of a diagram-to-document call: the model's Markdown and who produced it. */
export interface DiagramToDocumentResult {
  markdown: string;
  provenance: GenerateFromTextProvenance;
}

/** Raised when the server refused or could not complete a diagram-to-document request. */
export class DiagramToDocumentFailed extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'DiagramToDocumentFailed';
    this.status = status;
  }
}

/**
 * "Diagram to document" (build-spec-v0.1.md section 9, MVP AI feature 2; docs/api.md, design/atlas/ai-panel.md).
 * The server reads the document's outline, asks the model for a design doc, runbook or explanation that cites the
 * diagram's elements by id, and returns the Markdown. Nothing is written to the document, so there is no local
 * apply step: the panel shows the Markdown and offers Copy and Download.
 */
export async function diagramToDocument(
  documentId: string,
  outputKind: DiagramToDocumentOutputKind,
  fetchImpl: typeof fetch = fetch,
): Promise<DiagramToDocumentResult> {
  const response = await fetchImpl('/api/ai/diagram-to-document', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ documentId, outputKind }),
  });
  if (!response.ok) {
    let detail = `Diagram to document failed: the server returned ${String(response.status)}`;
    try {
      const problem = (await response.json()) as { detail?: string; title?: string };
      detail = problem.detail ?? problem.title ?? detail;
    } catch {
      // The status alone is enough to act on; the body is a nicety.
    }
    throw new DiagramToDocumentFailed(response.status, detail);
  }
  const body = (await response.json()) as { markdown: string; provenance: GenerateFromTextProvenance };
  return { markdown: body.markdown, provenance: body.provenance };
}

/** Reads a document to learn its version, so a rename can quote it as its base version. */
export async function fetchDocumentEtag(id: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(id)}`, { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Reading the document failed: the server returned ${String(response.status)}`);
  }
  const etag = readDocumentVersion(response);
  if (etag === undefined) {
    throw new Error('Reading the document failed: the response carried no version (X-Document-Version or ETag)');
  }
  return etag;
}
