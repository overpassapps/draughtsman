/**
 * The keyboard-shortcut sheet (design/atlas/help-about-status.md; Help menu, `?`): every registered editor action that
 * has a shortcut, grouped under the same menus the top bar lists them in (menus.ts), so it is built from the registry
 * and cannot drift from what the keys do. A modal dialog on the Import SQL dialog's styles (`sql-import-*`) and rules:
 * Tab kept inside, every key kept from the canvas behind it, Escape or Close dismisses it.
 */
import { useEffect, useId, useRef } from 'react';
import type { EditorAction } from './actions.ts';
import { keyCaps, shortcutRows } from './shortcutRows.ts';
import { platformShortcut } from './platform.ts';

export interface ShortcutSheetProps {
  actions: readonly EditorAction[];
  onClose: () => void;
}

export function ShortcutSheet({ actions, onClose }: ShortcutSheetProps) {
  const headingId = useId();
  const close = useRef<HTMLButtonElement>(null);
  const sections = shortcutRows(actions);

  useEffect(() => {
    close.current?.focus();
  }, []);

  return (
    <div
      className="sql-import-backdrop"
      role="presentation"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) {
          onClose();
        }
      }}
    >
      <div
        className="sql-import shortcut-sheet"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        onKeyDown={(e) => {
          e.stopPropagation();
          if (e.key === 'Escape') {
            onClose();
          }
          if (e.key === 'Tab') {
            // One control to land on, so Tab keeps it there rather than leaving the dialog.
            e.preventDefault();
            close.current?.focus();
          }
        }}
      >
        <h2 id={headingId} className="sql-import-title">Keyboard shortcuts</h2>
        <p className="sql-import-help">Cmd on a Mac is Ctrl elsewhere. Press ? to open this again.</p>
        <div className="shortcut-sheet-scroll">
          {sections.map((section) => (
            <section key={section.menu} aria-label={section.menu}>
              <h3 className="shortcut-sheet-section">{section.menu}</h3>
              <ul className="shortcut-sheet-list">
                {section.actions.map((action) => (
                  <li key={action.id}>
                    <span>{action.label}</span>
                    <span className="shortcut-sheet-keys">
                      {keyCaps(platformShortcut(action.shortcut?.label ?? '')).map((cap, index) => (
                        <kbd key={`${cap}${String(index)}`}>{cap}</kbd>
                      ))}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
        <div className="sql-import-actions">
          <button ref={close} type="button" className="sql-import-create" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}
