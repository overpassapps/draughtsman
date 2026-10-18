/**
 * The client half of a document's history (docs/api.md "History, compare and restore", design/atlas/version-history.md):
 * the stored versions, naming one, the structured diff between two revisions, restoring one, and the critique. The
 * diff and the critique are the server's own `DocumentDiff` and `CritiqueAnalyser` (the `diff_documents` and
 * `critique_document` tools), returned in the same shapes, not re-derived here.
 */

import { csrfHeaders } from './csrf.ts';
import { SaveConflict } from './documentClient.ts';
import type { Document, Edge, Node } from './types.ts';

/** One stored version, as `GET /api/documents/{id}/versions` lists it. */
export interface VersionSummary {
  id: string;
  documentId: string;
  /** What a person called it, or `checkpoint` for the automatic snapshot taken before an agent's edit or a restore. */
  name: string;
  createdAt: string;
  /** `user:<name>` or `agent:<name>/<model>`. */
  actor: string;
  reason?: string;
  /** Length of the stored snapshot, in characters. */
  size: number;
}

export interface VersionListing {
  items: VersionSummary[];
  total: number;
  offset: number;
  limit: number;
}

/** The name the server gives automatic snapshots; a person cannot choose it. */
export const CHECKPOINT_NAME = 'checkpoint';

export function isCheckpoint(version: VersionSummary): boolean {
  return version.name === CHECKPOINT_NAME;
}

export const VERSION_PAGE = 50;

async function problemText(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    return problem.detail ?? problem.title ?? fallback;
  } catch {
    return fallback;
  }
}

export async function listVersions(documentId: string, offset = 0, limit = VERSION_PAGE, fetchImpl: typeof fetch = fetch): Promise<VersionListing> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(documentId)}/versions?offset=${String(offset)}&limit=${String(limit)}`, {
    headers: { Accept: 'application/json' },
    cache: 'no-store',
  });
  if (!response.ok) {
    throw new Error(`Listing the history failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as VersionListing;
}

/** Names a snapshot of the document as it is stored now. The server refuses an empty name and the reserved `checkpoint`. */
export async function nameSnapshot(documentId: string, name: string, reason?: string, fetchImpl: typeof fetch = fetch): Promise<VersionSummary> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(documentId)}/versions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(reason === undefined || reason.trim() === '' ? { name } : { name, reason }),
  });
  if (!response.ok) {
    throw new Error(await problemText(response, `Naming the version failed: the server returned ${String(response.status)}`));
  }
  return (await response.json()) as VersionSummary;
}

/**
 * Puts the document back to a version. A guarded save like any other: `etag` is the version the editor is looking at,
 * and a stale one throws `SaveConflict` and changes nothing. The server checkpoints the document as it is first.
 */
export async function restoreVersion(documentId: string, versionId: string, etag: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(documentId)}/versions/${encodeURIComponent(versionId)}/restore`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'If-Match': etag, ...csrfHeaders() },
  });
  if (response.status === 412 || response.status === 428) {
    throw new SaveConflict(response.status, await problemText(response, 'The document changed since it was opened.'));
  }
  if (!response.ok) {
    throw new Error(await problemText(response, `Restoring the version failed: the server returned ${String(response.status)}`));
  }
}

/** Which top-level fields of one element differ between the two revisions (`FieldChange` in DocumentDiff, values left unparsed). */
export interface FieldChange {
  field: string;
  from?: unknown;
  to?: unknown;
}

export interface ElementChange {
  id: string;
  fields: FieldChange[];
}

/** `DocumentDiffResult`: what `diff_documents` returns, matched by id. */
export interface DocumentDiffResult {
  addedNodes: Node[];
  removedNodes: Node[];
  changedNodes: ElementChange[];
  addedEdges: Edge[];
  removedEdges: Edge[];
  changedEdges: ElementChange[];
  movedNodes?: { id: string; after?: string }[];
  movedEdges?: { id: string; after?: string }[];
}

export interface RevisionDiff {
  fromVersion: string;
  toVersion: string;
  diff: DocumentDiffResult;
}

/** The diff between two revisions of a document: a version id, or `current` for the stored document. */
export async function diffRevisions(documentId: string, from: string, to = 'current', fetchImpl: typeof fetch = fetch): Promise<RevisionDiff> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(documentId)}/diff?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`, {
    headers: { Accept: 'application/json' },
    cache: 'no-store',
  });
  if (!response.ok) {
    throw new Error(await problemText(response, `Comparing the versions failed: the server returned ${String(response.status)}`));
  }
  return (await response.json()) as RevisionDiff;
}

/** The document at one stored version, for drawing where a removed element used to be. */
export async function fetchVersionDocument(documentId: string, versionId: string, fetchImpl: typeof fetch = fetch): Promise<Document> {
  const response = await fetchImpl(`/api/documents/${encodeURIComponent(documentId)}/versions/${encodeURIComponent(versionId)}`, {
    headers: { Accept: 'application/json' },
    cache: 'no-store',
  });
  if (!response.ok) {
    throw new Error(`Reading the version failed: the server returned ${String(response.status)}`);
  }
  return ((await response.json()) as { document: Document }).document;
}

export type FindingSeverity = 'error' | 'warning';

/** One structural defect the critique found (`CritiqueFinding`): a stable rule id, what is wrong, and the ids that are the evidence. */
export interface CritiqueFinding {
  rule: string;
  severity: FindingSeverity;
  message: string;
  nodeIds: string[];
  edgeIds: string[];
}

/**
 * Critiques the document as it is on the canvas (`POST /api/critique`, the `critique_document` analyser), so a finding
 * is about what the person is looking at, unsaved edits included.
 */
export async function critiqueDocument(document: Document, fetchImpl: typeof fetch = fetch, signal?: AbortSignal): Promise<CritiqueFinding[]> {
  const response = await fetchImpl('/api/critique', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(document),
    ...(signal === undefined ? {} : { signal }),
  });
  if (!response.ok) {
    throw new Error(await problemText(response, `Checking the diagram failed: the server returned ${String(response.status)}`));
  }
  const body = (await response.json()) as { findings?: { rule: string; severity: string | number; message: string; nodeIds?: string[]; edgeIds?: string[] }[] };
  return (body.findings ?? []).map((finding) => ({
    rule: finding.rule,
    severity: String(finding.severity).toLowerCase() === 'error' || finding.severity === 0 ? 'error' : 'warning',
    message: finding.message,
    nodeIds: finding.nodeIds ?? [],
    edgeIds: finding.edgeIds ?? [],
  }));
}
