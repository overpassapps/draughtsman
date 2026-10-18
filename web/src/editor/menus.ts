/**
 * The top bar menu's sections (design/atlas/editor-shell.md, "menu button opening File, Edit, View, Arrange and Help
 * groups"; checklist item 28): every registered editor action (actions.ts) under one of the atlas's five menus, so the
 * menu, the palette and the shortcuts run the same records. The palette's finer groups fold into the menus here rather
 * than each action naming a menu, so an action added to the registry is in the menu without further wiring.
 *
 * Help holds what is not an editor action in the registry's sense (the command palette itself), passed separately.
 */

import type { ActionGroup, EditorAction } from './actions.ts';

export const MENUS = ['File', 'Edit', 'View', 'Arrange', 'Help'] as const;

export type MenuName = (typeof MENUS)[number];

const MENU_OF: Readonly<Record<ActionGroup, MenuName>> = {
  Document: 'File',
  Edit: 'Edit',
  Insert: 'Edit',
  Tools: 'Edit',
  View: 'View',
  Move: 'Arrange',
  Arrange: 'Arrange',
};

export function menuOf(group: ActionGroup): MenuName {
  return MENU_OF[group];
}

export interface MenuSection {
  menu: MenuName;
  actions: readonly EditorAction[];
}

/**
 * The menu's sections in MENUS order, each holding its actions in the order given (pass them through orderActions for
 * the palette's order). A menu with nothing in it is left out, as File is on a read-only preview.
 */
export function menuSections(actions: readonly EditorAction[], help: readonly EditorAction[] = []): MenuSection[] {
  return MENUS.map((menu) => ({ menu, actions: menu === 'Help' ? help : actions.filter((action) => menuOf(action.group) === menu) })).filter(
    (section) => section.actions.length > 0,
  );
}
