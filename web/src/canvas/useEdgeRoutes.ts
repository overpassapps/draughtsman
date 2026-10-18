/**
 * Keeps the canvas's orthogonal routes current (edgeRoutes.ts) through a routing worker the hook owns. Input is never
 * blocked: the index goes to the scheduler, the routes come back as state when the worker answers, and until then the
 * previous routes stay on screen. Where no worker can run (jsdom, a browser without module workers) nothing is routed
 * and orthogonal edges draw as the index builds them.
 */

import { useEffect, useRef, useState } from 'react';
import type { Id } from '../model/types.ts';
import type { DocumentIndex } from './documentIndex.ts';
import { EdgeRouteScheduler, type RouteSource } from './edgeRoutes.ts';
import type { OrthogonalRoute } from './routing.ts';
import { createRoutingClient } from './routingWorker.ts';

/** A routing backend the canvas owns for its lifetime: a RoutingClient, or an in-process stand-in in tests. */
export interface RoutingBackend extends RouteSource {
  dispose(): void;
}

/** The default backend: the real routing worker when the browser has one, none otherwise. */
export function defaultRoutingBackend(): RoutingBackend | undefined {
  return typeof Worker === 'undefined' ? undefined : createRoutingClient();
}

const NO_ROUTES: ReadonlyMap<Id, OrthogonalRoute> = new Map();

export function useEdgeRoutes(index: DocumentIndex, createBackend: () => RoutingBackend | undefined): ReadonlyMap<Id, OrthogonalRoute> {
  const [routes, setRoutes] = useState<ReadonlyMap<Id, OrthogonalRoute>>(NO_ROUTES);
  const scheduler = useRef<EdgeRouteScheduler | undefined>(undefined);
  const latestIndex = useRef(index);

  // Declared first so it has recorded the index by the time the backend effect below reads it on mount.
  useEffect(() => {
    latestIndex.current = index;
    scheduler.current?.update(index);
  }, [index]);

  useEffect(() => {
    const backend = createBackend();
    if (backend === undefined) {
      return;
    }
    const current = new EdgeRouteScheduler(backend, setRoutes);
    scheduler.current = current;
    current.update(latestIndex.current);
    return () => {
      current.dispose();
      backend.dispose();
      scheduler.current = undefined;
      setRoutes(NO_ROUTES);
    };
  }, [createBackend]);

  return routes;
}
