/**
 * What the Text pane remembers for this viewer in this browser: whether it is open, and which text it shows. View state
 * only, never part of the document, so it lives in localStorage like the empty-canvas hint's memory (canvasHint.ts).
 * Every storage access is guarded: a blocked or full store, a private window or a value someone edited by hand only
 * means the pane starts closed on the DSL, never an error.
 */

export const TEXT_PANE_KEY = 'draughtsman.textPane';

export type TextFormat = 'dsl' | 'mermaid';

export interface TextPanePrefs {
  open: boolean;
  format: TextFormat;
}

const DEFAULTS: TextPanePrefs = { open: false, format: 'dsl' };

export function loadTextPanePrefs(): TextPanePrefs {
  try {
    const raw = window.localStorage.getItem(TEXT_PANE_KEY);
    if (raw === null) {
      return DEFAULTS;
    }
    const stored = JSON.parse(raw) as { open?: unknown; format?: unknown } | null;
    return {
      open: stored?.open === true,
      format: stored?.format === 'mermaid' ? 'mermaid' : 'dsl',
    };
  } catch {
    return DEFAULTS;
  }
}

/** Remembers the given parts and keeps the other as it was. A store that refuses is ignored. */
export function saveTextPanePrefs(change: Partial<TextPanePrefs>): void {
  try {
    window.localStorage.setItem(TEXT_PANE_KEY, JSON.stringify({ ...loadTextPanePrefs(), ...change }));
  } catch {
    // Blocked storage: the pane simply starts closed next time, which is harmless.
  }
}
