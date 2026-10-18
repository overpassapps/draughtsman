/**
 * Runs routeOrthogonal off the main thread (checklist item 29). `route` submits a batch of edges and resolves with the
 * results still current when the worker answers; an edge that a newer `route` call has asked for again is left out of
 * the older call's results (the stale-result rule in routingProtocol.ts). DiagramCanvas drives it through
 * EdgeRouteScheduler (edgeRoutes.ts), which keeps at most one batch in flight.
 *
 * The client talks to a RoutingPort rather than a Worker, so its bookkeeping is testable without one; createRoutingClient
 * connects it to the real worker.
 */

import {
  RouteBatchTracker,
  type RouteBatchMessage,
  type RouteNode,
  type RouteReplyMessage,
  type RouteRequest,
  type RouteResult,
} from './routingProtocol.ts';

/** The two things the client needs from its worker. */
export interface RoutingPort {
  post(message: RouteBatchMessage): void;
  terminate(): void;
}

interface Pending {
  resolve: (results: RouteResult[]) => void;
  reject: (error: Error) => void;
}

export class RoutingClient {
  private readonly tracker = new RouteBatchTracker();
  private readonly pending = new Map<number, Pending>();
  private readonly port: RoutingPort;
  private closed: Error | undefined;

  constructor(port: RoutingPort) {
    this.port = port;
  }

  /**
   * Routes a batch of edges around `nodes`, which every request's endpoints name and which is posted once for the whole
   * batch. Resolves with the results not superseded by a later call, in request order.
   */
  route(nodes: readonly RouteNode[], requests: readonly RouteRequest[]): Promise<RouteResult[]> {
    if (this.closed !== undefined) {
      return Promise.reject(this.closed);
    }
    const batch = this.tracker.begin(requests.map((request) => request.edgeId));
    return new Promise<RouteResult[]>((resolve, reject) => {
      this.pending.set(batch, { resolve, reject });
      this.port.post({ type: 'route', batch, nodes: [...nodes], requests: [...requests] });
    });
  }

  /** Feeds in a message from the worker. A reply for a batch this client is not waiting on is ignored. */
  receive(message: RouteReplyMessage): void {
    const waiting = this.pending.get(message.batch);
    if (waiting === undefined) {
      return;
    }
    this.pending.delete(message.batch);
    if (message.type === 'failed') {
      this.tracker.settle(message.batch, []);
      waiting.reject(new Error(`Draughtsman router: routing batch failed: ${message.message}`));
      return;
    }
    waiting.resolve(this.tracker.settle(message.batch, message.results));
  }

  /**
   * The worker itself failed (it could not load, or crashed): it is stopped, every batch still waiting is rejected, and
   * further calls to `route` reject straight away with the same error.
   */
  fail(reason: string): void {
    this.close(new Error(`Draughtsman router: routing worker error: ${reason}`));
  }

  /** Stops the worker and rejects every batch still waiting. Further calls to `route` reject straight away. */
  dispose(): void {
    this.close(new Error('Draughtsman router: the routing worker has been disposed'));
  }

  private close(error: Error): void {
    if (this.closed !== undefined) {
      return;
    }
    this.closed = error;
    this.port.terminate();
    const waiting = [...this.pending.entries()];
    this.pending.clear();
    for (const [batch, { reject }] of waiting) {
      this.tracker.settle(batch, []);
      reject(error);
    }
  }
}

/** A client backed by a real Web Worker running routing.worker.ts, bundled by Vite's standard worker import. */
export function createRoutingClient(): RoutingClient {
  const worker = new Worker(new URL('./routing.worker.ts', import.meta.url), { type: 'module' });
  const client = new RoutingClient({
    post: (message) => {
      worker.postMessage(message);
    },
    terminate: () => {
      worker.terminate();
    },
  });
  worker.addEventListener('message', (event: MessageEvent<RouteReplyMessage>) => {
    client.receive(event.data);
  });
  worker.addEventListener('error', (event: Event) => {
    const message = event instanceof ErrorEvent ? event.message : '';
    client.fail(message === '' ? 'the worker could not be loaded or crashed' : message);
  });
  return client;
}
