/**
 * Pure helpers for the styling panel (editor/StylePanel.tsx): the shared-or-mixed state of one property across the
 * selection, kept apart from the component so it is unit-testable without a DOM render.
 *
 * A property is "shared" when every selected element carries the same value for it (absent counts as the element
 * inheriting the theme, and two absent elements are equal). "Mixed" means the selection disagrees. A change applies
 * the same value to every selected element, and a reset clears the override on all of them, so the shared/mixed
 * reading is the whole contract the panel needs to label a control.
 */

import type { Dash } from '../model/types.ts';

/** A style property the panel can edit on a shape or a connector. */
export type StylableField = 'fill' | 'stroke' | 'strokeWidth' | 'dash' | 'font' | 'fontSize' | 'bold';

/**
 * The state of one property across `count` selected elements whose values are `values` (one per element). Shared when
 * there is exactly one distinct value; mixed otherwise. The value is the shared one, or undefined when mixed.
 */
export function sharedValue(values: readonly unknown[]): { value: unknown; mixed: boolean } {
  if (values.length === 0) {
    return { value: undefined, mixed: false };
  }
  const [first, ...rest] = values;
  const mixed = rest.some((value) => value !== first);
  return { value: mixed ? undefined : first, mixed };
}

/** The dash pattern options the panel offers, in presentation order. */
export const DASH_OPTIONS: readonly Dash[] = ['solid', 'dashed', 'dotted'];

/** The smallest and largest font size the panel's size stepper offers. */
export const MIN_FONT_SIZE = 8;
export const MAX_FONT_SIZE = 72;

/** A palette key as a person would say it: `redDeep` is "Red deep". */
export function swatchName(key: string): string {
  const words = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
