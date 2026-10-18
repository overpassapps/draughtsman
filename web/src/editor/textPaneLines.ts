/**
 * Where the elements are in the text the Text pane shows. The DSL is one statement per line (docs/spec/dsl.md), and a
 * node, container or edge statement starts `node|container|edge <id>`, so the line of an element is a read of the text
 * itself: it keeps working on text the person has typed and not applied, and it needs no server. Lines are 1-based, the
 * way the server's parse errors and warnings number them.
 */

import type { Id } from '../model/types.ts';

const STATEMENT = /^\s*(?:node|container|edge)\s+([^\s"]+)/;

function linesOf(text: string): string[] {
  return text.split(/\r?\n/);
}

/** The 1-based line count of `text` (an empty text is one empty line, as a textarea shows it). */
export function lineCount(text: string): number {
  return linesOf(text).length;
}

/** Each element id the text declares, with the line of its first statement. */
export function elementLines(text: string): Map<Id, number> {
  const found = new Map<Id, number>();
  linesOf(text).forEach((line, index) => {
    const id = STATEMENT.exec(line)?.[1];
    if (id !== undefined && !found.has(id)) {
      found.set(id, index + 1);
    }
  });
  return found;
}

/** The lines of the given elements, ascending, for the ones the text declares. */
export function selectedLines(text: string, ids: ReadonlySet<Id>): number[] {
  if (ids.size === 0) {
    return [];
  }
  const lines = elementLines(text);
  return [...ids].flatMap((id) => lines.get(id) ?? []).sort((a, b) => a - b);
}

/** The element a line declares, when it is a node, container or edge statement. */
export function elementAtLine(text: string, line: number): Id | undefined {
  const source = linesOf(text)[line - 1];
  return source === undefined ? undefined : STATEMENT.exec(source)?.[1];
}

/** The 1-based line holding the character at `offset`. */
export function lineAtOffset(text: string, offset: number): number {
  let line = 1;
  const end = Math.min(Math.max(offset, 0), text.length);
  for (let index = 0; index < end; index += 1) {
    if (text.charCodeAt(index) === 10) {
      line += 1;
    }
  }
  return line;
}

/** The character range of a 1-based line (without its line break), for putting the caret on it. */
export function lineRange(text: string, line: number): { start: number; end: number } {
  let start = 0;
  for (let current = 1; current < line; current += 1) {
    const next = text.indexOf('\n', start);
    if (next === -1) {
      return { start: text.length, end: text.length };
    }
    start = next + 1;
  }
  const next = text.indexOf('\n', start);
  const end = next === -1 ? text.length : next;
  return { start, end: end > start && text.charCodeAt(end - 1) === 13 ? end - 1 : end };
}
