// @vitest-environment jsdom
import { fireEvent, render, screen } from '@testing-library/react';
import { MermaidTextDialog } from './MermaidTextDialog.tsx';

const TEXT = 'flowchart TD\n    a["A"]\n';

describe('MermaidTextDialog', () => {
  it('shows the text, read-only and selected, in a modal dialog', () => {
    render(<MermaidTextDialog text={TEXT} onClose={() => undefined} />);

    const dialog = screen.getByRole('dialog', { name: 'Export Mermaid' });
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    const box = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Mermaid text' });
    expect(box.value).toBe(TEXT);
    expect(box.readOnly).toBe(true);
    expect(globalThis.document.activeElement).toBe(box);
    expect([box.selectionStart, box.selectionEnd]).toEqual([0, TEXT.length]);
  });

  it('lists what the export simplified under the opening sentence, and says nothing of it when nothing was', () => {
    const { rerender } = render(<MermaidTextDialog text={TEXT} onClose={() => undefined} notes={['a: shape x drawn as a rectangle', 'b: arrowhead y drawn plain']} intro="Saved as a.mmd. Two things were simplified:" />);
    expect(screen.getByRole('dialog').textContent).toContain('Saved as a.mmd. Two things were simplified:');
    expect(screen.getAllByRole('listitem').map((item) => item.textContent)).toEqual(['a: shape x drawn as a rectangle', 'b: arrowhead y drawn plain']);
    rerender(<MermaidTextDialog text={TEXT} onClose={() => undefined} />);
    expect(screen.queryByRole('list')).toBeNull();
    expect(screen.getByRole('dialog').textContent).toContain('The browser would not copy this for you.');
  });

  it('closes with Escape, the Close button and a click on the backdrop, and not on a click inside', () => {
    const onClose = vi.fn();
    render(<MermaidTextDialog text={TEXT} onClose={onClose} />);

    fireEvent.mouseDown(screen.getByRole('dialog'));
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    const backdrop = screen.getByRole('dialog').parentElement;
    if (backdrop === null) {
      throw new Error('the dialog has no backdrop');
    }
    fireEvent.mouseDown(backdrop);

    expect(onClose).toHaveBeenCalledTimes(3);
  });

  it('keeps Tab inside and keeps every key from the canvas behind it', () => {
    render(<MermaidTextDialog text={TEXT} onClose={() => undefined} />);
    const behind = vi.fn();
    globalThis.window.addEventListener('keydown', behind);
    try {
      const close = screen.getByRole('button', { name: 'Close' });
      close.focus();
      const tab = fireEvent.keyDown(close, { key: 'Tab' });
      expect(tab).toBe(false); // prevented: focus wraps to the text box instead of leaving
      expect(globalThis.document.activeElement).toBe(screen.getByRole('textbox', { name: 'Mermaid text' }));
      fireEvent.keyDown(close, { key: 'z' });
      expect(behind).not.toHaveBeenCalled();
    } finally {
      globalThis.window.removeEventListener('keydown', behind);
    }
  });
});
