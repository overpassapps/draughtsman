import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { csrfHeaders } from '../model/csrf.ts';
import { sameValue } from '../model/documentDiff.ts';
import { exportMermaidDetailed, type MermaidExport } from '../model/mermaidExport.ts';
import { parseDocument } from '../model/parse.ts';
import type { MergeConflict } from '../model/remoteMerge.ts';
import { describeTextConflicts } from '../model/textMerge.ts';
import type { Document, Id } from '../model/types.ts';
import { platformShortcut } from './platform.ts';
import { elementAtLine, lineAtOffset, lineCount, lineRange, selectedLines } from './textPaneLines.ts';
import { loadTextPanePrefs, saveTextPanePrefs, type TextFormat } from './textPanePrefs.ts';

interface Props {
  document: Document;
  /** False while the tab is hidden: no export requests and no stale check run, and the draft is kept. */
  active: boolean;
  /** True when the person may look but not change: the text can be read and copied, and Apply is not offered. */
  readOnly?: boolean;
  /** The canvas's selection: the lines of these elements are marked, and a click on a line selects its element. */
  selectedIds?: ReadonlySet<Id>;
  onSelect?: (ids: ReadonlySet<Id>) => void;
  /** Whether to move focus into the text when the panel opens. False when it was restored from the last visit, so a reload does not take the keyboard away from the canvas. */
  autoFocus?: boolean;
  /** Applies text edited from `base` while the diagram is still `base`, as one undo step (the exact diff). */
  onApply: (next: Document, base: Document) => void;
  /** Applies text edited from an older `base` onto the diagram as it is now, as one undo step; returns what both changed. */
  onMerge?: (next: Document, base: Document) => MergeConflict[];
  /** Takes the text's side of conflicts `onMerge` returned, as one more undo step. */
  onTakeMine?: (next: Document, conflicts: readonly MergeConflict[]) => void;
  onEscape: () => void;
}
interface Draft { text: string; original: string; base: Document }
/** A parser error or lint warning: where it is, when it names a line, and what is wrong. */
interface Problem { message: string; line?: number; column?: number }
interface Merged { mine: Document; conflicts: readonly MergeConflict[]; items: string[] }

/** The text's line grid, shared with app.css (`--dsl-line`, `--dsl-pad`): the gutter and the marked lines are placed from it. */
const LINE_HEIGHT = 21;
const PAD = 8;

class RequestProblem extends Error {
  readonly line: number | undefined;
  readonly column: number | undefined;
  constructor(message: string, line?: number, column?: number) {
    super(message);
    this.line = line;
    this.column = column;
  }
}

async function problem(response: Response): Promise<RequestProblem> {
  try {
    const body = await response.json() as { detail?: string; title?: string; line?: number; column?: number };
    return new RequestProblem(body.detail ?? body.title ?? `Request failed (${String(response.status)})`, body.line, body.column);
  } catch {
    return new RequestProblem(`Request failed (${String(response.status)})`);
  }
}

function place(line: number | undefined, column: number | undefined): string {
  return line === undefined ? '' : column === undefined ? `Line ${String(line)}` : `Line ${String(line)}, column ${String(column)}`;
}

/**
 * The document as text beside the canvas: the DSL to edit (explicit Apply keeps incomplete text out of the model, and a
 * dirty draft never gets overwritten by a canvas edit) or its Mermaid export to read. design/atlas/properties-panel.md.
 */
export function DslPanel({ document, active, readOnly = false, selectedIds, onSelect, autoFocus = true, onApply, onMerge, onTakeMine, onEscape }: Props) {
  const [format, setFormatState] = useState<TextFormat>(() => loadTextPanePrefs().format);
  const [draft, setDraft] = useState<Draft>();
  const [error, setError] = useState<Problem>();
  const [warnings, setWarnings] = useState<Problem[]>([]);
  const [applying, setApplying] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [keptMine, setKeptMine] = useState(false);
  const [merged, setMerged] = useState<Merged>();
  const [scrollTop, setScrollTop] = useState(0);
  const [mermaid, setMermaid] = useState<MermaidExport>();
  const [mermaidError, setMermaidError] = useState<string>();
  const field = useRef<HTMLTextAreaElement>(null);
  const mermaidField = useRef<HTMLTextAreaElement>(null);
  const dsl = format === 'dsl';
  const draftBase = draft?.base;
  const dirty = draft !== undefined && draft.text !== draft.original;
  // Memoised: the panel re-renders on every scroll and keystroke, and comparing a large diagram is not free.
  const stale = useMemo(() => active && draftBase !== undefined && !sameValue(draftBase, document), [active, draftBase, document]);
  const changedUnderneath = dirty && stale;
  useEffect(() => { if (autoFocus) field.current?.focus(); }, [autoFocus]);
  // Closing or reloading the tab with unapplied text asks first (the browser words the question). Leaving by an in-app link is
  // not held: the editor's leave guard (useLeaveGuard) is about the saved document, and its Save and Discard are not about this text.
  useEffect(() => {
    if (!dirty) return;
    const hold = (event: BeforeUnloadEvent): void => { event.preventDefault(); };
    window.addEventListener('beforeunload', hold);
    return () => { window.removeEventListener('beforeunload', hold); };
  }, [dirty]);
  useEffect(() => {
    if (dirty || !active || !dsl) return;
    // Text that is already this diagram (just applied from here, or just printed) is left as it is, with the cursor in it.
    if (draftBase !== undefined && sameValue(draftBase, document)) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      void (async () => {
        try {
          const response = await fetch('/api/export/dsl', {
            method: 'POST', headers: { 'Content-Type': 'application/json', ...csrfHeaders() }, body: JSON.stringify(document), signal: controller.signal,
          });
          if (!response.ok) throw await problem(response);
          const text = await response.text();
          if (!controller.signal.aborted) {
            setDraft({ text, original: text, base: document });
            setError(undefined);
          }
        } catch (error) {
          if (!controller.signal.aborted) setError({ message: error instanceof Error ? error.message : 'Could not load DSL.' });
        }
      })();
    }, 150);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [document, dirty, active, dsl, refresh, draftBase]);
  // The Mermaid view is the diagram as it is now, read only: Mermaid cannot carry the model (geometry, ports, props), so
  // text typed there could not be applied faithfully. It re-renders with the document, like clean DSL text does.
  useEffect(() => {
    if (!active || dsl) return;
    let current = true;
    const timer = setTimeout(() => {
      exportMermaidDetailed(document)
        .then((result) => { if (current) { setMermaid(result); setMermaidError(undefined); } })
        .catch((error: unknown) => { if (current) setMermaidError(error instanceof Error ? error.message : 'Could not export Mermaid.'); });
    }, 150);
    return () => { current = false; clearTimeout(timer); };
  }, [document, active, dsl]);

  const text = draft?.text ?? '';
  const markedLines = useMemo(() => (dsl ? selectedLines(text, selectedIds ?? new Set<Id>()) : []), [dsl, text, selectedIds]);
  const problemLines = useMemo(() => [error, ...warnings].flatMap((item) => item?.line ?? []), [error, warnings]);
  // A selection made on the canvas can be a line the text has scrolled off: bring the first one into view.
  const firstMarked = markedLines[0];
  useEffect(() => {
    const element = field.current;
    if (!dsl || element === null || firstMarked === undefined || element.clientHeight === 0) return;
    const top = (firstMarked - 1) * LINE_HEIGHT;
    if (top < element.scrollTop || top + LINE_HEIGHT + PAD > element.scrollTop + element.clientHeight) {
      element.scrollTop = Math.max(0, top - Math.floor(element.clientHeight / 3));
    }
  }, [dsl, firstMarked]);

  const setFormat = (next: TextFormat): void => {
    setFormatState(next);
    saveTextPanePrefs({ format: next });
  };
  const reload = (): void => {
    setDraft(undefined); setError(undefined); setWarnings([]); setMerged(undefined); setKeptMine(false); setRefresh((value) => value + 1);
  };
  const goToLine = (line: number, column?: number): void => {
    const element = field.current;
    if (element === null) return;
    const { start, end } = lineRange(element.value, line);
    element.focus();
    element.setSelectionRange(Math.min(start + Math.max((column ?? 1) - 1, 0), end), end);
    const top = (line - 1) * LINE_HEIGHT;
    if (element.clientHeight > 0 && (top < element.scrollTop || top + LINE_HEIGHT + PAD > element.scrollTop + element.clientHeight)) {
      element.scrollTop = Math.max(0, top - LINE_HEIGHT * 3);
    }
  };
  const selectClickedLine = (): void => {
    const element = field.current;
    if (element === null || onSelect === undefined || element.selectionStart !== element.selectionEnd) return;
    const id = elementAtLine(element.value, lineAtOffset(element.value, element.selectionStart));
    if (id !== undefined && (document.nodes.some((node) => node.id === id) || document.edges.some((edge) => edge.id === id))) {
      onSelect(new Set([id]));
    }
  };

  const apply = async (): Promise<void> => {
    if (readOnly || !dsl || draft === undefined || applying || !dirty) return;
    setApplying(true);
    setError(undefined);
    setMerged(undefined);
    try {
      const response = await fetch('/api/import/dsl', { method: 'POST', headers: { 'Content-Type': 'text/plain', ...csrfHeaders() }, body: draft.text });
      if (!response.ok) throw await problem(response);
      const result = await response.json() as { document: unknown; warnings: { message: string; line?: number }[] };
      const parsed = parseDocument(result.document);
      // Pasted text often names another id (or none, which the importer slugs from the title): the diagram being edited
      // keeps its own, and the panel says so rather than refusing the text.
      const next = parsed.id === draft.base.id ? parsed : { ...parsed, id: draft.base.id };
      const notes: Problem[] = [
        ...(parsed.id === draft.base.id ? [] : [{ message: `The text named the id "${parsed.id}"; this diagram keeps its own id, "${draft.base.id}".` }]),
        ...result.warnings.map((warning): Problem => (warning.line === undefined ? { message: warning.message } : { message: warning.message, line: warning.line })),
      ];
      if (stale && onMerge !== undefined) {
        // The diagram moved on while this was typed: fold the text's changes into it rather than refuse them.
        const conflicts = onMerge(next, draft.base);
        if (conflicts.length > 0) setMerged({ mine: next, conflicts, items: describeTextConflicts(conflicts, document) });
        // What is in the diagram now is not exactly what was typed, so the text is printed again from it.
        setDraft(undefined);
        setRefresh((value) => value + 1);
        setWarnings(notes.map(({ message }) => ({ message })));
      } else {
        onApply(next, draft.base);
        // The typed text stays (the cursor and the warnings' line numbers with it) until the diagram changes again.
        setDraft({ text: draft.text, original: draft.text, base: next });
        setWarnings(notes);
      }
      setKeptMine(false);
    } catch (caught) {
      setError(caught instanceof RequestProblem && caught.line !== undefined
        ? { message: caught.message, line: caught.line, ...(caught.column === undefined ? {} : { column: caught.column }) }
        : { message: caught instanceof Error ? caught.message : 'Could not apply DSL.' });
    } finally {
      setApplying(false);
    }
  };
  const copy = (): void => {
    const target = dsl ? field.current : mermaidField.current;
    if (target === null) return;
    target.focus(); target.select();
    // Native copy works on self-hosted HTTP too; the asynchronous clipboard API requires a secure context.
    // eslint-disable-next-line @typescript-eslint/no-deprecated
    if (!window.document.execCommand('copy')) {
      const message = `Text selected. Press ${platformShortcut('Cmd/Ctrl+C')} to copy.`;
      if (dsl) setError({ message }); else setMermaidError(message);
    }
  };
  const lines = lineCount(text);
  const numbers = useMemo(() => Array.from({ length: lines }, (_, index) => index + 1).join('\n'), [lines]);
  const gridStyle = { '--dsl-line': `${String(LINE_HEIGHT)}px`, '--dsl-pad': `${String(PAD)}px` } as CSSProperties;
  const band = (line: number, kind: 'selected' | 'problem') => (
    <div key={`${kind}-${String(line)}`} className={`dsl-band dsl-band-${kind}`} style={{ transform: `translateY(${String(PAD + (line - 1) * LINE_HEIGHT - scrollTop)}px)` }} />
  );
  const goButton = (item: Problem) => item.line === undefined ? null : (
    <>
      <button type="button" className="dsl-goto" onClick={() => { goToLine(item.line ?? 1, item.column); }}>{place(item.line, item.column)}</button>
      {': '}
    </>
  );

  return (
    <section className="dsl-panel" aria-label="Text (DSL)" onKeyDown={(event) => {
      event.stopPropagation();
      if (event.key === 'Escape') { event.preventDefault(); onEscape(); }
      else if (event.key === 'Enter' && (event.metaKey || event.ctrlKey) && dsl) { event.preventDefault(); void apply(); }
    }}>
      <div className="dsl-controls dsl-format" role="group" aria-label="Text format">
        <button type="button" aria-pressed={dsl} onClick={() => { setFormat('dsl'); }}>DSL</button>
        <button type="button" aria-pressed={!dsl} onClick={() => { setFormat('mermaid'); }}>Mermaid</button>
      </div>
      {/* Both views stay mounted so a draft, its scroll position and its cursor survive a look at the other one. */}
      <div hidden={!dsl} className="dsl-view">
        <label htmlFor="document-dsl">Document DSL</label>
        <div className="dsl-editor" style={gridStyle}>
          <div className="dsl-gutter" aria-hidden="true"><pre style={{ transform: `translateY(${String(-scrollTop)}px)` }}>{numbers}</pre></div>
          <textarea id="document-dsl" ref={field} value={text} spellCheck={false} wrap="off"
            readOnly={readOnly || draft === undefined || applying} aria-invalid={error !== undefined} aria-describedby={error === undefined ? undefined : 'dsl-error'}
            aria-keyshortcuts={readOnly ? undefined : 'Control+Enter Meta+Enter'}
            onScroll={(event) => { setScrollTop(event.currentTarget.scrollTop); }}
            onClick={selectClickedLine}
            onChange={(event) => { if (draft) setDraft({ ...draft, text: event.target.value }); setError(undefined); setWarnings([]); setMerged(undefined); }} />
          <div className="dsl-bands" aria-hidden="true">
            {markedLines.map((line) => band(line, 'selected'))}
            {problemLines.map((line) => band(line, 'problem'))}
          </div>
        </div>
        <div className="dsl-controls">
          {!readOnly && <button type="button" disabled={!dirty || applying} title={`Apply (${platformShortcut('Cmd/Ctrl+Enter')})`} onClick={() => { void apply(); }}>Apply</button>}
          {!readOnly && <button type="button" disabled={applying} onClick={reload}>Reset text</button>}
          <button type="button" disabled={draft === undefined} onClick={copy}>Copy</button>
        </div>
        {readOnly && <p role="status">View only: you can read and copy this text, but not change the diagram from it.</p>}
        {applying && <p role="status">Applying…</p>}
        {changedUnderneath && !keptMine && (
          <div className="dsl-notice" role="status">
            <p>Diagram changed. Your unapplied text is kept here.</p>
            <div className="dsl-controls">
              <button type="button" onClick={reload}>Reload text</button>
              <button type="button" onClick={() => { setKeptMine(true); }}>Keep mine</button>
            </div>
          </div>
        )}
        {changedUnderneath && keptMine && <p role="status">Keeping your text. Apply folds it into the diagram as it is now; where you both changed the same thing, the diagram’s version stays and you are told.</p>}
        {merged !== undefined && (
          <div className="dsl-notice" role="status">
            <p>Applied. You and the diagram both changed {merged.items.length === 1 ? 'one thing' : `${String(merged.items.length)} things`}, so the diagram’s version stayed:</p>
            <ul>{merged.items.map((item, index) => <li key={index}>{item}</li>)}</ul>
            {onTakeMine !== undefined && (
              <div className="dsl-controls">
                <button type="button" onClick={() => { onTakeMine(merged.mine, merged.conflicts); setMerged(undefined); }}>Use my text for these</button>
                <button type="button" onClick={() => { setMerged(undefined); }}>Dismiss</button>
              </div>
            )}
          </div>
        )}
        {error !== undefined && <p id="dsl-error" role="alert">{goButton(error)}{error.message}</p>}
        {warnings.length > 0 && <ul aria-label="DSL notes and warnings">{warnings.map((warning, index) => <li key={index}>{goButton(warning)}{warning.message}</li>)}</ul>}
      </div>
      <div hidden={dsl} className="dsl-view">
        <label htmlFor="document-mermaid">Document Mermaid (read only)</label>
        <textarea id="document-mermaid" ref={mermaidField} className="dsl-mermaid" value={mermaid?.text ?? ''} readOnly spellCheck={false} wrap="off" />
        <div className="dsl-controls">
          <button type="button" disabled={mermaid === undefined} onClick={copy}>Copy</button>
        </div>
        <p>This is the diagram as it is now{dirty ? ', without your unapplied DSL text' : ''}. Mermaid cannot carry positions, ports or properties, so it is for reading and copying; edit the DSL to change the diagram.</p>
        {mermaid !== undefined && mermaid.warnings > 0 && (
          <div>
            <p>Mermaid has no spelling for {mermaid.warnings === 1 ? 'one thing' : `${String(mermaid.warnings)} things`} here, so {mermaid.warnings === 1 ? 'it is' : 'they are'} simplified:</p>
            {mermaid.notes.length > 0 && <ul aria-label="What Mermaid simplified">{mermaid.notes.map((note, index) => <li key={index}>{note}</li>)}</ul>}
          </div>
        )}
        {mermaidError !== undefined && <p role="alert">{mermaidError}</p>}
      </div>
    </section>
  );
}
