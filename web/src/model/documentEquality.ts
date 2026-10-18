/**
 * Structural equality of two documents, for the editor's "has anything changed since the last save" question: JSON
 * values compared by content, an object key whose value is `undefined` counting as absent (a Yjs round trip can leave
 * one where the stored JSON has none) and key order ignored. It exits at the first difference, so the per-frame
 * check while a drag is unsaved stays cheap on the diagrams the editor handles.
 */

import type { Document } from './types.ts';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function definedKeys(value: Record<string, unknown>): string[] {
  return Object.keys(value).filter((key) => value[key] !== undefined);
}

function valuesEqual(a: unknown, b: unknown): boolean {
  if (a === b) {
    return true;
  }
  if (Array.isArray(a)) {
    return Array.isArray(b) && a.length === b.length && a.every((item, index) => valuesEqual(item, b[index]));
  }
  if (isRecord(a) && isRecord(b)) {
    const keys = definedKeys(a);
    return keys.length === definedKeys(b).length && keys.every((key) => valuesEqual(a[key], b[key]));
  }
  return false;
}

/** True when `a` and `b` are the same document by content. */
export function documentsEqual(a: Document, b: Document): boolean {
  return valuesEqual(a, b);
}
