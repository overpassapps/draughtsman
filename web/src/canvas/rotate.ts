/**
 * Rotation for the selection's rotate handle (checklist item 4). Pure: a node's start geometry, the pointer's
 * clockwise angle from the node's centre at the press and now, and whether Shift is held go in; the node's new
 * geometry (rotation only, quantised to 1° or 15° steps and normalised to [0, 360)) comes out. The canvas owns the
 * gesture and the model write, exactly as it does for resizeGeometry.
 *
 * The angle uses the same y-down clockwise convention as resize.ts's rotation matrix: atan2 over (pointer - centre)
 * grows exactly as `geometry.rotation` does, so the difference between the start and current angle is added to the
 * start rotation directly, with no sign flip.
 */

import type { Geometry, Point } from '../model/types.ts';

/** The rotate drag's quantisation step in degrees: 1°, or 15° with Shift held. */
export function rotationStep(shiftKey: boolean): number {
  return shiftKey ? 15 : 1;
}

/**
 * `degrees` wrapped into [0, 360), so 360 and -1 both read as the checklist's normalised range expects. The double
 * mod avoids a signed-zero result (`-360 % 360` is `-0` in JS, which is a valid 0 but fails a strict `=== 0`).
 */
export function normalizeRotation(degrees: number): number {
  return ((degrees % 360) + 360) % 360;
}

/** The clockwise angle in degrees from `centre` to `point`, in the same convention as `geometry.rotation`. */
export function angleFromCentre(centre: Point, point: Point): number {
  return (Math.atan2(point.y - centre.y, point.x - centre.x) * 180) / Math.PI;
}

/**
 * The geometry after rotating the handle from `startAngle` (the pointer's angle from the node's centre at the press)
 * to `pointerAngle` (its angle now), starting from `start.rotation`. `shiftKey` selects the 15° step. Only
 * `rotation` changes: the node's centre does not move.
 */
export function rotateGeometry(start: Geometry, startAngle: number, pointerAngle: number, shiftKey: boolean): Geometry {
  const step = rotationStep(shiftKey);
  const raw = (start.rotation ?? 0) + (pointerAngle - startAngle);
  const rotation = normalizeRotation(Math.round(raw / step) * step);
  return { ...start, rotation };
}
