/**
 * "Import SQL": a paste box for CREATE TABLE / ALTER TABLE DDL that draws the schema (docs/er-entities.md, docs/api.md
 * `POST /api/import/sql`). The home screen's Import SQL button and the editor's File menu action both open it. It is a
 * two-step dialog: "Read schema" imports and lays the text out without saving anything and shows what it found (tables,
 * relationships) and everything it skipped as warnings, so a wrong paste is caught before a document exists; "Create
 * diagram" then saves it as a new document and hands its id to `onCreated`. Changing the text or a setting after a read
 * discards that read, so what is created is always what the box currently says.
 */

import { useEffect, useId, useRef, useState } from 'react';
import {
  SQL_DIALECTS,
  createImportedDocument,
  previewSqlImport,
  type SqlDialectChoice,
  type SqlImportPreview,
  type SqlImportType,
} from '../model/sqlImport.ts';

/** Warnings past this many are summarised, so a script with hundreds of skipped statements does not bury the buttons. */
const MAX_WARNINGS_SHOWN = 100;

const runNow = (go: () => void): void => {
  go();
};

type Status = { kind: 'idle' } | { kind: 'reading' } | { kind: 'ready'; preview: SqlImportPreview } | { kind: 'creating'; preview: SqlImportPreview };

export interface ImportSqlDialogProps {
  onClose: () => void;
  /** Called with the new document's id once it is saved; the caller opens it. It must not be able to refuse: the asking is `confirmLeave`. */
  onCreated: (id: string) => void;
  /**
   * Runs `go` when the caller is happy for the diagram to be created (default: at once). A caller whose page may hold
   * unsaved edits asks the user here, before anything is saved, and calls `go` only on Save or Discard.
   */
  confirmLeave?: (go: () => void) => void;
}

export function ImportSqlDialog({ onClose, onCreated, confirmLeave = runNow }: ImportSqlDialogProps) {
  const headingId = useId();
  const sqlId = useId();
  const titleId = useId();
  const dialectId = useId();
  const typeId = useId();
  const tagsId = useId();
  const impliedId = useId();
  const [sql, setSql] = useState('');
  const [title, setTitle] = useState('');
  const [dialect, setDialect] = useState<SqlDialectChoice>('auto');
  const [type, setType] = useState<SqlImportType>('architecture');
  const [tags, setTags] = useState(true);
  const [implied, setImplied] = useState(true);
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
    previewSqlImport(sql, { dialect, type, title, tags, implied })
      .then((preview) => {
        setStatus({ kind: 'ready', preview });
      })
      .catch((cause: unknown) => {
        setStatus({ kind: 'idle' });
        setError(cause instanceof Error ? cause.message : 'The SQL could not be read.');
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
  const empty = preview?.tables === 0;

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
        <h2 id={headingId} className="sql-import-title">Import SQL</h2>
        <p className="sql-import-help">
          Paste CREATE TABLE statements (and any ALTER TABLE … ADD CONSTRAINT) from SQL Server, PostgreSQL, MySQL or SQLite. Each table
          becomes a box and each foreign key a relationship. Nothing is saved until you create the diagram.
        </p>

        <div className="sql-import-field">
          <label htmlFor={sqlId}>SQL</label>
          <textarea
            id={sqlId}
            ref={textarea}
            className="sql-import-text"
            rows={12}
            spellCheck={false}
            value={sql}
            placeholder={'create table Customers (\n    Id int not null primary key,\n    Name nvarchar(100) not null\n)'}
            onChange={(e) => {
              setSql(e.target.value);
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
              placeholder="Imported schema"
              onChange={(e) => {
                setTitle(e.target.value);
              }}
            />
          </div>
          <div className="sql-import-field">
            <label htmlFor={dialectId}>Dialect</label>
            <select
              id={dialectId}
              value={dialect}
              onChange={(e) => {
                setDialect(e.target.value as SqlDialectChoice);
                changed();
              }}
            >
              {SQL_DIALECTS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </div>
          <div className="sql-import-field">
            <label htmlFor={typeId}>Diagram type</label>
            <select
              id={typeId}
              value={type}
              onChange={(e) => {
                setType(e.target.value as SqlImportType);
                changed();
              }}
            >
              <option value="architecture">Architecture</option>
              <option value="freeform">Freeform</option>
            </select>
          </div>
          <div className="sql-import-field">
            <label htmlFor={tagsId}>
              <input
                id={tagsId}
                type="checkbox"
                checked={tags}
                onChange={(e) => {
                  setTags(e.target.checked);
                  changed();
                }}
              />{' '}
              Colour tables by their [TAG] comment
            </label>
          </div>
          <div className="sql-import-field">
            <label htmlFor={impliedId}>
              <input
                id={impliedId}
                type="checkbox"
                checked={implied}
                onChange={(e) => {
                  setImplied(e.target.checked);
                  changed();
                }}
              />{' '}
              Draw relationships noted in comments as dashed lines
            </label>
          </div>
        </div>

        {error !== undefined && <p className="sql-import-error" role="alert">{error}</p>}

        {preview !== undefined && (
          <div className="sql-import-result" role="status">
            <p className="sql-import-summary">
              {empty
                ? 'No tables were found in that SQL.'
                : `Found ${String(preview.tables)} ${preview.tables === 1 ? 'table' : 'tables'} and ${String(preview.relationships)} ${preview.relationships === 1 ? 'relationship' : 'relationships'}.`}
              {preview.warnings.length > 0 && ` ${String(preview.warnings.length)} ${preview.warnings.length === 1 ? 'thing was' : 'things were'} skipped or ${preview.warnings.length === 1 ? 'needs' : 'need'} a look:`}
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
          <button type="button" onClick={read} disabled={busy || sql.trim() === ''}>
            {status.kind === 'reading' ? 'Reading…' : 'Read schema'}
          </button>
          <button type="button" className="sql-import-create" onClick={create} disabled={busy || preview === undefined || empty}>
            {status.kind === 'creating' ? 'Creating…' : 'Create diagram'}
          </button>
        </div>
      </div>
    </div>
  );
}
