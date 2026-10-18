// @vitest-environment jsdom
import { fireEvent, render, screen, within } from '@testing-library/react';
import type { ActionGroup, EditorAction } from './actions.ts';
import { EditorMenu } from './EditorMenu.tsx';
import { menuSections } from './menus.ts';

function action(label: string, group: ActionGroup, enabled = true, shortcut?: string): EditorAction & { runs: number } {
  const record = {
    id: label.toLowerCase(),
    label,
    group,
    runs: 0,
    ...(shortcut === undefined ? {} : { shortcut: { label: shortcut, matches: () => false } }),
    enabled: () => enabled,
    run: () => {
      record.runs += 1;
    },
  };
  return record;
}

function setup() {
  const save = action('Save', 'Document');
  const undo = action('Undo', 'Edit', false, 'Ctrl+Z');
  const remove = action('Delete', 'Edit', true, 'Delete');
  const duplicate = action('Duplicate', 'Edit', true, 'Ctrl+D');
  const zoom = action('Zoom in', 'View', true, 'Ctrl+=');
  const front = action('Bring to front', 'Arrange', true);
  const palette = action('Command palette', 'View', true, 'Ctrl+K');
  const onRun = vi.fn((chosen: EditorAction) => {
    chosen.run();
  });
  const view = render(
    <>
      <EditorMenu sections={() => menuSections([save, undo, remove, duplicate, zoom, front], [palette])} onRun={onRun} />
      <button type="button">Outside</button>
    </>,
  );
  const button = screen.getByRole('button', { name: 'Menu' });
  const focused = () => globalThis.document.activeElement?.getAttribute('aria-label');
  return { ...view, button, onRun, focused, save, undo, remove, duplicate, zoom, front, palette };
}

describe('EditorMenu', () => {
  it('opens on Enter at the first item and on Up at the last, showing the atlas’s menus with each shortcut', () => {
    const { button, focused } = setup();
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('menu')).toBeNull();
    fireEvent.keyDown(button, { key: 'Enter' });
    const menu = screen.getByRole('menu', { name: 'Menu' });
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.getAttribute('aria-controls')).toBe(menu.id);
    expect(within(menu).getAllByRole('group').map((group) => group.getAttribute('aria-label'))).toEqual(['File', 'Edit', 'View', 'Arrange', 'Help']);
    expect(within(menu).getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'))).toEqual([
      'Save',
      'Undo, Ctrl+Z',
      'Delete, Delete',
      'Duplicate, Ctrl+D',
      'Zoom in, Ctrl+=',
      'Bring to front',
      'Command palette, Ctrl+K',
    ]);
    expect(within(menu).getByRole('menuitem', { name: 'Undo, Ctrl+Z' }).getAttribute('aria-disabled')).toBe('true');
    expect(within(menu).getByRole('menuitem', { name: 'Delete, Delete' }).getAttribute('aria-disabled')).toBe('false');
    expect(focused()).toBe('Save');

    fireEvent.keyDown(globalThis.document.activeElement ?? menu, { key: 'Escape' });
    expect(screen.queryByRole('menu')).toBeNull();
    expect(globalThis.document.activeElement).toBe(button);
    fireEvent.keyDown(button, { key: 'ArrowUp' });
    expect(focused()).toBe('Command palette, Ctrl+K');
  });

  it('moves with Up and Down, wrapping, jumps with Home and End, and finds an item by its first letter', () => {
    const { button, focused } = setup();
    fireEvent.keyDown(button, { key: 'ArrowDown' });
    const key = (name: string) => fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: name });
    key('ArrowDown');
    expect(focused()).toBe('Undo, Ctrl+Z');
    key('ArrowUp');
    key('ArrowUp');
    expect(focused()).toBe('Command palette, Ctrl+K');
    key('ArrowDown');
    expect(focused()).toBe('Save');
    key('End');
    expect(focused()).toBe('Command palette, Ctrl+K');
    key('Home');
    expect(focused()).toBe('Save');
    // D finds Delete, then Duplicate, then wraps back to Delete.
    key('d');
    expect(focused()).toBe('Delete, Delete');
    key('d');
    expect(focused()).toBe('Duplicate, Ctrl+D');
    key('d');
    expect(focused()).toBe('Delete, Delete');
  });

  it('runs the focused item on Enter or Space as a keyboard choice and closes; a disabled one runs nothing and stays open', () => {
    const { button, onRun, focused, undo, remove, zoom } = setup();
    fireEvent.keyDown(button, { key: 'Enter' });
    fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'ArrowDown' });
    expect(focused()).toBe('Undo, Ctrl+Z');
    fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'Enter' });
    expect(onRun).not.toHaveBeenCalled();
    expect(undo.runs).toBe(0);
    expect(screen.getByRole('menu')).toBeTruthy();

    fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'ArrowDown' });
    fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: ' ' });
    expect(onRun).toHaveBeenCalledExactlyOnceWith(remove, true);
    expect(remove.runs).toBe(1);
    expect(screen.queryByRole('menu')).toBeNull();

    fireEvent.click(button);
    fireEvent.click(screen.getByRole('menuitem', { name: 'Zoom in, Ctrl+=' }));
    expect(onRun).toHaveBeenLastCalledWith(zoom, false);
    expect(zoom.runs).toBe(1);
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('keeps every key pressed inside it from the page behind, and closes on Tab with focus on the button', () => {
    const { button } = setup();
    const onWindowKey = vi.fn();
    window.addEventListener('keydown', onWindowKey);
    try {
      fireEvent.keyDown(button, { key: 'Enter' });
      expect(onWindowKey).not.toHaveBeenCalled();
      fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'v' });
      fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'Tab' });
      expect(onWindowKey).not.toHaveBeenCalled();
      expect(screen.queryByRole('menu')).toBeNull();
      expect(globalThis.document.activeElement).toBe(button);
      // Closed, the button's other keys reach the page as usual.
      fireEvent.keyDown(button, { key: 'v' });
      expect(onWindowKey).toHaveBeenCalledTimes(1);
    } finally {
      window.removeEventListener('keydown', onWindowKey);
    }
  });

  it('closes on a press outside it, running nothing, and toggles closed from its own button', () => {
    const { button, onRun } = setup();
    fireEvent.click(button);
    expect(screen.getByRole('menu')).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole('menuitem', { name: 'Save' }));
    expect(screen.getByRole('menu')).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole('button', { name: 'Outside' }));
    expect(screen.queryByRole('menu')).toBeNull();
    expect(onRun).not.toHaveBeenCalled();
    fireEvent.click(button);
    expect(screen.getByRole('menu')).toBeTruthy();
    fireEvent.click(button);
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('reads the enabled states when it opens, not when it was first drawn', () => {
    let canDelete = false;
    const remove: EditorAction = { id: 'delete', label: 'Delete', group: 'Edit', enabled: () => canDelete, run: () => undefined };
    render(<EditorMenu sections={() => menuSections([remove])} onRun={() => undefined} />);
    const button = screen.getByRole('button', { name: 'Menu' });
    fireEvent.click(button);
    expect(screen.getByRole('menuitem', { name: 'Delete' }).getAttribute('aria-disabled')).toBe('true');
    fireEvent.click(button);
    canDelete = true;
    fireEvent.click(button);
    expect(screen.getByRole('menuitem', { name: 'Delete' }).getAttribute('aria-disabled')).toBe('false');
  });
});
