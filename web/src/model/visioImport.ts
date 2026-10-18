/**
 * The Visio import call (docs/api.md `POST /api/import/visio`): a preview followed by a create, like the draw.io, Mermaid and SQL
 * imports (model/drawioImport.ts, whose result shape and error it shares; `createImportedDocument` in model/sqlImport.ts saves the
 * result). Like draw.io's it never lays the document out: a .vsdx carries every shape's position and size, and moving them all would undo
 * the work the file's author did. A .vsdx is a binary ZIP package, so the file itself is the request body, not text; a file with several
 * pages imports one of them, so the preview names the pages and `page` asks for another.
 */

import { csrfHeaders } from './csrf.ts';
import { DRAWIO_IMPORT_TYPES, DrawioImportFailed, type DrawioImportPreview, type DrawioImportType } from './drawioImport.ts';
import { parseDocument } from './parse.ts';

/** The document types a drawing can be read into; a sequence document is made from a Mermaid sequenceDiagram, not a drawing. */
export type VisioImportType = DrawioImportType;

export const VISIO_IMPORT_TYPES = DRAWIO_IMPORT_TYPES;

export type VisioImportPreview = DrawioImportPreview;

/** The largest .vsdx the server reads, in bytes (the importer's own limit): a bigger file is refused here without being uploaded. */
export const MAX_VISIO_BYTES = 3_000_000;

async function problemDetail(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    return problem.detail ?? problem.title ?? fallback;
  } catch {
    return fallback;
  }
}

/** Reads `file` (a .vsdx) into a document without saving or laying it out. Raises `DrawioImportFailed` (the shared failure) carrying the server's own message. */
export async function previewVisioImport(
  file: Blob,
  options: { type?: VisioImportType; title?: string; page?: number } = {},
  fetchImpl: typeof fetch = fetch,
): Promise<VisioImportPreview> {
  if (file.size > MAX_VISIO_BYTES) {
    throw new DrawioImportFailed(413, `That file is ${(file.size / 1_000_000).toFixed(1)} MB, and Draughtsman reads Visio files up to 3 MB. Remove embedded pictures, or save one page per file.`);
  }
  const query = new URLSearchParams({ type: options.type ?? 'flowchart' });
  const title = options.title?.trim() ?? '';
  if (title !== '') {
    query.set('title', title);
  }
  if (options.page !== undefined && options.page > 1) {
    query.set('page', String(options.page));
  }
  const imported = await fetchImpl(`/api/import/visio?${query.toString()}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream', Accept: 'application/json', ...csrfHeaders() },
    body: file,
  });
  if (!imported.ok) {
    throw new DrawioImportFailed(imported.status, await problemDetail(imported, `The Visio file could not be read: the server returned ${String(imported.status)}`));
  }
  const body = (await imported.json()) as { document: unknown; warnings?: VisioImportPreview['warnings']; pages?: VisioImportPreview['pages'] };
  return { document: parseDocument(body.document), warnings: body.warnings ?? [], pages: body.pages ?? [] };
}
