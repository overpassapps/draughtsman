/**
 * "Import Visio": a Visio (.vsdx) drawing read into a new diagram (docs/api.md `POST /api/import/visio`,
 * docs/moving-from-lucidchart-visio-drawio.md, design/atlas/visio-import.md). The home screen's Import Visio button and the editor's File menu
 * action both open it. It is ImportDrawioDialog's twin: "Read diagram" imports without saving anything and shows what it found with every
 * guess, skip and approximation as a warning, so a wrong file is caught before a document exists; "Create diagram" then saves it as a new
 * document and hands its id to `onCreated`. Changing the file or the type after a read discards that read; the title is applied when the
 * diagram is created.
 *
 * A .vsdx is binary, so there is no paste box: the person chooses the file, and the file itself is what is sent. Positions arrive with the
 * file, so like the draw.io dialog this one never lays the diagram out. A file with several pages imports one at a time: the first, unless
 * the person picks another, which reads again at once. Lucidchart's export to Visio makes the same kind of file, so it comes in the same way.
 */

import { useEffect, useId, useRef, useState } from 'react';
import { createImportedDocument } from '../model/sqlImport.ts';
import { previewVisioImport, VISIO_IMPORT_TYPES, type VisioImportPreview, type VisioImportType } from '../model/visioImport.ts';
import { describeImportWarnings } from './importWarnings.ts';

/** Warnings past this many are summarised, so a large file does not bury the buttons. */
const MAX_WARNINGS_SHOWN = 100;

const runNow = (go: () => void): void => {
  go();
};

type Status = { kind: 'idle' } | { kind: 'reading' } | { kind: 'ready'; preview: VisioImportPreview } | { kind: 'creating'; preview: VisioImportPreview };

export interface ImportVisioDialogProps {
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

function summarise(preview: VisioImportPreview): string {
  const { document } = preview;
  if (document.nodes.length === 0) {
    return 'Nothing was found on that page.';
  }
  return `Found ${plural(document.nodes.length, 'shape', 'shapes')} and ${plural(document.edges.length, 'connector', 'connectors')}.`;
}

function describeSize(bytes: number): string {
  return bytes < 1_000_000 ? `${String(Math.max(1, Math.round(bytes / 1000)))} KB` : `${(bytes / 1_000_000).toFixed(1)} MB`;
}

export function ImportVisioDialog({ onClose, onCreated, confirmLeave = runNow }: ImportVisioDialogProps) {
  const headingId = useId();
  const fileId = useId();
  const titleId = useId();
  const typeId = useId();
  const pageId = useId();
  const [file, setFile] = useState<File | undefined>(undefined);
  const [title, setTitle] = useState('');
  const [type, setType] = useState<VisioImportType>('flowchart');
  const [status, setStatus] = useState<Status>({ kind: 'idle' });
  const [error, setError] = useState<string | undefined>(undefined);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    fileInput.current?.focus();
  }, []);

  const changed = (): void => {
    setStatus({ kind: 'idle' });
    setError(undefined);
  };

  const read = (page?: number): void => {
    if (file === undefined) {
      return;
    }
    setStatus({ kind: 'reading' });
    setError(undefined);
    previewVisioImport(file, { type, title, ...(page === undefined ? {} : { page }) })
      .then((preview) => {
        setStatus({ kind: 'ready', preview });
      })
      .catch((cause: unknown) => {
        setStatus({ kind: 'idle' });
        setError(cause instanceof Error ? cause.message : 'The Visio file could not be read.');
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
  const typeHint = VISIO_IMPORT_TYPES.find((option) => option.value === type)?.hint;

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
            const focusable = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('input, select, button:not(:disabled)'));
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
        <h2 id={headingId} className="sql-import-title">Import Visio</h2>
        <p className="sql-import-help">
          Choose a Visio drawing (.vsdx, from Visio 2013 or later, or exported to Visio from Lucidchart). Shapes, groups and connectors keep their positions, sizes
          and text; what has no equivalent here is drawn as a plain box and listed below. An older .vsd must be saved as .vsdx in Visio first. Nothing is saved
          until you create the diagram.
        </p>

        <div className="sql-import-field">
          <label htmlFor={fileId}>Visio file</label>
          <input
            id={fileId}
            ref={fileInput}
            type="file"
            accept=".vsdx,.vsdm,application/vnd.ms-visio.drawing,application/vnd.ms-visio.drawing.macroenabled.12"
            onChange={(e) => {
              const chosen = e.target.files?.[0];
              if (chosen !== undefined) {
                setFile(chosen);
                changed();
              }
              e.target.value = '';
            }}
          />
          {file !== undefined && <span className="sql-import-hint">Loaded {file.name} ({describeSize(file.size)}).</span>}
        </div>

        <div className="sql-import-row">
          <div className="sql-import-field">
            <label htmlFor={typeId}>Diagram type</label>
            <select
              id={typeId}
              value={type}
              onChange={(e) => {
                setType(e.target.value as VisioImportType);
                changed();
              }}
            >
              {VISIO_IMPORT_TYPES.map((option) => (
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
          <button type="button" onClick={() => { read(); }} disabled={busy || file === undefined}>
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
