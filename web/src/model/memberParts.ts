/**
 * The text compartments a shape lets a person edit as a list of lines, one member per line: a UML class's attributes
 * and operations (umlShapes.ts) and an ER entity's columns (erShapes.ts). The canvas, the member editor and the
 * editor's `setClassMembers` all ask this one table, so a new compartmented shape joins by adding a row here.
 */

import { erShape, type ErPart } from './erShapes.ts';
import { isClassShape, umlShape, type UmlPart } from './umlShapes.ts';

/** A compartment holding a list of lines: `props.<part>` is an array of strings. */
export type MemberPart = Exclude<UmlPart | ErPart, 'name'>;

/** Any part of a compartmented shape a person can edit: the name (the node label) or a member compartment. */
export type ShapePart = 'name' | MemberPart;

/** The member compartments the shape `shape` has, in top-to-bottom order; none for a shape that is not compartmented. */
export function memberPartsOf(shape: string): readonly MemberPart[] {
  if (isClassShape(umlShape(shape))) {
    return ['attributes', 'operations'];
  }
  return erShape(shape) === undefined ? [] : ['columns'];
}
