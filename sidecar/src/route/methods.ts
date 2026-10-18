/**
 * The route.document JSON-RPC method: every orthogonal connector of a document routed by the editor's own router.
 * Unlike layout.elk this does not copy the web code, it bundles it: `routeDocumentEdges` (web/src/canvas/routeDocument.ts)
 * is the very function the PNG/SVG export calls, over the very `routingSnapshot` and `routeOrthogonal` the canvas's
 * routing worker runs, so the server's render_document, PDF export and the editor cannot draw different paths for the
 * same document. The Dockerfile's sidecar stage therefore copies web/src and the kind vocabularies alongside sidecar/.
 *
 * Params `{ document }`; result `{ routes: { [edgeId]: { status, points, reason? } } }`, only for edges the editor
 * itself would route (`routing.mode: orthogonal`, no stored waypoints, both ends placed). Nodes with no geometry are
 * not placed and not routed around, so lay the document out first.
 */
import { indexDocument } from '../../../web/src/canvas/documentIndex.ts';
import { routeDocumentEdges } from '../../../web/src/canvas/routeDocument.ts';
import type { OrthogonalRoute } from '../../../web/src/canvas/routing.ts';
import type { Document as WebDocument } from '../../../web/src/model/types.ts';
import { ErrorCodes, RpcError, isObject } from '../rpc.ts';
import { assertDocumentShape } from '../yjs/shape.ts';

export interface RouteResult {
  routes: Record<string, OrthogonalRoute>;
}

export function routeDocument(params: unknown): RouteResult {
  if (!isObject(params)) {
    throw new RpcError(ErrorCodes.invalidParams, 'params must be an object');
  }
  const document = params['document'];
  assertDocumentShape(document, 'document');
  const routes: Record<string, OrthogonalRoute> = {};
  for (const [edgeId, route] of routeDocumentEdges(indexDocument(document as unknown as WebDocument))) {
    routes[edgeId] = route;
  }
  return { routes };
}
