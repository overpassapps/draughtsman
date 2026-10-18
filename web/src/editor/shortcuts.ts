/**
 * The editor's keyboard commands, as pure functions of a key event so each binding is testable on its own.
 * History (the M2 editor checklist, item 16): Cmd/Ctrl+Z undoes, Cmd/Ctrl+Shift+Z redoes. Either modifier works on
 * every platform, as the canvas's zoom shortcuts do. History ignores Alt.
 * Nudge (item 20): an arrow key moves the selection 1px, Shift+arrow 10px.
 * Delete (item 21): Delete or Backspace removes the selection.
 * Tools (design/atlas/editor-shell.md, left tool rail): a bare V selects, a bare C picks the connector.
 * Shape panel (design/atlas/shape-connector-palette.md): a bare S or Cmd/Ctrl+Shift+S toggles it.
 * Arrange (item 25): Cmd/Ctrl+] brings the selection forward, Cmd/Ctrl+[ sends it backward, and with Alt held they
 * bring it to the front and send it to the back. Shift is left alone: Cmd+Shift+] and [ switch browser tabs on macOS.
 * Grid (design/atlas/editor-shell.md, zoom cluster): Cmd/Ctrl+' turns the grid, and snapping to it, on and off.
 * Zoom (zoom cluster): Cmd/Ctrl+= in, Cmd/Ctrl+- out, Cmd/Ctrl+0 fit, Cmd/Ctrl+1 100%.
 * Clipboard (items 18, 19 and 21): Cmd/Ctrl+C copies, Cmd/Ctrl+X cuts, Cmd/Ctrl+V pastes, Cmd/Ctrl+D duplicates; the
 * first three are carried out by the browser's own copy, cut and paste events rather than an action.
 * Walk (item 21): Tab selects the next node in document order, Shift+Tab the previous.
 * Label (item 13): Enter edits the one selected node's label.
 * Group (item 22): Cmd/Ctrl+G groups the selection into a new container, Cmd/Ctrl+Shift+G ungroups a selected one.
 * Regions (item 28; editor-shell.md, Keyboard and accessibility): F6 moves focus to the next region of the shell,
 * Shift+F6 to the previous; unlike the rest, they also work from a text field in the chrome.
 * None of them fire while a text field has focus (design/atlas/editor-shell.md, Keyboard and accessibility), except
 * Cmd/Ctrl+K, which opens the command palette (item 23) from anywhere. Each shortcut runs through its action in the
 * registry (actions.ts), which is also what the palette lists.
 */

import type { CanvasTool } from '../canvas/DiagramCanvas.tsx';
import type { ArrangeCommand } from '../model/arrange.ts';
import type { Point } from '../model/types.ts';

export type KeyChord = Pick<KeyboardEvent, 'key' | 'code' | 'metaKey' | 'ctrlKey' | 'shiftKey' | 'altKey'> &
  Partial<Pick<KeyboardEvent, 'getModifierState'>>;

export type HistoryCommand = 'undo' | 'redo';

export type ZoomCommand = 'zoomIn' | 'zoomOut' | 'fit' | 'zoomTo100';

export type ClipboardCommand = 'copy' | 'cut' | 'paste' | 'duplicate';

/** Canvas units an arrow key moves the selection, and with Shift held. */
export const NUDGE_STEP = 1;
export const NUDGE_STEP_LARGE = 10;

export function historyCommand(e: KeyChord): HistoryCommand | undefined {
  if (!(e.metaKey || e.ctrlKey) || e.altKey || e.key.toLowerCase() !== 'z') {
    return undefined;
  }
  return e.shiftKey ? 'redo' : 'undo';
}

const ARROWS: Readonly<Record<string, Point>> = {
  ArrowLeft: { x: -1, y: 0 },
  ArrowRight: { x: 1, y: 0 },
  ArrowUp: { x: 0, y: -1 },
  ArrowDown: { x: 0, y: 1 },
};

/** The world-space delta an arrow key nudges the selection by, or undefined for any other chord. */
export function nudgeDelta(e: KeyChord): Point | undefined {
  const direction = Object.hasOwn(ARROWS, e.key) ? ARROWS[e.key] : undefined;
  if (direction === undefined || e.metaKey || e.ctrlKey || e.altKey) {
    return undefined;
  }
  const step = e.shiftKey ? NUDGE_STEP_LARGE : NUDGE_STEP;
  return { x: direction.x * step, y: direction.y * step };
}

/** Delete or Backspace with no command modifier. */
export function isDeleteCommand(e: KeyChord): boolean {
  return (e.key === 'Delete' || e.key === 'Backspace') && !e.metaKey && !e.ctrlKey && !e.altKey;
}

const CLIPBOARD_KEYS: Readonly<Record<string, ClipboardCommand>> = { c: 'copy', x: 'cut', v: 'paste', d: 'duplicate' };

/**
 * Cmd/Ctrl with C, X, V or D. Shift and Alt variants are left alone: the browser and OS own them (Cmd+Shift+V pastes
 * as plain text in most apps, and Cmd/Ctrl+Shift+D bookmarks every tab).
 */
export function clipboardCommand(e: KeyChord): ClipboardCommand | undefined {
  const key = e.key.toLowerCase();
  if (!(e.metaKey || e.ctrlKey) || e.altKey || e.shiftKey || !Object.hasOwn(CLIPBOARD_KEYS, key)) {
    return undefined;
  }
  return CLIPBOARD_KEYS[key];
}

/** Which way Tab walks the selection through the nodes: 1 for Tab, -1 for Shift+Tab, undefined for any other chord. */
export function selectionStep(e: KeyChord): 1 | -1 | undefined {
  if (e.key !== 'Tab' || e.metaKey || e.ctrlKey || e.altKey) {
    return undefined;
  }
  return e.shiftKey ? -1 : 1;
}

/**
 * The arrange command for Cmd/Ctrl+] or Cmd/Ctrl+[, with Alt for front and back. Without Alt it is read from `key`,
 * not the physical key, so a layout that puts another character on the bracket key (German `+`) still zooms. With
 * Alt, macOS reports a typographic character in `key` (Option+] is a quote mark), so the physical `code` also counts.
 * Windows and Linux report AltGr as Ctrl+Alt, and European layouts type the brackets with it, so a Ctrl chord with
 * AltGraph held is typing, not arranging; Cmd+Option still arranges in a browser that reports Option as AltGraph.
 */
export function arrangeShortcut(e: KeyChord): ArrangeCommand | undefined {
  if (!(e.metaKey || e.ctrlKey) || e.shiftKey || (!e.metaKey && e.getModifierState?.('AltGraph') === true)) {
    return undefined;
  }
  if (e.key === ']' || (e.altKey && e.code === 'BracketRight')) {
    return e.altKey ? 'bringToFront' : 'bringForward';
  }
  if (e.key === '[' || (e.altKey && e.code === 'BracketLeft')) {
    return e.altKey ? 'sendToBack' : 'sendBackward';
  }
  return undefined;
}

export type GroupCommand = 'group' | 'ungroup';

/**
 * Cmd/Ctrl+G groups and Cmd/Ctrl+Shift+G ungroups. Read from `key`, as undo is, so the letter follows the layout; Shift
 * reports an upper-case G. Alt is left alone (Ctrl+Alt is AltGr, which types on Windows and Linux).
 */
export function groupShortcut(e: KeyChord): GroupCommand | undefined {
  if (!(e.metaKey || e.ctrlKey) || e.altKey || e.key.toLowerCase() !== 'g') {
    return undefined;
  }
  return e.shiftKey ? 'ungroup' : 'group';
}

/**
 * A focused element that takes typing, where an editor shortcut must leave the key alone. Contenteditable is also
 * read from the attribute on the element or an ancestor, because jsdom does not implement `isContentEditable`.
 */
export function isTextField(target: EventTarget | null): boolean {
  if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) {
    return true;
  }
  return target instanceof HTMLElement && (target.isContentEditable || target.closest('[contenteditable]:not([contenteditable="false"])') !== null);
}

/** The rail tool a key picks: a single letter with no modifier, so Cmd/Ctrl+C (copy) is never taken for a tool. */
export function toolShortcut(e: KeyChord): CanvasTool | undefined {
  if (e.metaKey || e.ctrlKey || e.altKey || e.shiftKey) {
    return undefined;
  }
  switch (e.key.toLowerCase()) {
    case 'v':
      return 'select';
    case 'c':
      return 'connector';
    default:
      return undefined;
  }
}

/** A bare L connects (links) the selected shapes in the order they were selected (editor/connectSelection.ts). */
export function isConnectSelected(e: KeyChord): boolean {
  return e.key.toLowerCase() === 'l' && !e.metaKey && !e.ctrlKey && !e.altKey && !e.shiftKey;
}

/** Cmd/Ctrl+Enter plays the flythrough (Present), as Google Slides' present-from-start does. */
export function isPresent(e: KeyChord): boolean {
  return (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key === 'Enter';
}

/** Cmd/Ctrl+' toggles the grid. */
export function isGridToggle(e: KeyChord): boolean {
  return (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key === "'";
}

/** Cmd/Ctrl with =, -, 0 or 1 (+ and _ too, the same keys with Shift's character): the zoom cluster's keys. */
export function zoomShortcut(e: KeyChord): ZoomCommand | undefined {
  if (!(e.metaKey || e.ctrlKey) || e.altKey || e.shiftKey) {
    return undefined;
  }
  switch (e.key) {
    case '+':
    case '=':
      return 'zoomIn';
    case '-':
    case '_':
      return 'zoomOut';
    case '0':
      return 'fit';
    case '1':
      return 'zoomTo100';
    default:
      return undefined;
  }
}

/** A bare Enter, which edits the selected node's label. */
export function isEditLabelKey(e: KeyChord): boolean {
  return e.key === 'Enter' && !e.metaKey && !e.ctrlKey && !e.altKey && !e.shiftKey;
}

/** Cmd/Ctrl+K opens the command palette. */
/** A bare `?` (Shift+/ on most layouts) opens the keyboard-shortcut sheet; with Cmd, Ctrl or Alt held it is someone else's key. */
export function isShortcutSheet(e: KeyChord): boolean {
  return e.key === '?' && !e.metaKey && !e.ctrlKey && !e.altKey;
}

export function isCommandPalette(e: KeyChord): boolean {
  return (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k';
}

/** A bare S, or Cmd/Ctrl+Shift+S, toggles the shape panel. */
export function isShapePanelToggle(e: KeyChord): boolean {
  if (e.key.toLowerCase() !== 's') {
    return false;
  }
  if (!e.metaKey && !e.ctrlKey && !e.altKey && !e.shiftKey) {
    return true;
  }
  return (e.metaKey || e.ctrlKey) && e.shiftKey && !e.altKey;
}

/** F6 moves focus to the next region of the shell (1), Shift+F6 to the previous (-1). */
export function regionShortcut(e: KeyChord): 1 | -1 | undefined {
  if (e.key !== 'F6' || e.metaKey || e.ctrlKey || e.altKey) {
    return undefined;
  }
  return e.shiftKey ? -1 : 1;
}
