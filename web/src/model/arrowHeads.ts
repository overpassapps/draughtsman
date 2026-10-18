/**
 * The arrowhead ends the edge properties panel offers, grouped so the list stays usable. The basic heads are the
 * original vocabulary; the ER crow's foot and UML groups are the notation a real diagram needs. This table enumerates
 * the same names the model's `ArrowHead` union and the document schema admit (model/types.ts, document.schema.json).
 */

import type { ArrowHead } from './types.ts';

export interface ArrowHeadGroup {
  label: string;
  heads: readonly ArrowHead[];
}

/** The order the groups appear in the pickers; every head the model admits appears exactly once. */
export const ARROW_HEAD_GROUPS: readonly ArrowHeadGroup[] = [
  { label: 'Basic', heads: ['none', 'arrow', 'open', 'diamond', 'circle'] },
  { label: 'ER', heads: ['one', 'many', 'zeroOrOne', 'oneOrMany', 'zeroOrMany', 'oneAndOnlyOne'] },
  { label: 'UML', heads: ['inheritance', 'aggregation', 'composition'] },
];

/** The complete set of head names, in picker order; used to keep the list aligned with the model contract. */
export const ARROW_HEADS: readonly ArrowHead[] = ARROW_HEAD_GROUPS.flatMap((group) => group.heads);

/** A short human label for each head, for the picker options and any announcement. */
export const ARROW_HEAD_LABELS: Readonly<Record<ArrowHead, string>> = {
  none: 'None',
  arrow: 'Arrow',
  open: 'Open',
  diamond: 'Diamond',
  circle: 'Circle',
  one: 'One',
  many: 'Many',
  zeroOrOne: 'Zero or one',
  oneOrMany: 'One or many',
  zeroOrMany: 'Zero or many',
  oneAndOnlyOne: 'One and only one',
  inheritance: 'Inheritance',
  aggregation: 'Aggregation',
  composition: 'Composition',
};