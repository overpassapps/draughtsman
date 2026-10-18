/**
 * The draw.io import call (docs/api.md `POST /api/import/drawio`): a preview followed by a create, like the Mermaid and SQL
 * imports (model/mermaidImport.ts, model/sqlImport.ts, whose `createImportedDocument` saves the result). Unlike them it never
 * lays the document out: a draw.io file carries every shape's position and size, and moving them all would undo the work the
 * file's author did. A file with several pages imports one of them, so the preview names the pages and `page` asks for another.
 */

import { csrfHeaders } from './csrf.ts';
import { parseDocument } from './parse.ts';
import type { Document } from './types.ts';

/** The document types a drawing can be read into; a sequence document is made from a Mermaid sequenceDiagram, not a drawing. */
export type DrawioImportType = 'flowchart' | 'architecture' | 'freeform';

export const DRAWIO_IMPORT_TYPES: readonly { value: DrawioImportType; label: string; hint: string }[] = [
  { value: 'flowchart', label: 'Flowchart', hint: 'Kinds are guessed from the shapes: process, decision, terminator, data.' },
  { value: 'architecture', label: 'Architecture', hint: 'Kinds are guessed from the shapes: service, database, queue, actor.' },
  { value: 'freeform', label: 'Freeform', hint: 'No kinds are guessed: every shape stays a plain shape.' },
];

/** One thing the importer guessed, skipped or left out, as the server reports it. */
export interface DrawioImportWarning {
  code: string;
  message: string;
  line?: number;
  elementId?: string;
}

/** One page of the file. `index` counts from 1, as the `page` argument does; `name` is empty when the file did not name it. */
export interface DrawioImportPage {
  index: number;
  name: string;
  selected: boolean;
}

export interface DrawioImportPreview {
  /** The imported document, with the positions the file gave it. */
  document: Document;
  warnings: DrawioImportWarning[];
  /** Every page of the file; empty for a source that is one bare diagram. */
  pages: DrawioImportPage[];
}

/** Raised when the importer refused the source or the request, carrying the server's own message (not a draw.io file, a page that is not there, a limit). */
export class DrawioImportFailed extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'DrawioImportFailed';
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

/** Reads `source` (the file's XML, plain or compressed) into a document without saving or laying it out. */
export async function previewDrawioImport(
  source: string,
  options: { type?: DrawioImportType; title?: string; page?: number } = {},
  fetchImpl: typeof fetch = fetch,
): Promise<DrawioImportPreview> {
  const query = new URLSearchParams({ type: options.type ?? 'flowchart' });
  const title = options.title?.trim() ?? '';
  if (title !== '') {
    query.set('title', title);
  }
  if (options.page !== undefined && options.page > 1) {
    query.set('page', String(options.page));
  }
  const imported = await fetchImpl(`/api/import/drawio?${query.toString()}`, {
    method: 'POST',
    headers: { 'Content-Type': 'text/plain', Accept: 'application/json', ...csrfHeaders() },
    body: source,
  });
  if (!imported.ok) {
    throw new DrawioImportFailed(imported.status, await problemDetail(imported, `The draw.io file could not be read: the server returned ${String(imported.status)}`));
  }
  const body = (await imported.json()) as { document: unknown; warnings?: DrawioImportWarning[]; pages?: DrawioImportPage[] };
  return { document: parseDocument(body.document), warnings: body.warnings ?? [], pages: body.pages ?? [] };
}

/** The text of a file the person chose. FileReader rather than `Blob.text()`, which not every environment this runs in has. */
export function readFileText(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      resolve(typeof reader.result === 'string' ? reader.result : '');
    };
    reader.onerror = () => {
      reject(new Error('That file could not be read.'));
    };
    reader.readAsText(file);
  });
}
