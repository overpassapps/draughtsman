/**
 * The question the leave guard asks when unsaved edits would be lost (design/atlas/editor-safety.md, editor/useLeaveGuard.ts):
 * Save, Discard or Cancel. It reuses the Import SQL dialog's chrome (`sql-import-*`) and its rules: modal, Tab kept
 * inside, every key kept from the canvas behind it. Escape is Cancel, the safe answer, and so is a click on the backdrop.
 */

import { useEffect, useId, useRef } from 'react';

export interface LeaveDialogProps {
  /** A save started from here is running: the buttons wait. */
  saving: boolean;
  /** Why the last save did not go through, when it did not. */
  error: string | undefined;
  onSave: () => void;
  onDiscard: () => void;
  onCancel: () => void;
}

export function LeaveDialog({ saving, error, onSave, onDiscard, onCancel }: LeaveDialogProps) {
  const headingId = useId();
  const save = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    save.current?.focus();
  }, []);

  return (
    <div
      className="sql-import-backdrop"
      role="presentation"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget && !saving) {
          onCancel();
        }
      }}
    >
      <div
        className="sql-import leave-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        onKeyDown={(e) => {
          e.stopPropagation();
          if (e.key === 'Escape' && !saving) {
            onCancel();
          }
          if (e.key === 'Tab') {
            const focusable = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled)'));
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
        <h2 id={headingId} className="sql-import-title">Unsaved changes</h2>
        <p className="sql-import-help">This diagram has changes that are not saved. Leaving now would lose them.</p>
        {error !== undefined && <p className="sql-import-error" role="alert">{error}</p>}
        <div className="sql-import-actions">
          <button type="button" onClick={onCancel} disabled={saving}>Cancel</button>
          <button type="button" onClick={onDiscard} disabled={saving}>Discard changes</button>
          <button type="button" ref={save} className="sql-import-create" onClick={onSave} disabled={saving}>{saving ? 'Saving…' : 'Save and leave'}</button>
        </div>
      </div>
    </div>
  );
}
