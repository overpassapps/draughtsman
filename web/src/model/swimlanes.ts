/**
 * Swimlane containers (pool and lane) as pure plans over a document. They read a document and return what to
 * change; the editor (editor/useDocumentEditor.ts) writes it to the Y.Doc as one undo step. Geometry is absolute
 * (model/containers.ts), so a lane's box is its absolute rectangle and moving a pool already moves its lanes and
 * their contents (selection.ts `dragStarts` walks children).
 *
 * A pool is a container with a title band along its top; a lane is a child band of a pool. Orientation is a pool
 * property (`props.orientation`, "horizontal" by default): horizontal lanes are rows stacked top to bottom, vertical
 * lanes are columns side by side. The title band is drawn along the top in both orientations, so the renderers need
 * no orientation; only this module's band maths does. Lanes are laid out in document order, packed edge to edge
 * inside the pool's band-free area, so they resize with the pool and can be added, removed and reordered.
 *
 * The lane header height is the renderer's business (web/src/canvas/shapes.tsx, SvgRenderer.cs), but the two must
 * agree on how much of the pool the header takes so a lane never lands under it; the shared constants below are
 * mirrored, like the state-shape geometry constants, and swept by a test on each side.
 */

import type { Rect } from '../canvas/geometry.ts';
import { randomContainerId, type ContainerIdGenerator } from './grouping.ts';
import { LANE_KIND, POOL_KIND, isSwimlaneKind } from './swimlaneKinds.ts';
import type { Document, Geometry, Id, Node } from './types.ts';

/** The semantic kind of a swimlane pool and of a lane. */
export { LANE_KIND, POOL_KIND };

/** The presentation shapes a pool and a lane are drawn as, `custom:swimlane/<name>` (the schema admits the branch). */
export const POOL_SHAPE = 'custom:swimlane/pool';
export const LANE_SHAPE = 'custom:swimlane/lane';

/** Every swimlane shape, for the render sweeps on both sides. */
export const SWIMLANE_SHAPES: readonly string[] = [POOL_SHAPE, LANE_SHAPE];

/** How a pool arranges its lanes: rows (horizontal) or columns (vertical). */
export type SwimlaneOrientation = 'horizontal' | 'vertical';

/** The title-band height of a pool: a fraction of the box, held between a floor and a cap. Mirrors shapes.tsx. */
export const POOL_HEADER_RATIO = 0.2;
export const POOL_HEADER_MIN = 24;
export const POOL_HEADER_MAX = 48;

/** The title-band height of a lane: a shorter band than the pool's, held between its own floor and cap. */
export const LANE_HEADER_RATIO = 0.15;
export const LANE_HEADER_MIN = 16;
export const LANE_HEADER_MAX = 32;

/** The default lane label a palette glyph or an Add lane command places. */
export const DEFAULT_POOL_LABEL = 'Pool';
export const DEFAULT_LANE_LABEL = 'Lane';

export function isPool(node: Node): boolean {
  return node.kind === POOL_KIND;
}

export function isLane(node: Node): boolean {
  return node.kind === LANE_KIND;
}

/** Whether the kind is one the renderer and the container rules treat as a swimlane container. */
export function isSwimlaneContainer(node: Node): boolean {
  return isSwimlaneKind(node.kind);
}

/** A pool's orientation, from `props.orientation`; anything but "vertical" is horizontal. */
export function poolOrientation(node: Node): SwimlaneOrientation {
  return node.props?.orientation === 'vertical' ? 'vertical' : 'horizontal';
}

/** A lane's orientation, taken from the pool it sits in; a lane outside a pool arranges as horizontal. */
export function laneOrientation(document: Document, lane: Node): SwimlaneOrientation {
  const pool = lane.parent === undefined ? undefined : document.nodes.find((candidate) => candidate.id === lane.parent);
  return pool !== undefined && isPool(pool) ? poolOrientation(pool) : 'horizontal';
}

/** The pool title band's height for a box of height `h`, never more than the box itself. */
export function poolHeaderHeight(h: number): number {
  return Math.min(h, Math.max(POOL_HEADER_MIN, Math.min(h * POOL_HEADER_RATIO, POOL_HEADER_MAX)));
}

/** The lane title band's height for a box of height `h`, never more than the box itself. */
export function laneHeaderHeight(h: number): number {
  return Math.min(h, Math.max(LANE_HEADER_MIN, Math.min(h * LANE_HEADER_RATIO, LANE_HEADER_MAX)));
}

/** The pool's band-free inner area: its box less the title band along the top. */
export function poolLaneArea(pool: Node): Rect | undefined {
  const box = pool.geometry;
  if (box === undefined) {
    return undefined;
  }
  const band = poolHeaderHeight(box.h);
  return { x: box.x, y: box.y + band, w: box.w, h: box.h - band };
}

/** A pool's lane children, in document order (the order they are laid out, reordered and renumbered). */
export function orderedLanes(document: Document, poolId: Id): Node[] {
  return document.nodes.filter((node) => node.parent === poolId && isLane(node));
}

/** The pool a lane belongs to, or undefined when its parent is missing or is not a pool. */
export function lanePool(document: Document, lane: Node): Node | undefined {
  const parent = lane.parent === undefined ? undefined : document.nodes.find((node) => node.id === lane.parent);
  return parent !== undefined && isPool(parent) ? parent : undefined;
}

/**
 * The geometry of every lane under `poolId`, packed edge to edge inside the pool's band-free area in document order.
 * Horizontal lanes are equal-height rows spanning the area's width; vertical lanes are equal-width columns spanning
 * its height. The last lane takes the remainder, so no rounding gap is left at the far edge. A pool without geometry
 * or without lanes returns an empty map.
 */
export function laneBands(document: Document, poolId: Id): Map<Id, Geometry> {
  const bands = new Map<Id, Geometry>();
  const pool = document.nodes.find((node) => node.id === poolId);
  const area = pool === undefined ? undefined : poolLaneArea(pool);
  const lanes = orderedLanes(document, poolId);
  if (pool === undefined || area === undefined || lanes.length === 0 || area.w <= 0 || area.h <= 0) {
    return bands;
  }
  const orientation = poolOrientation(pool);
  const count = lanes.length;
  lanes.forEach((lane, index) => {
    bands.set(
      lane.id,
      orientation === 'vertical'
        ? { x: area.x + (area.w * index) / count, y: area.y, w: area.w / count, h: area.h }
        : { x: area.x, y: area.y + (area.h * index) / count, w: area.w, h: area.h / count },
    );
  });
  return bands;
}

/** Same comparison `containers.ts` uses to decide a fit changed nothing. */
function sameGeometry(a: Geometry | undefined, b: Geometry): boolean {
  if (a === undefined) {
    return false;
  }
  return a.x === b.x && a.y === b.y && a.w === b.w && a.h === b.h && (a.rotation ?? 0) === (b.rotation ?? 0);
}

/**
 * The lane geometries that change when the pool's bands are recomputed, for a pool resize or a change of lane set or
 * order. Lanes already at their computed spot are left out, so a no-op writes nothing and records no undo step.
 */
export function layoutLanes(document: Document, poolId: Id): Map<Id, Geometry> {
  const changed = new Map<Id, Geometry>();
  for (const [laneId, band] of laneBands(document, poolId)) {
    const current = document.nodes.find((node) => node.id === laneId)?.geometry;
    if (!sameGeometry(current, band)) {
      changed.set(laneId, band);
    }
  }
  return changed;
}

/**
 * The lane updates a frame of a gesture makes: every lane under a pool that is moving or resizing is repacked into
 * the pool's new box, unless that lane is itself being dragged this frame (a moved pool's lanes travel with it
 * already, from selection.ts `dragStarts`, and repacking them would fight the drag). Returns an empty map when no
 * moved node is a pool.
 */
export function relayoutAfterMoves(document: Document, moves: ReadonlyMap<Id, Geometry>): Map<Id, Geometry> {
  const updates = new Map<Id, Geometry>();
  for (const [id, geometry] of moves) {
    const node = document.nodes.find((candidate) => candidate.id === id);
    if (node === undefined || !isPool(node)) {
      continue;
    }
    const working: Document = { ...document, nodes: document.nodes.map((candidate) => (candidate.id === id ? { ...candidate, geometry } : candidate)) };
    for (const [laneId, band] of laneBands(working, id)) {
      if (moves.has(laneId)) {
        continue;
      }
      const current = working.nodes.find((candidate) => candidate.id === laneId)?.geometry;
      if (!sameGeometry(current, band)) {
        updates.set(laneId, band);
      }
    }
  }
  return updates;
}

/** A new lane added to a pool, and the lane geometry writes that go with it. */
export interface AddLanePlan {
  lane: Node;
  /** Every lane that moves to sit in the new packing, the new lane included. */
  geometries: Map<Id, Geometry>;
}

/**
 * The plan that adds one lane to `poolId`: the new lane node (parented to the pool, carrying an unused id and the
 * title band's label) and the geometry of every lane once the new one is packed in. Undefined when the pool does not
 * exist or has no geometry. The lane is appended last, so it lands at the pool's far edge.
 */
export function addLanePlan(document: Document, poolId: Id, generateId: ContainerIdGenerator = randomContainerId): AddLanePlan | undefined {
  const pool = document.nodes.find((node) => node.id === poolId);
  if (pool === undefined || !isPool(pool) || pool.geometry === undefined) {
    return undefined;
  }
  const id = unusedId(document, generateId);
  const draft: Node = { id, kind: LANE_KIND, label: DEFAULT_LANE_LABEL, shape: LANE_SHAPE, parent: poolId };
  const working: Document = { ...document, nodes: [...document.nodes, draft] };
  const bands = laneBands(working, poolId);
  const geometry = bands.get(id);
  if (geometry === undefined) {
    return undefined;
  }
  const lane: Node = { ...draft, geometry };
  const geometries = new Map<Id, Geometry>();
  for (const [laneId, band] of bands) {
    const current = document.nodes.find((node) => node.id === laneId)?.geometry;
    if (laneId === id || !sameGeometry(current, band)) {
      geometries.set(laneId, band);
    }
  }
  return { lane, geometries };
}

/** The size a pool lands at from the palette: a working surface, wide enough for a process to read across its lanes (QA P4). */
export const NEW_POOL_SIZE = { w: 640, h: 320 } as const;
/** How many lanes a pool dropped from the palette starts with. */
export const NEW_POOL_LANES = 2;

/**
 * A pool as the palette places it: the pool itself and `NEW_POOL_LANES` lanes packed into it ("Lane 1", "Lane 2"), so the
 * drop is a starting point rather than an empty 160 x 80 box. `pool` carries its geometry already; the lanes are children
 * of it with ids the document does not use. Add them all in one step, pool first.
 */
export function poolWithLanes(document: Document, pool: Node, generateId: ContainerIdGenerator = randomContainerId): Node[] {
  let working: Document = { ...document, nodes: [...document.nodes, pool] };
  const lanes: Node[] = [];
  for (let n = 1; n <= NEW_POOL_LANES; n++) {
    const draft: Node = { id: unusedId(working, generateId), kind: LANE_KIND, label: `${DEFAULT_LANE_LABEL} ${String(n)}`, shape: LANE_SHAPE, parent: pool.id };
    lanes.push(draft);
    working = { ...working, nodes: [...working.nodes, draft] };
  }
  const bands = laneBands(working, pool.id);
  return [
    pool,
    ...lanes.map((lane) => {
      const geometry = bands.get(lane.id);
      return geometry === undefined ? lane : { ...lane, geometry };
    }),
  ];
}

/** The pool relayout that follows a lane removal: the lane's pool id and the remaining lanes' packed geometry. */
export function removeLaneLayout(document: Document, laneId: Id): { poolId: Id; geometries: Map<Id, Geometry> } | undefined {
  const lane = document.nodes.find((node) => node.id === laneId);
  if (lane === undefined || !isLane(lane) || lane.parent === undefined) {
    return undefined;
  }
  const pool = document.nodes.find((node) => node.id === lane.parent);
  if (pool === undefined || !isPool(pool)) {
    return undefined;
  }
  return { poolId: pool.id, geometries: laneBands(document, pool.id) };
}

/** The plan that moves a lane one place earlier (-1) or later (+1) among its pool's lanes, repacking them. */
export interface MoveLanePlan {
  /** The document's nodes in their new order, so render order and the lane sequence agree. */
  nodes: Node[];
  geometries: Map<Id, Geometry>;
}

/**
 * The plan that swaps `laneId` with its neighbour in `direction`, then repacks the pool. Undefined when the lane is
 * not a lane, is not in a pool, or is already at the end it is being moved towards. Lanes are ordered by document
 * order, so the swap moves the lane in the node list itself, and the returned `nodes` is what `setElementOrder` needs.
 */
export function moveLanePlan(document: Document, laneId: Id, direction: -1 | 1): MoveLanePlan | undefined {
  const lane = document.nodes.find((node) => node.id === laneId);
  if (lane === undefined || !isLane(lane) || lane.parent === undefined) {
    return undefined;
  }
  const siblings = orderedLanes(document, lane.parent);
  const from = siblings.findIndex((candidate) => candidate.id === laneId);
  const to = from + direction;
  if (from < 0 || to < 0 || to >= siblings.length) {
    return undefined;
  }
  const other = siblings[to];
  if (other === undefined) {
    return undefined;
  }
  const nodes = document.nodes.map((node) => {
    if (node.id === lane.id) {
      return other;
    }
    return node.id === other.id ? lane : node;
  });
  const working: Document = { ...document, nodes };
  return { nodes, geometries: layoutLanes(working, lane.parent) };
}

/** The id the Add lane command proposes; the group command's generator, kept here so a lane id is distinguishable. */
export function randomLaneId(): Id {
  return `n_lane_${Math.random().toString(36).slice(2, 8)}`;
}

function unusedId(document: Document, generateId: ContainerIdGenerator): Id {
  const taken = new Set<Id>([...document.nodes.map((node) => node.id), ...document.edges.map((edge) => edge.id)]);
  for (let attempt = 0; attempt < 1000; attempt++) {
    const id = generateId();
    if (!taken.has(id)) {
      return id;
    }
  }
  throw new Error('No unused lane id after 1000 attempts; the id generator is stuck.');
}
