/**
 * The command palette (checklist item 23; design/atlas/editor-shell.md, Command palette): a search field over every
 * registered editor action (actions.ts), each row showing its shortcut. An action that cannot run now is listed and
 * marked disabled rather than hidden, so the list keeps its shape and a screen reader hears why nothing happened.
 *
 * Keyboard: typing filters, Up and Down move the highlight (wrapping), Enter runs the highlighted action if it can
 * run, Escape closes. Focus stays in the search field throughout (a combobox whose highlighted row is its
 * aria-activedescendant), and Tab is held so focus cannot fall out of the dialog behind it. The caller closes the
 * palette and returns focus to the canvas before running the chosen action, so a key action and its palette row
 * start from the same place. "Go to node" and recent documents, which the atlas also lists here, are not built yet.
 */

import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { filterActions, type EditorAction } from './actions.ts';
import { platformShortcut } from './platform.ts';

export interface CommandPaletteProps {
  /** Every registered action, in palette order (orderActions). */
  actions: readonly EditorAction[];
  onRun: (action: EditorAction) => void;
  onClose: () => void;
}

export function CommandPalette({ actions, onRun, onClose }: CommandPaletteProps) {
  const [query, setQuery] = useState('');
  const rows = useMemo(() => filterActions(actions, query).map((action) => ({ action, enabled: action.enabled() })), [actions, query]);
  // Starts on the first row that can run, so Enter straight after typing does something whenever anything can.
  const [active, setActive] = useState(() => Math.max(0, rows.findIndex((row) => row.enabled)));
  const input = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLUListElement>(null);
  const listId = useId();
  const optionId = (index: number): string => `${listId}-${String(index)}`;
  const current = rows[active];

  useEffect(() => {
    input.current?.focus();
  }, []);
  useEffect(() => {
    // Typed as optional because jsdom has no scrollIntoView.
    const row: Partial<Pick<Element, 'scrollIntoView'>> | null | undefined = list.current?.querySelector(`[data-index="${String(active)}"]`);
    row?.scrollIntoView?.({ block: 'nearest' });
  }, [active]);

  const run = (index: number): void => {
    const row = rows[index];
    if (row?.enabled === true) {
      onRun(row.action);
    }
  };
  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
    switch (e.key) {
      case 'ArrowDown':
      case 'ArrowUp':
        e.preventDefault();
        if (rows.length > 0) {
          setActive((index) => (index + (e.key === 'ArrowDown' ? 1 : rows.length - 1)) % rows.length);
        }
        break;
      case 'Enter':
        e.preventDefault();
        run(active);
        break;
      case 'Escape':
        e.preventDefault();
        onClose();
        break;
      case 'Tab':
        e.preventDefault();
        break;
      default:
        break;
    }
  };

  return (
    <div
      className="palette-backdrop"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) {
          e.preventDefault();
          onClose();
        }
      }}
    >
      <div
        className="palette"
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        onMouseDown={(e) => {
          // Keeps focus, and so the keyboard, in the search field; a press in the field itself still places the caret.
          if (e.target !== input.current) {
            e.preventDefault();
          }
        }}
      >
        <input
          ref={input}
          className="palette-search"
          type="text"
          role="combobox"
          aria-label="Search commands"
          aria-expanded="true"
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={current === undefined ? undefined : optionId(active)}
          placeholder="Type a command"
          autoComplete="off"
          spellCheck={false}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            const next = filterActions(actions, e.target.value).findIndex((action) => action.enabled());
            setActive(Math.max(0, next));
          }}
          onKeyDown={onKeyDown}
        />
        <ul ref={list} id={listId} className="palette-list" role="listbox" aria-label="Commands">
          {rows.map(({ action, enabled }, index) => (
            <li
              key={action.id}
              id={optionId(index)}
              data-index={index}
              className="palette-option"
              role="option"
              aria-selected={index === active}
              aria-disabled={!enabled}
              aria-label={action.shortcut === undefined ? action.label : `${action.label}, ${platformShortcut(action.shortcut.label)}`}
              onMouseMove={() => {
                if (index !== active) {
                  setActive(index);
                }
              }}
              onClick={() => {
                run(index);
              }}
            >
              <span className="palette-group">{action.group}</span>
              <span className="palette-label">{action.label}</span>
              {action.shortcut !== undefined && <kbd className="palette-shortcut">{platformShortcut(action.shortcut.label)}</kbd>}
            </li>
          ))}
        </ul>
        {rows.length === 0 && <p className="palette-empty">No matching commands</p>}
      </div>
    </div>
  );
}
