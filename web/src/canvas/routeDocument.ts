/**
 * Every orthogonal connector of a document routed at once, in this thread. The canvas routes in a worker as the
 * document changes (edgeRoutes.ts, useEdgeRoutes.ts); anything that draws a document without an interactive canvas
 * (the PNG and SVG export, and the server renderer through the sidecar's `route.document`, sidecar/src/route) asks
 * here instead, so the same `routingSnapshot` builds the same requests and the same `handleRouteBatch` runs the same
 * `routeOrthogonal` search: one router, never a second copy. Pure and DOM-free (the sidecar bundles it), so it holds
 * to decision D4 a as the canvas does: a route is derived from the document and never written back to it.
 *
 * Which edges route is `routingSnapshot`'s call: `routing.mode: orthogonal` and no stored waypoints. A pinned
 * connector (a dragged segment, ELK's bend points) is not here, and the index already draws its waypoints.
 */

import type { Id } from '../model/types.ts';
import { EDGE_BOUNDS_PADDING, type DocumentIndex } from './documentIndex.ts';
import { routingSnapshot, withRoutes } from './edgeRoutes.ts';
import { boundsOfPoints, padRect, unionRects } from './geometry.ts';
import type { OrthogonalRoute } from './routing.ts';
import { handleRouteBatch } from './routingProtocol.ts';

/** The route of each orthogonal, unpinned edge of the indexed document; an empty map when it has none. */
export function routeDocumentEdges(index: DocumentIndex): Map<Id, OrthogonalRoute> {
  const snapshot = routingSnapshot(index);
  const routes = new Map<Id, OrthogonalRoute>();
  if (snapshot.inputs.size === 0) {
    return routes;
  }
  const reply = handleRouteBatch({ type: 'route', batch: 0, nodes: snapshot.nodes, requests: [...snapshot.inputs.values()].map((input) => input.request) });
  if (reply.type === 'failed') {
    // A routing bug or a request naming a node the batch lacks: said loudly, because a silent fallback would draw the
    // straight lines this module exists to prevent.
    throw new Error(`Connector routing failed: ${reply.message}`);
  }
  for (const result of reply.results) {
    routes.set(result.edgeId, result.route);
  }
  return routes;
}

/**
 * The index with every orthogonal connector routed, and `contentBounds` grown to hold any route that detours outside
 * the nodes' own box, so a crop to the content never clips a connector.
 */
export function indexWithRoutes(index: DocumentIndex): { index: DocumentIndex; routes: ReadonlyMap<Id, OrthogonalRoute> } {
  const routes = routeDocumentEdges(index);
  if (routes.size === 0) {
    return { index, routes };
  }
  const routed = withRoutes(index, routes);
  const extents = [...routes.values()].map((route) => boundsOfPoints(route.points)).flatMap((box) => (box === undefined ? [] : [padRect(box, EDGE_BOUNDS_PADDING)]));
  const contentBounds = unionRects([...(index.contentBounds === undefined ? [] : [index.contentBounds]), ...extents]);
  return { index: { ...routed, contentBounds }, routes };
}
