/**
 * The two notices of save recovery (design/atlas/editor-safety.md, "Autosave"), both on the `viewer-conflict` bar the
 * merge conflicts already use:
 *
 * - `StaleSaveBar`: the server refused the save because the document changed elsewhere. Reload theirs, Keep mine
 *   (asks "Overwrite?" first, since it replaces someone else's change) or Save mine as a new document. Nothing is
 *   decided for the user and their version is already kept aside as a local copy.
 * - `LocalCopyBar`: a version of the user's that the server did not take is kept in this browser (from this visit after
 *   they reloaded theirs, or from an earlier one); Save it as a new document or Discard it.
 */

import { useState } from 'react';
import { formatDateTime } from '../model/dateFormat.ts';
import type { StaleState } from './useDocumentEditor.ts';
import type { LocalCopy } from './localCopy.ts';

export interface StaleSaveBarProps {
  stale: StaleState;
  onReloadTheirs: () => void;
  onKeepMine: () => void;
  onSaveMineAsNew: () => void;
}

export function StaleSaveBar({ stale, onReloadTheirs, onKeepMine, onSaveMineAsNew }: StaleSaveBarProps) {
  const [confirming, setConfirming] = useState(false);
  return (
    <div className="viewer-conflict" role="alert" data-testid="stale-save">
      {confirming ? (
        <>
          <span>Overwrite the newer version with yours? What was saved there since you opened this document will be replaced.</span>
          <button
            type="button"
            disabled={stale.busy}
            onClick={() => {
              setConfirming(false);
              onKeepMine();
            }}
          >
            Overwrite with mine
          </button>
          <button
            type="button"
            disabled={stale.busy}
            onClick={() => {
              setConfirming(false);
            }}
          >
            Cancel
          </button>
        </>
      ) : (
        <>
          <span>
            This document was changed somewhere else since you opened it, so your latest edits are not saved. Your version is kept as a local copy.
            {stale.error !== undefined && ` ${stale.error}`}
          </span>
          <button type="button" disabled={stale.busy} onClick={onReloadTheirs}>
            Reload theirs
          </button>
          <button
            type="button"
            disabled={stale.busy}
            onClick={() => {
              setConfirming(true);
            }}
          >
            Keep mine…
          </button>
          <button type="button" disabled={stale.busy} onClick={onSaveMineAsNew}>
            Save mine as a new document
          </button>
        </>
      )}
    </div>
  );
}

export interface LocalCopyBarProps {
  copy: LocalCopy;
  /** How many other copies are kept for this document, shown so none is forgotten. */
  others: number;
  busy: boolean;
  error: string | undefined;
  onSaveAsNew: () => void;
  onDiscard: () => void;
}

export function LocalCopyBar({ copy, others, busy, error, onSaveAsNew, onDiscard }: LocalCopyBarProps) {
  const when = formatDateTime(copy.keptAt, 'earlier');
  return (
    <div className="viewer-conflict" role="status" data-testid="local-copy">
      <span>
        A version of yours that the server did not take is kept in this browser (from {when}
        {others > 0 ? `; ${String(others)} older ${others === 1 ? 'copy' : 'copies'} too` : ''}).
        {error !== undefined && ` ${error}`}
      </span>
      <button type="button" disabled={busy} onClick={onSaveAsNew}>
        Save it as a new document
      </button>
      <button type="button" disabled={busy} onClick={onDiscard}>
        Discard copy
      </button>
    </div>
  );
}

export interface SavedCopyNoticeProps {
  onOpen: () => void;
  onDismiss: () => void;
}

/** After a copy was stored as a document of its own: where it went. */
export function SavedCopyNotice({ onOpen, onDismiss }: SavedCopyNoticeProps) {
  return (
    <div className="viewer-conflict" role="status" data-testid="saved-copy">
      <span>Your version was saved as a new document.</span>
      <button type="button" onClick={onOpen}>
        Open it
      </button>
      <button type="button" onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  );
}
