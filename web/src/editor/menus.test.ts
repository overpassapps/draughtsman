import { ACTION_GROUPS, type ActionGroup, type EditorAction } from './actions.ts';
import { MENUS, menuOf, menuSections } from './menus.ts';

const action = (id: string, group: ActionGroup): EditorAction => ({ id, label: id, group, enabled: () => true, run: () => undefined });

describe('menuSections', () => {
  it('files every registry group under one of the atlas’s menus, never Help', () => {
    for (const group of ACTION_GROUPS) {
      expect(MENUS).toContain(menuOf(group));
      expect(menuOf(group)).not.toBe('Help');
    }
    expect(ACTION_GROUPS.map(menuOf)).toEqual(['Edit', 'Edit', 'Edit', 'Arrange', 'Arrange', 'View', 'File']);
  });

  it('lists each action once, under its menu, in the atlas’s menu order and the order given within a menu', () => {
    const actions = [action('zoom', 'View'), action('undo', 'Edit'), action('save', 'Document'), action('front', 'Arrange'), action('nudge', 'Move'), action('add', 'Insert'), action('tool', 'Tools')];
    const palette = action('palette', 'View');
    const sections = menuSections(actions, [palette]);
    expect(sections.map((section) => [section.menu, section.actions.map((listed) => listed.id)])).toEqual([
      ['File', ['save']],
      ['Edit', ['undo', 'add', 'tool']],
      ['View', ['zoom']],
      ['Arrange', ['front', 'nudge']],
      ['Help', ['palette']],
    ]);
    // Every action given is listed exactly once, and nothing else is.
    expect(sections.flatMap((section) => section.actions).sort((a, b) => a.id.localeCompare(b.id))).toEqual([...actions, palette].sort((a, b) => a.id.localeCompare(b.id)));
  });

  it('leaves out a menu with nothing in it', () => {
    expect(menuSections([action('zoom', 'View')]).map((section) => section.menu)).toEqual(['View']);
    expect(menuSections([])).toEqual([]);
  });
});
