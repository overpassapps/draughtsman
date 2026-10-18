/**
 * The Review view of the editor's right-hand panel (design/atlas/critique-panel.md): the deterministic critique the
 * server already runs for MCP (`critique_document`, `CritiqueAnalyser`) for the document as it is on the canvas. It is
 * posted as it stands, so a finding is about what the person is looking at, unsaved edits included. It runs when the tab
 * opens, on Refresh, and (debounced) when the document changes while the tab is open; nothing runs while it is closed.
 * Read-only: clicking a finding, or one of the elements it names, selects them and centres the first on the canvas.
 */

import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { critiqueDocument, type CritiqueFinding } from '../model/historyClient.ts';
import { formatTime } from '../model/dateFormat.ts';
import { orderFindings, REVIEW_DEBOUNCE_MS, summaryLine } from './reviewFormat.ts';
import type { Document, Id } from '../model/types.ts';

export interface ReviewPanelProps {
  document: Document;
  /** True while the Review tab is showing. */
  active: boolean;
  /** Selects these elements and centres the first that is drawn. */
  onSelect: (ids: readonly Id[]) => void;
  onEscape: () => void;
}

type State =
  | { kind: 'idle' }
  | { kind: 'running'; previous: CritiqueFinding[] | undefined; asOf: Date | undefined }
  | { kind: 'ok'; findings: CritiqueFinding[]; asOf: Date }
  | { kind: 'error'; message: string; previous: CritiqueFinding[] | undefined; asOf: Date | undefined };

function message(cause: unknown): string {
  return cause instanceof Error ? cause.message : 'Unknown error';
}

export function ReviewPanel({ document, active, onSelect, onEscape }: ReviewPanelProps) {
  const [state, setState] = useState<State>({ kind: 'idle' });
  const generation = useRef(0);
  const abort = useRef<AbortController | undefined>(undefined);
  const documentRef = useRef(document);
  useEffect(() => {
    documentRef.current = document;
  });

  const run = useCallback((): void => {
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    const mine = ++generation.current;
    setState((current) => ({
      kind: 'running',
      previous: current.kind === 'ok' ? current.findings : current.kind === 'running' || current.kind === 'error' ? current.previous : undefined,
      asOf: current.kind === 'ok' ? current.asOf : current.kind === 'running' || current.kind === 'error' ? current.asOf : undefined,
    }));
    critiqueDocument(documentRef.current, fetch, controller.signal)
      .then((findings) => {
        if (mine === generation.current) {
          setState({ kind: 'ok', findings: orderFindings(findings), asOf: new Date() });
        }
      })
      .catch((cause: unknown) => {
        if (mine !== generation.current || (cause instanceof DOMException && cause.name === 'AbortError')) {
          return;
        }
        setState((current) => ({
          kind: 'error',
          message: message(cause),
          previous: current.kind === 'ok' ? current.findings : current.kind === 'running' ? current.previous : undefined,
          asOf: current.kind === 'ok' ? current.asOf : current.kind === 'running' ? current.asOf : undefined,
        }));
      });
  }, []);

  // Opening the tab runs it at once; after that an edit re-runs it once the document has been still for a moment.
  const first = useRef(true);
  useEffect(() => {
    if (!active) {
      first.current = true;
      abort.current?.abort();
      generation.current += 1;
      return undefined;
    }
    if (first.current) {
      first.current = false;
      run();
      return undefined;
    }
    const timer = setTimeout(run, REVIEW_DEBOUNCE_MS);
    return () => { clearTimeout(timer); };
  }, [active, document, run]);

  useEffect(() => () => { abort.current?.abort(); }, []);

  const nodeLabel = (id: Id): string => {
    const node = document.nodes.find((candidate) => candidate.id === id);
    return node === undefined || node.label.trim() === '' ? id : node.label;
  };
  const edgeLabel = (id: Id): string => {
    const edge = document.edges.find((candidate) => candidate.id === id);
    if (edge === undefined) {
      return id;
    }
    return edge.label !== undefined && edge.label.trim() !== '' ? edge.label : `${nodeLabel(edge.from.node)} → ${nodeLabel(edge.to.node)}`;
  };

  const findings = state.kind === 'ok' ? state.findings : state.kind === 'idle' ? undefined : state.previous;
  const asOf = state.kind === 'idle' ? undefined : state.asOf;

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
    if (e.key === 'Escape') {
      onEscape();
    }
  };

  return (
    <div className="review-panel" onKeyDown={onKeyDown}>
      <div className="history-head">
        <h2 className="panel-heading">Review</h2>
        <div className="history-head-actions">
          <button type="button" onClick={run} disabled={state.kind === 'running'}>Refresh</button>
        </div>
      </div>
      <p className="panel-hint">Structural checks on this diagram: what the diagram's kinds make decidable, not a judgement of its content.</p>

      {state.kind === 'running' && findings === undefined && <p className="panel-hint">Checking…</p>}
      {state.kind === 'error' && (
        <p className="history-error" role="alert">
          Cannot check the diagram: {state.message}{' '}
          <button type="button" className="home-link-button" onClick={run}>Try again</button>
          {findings !== undefined && asOf !== undefined && <> Showing the result as of {formatTime(asOf)}.</>}
        </p>
      )}
      {findings !== undefined && (
        <>
          <p className="review-summary" role="status" aria-live="polite" data-testid="review-summary">{summaryLine(findings)}</p>
          {findings.length > 0 && (
            <ul className="review-list" aria-label="Findings">
              {findings.map((finding, index) => {
                const ids = [...finding.nodeIds, ...finding.edgeIds];
                return (
                  <li key={`${finding.rule}:${String(index)}`} className={`review-finding review-${finding.severity}`}>
                    <button
                      type="button"
                      className="review-finding-main"
                      aria-label={`${finding.severity}: ${finding.message}`}
                      disabled={ids.length === 0}
                      onClick={() => { onSelect(ids); }}
                    >
                      <span className="review-severity">
                        <span aria-hidden="true">{finding.severity === 'error' ? '✕' : '!'}</span> {finding.severity === 'error' ? 'Error' : 'Warning'}
                      </span>
                      <code className="review-rule">{finding.rule}</code>
                      <span className="review-message">{finding.message}</span>
                    </button>
                    {ids.length > 0 && (
                      <span className="review-chips">
                        {finding.nodeIds.map((id) => (
                          <button key={id} type="button" className="review-chip" onClick={() => { onSelect([id]); }}>{nodeLabel(id)}</button>
                        ))}
                        {finding.edgeIds.map((id) => (
                          <button key={id} type="button" className="review-chip" onClick={() => { onSelect([id]); }}>{edgeLabel(id)}</button>
                        ))}
                      </span>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
          {asOf !== undefined && state.kind === 'ok' && <p className="panel-hint">Checked at {formatTime(asOf)}.</p>}
        </>
      )}
    </div>
  );
}
