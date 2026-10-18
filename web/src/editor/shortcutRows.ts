/** What the keyboard-shortcut sheet (ShortcutSheet.tsx) lists, as pure functions of the action registry. */
import type { EditorAction } from './actions.ts';
import { menuSections } from './menus.ts';

/** `Cmd/Ctrl+Shift+Z` as its key caps. */
export function keyCaps(label: string): string[] {
  return label.split(/\+(?=.)/).filter((part) => part !== '');
}

/** The actions worth listing: those with a shortcut. Clipboard rows carry one that never matches a keydown but names the key. */
export function shortcutRows(actions: readonly EditorAction[]): ReturnType<typeof menuSections> {
  return menuSections(actions.filter((action) => action.shortcut !== undefined));
}
