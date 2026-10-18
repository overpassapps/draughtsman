/**
 * The top bar's menu button (design/atlas/editor-shell.md, "menu button (Lucide `menu`) opening File, Edit, View,
 * Arrange and Help groups"): how every canvas action is reachable from a visible menu as well as by gesture or
 * shortcut (checklist item 28). Its items are the action registry's records (actions.ts, sorted into menus by
 * menus.ts), each showing its shortcut, so a menu item, a palette row and a key all run the same `run`. An action that
 * cannot run now is listed and marked disabled rather than hidden, as in the palette.
 *
 * Keyboard, as the WAI-ARIA menu button pattern has it: Enter, Space or Down on the button opens the menu on its first
 * item, Up on its last; Up and Down move (wrapping), Home and End jump, a letter moves to the next item starting with
 * it; Enter or Space runs the focused item if it can run; Escape, or Tab, closes the menu with focus back on the
 * button. Every key pressed inside the menu stops there, so a letter typed to find an item never picks a rail tool.
 * The caller hands focus back to the canvas before running the chosen action (as the palette does), so an item and its
 * shortcut start from the same place. The enabled states are read once, when the menu opens.
 */

import { useCallback, useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import type { EditorAction } from './actions.ts';
import type { MenuSection } from './menus.ts';
import { platformShortcut } from './platform.ts';

export interface EditorMenuProps {
  /** Read when the menu opens, so it lists the registry as it is then. */
  sections: () => readonly MenuSection[];
  /** Runs a chosen item that can run; `keyboard` says whether a key rather than a click chose it. */
  onRun: (action: EditorAction, keyboard: boolean) => void;
}

interface Item {
  action: EditorAction;
  enabled: boolean;
}

interface OpenMenu {
  sections: readonly { menu: string; items: readonly Item[] }[];
  /** The flat index to focus once the items are in the DOM. */
  focus: number;
}

export function EditorMenu({ sections, onRun }: EditorMenuProps) {
  const [open, setOpen] = useState<OpenMenu | undefined>(undefined);
  const id = useId();
  const button = useRef<HTMLButtonElement>(null);
  const wrapper = useRef<HTMLDivElement>(null);
  const items = useRef<(HTMLButtonElement | null)[]>([]);
  const flat = open?.sections.flatMap((section) => section.items) ?? [];

  const show = useCallback(
    (at: 'first' | 'last') => {
      const listed = sections().map((section) => ({ menu: section.menu, items: section.actions.map((action) => ({ action, enabled: action.enabled() })) }));
      const count = listed.reduce((total, section) => total + section.items.length, 0);
      items.current = [];
      setOpen({ sections: listed, focus: at === 'first' ? 0 : count - 1 });
    },
    [sections],
  );
  const close = useCallback((refocus: boolean) => {
    setOpen(undefined);
    if (refocus) {
      button.current?.focus();
    }
  }, []);

  useEffect(() => {
    if (open !== undefined) {
      items.current[open.focus]?.focus();
    }
  }, [open]);
  // A press anywhere outside closes the menu and leaves focus to wherever the press puts it.
  useEffect(() => {
    if (open === undefined) {
      return;
    }
    const onPress = (e: PointerEvent | MouseEvent): void => {
      if (e.target instanceof Node && wrapper.current?.contains(e.target) !== true) {
        setOpen(undefined);
      }
    };
    window.addEventListener('mousedown', onPress, true);
    window.addEventListener('pointerdown', onPress, true);
    return () => {
      window.removeEventListener('mousedown', onPress, true);
      window.removeEventListener('pointerdown', onPress, true);
    };
  }, [open]);

  const run = (index: number, keyboard: boolean): void => {
    const item = flat[index];
    if (item?.enabled === true) {
      setOpen(undefined);
      onRun(item.action, keyboard);
    }
  };
  const focusItem = (index: number): void => {
    items.current[index]?.focus();
  };
  const current = (): number => items.current.findIndex((item) => item !== null && item === globalThis.document.activeElement);

  const onButtonKeyDown = (e: KeyboardEvent<HTMLButtonElement>): void => {
    if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      e.stopPropagation();
      show(e.key === 'ArrowUp' ? 'last' : 'first');
    }
  };
  const onMenuKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
    // Nothing typed in the menu reaches the editor's shortcuts behind it.
    e.stopPropagation();
    const count = flat.length;
    const at = current();
    switch (e.key) {
      case 'ArrowDown':
      case 'ArrowUp':
        e.preventDefault();
        if (count > 0) {
          focusItem(at < 0 ? 0 : (at + (e.key === 'ArrowDown' ? 1 : count - 1)) % count);
        }
        return;
      case 'Home':
      case 'End':
        e.preventDefault();
        focusItem(e.key === 'Home' ? 0 : count - 1);
        return;
      case 'Enter':
      case ' ':
        e.preventDefault();
        run(at, true);
        return;
      case 'Escape':
      case 'Tab':
        e.preventDefault();
        close(true);
        return;
      default:
        break;
    }
    // Type-ahead: the next item after the focused one whose label starts with the letter typed.
    if (e.key.length === 1 && !e.metaKey && !e.ctrlKey && !e.altKey) {
      const letter = e.key.toLowerCase();
      for (let step = 1; step <= count; step++) {
        const index = (at + step + count) % count;
        if (flat[index]?.action.label.toLowerCase().startsWith(letter) === true) {
          focusItem(index);
          break;
        }
      }
    }
  };

  // Each section's first flat index, so an item knows its place in the Up and Down order.
  const starts = open?.sections.map((_, at) => open.sections.slice(0, at).reduce((total, section) => total + section.items.length, 0)) ?? [];
  return (
    <div ref={wrapper} className="editor-menu">
      <button
        ref={button}
        type="button"
        className="editor-menu-button"
        aria-label="Menu"
        title="Menu"
        aria-haspopup="menu"
        aria-expanded={open !== undefined}
        aria-controls={open === undefined ? undefined : `${id}-menu`}
        onKeyDown={onButtonKeyDown}
        onClick={() => {
          if (open === undefined) {
            show('first');
          } else {
            close(false);
          }
        }}
      >
        {/* Lucide menu (ISC licence) */}
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M4 12h16" />
          <path d="M4 6h16" />
          <path d="M4 18h16" />
        </svg>
      </button>
      {open !== undefined && (
        <div id={`${id}-menu`} className="editor-menu-popup" role="menu" aria-label="Menu" onKeyDown={onMenuKeyDown}>
          {open.sections.map((section, sectionAt) => (
            <div key={section.menu} className="editor-menu-section" role="group" aria-label={section.menu}>
              <div className="editor-menu-heading" aria-hidden="true">
                {section.menu}
              </div>
              {section.items.map(({ action, enabled }, itemAt) => {
                const index = (starts[sectionAt] ?? 0) + itemAt;
                return (
                  <button
                    key={action.id}
                    ref={(element) => {
                      items.current[index] = element;
                    }}
                    type="button"
                    role="menuitem"
                    className="editor-menu-item"
                    tabIndex={-1}
                    aria-disabled={!enabled}
                    aria-label={action.shortcut === undefined ? action.label : `${action.label}, ${platformShortcut(action.shortcut.label)}`}
                    onClick={() => {
                      run(index, false);
                    }}
                  >
                    <span className="editor-menu-label">{action.label}</span>
                    {action.shortcut !== undefined && <kbd className="editor-menu-shortcut">{platformShortcut(action.shortcut.label)}</kbd>}
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
