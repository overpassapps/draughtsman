/**
 * "Import draw.io": a draw.io (diagrams.net) file, or its XML pasted, read into a new diagram (docs/api.md
 * `POST /api/import/drawio`, docs/drawio-import.md, design/atlas/drawio-import.md). The home screen's Import draw.io button and
 * the editor's File menu action both open it. It follows ImportMermaidDialog's two steps: "Read diagram" imports without saving
 * anything and shows what it found with every guess, skip and approximation as a warning, so a wrong file is caught before a
 * document exists; "Create diagram" then saves it as a new document and hands its id to `onCreated`. Changing the XML or the type
 * after a read discards that read; the title is applied when the diagram is created.
 *
 * Positions arrive with the file, so unlike the Mermaid and SQL dialogs this one never lays the diagram out. A file with several
 * pages imports one at a time: the first, unless the person picks another, which reads again at once.
 */

import { useEffect, useId, useRef, useState } from 'react';
import { DRAWIO_IMPORT_TYPES, previewDrawioImport, readFileText, type DrawioImportPreview, type DrawioImportType } from '../model/drawioImport.ts';
import { createImportedDocument } from '../model/sqlImport.ts';
import { describeImportWarnings } from './importWarnings.ts';

/** Warnings past this many are summarised, so a large file does not bury the buttons. */
const MAX_WARNINGS_SHOWN = 100;

const runNow = (go: () => void): void => {
  go();
};

type Status = { kind: 'idle' } | { kind: 'reading' } | { kind: 'ready'; preview: DrawioImportPreview } | { kind: 'creating'; preview: DrawioImportPreview };

export interface ImportDrawioDialogProps {
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

function summarise(preview: DrawioImportPreview): string {
  const { document } = preview;
  if (document.nodes.length === 0) {
    return 'Nothing was found on that page.';
  }
  return `Found ${plural(document.nodes.length, 'shape', 'shapes')} and ${plural(document.edges.length, 'connector', 'connectors')}.`;
}

export function ImportDrawioDialog({ onClose, onCreated, confirmLeave = runNow }: ImportDrawioDialogProps) {
  const headingId = useId();
  const fileId = useId();
  const sourceId = useId();
  const titleId = useId();
  const typeId = useId();
  const pageId = useId();
  const [source, setSource] = useState('');
  const [fileName, setFileName] = useState<string | undefined>(undefined);
  const [title, setTitle] = useState('');
  const [type, setType] = useState<DrawioImportType>('flowchart');
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

  const read = (page?: number): void => {
    setStatus({ kind: 'reading' });
    setError(undefined);
    previewDrawioImport(source, { type, title, ...(page === undefined ? {} : { page }) })
      .then((preview) => {
        setStatus({ kind: 'ready', preview });
      })
      .catch((cause: unknown) => {
        setStatus({ kind: 'idle' });
        setError(cause instanceof Error ? cause.message : 'The draw.io file could not be read.');
      });
  };

  const chooseFile = (file: File | undefined): void => {
    if (file === undefined) {
      return;
    }
    readFileText(file)
      .then((text) => {
        setSource(text);
        setFileName(file.name);
        changed();
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'That file could not be read.');
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
  const pages = preview?.pages ?? [];
  const selectedPage = pages.find((page) => page.selected);
  const typeHint = DRAWIO_IMPORT_TYPES.find((option) => option.value === type)?.hint;

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
        <h2 id={headingId} className="sql-import-title">Import draw.io</h2>
        <p className="sql-import-help">
          Choose a draw.io file (.drawio or .xml, compressed or not) or paste its XML. Shapes, containers and connectors keep their positions, sizes and
          labels; what has no equivalent here is drawn as a plain box and listed below. Nothing is saved until you create the diagram.
        </p>

        <div className="sql-import-field">
          <label htmlFor={fileId}>draw.io file</label>
          <input
            id={fileId}
            type="file"
            accept=".drawio,.xml,.dio,application/xml,text/xml"
            onChange={(e) => {
              chooseFile(e.target.files?.[0]);
              e.target.value = '';
            }}
          />
          {fileName !== undefined && <span className="sql-import-hint">Loaded {fileName}.</span>}
        </div>

        <div className="sql-import-field">
          <label htmlFor={sourceId}>draw.io XML</label>
          <textarea
            id={sourceId}
            ref={textarea}
            className="sql-import-text"
            rows={10}
            spellCheck={false}
            value={source}
            placeholder={'<mxfile>\n  <diagram name="Page-1">…</diagram>\n</mxfile>'}
            onChange={(e) => {
              setSource(e.target.value);
              setFileName(undefined);
              changed();
            }}
          />
        </div>

        <div className="sql-import-row">
          <div className="sql-import-field">
            <label htmlFor={typeId}>Diagram type</label>
            <select
              id={typeId}
              value={type}
              onChange={(e) => {
                setType(e.target.value as DrawioImportType);
                changed();
              }}
            >
              {DRAWIO_IMPORT_TYPES.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
            {typeHint !== undefined && <span className="sql-import-hint">{typeHint}</span>}
          </div>
          <div className="sql-import-field">
            <label htmlFor={titleId}>Diagram title</label>
            <input
              id={titleId}
              type="text"
              value={title}
              placeholder="The page name"
              onChange={(e) => {
                setTitle(e.target.value);
              }}
            />
          </div>
        </div>

        {error !== undefined && <p className="sql-import-error" role="alert">{error}</p>}

        {pages.length > 1 && (
          <div className="sql-import-field">
            <label htmlFor={pageId}>Page to import</label>
            <select
              id={pageId}
              value={String(selectedPage?.index ?? 1)}
              disabled={busy}
              onChange={(e) => {
                read(Number(e.target.value));
              }}
            >
              {pages.map((page) => (
                <option key={page.index} value={String(page.index)}>{page.name === '' ? `Page ${String(page.index)}` : page.name}</option>
              ))}
            </select>
            <span className="sql-import-hint">This file has {String(pages.length)} pages; one is imported at a time. Choosing another reads it again.</span>
          </div>
        )}

        {preview !== undefined && (
          <div className="sql-import-result" role="status">
            <p className="sql-import-summary">
              {summarise(preview)}
              {describeImportWarnings(preview.warnings)}
            </p>
            {preview.warnings.length > 0 && (
              <ul className="sql-import-warnings" aria-label="Import warnings">
                {preview.warnings.slice(0, MAX_WARNINGS_SHOWN).map((warning, index) => (
                  <li key={`${String(index)}-${warning.code}`}>{warning.message}</li>
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
          <button type="button" onClick={() => { read(); }} disabled={busy || source.trim() === ''}>
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
