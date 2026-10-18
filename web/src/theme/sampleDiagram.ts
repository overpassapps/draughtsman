/**
 * The small architecture diagram the Themes page draws to show a diagram theme: every kind a theme styles most visibly
 * (actor, service, database, queue, an external system, a container) and both connector kinds with a label, so a
 * palette or background change shows up somewhere in it. Fixed geometry and straight connectors: the preview must not
 * depend on the router or the sidecar.
 */

import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';

export const SAMPLE_DIAGRAM_SIZE = { width: 560, height: 280 } as const;

export const SAMPLE_DIAGRAM: Document = parseDocument({
  id: 'doc_theme_preview',
  schemaVersion: 1,
  title: 'Theme preview',
  type: 'architecture',
  nodes: [
    { id: 'n_user', kind: 'actor', label: 'Customer', shape: 'rounded', geometry: { x: 24, y: 112, w: 120, h: 56 } },
    { id: 'c_platform', kind: 'container', label: 'Platform', shape: 'rectangle', geometry: { x: 192, y: 16, w: 352, h: 248 } },
    { id: 'n_api', kind: 'service', label: 'Orders API', shape: 'rounded', geometry: { x: 216, y: 112, w: 128, h: 56 }, parent: 'c_platform' },
    { id: 'n_db', kind: 'database', label: 'Postgres', shape: 'cylinder', geometry: { x: 400, y: 48, w: 120, h: 64 }, parent: 'c_platform' },
    { id: 'n_queue', kind: 'queue', label: 'Events', shape: 'rectangle', geometry: { x: 400, y: 136, w: 120, h: 48 }, parent: 'c_platform' },
    { id: 'n_ext', kind: 'external', label: 'Payments', shape: 'rectangle', geometry: { x: 216, y: 200, w: 128, h: 48 }, parent: 'c_platform' },
  ],
  edges: [
    { id: 'e_1', kind: 'flow', label: 'HTTPS', from: { node: 'n_user' }, to: { node: 'n_api' } },
    { id: 'e_2', kind: 'data', label: 'SQL', from: { node: 'n_api' }, to: { node: 'n_db' } },
    { id: 'e_3', kind: 'data', from: { node: 'n_api' }, to: { node: 'n_queue' } },
    { id: 'e_4', kind: 'flow', from: { node: 'n_api' }, to: { node: 'n_ext' } },
  ],
  meta: { createdAt: '2026-09-30T00:00:00.000Z', updatedAt: '2026-09-30T00:00:00.000Z', createdBy: 'theme-preview' },
});
