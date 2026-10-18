/**
 * The Mermaid import call (docs/api.md `POST /api/import/mermaid`): a preview followed by a create, like the SQL import
 * (model/sqlImport.ts, whose `createImportedDocument` saves the result). The header picks the document type, so a
 * `sequenceDiagram` arrives as a `sequence` document and anything else as a flowchart. A flowchart's nodes have no
 * geometry, so it is laid out before it is shown; a sequence diagram derives its geometry and is never laid out. A layout
 * that cannot run does not fail the import: the document is returned as imported and the reason is added to the warnings.
 */

import { csrfHeaders } from './csrf.ts';
import { unpinOrthogonalRoutes } from './layoutClient.ts';
import { parseDocument } from './parse.ts';
import type { Document } from './types.ts';

/** One thing the importer guessed, skipped or renamed, as the server reports it. `line` is the line of the pasted source. */
export interface MermaidImportWarning {
  code: string;
  message: string;
  line?: number;
  elementId?: string;
}

export interface MermaidImportPreview {
  /** The imported document, laid out when it is a flowchart. */
  document: Document;
  warnings: MermaidImportWarning[];
}

/** Raised when the importer refused the source, carrying the server's own message (a bad header, an empty paste). */
export class MermaidImportFailed extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'MermaidImportFailed';
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

/** Reads `source` into a document without saving it. `title` becomes the document's title when it is not blank. */
export async function previewMermaidImport(source: string, title = '', fetchImpl: typeof fetch = fetch): Promise<MermaidImportPreview> {
  const query = new URLSearchParams();
  if (title.trim() !== '') {
    query.set('title', title.trim());
  }
  const suffix = query.size > 0 ? `?${query.toString()}` : '';
  const imported = await fetchImpl(`/api/import/mermaid${suffix}`, {
    method: 'POST',
    headers: { 'Content-Type': 'text/plain', Accept: 'application/json', ...csrfHeaders() },
    body: source,
  });
  if (!imported.ok) {
    throw new MermaidImportFailed(imported.status, await problemDetail(imported, `The Mermaid could not be read: the server returned ${String(imported.status)}`));
  }
  const body = (await imported.json()) as { document: unknown; warnings?: MermaidImportWarning[] };
  let document = parseDocument(body.document);
  const warnings = [...(body.warnings ?? [])];

  if (document.type !== 'sequence' && document.nodes.length > 0) {
    try {
      const laidOut = await fetchImpl('/api/layout?algorithm=layered', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
        body: JSON.stringify(document),
      });
      if (laidOut.ok) {
        document = unpinOrthogonalRoutes(parseDocument(((await laidOut.json()) as { document: unknown }).document));
      } else {
        warnings.push({ code: 'layout-unavailable', message: `The diagram could not be laid out (${await problemDetail(laidOut, String(laidOut.status))}); Tidy layout in the editor will place it.` });
      }
    } catch (error: unknown) {
      warnings.push({ code: 'layout-unavailable', message: `The diagram could not be laid out (${error instanceof Error ? error.message : 'unknown error'}); Tidy layout in the editor will place it.` });
    }
  }

  return { document, warnings };
}
