import { dispatchShortcut, filterActions, orderActions, type EditorAction } from './actions.ts';
import { historyCommand } from './shortcuts.ts';

const chord = (key: string, mods: Partial<Record<'metaKey' | 'ctrlKey' | 'shiftKey' | 'altKey', boolean>> = {}) => {
  let prevented = false;
  return {
    key,
    code: '',
    metaKey: false,
    ctrlKey: false,
    shiftKey: false,
    altKey: false,
    ...mods,
    preventDefault: () => {
      prevented = true;
    },
    prevented: () => prevented,
  };
};

function action(id: string, overrides: Partial<EditorAction> = {}): EditorAction & { runs: () => number } {
  let runs = 0;
  return {
    id,
    label: id,
    group: 'Edit',
    enabled: () => true,
    run: () => {
      runs += 1;
    },
    runs: () => runs,
    ...overrides,
  };
}

describe('dispatchShortcut', () => {
  it('runs the enabled action a key matches, and only that one, preventing the default', () => {
    const undo = action('undo', { shortcut: { label: 'Cmd/Ctrl+Z', matches: (e) => historyCommand(e) === 'undo' } });
    const redo = action('redo', { shortcut: { label: 'Cmd/Ctrl+Shift+Z', matches: (e) => historyCommand(e) === 'redo' } });
    const e = chord('z', { metaKey: true });
    expect(dispatchShortcut([redo, undo], e)).toBe(true);
    expect([undo.runs(), redo.runs()]).toEqual([1, 0]);
    expect(e.prevented()).toBe(true);
  });

  it('leaves a key no action matches alone', () => {
    const undo = action('undo', { shortcut: { label: 'Cmd/Ctrl+Z', matches: (e) => historyCommand(e) === 'undo' } });
    const e = chord('y', { metaKey: true });
    expect(dispatchShortcut([undo], e)).toBe(false);
    expect(undo.runs()).toBe(0);
    expect(e.prevented()).toBe(false);
  });

  it('never runs a disabled action, and keeps its key from the browser only when the shortcut claims it', () => {
    let claimed = false;
    const disabled = action('order', { enabled: () => false, shortcut: { label: 'Cmd/Ctrl+[', matches: (e) => e.key === '[', claim: () => claimed } });
    const unclaimed = chord('[', { metaKey: true });
    expect(dispatchShortcut([disabled], unclaimed)).toBe(true);
    expect(unclaimed.prevented()).toBe(false);
    claimed = true;
    const kept = chord('[', { metaKey: true });
    expect(dispatchShortcut([disabled], kept)).toBe(true);
    expect(kept.prevented()).toBe(true);
    expect(disabled.runs()).toBe(0);
  });

  it('skips actions without a shortcut', () => {
    const save = action('save');
    expect(dispatchShortcut([save], chord('s', { metaKey: true }))).toBe(false);
    expect(save.runs()).toBe(0);
  });
});

describe('orderActions', () => {
  it('sorts by group in palette order and keeps each owner\'s order within a group', () => {
    const zoom = action('zoom', { group: 'View' });
    const undo = action('undo', { group: 'Edit' });
    const grid = action('grid', { group: 'View' });
    const save = action('save', { group: 'Document' });
    const align = action('align', { group: 'Arrange' });
    expect(orderActions([zoom, save, undo, grid, align]).map((a) => a.id)).toEqual(['undo', 'align', 'zoom', 'grid', 'save']);
  });
});

describe('filterActions', () => {
  const actions = [
    action('undo', { label: 'Undo', group: 'Edit' }),
    action('delete', { label: 'Delete', group: 'Edit', keywords: 'remove' }),
    action('front', { label: 'Bring to front', group: 'Arrange' }),
    action('zoom', { label: 'Zoom in', group: 'View' }),
  ];
  const ids = (query: string) => filterActions(actions, query).map((a) => a.id);

  it('finds everything for an empty or blank query', () => {
    expect(ids('')).toEqual(['undo', 'delete', 'front', 'zoom']);
    expect(ids('   ')).toEqual(['undo', 'delete', 'front', 'zoom']);
  });

  it('matches every word against label, group and keywords, ignoring case', () => {
    expect(ids('FRONT')).toEqual(['front']);
    expect(ids('arrange')).toEqual(['front']);
    expect(ids('remove')).toEqual(['delete']);
    expect(ids('bring front')).toEqual(['front']);
  });

  it('drops an action missing any word', () => {
    expect(ids('bring back')).toEqual([]);
    expect(ids('zoom out')).toEqual([]);
  });
});
