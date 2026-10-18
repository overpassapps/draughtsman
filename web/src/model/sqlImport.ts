/**
 * The SQL import and export calls (docs/api.md `POST /api/import/sql`, `POST /api/export/sql`). Import is a preview
 * followed by a create: the pasted DDL goes to the importer, which returns the entities and relationships it read and
 * everything it skipped, and the document is then laid out (the importer places nothing) so it opens tidy. Nothing is
 * saved until `createImportedDocument`, so the paste box can show the warnings first. A layout that cannot run does not
 * fail the import: the document is returned as imported (the canvas shows an unplaced node on its fallback grid) and
 * the reason is added to the warnings.
 */

import { csrfHeaders } from './csrf.ts';
import { unpinOrthogonalRoutes } from './layoutClient.ts';
import { parseDocument } from './parse.ts';
import type { Document } from './types.ts';

/** The dialects the importer and exporter know; `auto` (import only) detects it from the text. */
export type SqlDialectChoice = 'auto' | 'tsql' | 'postgres' | 'mysql' | 'sqlite';

export const SQL_DIALECTS: readonly { value: SqlDialectChoice; label: string }[] = [
  { value: 'auto', label: 'Detect from the SQL' },
  { value: 'tsql', label: 'SQL Server' },
  { value: 'postgres', label: 'PostgreSQL' },
  { value: 'mysql', label: 'MySQL' },
  { value: 'sqlite', label: 'SQLite' },
];

/** The document types that can hold a table. */
export type SqlImportType = 'architecture' | 'freeform';

/** One thing the importer skipped or could not carry, as the server reports it. `line` is the line of the pasted SQL. */
export interface SqlImportWarning {
  code: string;
  message: string;
  line?: number;
}

export interface SqlImportPreview {
  /** The imported, laid-out document (or the imported one alone when layout was unavailable). */
  document: Document;
  warnings: SqlImportWarning[];
  tables: number;
  relationships: number;
}

/** Raised when the import request itself was refused (bad dialect, empty or oversized text), with the server's own message. */
export class SqlImportFailed extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'SqlImportFailed';
    this.status = status;
  }
}

async function problemDetail(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    return problem.detail ?? problem.title ?? fallback;
  } catch {
    return fallback;
  }
}

/** Reads `sql` into a laid-out document without saving it. `title` becomes the document's title when it is not blank. */
export async function previewSqlImport(
  sql: string,
  options: { dialect: SqlDialectChoice; type: SqlImportType; title?: string; tags?: boolean; implied?: boolean },
  fetchImpl: typeof fetch = fetch,
): Promise<SqlImportPreview> {
  const query = new URLSearchParams({ type: options.type });
  if (options.dialect !== 'auto') {
    query.set('dialect', options.dialect);
  }
  if (options.tags === false) {
    query.set('tags', 'off');
  }
  if (options.implied === false) {
    query.set('implied', 'off');
  }
  const title = options.title?.trim() ?? '';
  if (title !== '') {
    query.set('title', title);
  }
  const imported = await fetchImpl(`/api/import/sql?${query.toString()}`, {
    method: 'POST',
    headers: { 'Content-Type': 'text/plain', Accept: 'application/json', ...csrfHeaders() },
    body: sql,
  });
  if (!imported.ok) {
    throw new SqlImportFailed(imported.status, await problemDetail(imported, `The SQL could not be read: the server returned ${String(imported.status)}`));
  }
  const body = (await imported.json()) as { document: unknown; warnings?: SqlImportWarning[] };
  let document = parseDocument(body.document);
  const warnings = [...(body.warnings ?? [])];

  if (document.nodes.length > 0) {
    try {
      const laidOut = await fetchImpl('/api/layout?algorithm=layered', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
        body: JSON.stringify(document),
      });
      if (laidOut.ok) {
        document = unpinOrthogonalRoutes(parseDocument(((await laidOut.json()) as { document: unknown }).document));
      } else {
        warnings.push({ code: 'layout-unavailable', message: `The tables could not be laid out (${await problemDetail(laidOut, String(laidOut.status))}); Tidy layout in the editor will place them.` });
      }
    } catch (error: unknown) {
      warnings.push({ code: 'layout-unavailable', message: `The tables could not be laid out (${error instanceof Error ? error.message : 'unknown error'}); Tidy layout in the editor will place them.` });
    }
  }

  return { document, warnings, tables: document.nodes.filter((node) => node.kind === 'entity' || node.kind === 'weak-entity').length, relationships: document.edges.length };
}

/**
 * Saves an imported document as a new one and returns its id (docs/api.md `POST /api/documents`). The importer's own id
 * and header are left out, so the server assigns a fresh id and records the signed-in user as the author.
 */
export async function createImportedDocument(document: Document, fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl('/api/documents', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ ...document, id: undefined, meta: undefined }),
  });
  if (!response.ok) {
    throw new Error(await problemDetail(response, `Creating the document failed: the server returned ${String(response.status)}`));
  }
  return ((await response.json()) as { id: string }).id;
}

/** The DDL for `document`'s entities in `dialect` (docs/api.md `POST /api/export/sql`), as text ready to save. */
export async function exportSql(document: Document, dialect: Exclude<SqlDialectChoice, 'auto'>, fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl(`/api/export/sql?dialect=${dialect}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/plain', ...csrfHeaders() },
    body: JSON.stringify(document),
  });
  if (!response.ok) {
    throw new Error(await problemDetail(response, `Export failed: the server returned ${String(response.status)}`));
  }
  return response.text();
}

/** Whether a document holds any table, which is what makes Export SQL worth offering. */
export function hasEntities(document: Document): boolean {
  return document.nodes.some((node) => node.kind === 'entity' || node.kind === 'weak-entity' || node.shape.startsWith('custom:er/'));
}
