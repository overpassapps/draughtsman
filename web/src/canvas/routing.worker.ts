/**
 * Web Worker entry for connector routing. Loaded by createRoutingClient (routingWorker.ts) through Vite's standard
 * `new Worker(new URL(...), { type: 'module' })` form; everything it does is handleRouteBatch, which is unit-tested.
 */

import { handleRouteBatch, type RouteBatchMessage } from './routingProtocol.ts';

addEventListener('message', (event: MessageEvent<RouteBatchMessage>) => {
  postMessage(handleRouteBatch(event.data));
});
