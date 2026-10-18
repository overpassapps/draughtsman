/**
 * The DSL export call (docs/api.md `POST /api/export/dsl`): the open document as the text the Text tab shows, so File >
 * Export DSL can hand it over as a file (QA P8). The DSL is the document's own text form, geometry and style included.
 */

import { csrfHeaders } from './csrf.ts';
import type { Document } from './types.ts';

export async function exportDsl(document: Document, fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl('/api/export/dsl', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/plain', ...csrfHeaders() },
    body: JSON.stringify(document),
  });
  if (!response.ok) {
    let detail = `Export failed: the server returned ${String(response.status)}`;
    try {
      const problem = (await response.json()) as { detail?: string; title?: string };
      detail = problem.detail ?? problem.title ?? detail;
    } catch {
      // The status is enough to act on.
    }
    throw new Error(detail);
  }
  return response.text();
}
