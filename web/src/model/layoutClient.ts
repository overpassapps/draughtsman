/**
 * The layout call the editor's Tidy action makes: POST /api/layout (docs/api.md), which recomputes every node's
 * position and returns the whole document plus the warnings for anything it left alone. Layered suits flowcharts and
 * architecture, stress a freeform graph, and tree an org chart (a parent above its children, top-down).
 *
 * The response is parsed into the model before it is returned, so a body the model cannot express throws here rather
 * than reaching the Y.Doc. Nothing about the document's structure changes: only geometry and edge routing, which the
 * caller applies through the same document diff the DSL panel uses.
 */

import { csrfHeaders } from './csrf.ts';
import { parseDocument } from './parse.ts';
import type { Document } from './types.ts';

/** The layout algorithms POST /api/layout accepts, lower-case as the server documents them. */
export type LayoutAlgorithm = 'layered' | 'stress' | 'tree';

export interface LayoutResult {
  document: Document;
  warnings: string[];
}

/** Raised when the layout request failed, so the caller can report the server's own message. */
export class LayoutFailed extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'LayoutFailed';
    this.status = status;
  }
}

/**
 * The document with the bend points ELK wrote on orthogonal connectors removed, so the canvas's own router draws them.
 * ELK routes from the middle of a box side, but a connector attaches to the outline of a diamond or a pill and to the
 * facing side of a table, so a pinned ELK route leaves a short diagonal stub at the end and can run through the gap
 * between two tables with its label floating. A connector with no waypoints is derived from where it really attaches
 * (decision D4 a); the router avoids every shape the way ELK's route did. Curved and straight edges keep theirs.
 */
export function unpinOrthogonalRoutes(document: Document): Document {
  if (!document.edges.some((edge) => edge.routing?.mode === 'orthogonal' && edge.routing.waypoints !== undefined)) {
    return document;
  }
  return {
    ...document,
    edges: document.edges.map((edge) => {
      if (edge.routing?.mode !== 'orthogonal' || edge.routing.waypoints === undefined) {
        return edge;
      }
      return { ...edge, routing: { mode: edge.routing.mode } };
    }),
  };
}

/** Lays `document` out with `algorithm` and returns the server's laid-out document and warnings. */
export async function requestLayout(
  document: Document,
  algorithm: LayoutAlgorithm,
  fetchImpl: typeof fetch = fetch,
): Promise<LayoutResult> {
  const response = await fetchImpl(`/api/layout?algorithm=${algorithm}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(document),
  });
  if (!response.ok) {
    let detail = `Layout failed: the server returned ${String(response.status)}`;
    try {
      const problem = (await response.json()) as { detail?: string; title?: string };
      detail = problem.detail ?? problem.title ?? detail;
    } catch {
      // The status alone is enough to act on; the body is a nicety.
    }
    throw new LayoutFailed(response.status, detail);
  }
  const body = (await response.json()) as { document: unknown; warnings?: string[] };
  return { document: unpinOrthogonalRoutes(parseDocument(body.document)), warnings: body.warnings ?? [] };
}
