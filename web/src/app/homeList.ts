/** Timings of the home list (pages/HomePage.tsx), kept apart so the page file exports only a component. */

/** How long the search waits after the last keystroke before asking the server. */
export const SEARCH_DEBOUNCE_MS = 250;

/** How long "Moved to the bin" stays on screen with its Undo. */
export const UNDO_TOAST_MS = 10_000;

/** How long a status line with nothing to undo stays up (restored, duplicated, deleted for good). */
export const STATUS_MS = 6_000;
