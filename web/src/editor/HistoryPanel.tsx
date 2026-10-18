/**
 * The History view of the editor's right-hand panel (design/atlas/version-history.md): the document's stored versions
 * newest first, naming one, comparing one with the document as it is now, and restoring one.
 *
 * The list is the server's (`historyClient.ts`): a person's named snapshots and the automatic `checkpoint` taken before an
 * agent's edit or a restore. Compare asks the server for its structured diff (the `diff_documents` tool's own
 * `DocumentDiff`) and shows it as a readable list while the canvas marks the same elements (`CompareMarks`, view state
 * only). Restore is the editor's `restoreVersion`, which saves what is unsaved, asks the server to restore (it checkpoints
 * the current document first, so the restore itself appears here as a checkpoint that undoes it) and adopts the result.
 * Saving first matters twice: a snapshot or a comparison is of what the person sees, and a restore quotes the version the
 * server really holds.
 */

import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { compareResult, summarise, type CompareEntry, type CompareMarks, type CompareResult, type CompareStatus } from '../model/compare.ts';
import { SaveConflict } from '../model/documentClient.ts';
import { dateTimeTitle } from '../model/dateFormat.ts';
import { actorLabel, formatSize, readableReason, relativeTime as relative } from './historyFormat.ts';
import { diffRevisions, isCheckpoint, listVersions, nameSnapshot, VERSION_PAGE, type VersionSummary } from '../model/historyClient.ts';
import type { Document, Id } from '../model/types.ts';

export interface HistoryPanelProps {
  /** The document as it is on the canvas; used to name what a comparison lists. */
  document: Document;
  /** True while the History tab is showing: the list is read on becoming active. */
  active: boolean;
  /** Stores what is unsaved; resolves true only when the whole document is stored. */
  flush: () => Promise<boolean>;
  /** Puts the document back to a version (useDocumentEditor `restoreVersion`); rejects with a message to show. */
  onRestore: (versionId: string) => Promise<void>;
  /** The marks to draw on the canvas for the comparison under way; undefined clears them. */
  onMarks: (marks: CompareMarks | undefined) => void;
  /** Selects a present element and centres it on the canvas. */
  onReveal: (id: Id) => void;
  onEscape: () => void;
}

type ListState = { kind: 'loading' } | { kind: 'ok'; items: VersionSummary[]; total: number } | { kind: 'error'; message: string };

type CompareState =
  | { kind: 'loading'; version: VersionSummary }
  | { kind: 'ok'; version: VersionSummary; result: CompareResult }
  | { kind: 'error'; version: VersionSummary; message: string };

const GROUPS: readonly { status: CompareStatus; heading: string }[] = [
  { status: 'added', heading: 'Added since then' },
  { status: 'removed', heading: 'Removed since then' },
  { status: 'changed', heading: 'Changed since then' },
];

/** The server's largest page (`VersionQuery.MaxLimit`). */
const MAX_VERSIONS = 200;

const SYMBOLS: Record<CompareStatus, string> = { added: '+', removed: '−', changed: '~' };

function message(cause: unknown): string {
  return cause instanceof Error ? cause.message : 'Unknown error';
}

function versionTitle(version: VersionSummary): string {
  return isCheckpoint(version) ? 'Automatic checkpoint' : version.name;
}

export function HistoryPanel({ document, active, flush, onRestore, onMarks, onReveal, onEscape }: HistoryPanelProps) {
  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [naming, setNaming] = useState(false);
  const [name, setName] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const [status, setStatus] = useState('');
  const [compare, setCompare] = useState<CompareState | undefined>(undefined);
  const [confirming, setConfirming] = useState<VersionSummary | undefined>(undefined);
  const restoreOpener = useRef<HTMLElement | null>(null);
  const documentId = document.id;
  // A comparison is of the document as it was when it was asked for; the editor may have moved on since.
  const latestDocument = useRef(document);
  useEffect(() => {
    latestDocument.current = document;
  });

  // How many versions the list asks for: a page to begin with, a page more each time "Show more" is pressed.
  const limit = useRef(VERSION_PAGE);
  const load = useCallback(
    (more = false): Promise<void> => {
      if (more) {
        limit.current += VERSION_PAGE;
      }
      return listVersions(documentId, 0, Math.min(limit.current, MAX_VERSIONS))
        .then((page) => {
          setList({ kind: 'ok', items: page.items, total: page.total });
        })
        .catch((cause: unknown) => {
          setList({ kind: 'error', message: message(cause) });
        });
    },
    [documentId],
  );

  useEffect(() => {
    if (active) {
      void load();
    }
  }, [active, load]);

  // The canvas shows the marks of the comparison on screen: none while another tab is showing, none once it is stopped.
  const marks = active && compare?.kind === 'ok' ? compare.result.marks : undefined;
  useEffect(() => {
    onMarks(marks);
  }, [marks, onMarks]);
  useEffect(() => () => { onMarks(undefined); }, [onMarks]);

  const stopComparing = useCallback((): void => {
    setCompare(undefined);
    setStatus('Stopped comparing.');
  }, []);

  const startCompare = (version: VersionSummary): void => {
    setError(undefined);
    setCompare({ kind: 'loading', version });
    void (async () => {
      try {
        if (!(await flush())) {
          throw new Error('Your latest changes could not be saved, so the comparison would be out of date. Resolve the save first.');
        }
        const diff = await diffRevisions(documentId, version.id);
        const result = compareResult(diff.diff, latestDocument.current);
        setCompare({ kind: 'ok', version, result });
        setStatus(`Comparing with ${versionTitle(version)}: ${summarise(result.counts)}.`);
      } catch (cause) {
        setCompare({ kind: 'error', version, message: message(cause) });
      }
    })();
  };

  const saveName = (): void => {
    const trimmed = name.trim();
    if (trimmed === '') {
      return;
    }
    setBusy(true);
    setError(undefined);
    void (async () => {
      try {
        if (!(await flush())) {
          throw new Error('Your latest changes could not be saved, so the snapshot would not match the canvas. Resolve the save first.');
        }
        await nameSnapshot(documentId, trimmed, note);
        setNaming(false);
        setName('');
        setNote('');
        setStatus(`Saved the version "${trimmed}".`);
        await load();
      } catch (cause) {
        setError(message(cause));
      } finally {
        setBusy(false);
      }
    })();
  };

  const confirmRestore = (): void => {
    if (confirming === undefined) {
      return;
    }
    const version = confirming;
    setBusy(true);
    setError(undefined);
    void (async () => {
      try {
        await onRestore(version.id);
        setConfirming(undefined);
        setCompare(undefined);
        setStatus(`Restored "${versionTitle(version)}". The document as it was is the newest checkpoint below.`);
        await load();
      } catch (cause) {
        setConfirming(undefined);
        setError(cause instanceof SaveConflict ? 'The document changed somewhere else while you were looking. Nothing was restored; look at the history again.' : message(cause));
        await load();
      } finally {
        setBusy(false);
      }
    })();
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
    if (e.key !== 'Escape' || confirming !== undefined) {
      return;
    }
    if (compare !== undefined) {
      stopComparing();
    } else if (naming) {
      setNaming(false);
    } else {
      onEscape();
    }
  };

  return (
    <div className="history-panel" onKeyDown={onKeyDown}>
      <div className="history-head">
        <h2 className="panel-heading">Version history</h2>
        <div className="history-head-actions">
          <button type="button" onClick={() => { setNaming((open) => !open); }} aria-expanded={naming} disabled={busy}>Name this version</button>
          <button type="button" onClick={() => { void load(); }} disabled={busy}>Refresh</button>
        </div>
      </div>

      {naming && (
        <form
          className="history-name"
          onSubmit={(e) => {
            e.preventDefault();
            saveName();
          }}
        >
          <label>
            Version name
            <input value={name} maxLength={256} autoFocus onChange={(e) => { setName(e.target.value); }} />
          </label>
          <label>
            Note (optional)
            <input value={note} onChange={(e) => { setNote(e.target.value); }} />
          </label>
          <div className="history-name-actions">
            <button type="submit" disabled={busy || name.trim() === ''}>Save version</button>
            <button type="button" onClick={() => { setNaming(false); }} disabled={busy}>Cancel</button>
          </div>
          <p className="panel-hint">A named version is kept until you delete the document. Automatic checkpoints are pruned after the newest 50.</p>
        </form>
      )}

      {error !== undefined && <p className="history-error" role="alert">{error}</p>}
      <p className="visually-hidden" role="status" aria-live="polite">{status}</p>

      {compare !== undefined && (
        <section className="history-compare" aria-label={`Comparison with ${versionTitle(compare.version)}`}>
          <div className="history-compare-head">
            <p className="history-compare-summary">
              {compare.kind === 'loading' && `Comparing with ${versionTitle(compare.version)}…`}
              {compare.kind === 'ok' && <>Since <strong>{versionTitle(compare.version)}</strong>: {summarise(compare.result.counts)}.</>}
              {compare.kind === 'error' && `Cannot compare with ${versionTitle(compare.version)}.`}
            </p>
            <button type="button" onClick={stopComparing}>Stop comparing</button>
          </div>
          {compare.kind === 'error' && <p className="history-error" role="alert">{compare.message}</p>}
          {compare.kind === 'ok' && compare.result.entries.length === 0 && <p className="panel-hint">The document is the same as this version.</p>}
          {compare.kind === 'ok' && GROUPS.map(({ status: groupStatus, heading }) => {
            const entries = compare.result.entries.filter((entry) => entry.status === groupStatus);
            if (entries.length === 0) {
              return null;
            }
            return (
              <div key={groupStatus} className="history-group">
                <h3 className="history-group-heading">{heading} ({String(entries.length)})</h3>
                <ul className="history-entries">
                  {entries.map((entry: CompareEntry) => (
                    <li key={`${entry.kind}:${entry.id}`}>
                      <button
                        type="button"
                        className={`history-entry history-entry-${entry.status}`}
                        disabled={!entry.present}
                        title={entry.present ? 'Show on the canvas' : 'No longer in the document'}
                        onClick={() => { onReveal(entry.id); }}
                      >
                        <span className="history-entry-symbol" aria-hidden="true">{SYMBOLS[entry.status]}</span>
                        <span className="history-entry-title">{entry.title}</span>
                        <span className="history-entry-detail">{entry.kind === 'edge' ? `connector, ${entry.detail}` : entry.detail}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            );
          })}
        </section>
      )}

      {list.kind === 'loading' && <p className="panel-hint">Loading the history…</p>}
      {list.kind === 'error' && <p className="history-error" role="alert">Cannot read the history: {list.message}</p>}
      {list.kind === 'ok' && list.items.length === 0 && <p className="panel-hint">No saved versions yet. Name one to keep a restore point, or ask an agent to edit: its changes are checkpointed here first.</p>}
      {list.kind === 'ok' && list.items.length > 0 && (
        <ul className="history-list" aria-label="Versions, newest first">
          {list.items.map((version) => {
            const title = versionTitle(version);
            return (
              <li key={version.id} className={`history-row${isCheckpoint(version) ? ' history-row-auto' : ''}`} aria-current={compare?.version.id === version.id ? 'true' : undefined}>
                <div className="history-row-main">
                  <span className="history-row-title">{title}</span>
                  <span className="history-row-meta">
                    <time dateTime={version.createdAt} title={dateTimeTitle(version.createdAt)}>{relative(version.createdAt)}</time>
                    {' · '}{actorLabel(version.actor)}{' · '}{formatSize(version.size)}
                  </span>
                  {version.reason !== undefined && version.reason !== '' && <span className="history-row-reason">{readableReason(version.reason)}</span>}
                </div>
                <div className="history-row-actions">
                  <button type="button" aria-label={`Compare with ${title}`} aria-pressed={compare?.version.id === version.id} onClick={() => { startCompare(version); }} disabled={busy}>Compare</button>
                  <button
                    type="button"
                    aria-label={`Restore ${title}`}
                    disabled={busy}
                    onClick={(e) => {
                      restoreOpener.current = e.currentTarget;
                      setConfirming(version);
                    }}
                  >
                    Restore
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
      {list.kind === 'ok' && list.total > list.items.length && (
        <button type="button" className="history-more" onClick={() => { void load(true); }}>Show more ({String(list.total - list.items.length)} older)</button>
      )}

      {confirming !== undefined && (
        <div className="home-confirm-backdrop" role="presentation" onClick={(e) => { if (e.target === e.currentTarget && !busy) { setConfirming(undefined); } }}>
          <div
            className="home-confirm"
            role="dialog"
            aria-modal="true"
            aria-labelledby="history-restore-title"
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                e.stopPropagation();
                setConfirming(undefined);
                restoreOpener.current?.focus();
              }
            }}
          >
            <h2 id="history-restore-title" className="home-confirm-title">Restore “{versionTitle(confirming)}”?</h2>
            <p className="home-confirm-body">
              The document goes back to how it was {relative(confirming.createdAt)}. As it is now, it is saved first as an automatic checkpoint at the top of this list, so you can undo the restore from here.
            </p>
            <div className="home-confirm-actions">
              <button type="button" autoFocus onClick={() => { setConfirming(undefined); restoreOpener.current?.focus(); }} disabled={busy}>Cancel</button>
              <button type="button" className="history-confirm-restore" onClick={confirmRestore} disabled={busy}>Restore</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
