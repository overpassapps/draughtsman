/**
 * The swimlane container kind names, in their own leaf module so the container rules (model/containers.ts,
 * canvas/documentIndex.ts) can name them without importing model/swimlanes.ts, which imports model/grouping.ts,
 * which imports model/containers.ts. Kept here rather than restated so a rename cannot drift them apart.
 */

/** The semantic kind of a swimlane pool and of a lane. */
export const POOL_KIND = 'pool';
export const LANE_KIND = 'lane';

/** Whether a node kind is a swimlane container (a pool or a lane). */
export function isSwimlaneKind(kind: string): boolean {
  return kind === POOL_KIND || kind === LANE_KIND;
}
