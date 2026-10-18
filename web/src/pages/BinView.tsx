/**
 * The bin view of the home screen (design/atlas/document-list.md, OP-02): the documents Delete moved aside, with who
 * deleted each and how long it has left, Restore on every row and - only where the server says the caller may - Delete
 * forever, which is a separate confirmation of its own and never the first step. The server decides who sees which entry
 * (an Editor their own, an administrator all) and whether a row can be removed for good (`canPurge`), so this shows what
 * it is given and hides nothing the server would allow.
 */

import { useCallback, useEffect, useState, type ReactElement } from 'react';
import { daysLeft, listBin, retentionLine, purgeDocument, restoreDocument, type BinEntry, type BinListing } from '../model/binClient.ts';
import type { DocumentType } from '../model/types.ts';
import { dateTimeTitle } from '../model/dateFormat.ts';

export interface BinViewProps {
  /** Called after a document is restored, so the documents list can be read again. */
  onRestored: () => void;
  /** Where the outcome of a restore or a permanent delete is said: the page's one status line, not a second live region here. */
  onStatus: (message: string) => void;
  /** Icon and accessible name of a document type, shared with the documents list. */
  typeIcon: (type: DocumentType) => ReactElement;
}

type State = { kind: 'loading' } | { kind: 'ok'; listing: BinListing } | { kind: 'error'; message: string };

function when(iso: string): { relative: string; absolute: string } {
  const then = Date.parse(iso);
  const absolute = dateTimeTitle(then);
  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (seconds < 60) {
    return { relative: 'just now', absolute };
  }
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return { relative: `${String(minutes)}m ago`, absolute };
  }
  const hours = Math.round(minutes / 60);
  if (hours < 24) {
    return { relative: `${String(hours)}h ago`, absolute };
  }
  return { relative: `${String(Math.round(hours / 24))}d ago`, absolute };
}

export function BinView({ onRestored, onStatus, typeIcon }: BinViewProps) {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const [confirmPurge, setConfirmPurge] = useState<BinEntry | undefined>(undefined);

  const reload = useCallback((): Promise<void> => {
    return listBin()
      .then((listing) => {
        setState({ kind: 'ok', listing });
      })
      .catch((cause: unknown) => {
        setState({ kind: 'error', message: cause instanceof Error ? cause.message : 'Unknown error' });
      });
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const restore = (entry: BinEntry): void => {
    setBusy(true);
    setError(undefined);
    void restoreDocument(entry.id)
      .then(async (restored) => {
        onStatus(restored ? `Restored “${entry.title}”.` : `“${entry.title}” is no longer in the bin.`);
        if (restored) {
          onRestored();
        }
        await reload();
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Unknown error');
      })
      .finally(() => {
        setBusy(false);
      });
  };

  const purge = (): void => {
    if (confirmPurge === undefined) {
      return;
    }
    const entry = confirmPurge;
    setConfirmPurge(undefined);
    setBusy(true);
    setError(undefined);
    void purgeDocument(entry.id)
      .then(async (removed) => {
        onStatus(removed ? `Deleted “${entry.title}” permanently.` : `“${entry.title}” is no longer in the bin.`);
        await reload();
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Unknown error');
      })
      .finally(() => {
        setBusy(false);
      });
  };

  return (
    <section className="bin-view" aria-label="Bin">
      {state.kind === 'loading' && <p className="page-message">Loading the bin…</p>}
      {state.kind === 'error' && <p className="page-message" role="alert">Cannot read the bin: {state.message}</p>}
      {error !== undefined && <p className="home-banner" role="alert">{error}</p>}
      {state.kind === 'ok' && (
        <>
          <p className="bin-retention">{retentionLine(state.listing.retentionDays)}</p>
          {state.listing.items.length === 0 ? (
            <p className="bin-empty">
              The bin is empty.
            </p>
          ) : (
            <table className="home-list bin-list">
              <thead>
                <tr>
                  <th scope="col">Type</th>
                  <th scope="col">Title</th>
                  <th scope="col">Deleted</th>
                  <th scope="col">Days left</th>
                  <th scope="col"><span className="visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                {state.listing.items.map((entry) => {
                  const deleted = when(entry.deletedAt);
                  const left = daysLeft(entry);
                  return (
                    <tr key={entry.id}>
                      <td className="home-type">{typeIcon(entry.type)}</td>
                      <td className="home-title-cell">{entry.title}</td>
                      <td className="home-modified" title={deleted.absolute}>
                        {deleted.relative}{entry.deletedBy === undefined ? '' : ` by ${entry.deletedBy}`}
                      </td>
                      <td className="home-modified">{left === undefined ? 'Kept' : `${String(left)} day${left === 1 ? '' : 's'}`}</td>
                      <td className="bin-actions">
                        <button type="button" onClick={() => { restore(entry); }} disabled={busy} aria-label={`Restore ${entry.title}`}>Restore</button>
                        {entry.canPurge && (
                          <button type="button" className="bin-forever" onClick={() => { setConfirmPurge(entry); }} disabled={busy} aria-label={`Delete forever: ${entry.title}`}>
                            Delete forever
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </>
      )}

      {confirmPurge !== undefined && (
        <div className="home-confirm-backdrop" role="presentation" onClick={(e) => { if (e.target === e.currentTarget) { setConfirmPurge(undefined); } }}>
          <div
            className="home-confirm"
            role="dialog"
            aria-modal="true"
            aria-labelledby="bin-purge-title"
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                setConfirmPurge(undefined);
              }
            }}
          >
            <h2 id="bin-purge-title" className="home-confirm-title">Delete “{confirmPurge.title}” forever?</h2>
            <p className="home-confirm-body">This removes the document and its whole history for good. It cannot be undone and it cannot be restored from the bin.</p>
            <div className="home-confirm-actions">
              <button type="button" autoFocus onClick={() => { setConfirmPurge(undefined); }}>Cancel</button>
              <button type="button" className="home-confirm-delete" onClick={purge}>Delete forever</button>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
