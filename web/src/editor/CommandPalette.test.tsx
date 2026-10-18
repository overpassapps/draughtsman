// @vitest-environment jsdom
import { fireEvent, render, screen } from '@testing-library/react';
import type { EditorAction } from './actions.ts';
import { CommandPalette } from './CommandPalette.tsx';

function setup(enabled: Record<string, boolean>) {
  const ran: string[] = [];
  const onClose = vi.fn();
  const actions: EditorAction[] = Object.entries(enabled).map(([label, can]) => ({
    id: label,
    label,
    group: 'Edit',
    shortcut: { label: `Key ${label}`, matches: () => false },
    enabled: () => can,
    run: () => {
      ran.push(label);
    },
  }));
  const view = render(
    <CommandPalette
      actions={actions}
      onRun={(action) => {
        action.run();
      }}
      onClose={onClose}
    />,
  );
  const search = screen.getByRole<HTMLInputElement>('combobox', { name: 'Search commands' });
  const active = () => {
    const id = search.getAttribute('aria-activedescendant');
    return id === null ? undefined : globalThis.document.getElementById(id)?.querySelector('.palette-label')?.textContent;
  };
  return { ...view, ran, onClose, search, active };
}

describe('CommandPalette', () => {
  it('focuses the search field and highlights the first action that can run', () => {
    const { search, active } = setup({ Undo: false, Delete: true, Save: true });
    expect(globalThis.document.activeElement).toBe(search);
    expect(active()).toBe('Delete');
  });

  it('shows each action with its shortcut, a disabled one marked as such', () => {
    setup({ Undo: false, Delete: true });
    const [undo, remove] = screen.getAllByRole('option');
    expect(undo?.textContent).toBe('EditUndoKey Undo');
    expect(undo?.getAttribute('aria-disabled')).toBe('true');
    expect(remove?.getAttribute('aria-disabled')).toBe('false');
  });

  it('wraps the highlight at both ends and runs the highlighted action on Enter', () => {
    const { search, active, ran } = setup({ Undo: true, Delete: true, Save: true });
    fireEvent.keyDown(search, { key: 'ArrowUp' });
    expect(active()).toBe('Save');
    fireEvent.keyDown(search, { key: 'ArrowDown' });
    expect(active()).toBe('Undo');
    fireEvent.keyDown(search, { key: 'ArrowDown' });
    expect(active()).toBe('Delete');
    fireEvent.keyDown(search, { key: 'Enter' });
    expect(ran).toEqual(['Delete']);
  });

  it('runs nothing for a disabled action, by Enter or by click', () => {
    const { search, active, ran } = setup({ Undo: false, Delete: true });
    fireEvent.keyDown(search, { key: 'ArrowUp' });
    expect(active()).toBe('Undo');
    fireEvent.keyDown(search, { key: 'Enter' });
    fireEvent.click(screen.getAllByRole('option')[0] ?? search);
    expect(ran).toEqual([]);
    fireEvent.click(screen.getAllByRole('option')[1] ?? search);
    expect(ran).toEqual(['Delete']);
  });

  it('filters as the user types, highlighting the first match that can run, and says when nothing matches', () => {
    const { search, active } = setup({ 'Bring to front': false, 'Send to back': true, 'Bring forward': true });
    fireEvent.change(search, { target: { value: 'bring' } });
    expect(screen.getAllByRole('option').map((o) => o.querySelector('.palette-label')?.textContent)).toEqual(['Bring to front', 'Bring forward']);
    expect(active()).toBe('Bring forward');
    expect(screen.queryByText('No matching commands')).toBeNull();
    fireEvent.change(search, { target: { value: 'rotate' } });
    expect(screen.queryAllByRole('option')).toHaveLength(0);
    expect(screen.getByText('No matching commands')).toBeTruthy();
    fireEvent.keyDown(search, { key: 'Enter' });
  });

  it('closes on Escape and on a press outside the dialog, but not on a press inside it', () => {
    const { search, onClose, container } = setup({ Undo: true });
    fireEvent.mouseDown(search);
    fireEvent.mouseDown(screen.getByRole('dialog'));
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.keyDown(search, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    const backdrop = container.querySelector('.palette-backdrop');
    if (!backdrop) {
      throw new Error('no backdrop');
    }
    fireEvent.mouseDown(backdrop);
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('keeps Tab in the dialog and a press on a row from taking focus', () => {
    const { search } = setup({ Undo: true });
    expect(fireEvent.keyDown(search, { key: 'Tab' })).toBe(false);
    expect(fireEvent.mouseDown(screen.getAllByRole('option')[0] ?? search)).toBe(false);
    expect(globalThis.document.activeElement).toBe(search);
  });

  it('keeps focus in the search field for a press anywhere else in the dialog, so Escape still closes', () => {
    const { search, onClose } = setup({ Undo: true });
    expect(fireEvent.mouseDown(search)).toBe(true);
    expect(fireEvent.mouseDown(screen.getByRole('dialog'))).toBe(false);
    fireEvent.change(search, { target: { value: 'rotate' } });
    expect(fireEvent.mouseDown(screen.getByText('No matching commands'))).toBe(false);
    expect(globalThis.document.activeElement).toBe(search);
    fireEvent.keyDown(search, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
