/**
 * What autosave does about a save that did not go through (design/atlas/editor-safety.md, "Autosave"): whether trying
 * again can help, what to tell the user, and how long to wait. Pure, so the rules are pinned without a timer.
 *
 * A refusal as stale (412/428, `SaveConflict`) is not here: retrying it cannot succeed, and it has its own recovery.
 */

import { SaveFailed } from '../model/documentClient.ts';

export interface SaveFailure {
  /** True when the same save may succeed later (the server is down or busy, the network is out, the session can be renewed). */
  retry: boolean;
  message: string;
}

/** The longest wait between automatic retries. */
export const MAX_RETRY_DELAY_MS = 30_000;

/** How a rejected save reads to the user, and whether autosave should keep trying. */
export function classifySaveFailure(error: unknown): SaveFailure {
  if (error instanceof SaveFailed) {
    const { status } = error;
    if (status === 401) {
      return { retry: true, message: 'You have been signed out, so nothing was saved. Sign in again (your edits are still here) and it will save.' };
    }
    if (status === 403) {
      return { retry: false, message: 'The server refused this save: you may not have permission to change this document. Your edits are still here.' };
    }
    if (status === 404) {
      return { retry: false, message: 'This document no longer exists on the server, so it cannot be saved. Your edits are still here.' };
    }
    if (status === 408 || status === 429 || status >= 500) {
      return { retry: true, message: `The server could not save just now (${String(status)}). Trying again.` };
    }
    return { retry: false, message: `The server refused this save (${String(status)}). Your edits are still here.` };
  }
  // fetch rejects with a TypeError when the request never reached a server: offline, DNS, refused connection.
  if (error instanceof TypeError) {
    return { retry: true, message: 'Cannot reach the server, so nothing was saved. Your edits are kept here and it will keep trying.' };
  }
  return { retry: false, message: error instanceof Error ? error.message : 'Saving failed.' };
}

/**
 * The wait before retry number `failures` (1 for the first): the autosave delay doubling each time up to
 * MAX_RETRY_DELAY_MS, so a server that is down is asked rarely and one that is back is noticed within half a minute.
 */
export function retryDelay(failures: number, baseMs: number): number {
  return Math.min(MAX_RETRY_DELAY_MS, baseMs * 2 ** Math.max(0, failures - 1));
}
