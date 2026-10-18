/**
 * The fallback for File > Export Mermaid (design/atlas/mermaid-export.md): the export itself worked but the browser
 * would not let the page write to the clipboard, so the text is shown, selected, to copy by hand. It reuses the Import
 * SQL dialog's styles (`sql-import-*`) and its rules: modal, Tab kept inside, every key kept from the canvas behind it,
 * Escape closes.
 */

import { useEffect, useId, useRef } from 'react';

export interface MermaidTextDialogProps {
  text: string;
  onClose: () => void;
  /** What Mermaid could only approximate, listed under the text (QA P8); none for a faithful export. */
  notes?: readonly string[];
  /** Replaces the opening sentence, for an export that worked (a saved file, a copy) but was lossy. */
  intro?: string;
}

export function MermaidTextDialog({ text, onClose, notes = [], intro }: MermaidTextDialogProps) {
  const headingId = useId();
  const textId = useId();
  const textarea = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    textarea.current?.focus();
    textarea.current?.select();
  }, []);

  return (
    <div
      className="sql-import-backdrop"
      role="presentation"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) {
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
          e.stopPropagation();
          if (e.key === 'Escape') {
            onClose();
          }
          if (e.key === 'Tab') {
            const focusable = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('textarea, button:not(:disabled)'));
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
        <h2 id={headingId} className="sql-import-title">Export Mermaid</h2>
        <p className="sql-import-help">
          {intro ?? 'The browser would not copy this for you. It is selected below: copy it, then paste it into a README or any page that renders Mermaid.'}
        </p>
        {notes.length > 0 && (
          <ul className="sql-import-warnings" aria-label="Simplified in the Mermaid export">
            {notes.map((note, index) => (
              <li key={index}>{note}</li>
            ))}
          </ul>
        )}
        <div className="sql-import-field">
          <label htmlFor={textId}>Mermaid text</label>
          <textarea id={textId} ref={textarea} className="sql-import-text" rows={16} spellCheck={false} readOnly value={text} />
        </div>
        <div className="sql-import-actions">
          <button type="button" className="sql-import-create" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}
