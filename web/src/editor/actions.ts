/**
 * The editor's action registry (checklist item 23; design/atlas/editor-shell.md, Command palette): every editor
 * action as one record of its name, the shortcut that runs it, whether it can run now and what it does. A shortcut
 * and the palette both run an action through its record, so the key and the palette row can never disagree about the
 * effect, and the shortcut the palette shows is the one the keyboard matches.
 *
 * Each action is contributed by the part of the editor that owns the state it acts on: history by the document
 * editor (useDocumentEditor.ts); selection, viewport and label editing by the canvas (DiagramCanvas.tsx, through its
 * handle); tools, grid, add shape, align and save by the viewer (DocumentViewer.tsx). Each owner's keydown listener
 * hands its own actions to `dispatchShortcut`, and the palette lists all of them together, in `ACTION_GROUPS` order.
 */

import type { KeyChord } from './shortcuts.ts';

export interface Shortcut {
  /** What the palette shows, in the chrome's "Cmd/Ctrl+Z" form. */
  label: string;
  /** Whether a key event is this shortcut; built from the matchers in shortcuts.ts. */
  matches: (e: KeyChord) => boolean;
  /**
   * Whether the key is kept from the browser even while the action cannot run, as Cmd/Ctrl+[ is kept from Back in an
   * editor with nothing selected. Without it a disabled action leaves the key to the browser.
   */
  claim?: () => boolean;
}

export const ACTION_GROUPS = ['Edit', 'Insert', 'Tools', 'Move', 'Arrange', 'View', 'Document'] as const;

export type ActionGroup = (typeof ACTION_GROUPS)[number];

export interface EditorAction {
  /** Stable and unique across the registry: `<owner>.<action>`. */
  id: string;
  /** The palette row's text, in the words the chrome uses for the same control. */
  label: string;
  group: ActionGroup;
  shortcut?: Shortcut;
  /** Other words the palette search matches, for a name the user may reach for instead ("remove" for Delete). */
  keywords?: string;
  /** Read when the palette renders and when the shortcut is pressed. */
  enabled: () => boolean;
  run: () => void;
}

/**
 * Runs the action a key event is the shortcut for. Returns whether an action claimed the key: an enabled one runs
 * and the event's default is prevented; a disabled one runs nothing and prevents the default only if its shortcut
 * claims the key. The caller decides where shortcuts are live (not in a text field, say) before calling this.
 */
export function dispatchShortcut(actions: readonly EditorAction[], e: KeyChord & Pick<KeyboardEvent, 'preventDefault'>): boolean {
  const action = actions.find((candidate) => candidate.shortcut?.matches(e) === true);
  if (action === undefined) {
    return false;
  }
  if (action.enabled()) {
    e.preventDefault();
    action.run();
  } else if (action.shortcut?.claim?.() === true) {
    e.preventDefault();
  }
  return true;
}

/** The registry's actions in palette order: by group, then as each owner listed them. */
export function orderActions(actions: readonly EditorAction[]): EditorAction[] {
  return actions
    .map((action, position) => ({ action, position }))
    .sort((a, b) => ACTION_GROUPS.indexOf(a.action.group) - ACTION_GROUPS.indexOf(b.action.group) || a.position - b.position)
    .map(({ action }) => action);
}

/**
 * The actions a palette query finds: every word of the query, ignoring case, appears in the action's group, label or
 * keywords. An empty query finds everything. Order is kept.
 */
export function filterActions(actions: readonly EditorAction[], query: string): EditorAction[] {
  const words = query.toLowerCase().split(/\s+/).filter((word) => word !== '');
  return actions.filter((action) => {
    const text = `${action.group} ${action.label} ${action.keywords ?? ''}`.toLowerCase();
    return words.every((word) => text.includes(word));
  });
}
