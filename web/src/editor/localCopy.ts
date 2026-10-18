/**
 * The local copies of a document the server did not take (design/atlas/editor-safety.md, "Autosave"): when a save is
 * refused as stale, or keeps failing, the user's version is written here so that closing the tab, reloading theirs or a
 * crash cannot lose it. They are per document id and live only in this browser (`localStorage`); none is sent anywhere
 * until the user chooses to save it as a new document.
 *
 * A document can hold several (an earlier one the user has not dealt with yet must survive a later refusal), newest
 * last, at most MAX_COPIES; past that the oldest goes. `localStorage` can be missing, full or blocked (private windows,
 * site data cleared), so every call is wrapped and a failure reports false rather than throwing: the copy is a safety
 * net, and the editor's own state is still the truth.
 */

import type { Document } from '../model/types.ts';

export interface LocalCopy {
  /** Names this copy among the document's, so a later write to the same copy replaces it instead of adding another. */
  copyId: string;
  documentId: string;
  /** The user's whole document at the moment it was kept. */
  document: Document;
  /** When it was kept, ISO 8601. */
  keptAt: string;
  /**
   * Set when the person answered the stale-save question with "Reload theirs": they chose the server's version, and this
   * copy is only the safety net under that choice. It is offered for the rest of that visit (strip with Save as new /
   * Discard) but not on every later open (QA S10: the strip stuck until Discard copy was pressed), and is dropped for
   * good ANSWERED_COPY_KEEP_DAYS after it was kept.
   */
  answered?: boolean;
}

export const ANSWERED_COPY_KEEP_DAYS = 7;

export const MAX_COPIES = 5;

const PREFIX = 'draughtsman.localCopy.';

const keyOf = (documentId: string): string => `${PREFIX}${documentId}`;

/** A fresh copy id; not `crypto.randomUUID`, which a page served over plain http on a LAN address does not have. */
export function newCopyId(): string {
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
}

function isLocalCopy(value: unknown, documentId: string): value is LocalCopy {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const copy = value as Partial<LocalCopy>;
  return copy.documentId === documentId && typeof copy.copyId === 'string' && typeof copy.keptAt === 'string' && typeof copy.document === 'object';
}

/** Every copy kept for a document, oldest first; empty when there are none or they cannot be read back. */
export function readLocalCopies(documentId: string): LocalCopy[] {
  try {
    const raw = localStorage.getItem(keyOf(documentId));
    if (raw === null) {
      return [];
    }
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((entry): entry is LocalCopy => isLocalCopy(entry, documentId)) : [];
  } catch {
    return [];
  }
}

function write(documentId: string, copies: readonly LocalCopy[]): boolean {
  try {
    if (copies.length === 0) {
      localStorage.removeItem(keyOf(documentId));
    } else {
      localStorage.setItem(keyOf(documentId), JSON.stringify(copies));
    }
    return true;
  } catch {
    return false;
  }
}

/** Keeps the copy, replacing the one with the same `copyId` or else adding it; false when the browser would not store it. */
export function storeLocalCopy(copy: LocalCopy): boolean {
  const others = readLocalCopies(copy.documentId).filter((existing) => existing.copyId !== copy.copyId);
  return write(copy.documentId, [...others, copy].slice(-MAX_COPIES));
}

/** Marks a copy as answered (see `LocalCopy.answered`); a copy that is not there is left alone. */
export function markLocalCopyAnswered(documentId: string, copyId: string): void {
  write(
    documentId,
    readLocalCopies(documentId).map((copy) => (copy.copyId === copyId ? { ...copy, answered: true } : copy)),
  );
}

/** True for an answered copy that has been kept long enough to go. */
export function answeredCopyExpired(copy: LocalCopy, now: number = Date.now()): boolean {
  return copy.answered === true && now - Date.parse(copy.keptAt) > ANSWERED_COPY_KEEP_DAYS * 24 * 60 * 60 * 1000;
}

/** Drops one copy, leaving the document's others. */
export function clearLocalCopy(documentId: string, copyId: string): void {
  write(
    documentId,
    readLocalCopies(documentId).filter((copy) => copy.copyId !== copyId),
  );
}
