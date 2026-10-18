/**
 * "Import Mermaid": a paste box for a Mermaid `flowchart` / `graph`, `sequenceDiagram` or `erDiagram` (docs/api.md
 * `POST /api/import/mermaid`). The home screen's Import Mermaid button and the editor's File menu action both open it.
 * It follows ImportSqlDialog's two steps: "Read diagram" imports without saving anything and shows what it found and
 * every guess or skip as warnings, so a wrong paste is caught before a document exists; "Create diagram" then saves it
 * as a new document and hands its id to `onCreated`. Changing the text after a read discards that read; the title is applied when the diagram is created.
 * The header picks the type, so there is no type choice here; a sequence diagram reads as participants and messages.
 */

import { useEffect, useId, useRef, useState } from 'react';
import { previewMermaidImport, type MermaidImportPreview } from '../model/mermaidImport.ts';
import { FRAGMENT_KIND, NOTE_KIND } from '../model/sequenceScene.ts';
import { createImportedDocument } from '../model/sqlImport.ts';

/** Warnings past this many are summarised, so a large paste does not bury the buttons. */
const MAX_WARNINGS_SHOWN = 100;

const runNow = (go: () => void): void => {
  go();
};

type Status = { kind: 'idle' } | { kind: 'reading' } | { kind: 'ready'; preview: MermaidImportPreview } | { kind: 'creating'; preview: MermaidImportPreview };

export interface ImportMermaidDialogProps {
  onClose: () => void;
  /** Called with the new document's id once it is saved; the caller opens it. It must not be able to refuse: the asking is `confirmLeave`. */
  onCreated: (id: string) => void;
  /**
   * Runs `go` when the caller is happy for the diagram to be created (default: at once). A caller whose page may hold
   * unsaved edits asks the user here, before anything is saved, and calls `go` only on Save or Discard.
   */
  confirmLeave?: (go: () => void) => void;
}

function plural(count: number, one: string, many: string): string {
  return `${String(count)} ${count === 1 ? one : many}`;
}

/** What the import found, in the document type's own words. */
/**
 * What the warnings add up to, in words that tell the two kinds apart (QA-08): a kind the importer chose from a shape's
 * look (`kind-guessed`) is a judgement to check, not something lost, so it is not reported as "skipped"; anything else
 * (an unsupported statement, an approximated style, a renamed id) is.
 */
function describeWarnings(warnings: readonly { code: string }[]): string {
  const guessed = warnings.filter((warning) => warning.code === 'kind-guessed').length;
  const other = warnings.length - guessed;
  const parts: string[] = [];
  if (guessed > 0) {
    parts.push(`${String(guessed)} ${guessed === 1 ? 'shape' : 'shapes'} could mean more than one thing, so ${guessed === 1 ? 'its kind was' : 'their kinds were'} chosen for you and listed below`);
  }
  if (other > 0) {
    parts.push(`${String(other)} ${other === 1 ? 'item was' : 'items were'} skipped or only partly understood`);
  }
  return parts.length === 0 ? '' : ` ${parts.join('; ')}:`;
}

function summarise(preview: MermaidImportPreview): string {
  const { document } = preview;
  if (document.nodes.length === 0) {
    return 'Nothing was found in that Mermaid.';
  }
  // A sequence diagram's fragments are nodes and its notes and interaction uses are edges, but neither is a participant or a message.
  const participants = document.nodes.filter((node) => node.kind !== FRAGMENT_KIND).length;
  const messages = document.edges.filter((edge) => edge.kind !== NOTE_KIND && edge.kind !== 'interaction-use').length;
  // An erDiagram arrives as a document of nothing but ER entities: tables and their relationships, as the SQL import words it.
  if (document.nodes.every((node) => node.shape.startsWith('custom:er/'))) {
    return `Found ${plural(document.nodes.length, 'table', 'tables')} and ${plural(document.edges.length, 'relationship', 'relationships')}.`;
  }
  return document.type === 'sequence'
    ? `Found ${plural(participants, 'participant', 'participants')} and ${plural(messages, 'message', 'messages')}.`
    : `Found ${plural(document.nodes.length, 'node', 'nodes')} and ${plural(document.edges.length, 'connector', 'connectors')}.`;
}

export function ImportMermaidDialog({ onClose, onCreated, confirmLeave = runNow }: ImportMermaidDialogProps) {
  const headingId = useId();
  const sourceId = useId();
  const titleId = useId();
  const [source, setSource] = useState('');
  const [title, setTitle] = useState('');
  const [status, setStatus] = useState<Status>({ kind: 'idle' });
  const [error, setError] = useState<string | undefined>(undefined);
  const textarea = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    textarea.current?.focus();
  }, []);

  const changed = (): void => {
    setStatus({ kind: 'idle' });
    setError(undefined);
  };

  const read = (): void => {
    setStatus({ kind: 'reading' });
    setError(undefined);
    previewMermaidImport(source, title)
      .then((preview) => {
        setStatus({ kind: 'ready', preview });
      })
      .catch((cause: unknown) => {
        setStatus({ kind: 'idle' });
        setError(cause instanceof Error ? cause.message : 'The Mermaid could not be read.');
      });
  };

  const create = (): void => {
    if (status.kind !== 'ready') {
      return;
    }
    const { preview } = status;
    // Asked before anything is created: a Cancel never calls `go`, so the dialog stays as it was and nothing was saved.
    confirmLeave(() => {
      setStatus({ kind: 'creating', preview });
      setError(undefined);
      createImportedDocument(title.trim() === '' ? preview.document : { ...preview.document, title: title.trim() })
        .then((id) => {
          // Ready again before the caller navigates, so a navigation that does not unmount this dialog leaves it usable.
          setStatus({ kind: 'ready', preview });
          onCreated(id);
        })
        .catch((cause: unknown) => {
          setStatus({ kind: 'ready', preview });
          setError(cause instanceof Error ? cause.message : 'The diagram could not be created.');
        });
    });
  };

  const preview = status.kind === 'ready' || status.kind === 'creating' ? status.preview : undefined;
  const busy = status.kind === 'reading' || status.kind === 'creating';
  const empty = preview?.document.nodes.length === 0;

  return (
    <div
      className="sql-import-backdrop"
      role="presentation"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget && !busy) {
          onClose();
        }
      }}
    >
      <div
        className="sql-import"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        onKeyDown={(e) => {
          // Stop every key here reaching the canvas or window shortcuts behind the dialog.
          e.stopPropagation();
          if (e.key === 'Escape' && !busy) {
            onClose();
          }
          // Keep Tab inside the dialog: the page behind it is inert while it is open.
          if (e.key === 'Tab') {
            const focusable = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('textarea, input, select, button:not(:disabled)'));
            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            if (e.shiftKey && document.activeElement === first) {
              e.preventDefault();
              last?.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
              e.preventDefault();
              first?.focus();
            }
          }
        }}
      >
        <h2 id={headingId} className="sql-import-title">Import Mermaid</h2>
        <p className="sql-import-help">
          Paste a Mermaid flowchart, sequenceDiagram or erDiagram. A sequence diagram becomes participants and messages in the order they are
          written; an erDiagram becomes tables and relationships. Nothing is saved until you create the diagram.
        </p>

        <div className="sql-import-field">
          <label htmlFor={sourceId}>Mermaid</label>
          <textarea
            id={sourceId}
            ref={textarea}
            className="sql-import-text"
            rows={12}
            spellCheck={false}
            value={source}
            placeholder={'sequenceDiagram\n    User->>System: Request\n    System-->>User: Response'}
            onChange={(e) => {
              setSource(e.target.value);
              changed();
            }}
          />
        </div>

        <div className="sql-import-row">
          <div className="sql-import-field">
            <label htmlFor={titleId}>Diagram title</label>
            <input
              id={titleId}
              type="text"
              value={title}
              placeholder="Imported diagram"
              onChange={(e) => {
                setTitle(e.target.value);
              }}
            />
          </div>
        </div>

        {error !== undefined && <p className="sql-import-error" role="alert">{error}</p>}

        {preview !== undefined && (
          <div className="sql-import-result" role="status">
            <p className="sql-import-summary">
              {summarise(preview)}
              {describeWarnings(preview.warnings)}
            </p>
            {preview.warnings.length > 0 && (
              <ul className="sql-import-warnings" aria-label="Import warnings">
                {preview.warnings.slice(0, MAX_WARNINGS_SHOWN).map((warning, index) => (
                  <li key={`${String(index)}-${warning.code}`}>
                    {warning.line !== undefined && <span className="sql-import-line">Line {String(warning.line)}</span>}
                    {warning.message}
                  </li>
                ))}
                {preview.warnings.length > MAX_WARNINGS_SHOWN && (
                  <li>…and {String(preview.warnings.length - MAX_WARNINGS_SHOWN)} more.</li>
                )}
              </ul>
            )}
          </div>
        )}

        <div className="sql-import-actions">
          <button type="button" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="button" onClick={read} disabled={busy || source.trim() === ''}>
            {status.kind === 'reading' ? 'Reading…' : 'Read diagram'}
          </button>
          <button type="button" className="sql-import-create" onClick={create} disabled={busy || preview === undefined || empty}>
            {status.kind === 'creating' ? 'Creating…' : 'Create diagram'}
          </button>
        </div>
      </div>
    </div>
  );
}
