/**
 * The method table. sidecar/README.md holds the maintained method list; keep the two in step. The table in
 * docs/adr/0002-crdt-and-layout-engine.md is the decision as taken and is deliberately not updated.
 */
import packageJson from '../package.json';
import { layoutElk } from './layout/methods.ts';
import type { MethodTable } from './rpc.ts';
import { routeDocument } from './route/methods.ts';
import { sequenceSceneMethod } from './sequence/methods.ts';
import { yjsApplyUpdates, yjsFromJson, yjsToJson } from './yjs/methods.ts';

export interface HealthResult {
  ok: true;
  version: string;
  node: string;
  pid: number;
}

export function ping(): HealthResult {
  return { ok: true, version: packageJson.version, node: process.version, pid: process.pid };
}

export const methods: MethodTable = {
  ping,
  'layout.elk': layoutElk,
  'route.document': routeDocument,
  'sequence.scene': sequenceSceneMethod,
  'yjs.fromJson': yjsFromJson,
  'yjs.toJson': yjsToJson,
  'yjs.applyUpdates': yjsApplyUpdates,
};
