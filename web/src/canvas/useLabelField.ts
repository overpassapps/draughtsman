/**
 * The behaviour every in-place label editor shares (the M2 editor checklist, items 11, 13 and 14): a plain-text
 * contenteditable that opens focused with its text selected, and owns every key and press while it is open. Its
 * native listeners stop propagation, so the canvas's own shortcuts (zoom, space-to-pan, Delete, arrows) and the
 * window-level undo never see them, and Cmd/Ctrl+Z inside the field is the browser's text undo. Cmd/Ctrl+Enter or
 * focus moving elsewhere in the page commits; focus leaving the window (Cmd+Tab, the address bar, devtools) does not,
 * and the browser refocuses the field on return. Escape discards.
 *
 * A multi-line field (a node label, which NodeView wraps and breaks) keeps Enter as a line break. A single-line field
 * (an edge label, which EdgeView draws as one line of text) commits on Enter as well, and reads any line break a paste
 * or the browser put in as a space, so what is committed is what the renderer can draw.
 *
 * Nothing here writes the model: the caller receives the committed text and decides whether it changed.
 */

import { useLayoutEffect, useRef, type RefObject } from 'react';
import { plainTextOf, trimLabelBreaks, type LabelEditEnd } from './labelEditing.ts';

export interface LabelFieldOptions {
  /** Enter commits and line breaks become spaces; for labels the renderer draws on one line. */
  singleLine?: boolean;
}

/** Replaces the field's selection with plain text, keeping the browser's own undo where it can. */
function insertPlainText(field: HTMLElement, text: string): void {
  // execCommand is deprecated but is the only way to insert that lands on the field's native undo stack.
  // eslint-disable-next-line @typescript-eslint/no-deprecated
  if (typeof document.execCommand === 'function' && document.execCommand('insertText', false, text)) {
    return;
  }
  const selection = window.getSelection();
  const range = selection !== null && selection.rangeCount > 0 ? selection.getRangeAt(0) : undefined;
  if (range === undefined || !field.contains(range.commonAncestorContainer)) {
    field.append(text);
    return;
  }
  range.deleteContents();
  const node = document.createTextNode(text);
  range.insertNode(node);
  range.setStartAfter(node);
  range.collapse(true);
  selection?.removeAllRanges();
  selection?.addRange(range);
}

function selectAll(field: HTMLElement): void {
  const selection = window.getSelection();
  if (selection === null) {
    return;
  }
  const range = document.createRange();
  range.selectNodeContents(field);
  selection.removeAllRanges();
  selection.addRange(range);
}

/** One line: every run of line breaks the text holds becomes a single space. */
export function toSingleLine(text: string): string {
  return text.replace(/[\r\n]+/g, ' ');
}

/**
 * Binds the shared behaviour to the returned ref's element once, on open. `initialText` is read then and never again,
 * so later changes to the element being edited do not reset what is being typed.
 */
export function useLabelField(
  initialText: string,
  onCommit: (text: string, end: LabelEditEnd) => void,
  onCancel: (end: LabelEditEnd) => void,
  { singleLine = false }: LabelFieldOptions = {},
): RefObject<HTMLDivElement> {
  const field = useRef<HTMLDivElement>(null);
  // The callbacks live in refs so the listeners bound once on open always reach the current ones.
  const commit = useRef(onCommit);
  const cancel = useRef(onCancel);
  useLayoutEffect(() => {
    commit.current = onCommit;
    cancel.current = onCancel;
  });
  const initial = useRef(initialText);
  const oneLine = useRef(singleLine);

  useLayoutEffect(() => {
    const el = field.current;
    if (!el) {
      return;
    }
    // Browsers without plaintext-only leave the field read-only; fall back to plain contenteditable, which the paste
    // handler and plainTextOf keep to plain text all the same.
    if (!el.isContentEditable) {
      el.contentEditable = 'true';
    }
    el.textContent = initial.current;
    el.focus({ preventScroll: true });
    selectAll(el);

    let finished = false;
    const finish = (keep: boolean, end: LabelEditEnd): void => {
      if (finished) {
        return;
      }
      finished = true;
      if (keep) {
        // A label neither starts nor ends in a line break: a leading or trailing Enter is trimmed, so the box and its exports stay as tall as the words.
        const text = trimLabelBreaks(plainTextOf(el));
        commit.current(oneLine.current ? toSingleLine(text) : text, end);
      } else {
        cancel.current(end);
      }
    };
    const onKeyDown = (e: KeyboardEvent): void => {
      e.stopPropagation();
      if (e.isComposing) {
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        finish(false, 'keyboard');
      } else if (e.key === 'Enter' && (oneLine.current || e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        finish(true, 'keyboard');
      }
    };
    const stop = (e: Event): void => {
      e.stopPropagation();
    };
    const onBlur = (): void => {
      if (!el.ownerDocument.hasFocus()) {
        return;
      }
      finish(true, 'blur');
    };
    const onPaste = (e: ClipboardEvent): void => {
      e.preventDefault();
      const text = e.clipboardData?.getData('text/plain') ?? '';
      insertPlainText(el, oneLine.current ? toSingleLine(text) : text);
    };
    el.addEventListener('keydown', onKeyDown);
    el.addEventListener('keyup', stop);
    el.addEventListener('pointerdown', stop);
    el.addEventListener('dblclick', stop);
    el.addEventListener('blur', onBlur);
    el.addEventListener('paste', onPaste);
    return () => {
      // Unmounting without an ending (the element was removed underneath the edit) writes nothing.
      finished = true;
      el.removeEventListener('keydown', onKeyDown);
      el.removeEventListener('keyup', stop);
      el.removeEventListener('pointerdown', stop);
      el.removeEventListener('dblclick', stop);
      el.removeEventListener('blur', onBlur);
      el.removeEventListener('paste', onPaste);
    };
  }, []);

  return field;
}
