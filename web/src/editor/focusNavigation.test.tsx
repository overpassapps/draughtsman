// @vitest-environment jsdom
import { fireEvent, render } from '@testing-library/react';
import { arrowFocusHandler, arrowStep, focusableIn, regionStep } from './focusNavigation.ts';
import { regionShortcut } from './shortcuts.ts';

describe('arrowStep', () => {
  it('moves down and up a rail, wrapping at both ends, and ignores the other axis', () => {
    const rail = { kind: 'vertical' } as const;
    expect(arrowStep('ArrowDown', rail, 0, 4)).toBe(1);
    expect(arrowStep('ArrowDown', rail, 3, 4)).toBe(0);
    expect(arrowStep('ArrowUp', rail, 1, 4)).toBe(0);
    expect(arrowStep('ArrowUp', rail, 0, 4)).toBe(3);
    expect(arrowStep('ArrowRight', rail, 0, 4)).toBeUndefined();
    expect(arrowStep('ArrowLeft', rail, 0, 4)).toBeUndefined();
  });

  it('moves right and left along a row, wrapping at both ends, and ignores the other axis', () => {
    const row = { kind: 'horizontal' } as const;
    expect(arrowStep('ArrowRight', row, 2, 6)).toBe(3);
    expect(arrowStep('ArrowRight', row, 5, 6)).toBe(0);
    expect(arrowStep('ArrowLeft', row, 0, 6)).toBe(5);
    expect(arrowStep('ArrowDown', row, 0, 6)).toBeUndefined();
    expect(arrowStep('ArrowUp', row, 0, 6)).toBeUndefined();
  });

  it('moves through a grid by one along a row and by a row up and down, stopping at every edge', () => {
    // Seven glyphs in rows of three: 0 1 2 / 3 4 5 / 6.
    const grid = { kind: 'grid', columns: 3 } as const;
    expect(arrowStep('ArrowRight', grid, 1, 7)).toBe(2);
    expect(arrowStep('ArrowLeft', grid, 1, 7)).toBe(0);
    expect(arrowStep('ArrowDown', grid, 1, 7)).toBe(4);
    expect(arrowStep('ArrowUp', grid, 4, 7)).toBe(1);
    expect(arrowStep('ArrowDown', grid, 3, 7)).toBe(6);
    // No row below 4's column, none above the first row, nothing past either end.
    expect(arrowStep('ArrowDown', grid, 4, 7)).toBe(4);
    expect(arrowStep('ArrowUp', grid, 2, 7)).toBe(2);
    expect(arrowStep('ArrowRight', grid, 6, 7)).toBe(6);
    expect(arrowStep('ArrowLeft', grid, 0, 7)).toBe(0);
  });

  it('jumps to either end on Home and End, and does nothing for another key or an unknown index', () => {
    for (const layout of [{ kind: 'vertical' }, { kind: 'horizontal' }, { kind: 'grid', columns: 3 }] as const) {
      expect(arrowStep('Home', layout, 2, 5)).toBe(0);
      expect(arrowStep('End', layout, 2, 5)).toBe(4);
      expect(arrowStep('Enter', layout, 2, 5)).toBeUndefined();
      expect(arrowStep('ArrowDown', layout, -1, 5)).toBeUndefined();
      expect(arrowStep('Home', layout, 0, 0)).toBeUndefined();
    }
  });
});

describe('regionStep', () => {
  it('cycles forward and back, wrapping, and starts at the first or last region from outside them all', () => {
    expect(regionStep(0, 4, 1)).toBe(1);
    expect(regionStep(3, 4, 1)).toBe(0);
    expect(regionStep(0, 4, -1)).toBe(3);
    expect(regionStep(2, 4, -1)).toBe(1);
    expect(regionStep(-1, 4, 1)).toBe(0);
    expect(regionStep(-1, 4, -1)).toBe(3);
  });

  it('is F6 forward and Shift+F6 back, and nothing with a command modifier', () => {
    const chord = (mods: Partial<Record<'metaKey' | 'ctrlKey' | 'shiftKey' | 'altKey', boolean>> = {}, key = 'F6') => ({ key, code: key, metaKey: false, ctrlKey: false, shiftKey: false, altKey: false, ...mods });
    expect(regionShortcut(chord())).toBe(1);
    expect(regionShortcut(chord({ shiftKey: true }))).toBe(-1);
    expect(regionShortcut(chord({ ctrlKey: true }))).toBeUndefined();
    expect(regionShortcut(chord({ metaKey: true }))).toBeUndefined();
    expect(regionShortcut(chord({ altKey: true }))).toBeUndefined();
    expect(regionShortcut(chord({}, 'F5'))).toBeUndefined();
  });
});

describe('focusableIn and arrowFocusHandler', () => {
  function Row({ within }: { within?: string }) {
    return (
      <div onKeyDown={arrowFocusHandler({ kind: 'horizontal' }, 'button', within)}>
        <div role="group">
          <button type="button">A</button>
          <button type="button" disabled>
            B
          </button>
          <button type="button">C</button>
        </div>
        <div role="group">
          <button type="button">D</button>
          <input aria-label="Field" />
        </div>
      </div>
    );
  }

  it('lists what Tab reaches, leaving out disabled controls, hidden ones and those taken out of the tab order', () => {
    const { container } = render(
      <div>
        <button type="button">One</button>
        <button type="button" disabled>
          Two
        </button>
        <span tabIndex={-1}>Three</span>
        <input aria-label="Four" />
        <span tabIndex={0}>Five</span>
        <div hidden>
          <button type="button">Six</button>
        </div>
      </div>,
    );
    expect(focusableIn(container).map((element) => element.textContent || element.getAttribute('aria-label'))).toEqual(['One', 'Four', 'Five']);
  });

  it('moves focus between the enabled buttons with the arrows, skipping a disabled one, and leaves modified arrows alone', () => {
    const { getByText } = render(<Row />);
    getByText('A').focus();
    expect(fireEvent.keyDown(getByText('A'), { key: 'ArrowRight' })).toBe(false);
    expect(globalThis.document.activeElement).toBe(getByText('C'));
    fireEvent.keyDown(getByText('C'), { key: 'ArrowRight' });
    expect(globalThis.document.activeElement).toBe(getByText('D'));
    fireEvent.keyDown(getByText('D'), { key: 'ArrowRight' });
    expect(globalThis.document.activeElement).toBe(getByText('A'));
    fireEvent.keyDown(getByText('A'), { key: 'ArrowLeft' });
    expect(globalThis.document.activeElement).toBe(getByText('D'));
    expect(fireEvent.keyDown(getByText('D'), { key: 'ArrowLeft', shiftKey: true })).toBe(true);
    expect(globalThis.document.activeElement).toBe(getByText('D'));
  });

  it('keeps to the focused button’s own group with `within`, and leaves a text field’s caret keys to it', () => {
    const { getByText, getByLabelText } = render(<Row within='[role="group"]' />);
    getByText('C').focus();
    fireEvent.keyDown(getByText('C'), { key: 'ArrowRight' });
    expect(globalThis.document.activeElement).toBe(getByText('A'));
    getByText('D').focus();
    fireEvent.keyDown(getByText('D'), { key: 'ArrowRight' });
    expect(globalThis.document.activeElement).toBe(getByText('D'));
    const field = getByLabelText('Field');
    field.focus();
    expect(fireEvent.keyDown(field, { key: 'ArrowLeft' })).toBe(true);
    expect(globalThis.document.activeElement).toBe(field);
  });
});
