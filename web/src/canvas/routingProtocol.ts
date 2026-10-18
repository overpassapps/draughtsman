/**
 * The message protocol between the editor and the connector-routing Web Worker (checklist item 29: routing runs off the
 * main thread so typing, dragging and keyboard input are never blocked while routes are recalculated). Pure: no Worker,
 * no DOM, so both halves of the conversation and the stale-result rule are unit-testable in Node.
 *
 * The editor sends a batch of route requests, one per edge, together with the node bounds they route around. The node
 * set travels once per batch, not once per request: posting a message structured-clones it on the main thread, and a
 * per-request obstacle list at the item 29 scale (2,500 edges over 2,000 nodes) is millions of rects. The worker
 * answers with one result per request under the same batch number. A newer batch that includes an edge supersedes every older batch still in flight for that edge:
 * when the older answer arrives, that edge's result is dropped, never applied. Edges of the older batch that nothing
 * newer asked about are still delivered, so a partial re-route does not throw away unrelated work.
 */

import type { Point } from '../model/types.ts';
import type { Rect } from './geometry.ts';
import { routeOrthogonal, type OrthogonalRoute, type RouteOptions, type Side } from './routing.ts';

/** A node the batch's edges attach to or route around. */
export interface RouteNode {
  id: string;
  bounds: Rect;
}

/** An edge end: where it leaves or enters the node it is attached to, by that node's id in the batch's node set. */
export interface RouteRequestEndpoint {
  point: Point;
  side: Side;
  nodeId: string;
}

/**
 * One edge to route. Its obstacles are every node in the batch other than the one or two it is attached to and the
 * ones it names in `passThrough`: the containers holding either endpoint, whose border a route into them must cross.
 */
export interface RouteRequest {
  edgeId: string;
  start: RouteRequestEndpoint;
  end: RouteRequestEndpoint;
  passThrough?: string[];
  options?: RouteOptions;
}

export interface RouteResult {
  edgeId: string;
  route: OrthogonalRoute;
}

/** Editor to worker: route these edges. */
export interface RouteBatchMessage {
  type: 'route';
  batch: number;
  nodes: RouteNode[];
  requests: RouteRequest[];
}

/** Worker to editor: the batch's routes, in request order, or why the batch could not be routed. */
export type RouteReplyMessage =
  | { type: 'routed'; batch: number; results: RouteResult[] }
  | { type: 'failed'; batch: number; message: string };

/**
 * The worker's side: route every request in the batch. An endpoint naming a node the batch does not carry, or a
 * routeOrthogonal bookkeeping bug, throws; a throw inside a worker would otherwise surface as an unattributed error
 * event, so it is caught and reported against the batch that caused it.
 */
export function handleRouteBatch(message: RouteBatchMessage): RouteReplyMessage {
  try {
    const bounds = new Map(message.nodes.map((node) => [node.id, node.bounds]));
    const boundsOf = (nodeId: string): Rect => {
      const rect = bounds.get(nodeId);
      if (rect === undefined) {
        throw new Error(`unknown node ${nodeId}`);
      }
      return rect;
    };
    const results = message.requests.map((request) => {
      const { start, end } = request;
      const passable = new Set([start.nodeId, end.nodeId, ...(request.passThrough ?? [])]);
      const obstacles = message.nodes.filter((node) => !passable.has(node.id)).map((node) => node.bounds);
      const route = routeOrthogonal(
        { point: start.point, side: start.side, bounds: boundsOf(start.nodeId) },
        { point: end.point, side: end.side, bounds: boundsOf(end.nodeId) },
        obstacles,
        request.options,
      );
      return { edgeId: request.edgeId, route };
    });
    return { type: 'routed', batch: message.batch, results };
  } catch (error) {
    return { type: 'failed', batch: message.batch, message: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * The editor's side of the stale-result rule. `begin` numbers a batch and makes it the latest for each of its edges;
 * `settle` takes that batch's answer and keeps only the results for edges it is still the latest for.
 */
export class RouteBatchTracker {
  private nextBatch = 1;
  /** The newest batch asked to route each edge that has one in flight. */
  private readonly latest = new Map<string, number>();
  private readonly edgesOf = new Map<number, string[]>();

  begin(edgeIds: readonly string[]): number {
    const batch = this.nextBatch;
    this.nextBatch += 1;
    for (const edgeId of edgeIds) {
      this.latest.set(edgeId, batch);
    }
    this.edgesOf.set(batch, [...edgeIds]);
    return batch;
  }

  /**
   * The results of `batch` that are still current. Settling also retires the batch: its edges no longer count as in
   * flight, so a later answer for any older batch is dropped too. Settling with no results (a failed batch) retires it
   * the same way. A batch that was never begun, or is already settled, yields nothing.
   */
  settle(batch: number, results: readonly RouteResult[]): RouteResult[] {
    const edges = this.edgesOf.get(batch);
    if (edges === undefined) {
      return [];
    }
    this.edgesOf.delete(batch);
    const current = new Set(edges.filter((edgeId) => this.latest.get(edgeId) === batch));
    for (const edgeId of current) {
      this.latest.delete(edgeId);
    }
    return results.filter((result) => current.has(result.edgeId));
  }
}
