/**
 * The empty-canvas hint's memory (design/atlas/empty-loading-error-states.md): once dismissed, or once a node has been
 * drawn, it does not come back in this browser. Every storage access is guarded, so a blocked store only means the hint
 * may show again on the next empty canvas.
 */

export const CANVAS_HINT_KEY = 'draughtsman.canvasHintDismissed';

export function canvasHintDismissed(): boolean {
  try {
    return window.localStorage.getItem(CANVAS_HINT_KEY) === '1';
  } catch {
    return false;
  }
}

export function rememberCanvasHintDismissed(): void {
  try {
    window.localStorage.setItem(CANVAS_HINT_KEY, '1');
  } catch {
    // Blocked storage: the hint may appear again on the next empty canvas, which is harmless.
  }
}

import { modifierName } from './platform.ts';

/** "Cmd" on a Mac, "Ctrl" elsewhere. */
export function paletteModifier(): string {
  return modifierName();
}
