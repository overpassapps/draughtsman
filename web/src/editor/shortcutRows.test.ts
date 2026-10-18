import type { EditorAction } from './actions.ts';
import { keyCaps, shortcutRows } from './shortcutRows.ts';

const action = (id: string, group: EditorAction['group'], label: string, shortcut?: string): EditorAction => ({
  id,
  label,
  group,
  ...(shortcut === undefined ? {} : { shortcut: { label: shortcut, matches: () => false } }),
  enabled: () => true,
  run: () => undefined,
});

describe('the keyboard-shortcut sheet rows', () => {
  it('splits a chord into its key caps, keeping a literal plus as a key', () => {
    expect(keyCaps('Cmd/Ctrl+Shift+Z')).toEqual(['Cmd/Ctrl', 'Shift', 'Z']);
    expect(keyCaps('?')).toEqual(['?']);
    expect(keyCaps('Cmd/Ctrl++')).toEqual(['Cmd/Ctrl', '+']);
    expect(keyCaps("Cmd/Ctrl+'")).toEqual(['Cmd/Ctrl', "'"]);
  });

  it('lists only actions that have a shortcut, under the menu their group folds into', () => {
    const rows = shortcutRows([
      action('a', 'Edit', 'Undo', 'Cmd/Ctrl+Z'),
      action('b', 'Edit', 'Add service'),
      action('c', 'Move', 'Bring forward', 'Cmd/Ctrl+]'),
      action('d', 'View', 'Keyboard shortcuts', '?'),
    ]);

    expect(rows.map((section) => [section.menu, section.actions.map((entry) => entry.label)])).toEqual([
      ['Edit', ['Undo']],
      ['View', ['Keyboard shortcuts']],
      ['Arrange', ['Bring forward']],
    ].sort((a, b) => ['File', 'Edit', 'View', 'Arrange', 'Help'].indexOf(a[0] as string) - ['File', 'Edit', 'View', 'Arrange', 'Help'].indexOf(b[0] as string)));
    expect(rows.flatMap((section) => section.actions.map((entry) => entry.label))).not.toContain('Add service');
  });
});
