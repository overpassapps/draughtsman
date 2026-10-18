/**
 * How the home screen lays its documents out for this viewer in this browser: a grid of cards with thumbnails (the default) or
 * the table. View state only, never part of a document, so it lives in localStorage like the Text pane's open state
 * (editor/textPanePrefs.ts). Every storage access is guarded: a blocked or full store, a private window or a value someone edited
 * by hand only means the grid, never an error.
 */

export const HOME_LAYOUT_KEY = 'draughtsman.homeLayout';

export type HomeLayout = 'grid' | 'list';

export function loadHomeLayout(): HomeLayout {
  try {
    return window.localStorage.getItem(HOME_LAYOUT_KEY) === 'list' ? 'list' : 'grid';
  } catch {
    return 'grid';
  }
}

export function saveHomeLayout(layout: HomeLayout): void {
  try {
    window.localStorage.setItem(HOME_LAYOUT_KEY, layout);
  } catch {
    // Blocked storage: the page simply opens on the grid next time, which is harmless.
  }
}
