/**
 * Keyboard focus movement for the editor's chrome (design/atlas/editor-shell.md, Keyboard and accessibility): arrow
 * keys move within a rail, a row of panel buttons or the shape panel's grid, while Tab keeps moving between controls;
 * F6 cycles focus between the shell's regions. The index arithmetic is pure so each layout is testable on its own;
 * the handlers only find the controls and move focus.
 */

import type { KeyboardEvent } from 'react';

/** How a set of controls is laid out, which decides the arrow keys that move between them. */
export type ArrowLayout = { kind: 'vertical' } | { kind: 'horizontal' } | { kind: 'grid'; columns: number };

/**
 * The index focus moves to from `index` of `count` controls on `key`, or undefined when the key does not move focus
 * in this layout. A rail or row wraps at either end; a grid stops at its edges, so Down on the last row stays put.
 * Home and End go to the first and last control in every layout.
 */
export function arrowStep(key: string, layout: ArrowLayout, index: number, count: number): number | undefined {
  if (count === 0 || index < 0 || index >= count) {
    return undefined;
  }
  if (key === 'Home') {
    return 0;
  }
  if (key === 'End') {
    return count - 1;
  }
  const wrap = (delta: number): number => (index + delta + count) % count;
  switch (layout.kind) {
    case 'vertical':
      return key === 'ArrowDown' ? wrap(1) : key === 'ArrowUp' ? wrap(-1) : undefined;
    case 'horizontal':
      return key === 'ArrowRight' ? wrap(1) : key === 'ArrowLeft' ? wrap(-1) : undefined;
    case 'grid':
      switch (key) {
        case 'ArrowRight':
          return Math.min(index + 1, count - 1);
        case 'ArrowLeft':
          return Math.max(index - 1, 0);
        case 'ArrowDown':
          return index + layout.columns < count ? index + layout.columns : index;
        case 'ArrowUp':
          return index - layout.columns >= 0 ? index - layout.columns : index;
        default:
          return undefined;
      }
  }
}

/** The region F6 (step 1) or Shift+F6 (step -1) moves to, from `current` of `count` (-1 when focus is in none). */
export function regionStep(current: number, count: number, step: 1 | -1): number {
  if (current < 0) {
    return step === 1 ? 0 : count - 1;
  }
  return (current + step + count) % count;
}

/** The controls inside `container` that Tab would reach, in document order: not disabled, not inside a `hidden` one. */
export function focusableIn(container: Element): HTMLElement[] {
  return [...container.querySelectorAll<HTMLElement>('button, input, select, textarea, [tabindex]')].filter(
    (element) => element.tabIndex >= 0 && !element.matches(':disabled') && element.closest('[hidden]') === null,
  );
}

/**
 * A keydown handler that moves focus between the enabled buttons matched by `selector` inside the element it is bound
 * to, as `layout` says. With `within`, the buttons are those inside the closest ancestor of the focused one matching
 * it (one row of the properties panel, say), so one handler serves every row of a panel. Modified arrows are left
 * alone, as is a key from anything that is not one of the buttons (a text field keeps its caret keys).
 */
export function arrowFocusHandler(layout: ArrowLayout, selector = 'button', within?: string): (e: KeyboardEvent<HTMLElement>) => void {
  return (e) => {
    if (e.metaKey || e.ctrlKey || e.altKey || e.shiftKey || !(e.target instanceof HTMLElement)) {
      return;
    }
    const scope = within === undefined ? e.currentTarget : e.target.closest(within);
    if (scope === null || !e.currentTarget.contains(scope)) {
      return;
    }
    const items = [...scope.querySelectorAll<HTMLElement>(selector)].filter((element) => !element.matches(':disabled'));
    const next = arrowStep(e.key, layout, items.indexOf(e.target), items.length);
    const target = next === undefined ? undefined : items[next];
    if (target !== undefined) {
      e.preventDefault();
      target.focus();
    }
  };
}
