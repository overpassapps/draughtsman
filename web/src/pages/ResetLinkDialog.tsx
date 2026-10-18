/**
 * The administrator's "Create reset link" dialog (design/atlas/password-recovery.md). Two states in one modal: a confirmation that
 * says what creating the link does, then the link itself with Copy, its expiry and a warning that anyone holding it can set the
 * person's password until then. The secret exists only in this dialog's state: the server kept a hash, so closing it is final.
 * Reuses the Import SQL dialog's chrome and its rules: modal, Tab kept inside, Escape closes.
 */
import { useEffect, useId, useRef, useState } from 'react';
import { DateTime } from '../app/DateTime.tsx';
import { AuthError, createResetLink, resetLinkOf, type AuthUser, type CreatedResetLink } from '../model/authClient.ts';

interface Props {
  user: AuthUser;
  onClose: () => void;
}

type Stage = { kind: 'confirm' } | { kind: 'working' } | { kind: 'created'; created: CreatedResetLink; link: string };

/**
 * Copies text with the async clipboard (a secure context: https, or localhost). Where that is refused or absent it selects the
 * field instead and returns false, so the person can copy by hand: the dialog says so.
 */
async function copyText(text: string, field: HTMLInputElement | null): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    field?.focus();
    field?.select();
    return false;
  }
}

export function ResetLinkDialog({ user, onClose }: Props) {
  const headingId = useId();
  const [stage, setStage] = useState<Stage>({ kind: 'confirm' });
  const [error, setError] = useState<string>();
  const [copied, setCopied] = useState<'no' | 'yes' | 'failed'>('no');
  const primary = useRef<HTMLButtonElement>(null);
  const linkField = useRef<HTMLInputElement>(null);
  const hours = stage.kind === 'created' ? stage.created.expiresInHours : undefined;

  useEffect(() => {
    primary.current?.focus();
  }, [stage.kind]);

  useEffect(() => {
    if (copied !== 'yes') {
      return;
    }
    const timer = window.setTimeout(() => {
      setCopied('no');
    }, 2000);
    return () => {
      window.clearTimeout(timer);
    };
  }, [copied]);

  const create = (): void => {
    setStage({ kind: 'working' });
    setError(undefined);
    void createResetLink(user.id)
      .then((created) => {
        setStage({ kind: 'created', created, link: resetLinkOf(created, window.location.origin) });
      })
      .catch((cause: unknown) => {
        setStage({ kind: 'confirm' });
        setError(cause instanceof AuthError ? cause.message : 'Could not create the reset link.');
      });
  };

  const copy = (link: string): void => {
    void copyText(link, linkField.current).then((ok) => {
      setCopied(ok ? 'yes' : 'failed');
    });
  };

  const busy = stage.kind === 'working';

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
        className="sql-import reset-link-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        onKeyDown={(e) => {
          e.stopPropagation();
          if (e.key === 'Escape' && !busy) {
            onClose();
          }
          if (e.key === 'Tab') {
            const focusable = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('input, button:not(:disabled)'));
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
        <h2 id={headingId} className="sql-import-title">
          {stage.kind === 'created' ? `Reset link for ${user.displayName}` : `Create a reset link for ${user.displayName}?`}
        </h2>

        {stage.kind !== 'created' && (
          <>
            <ul className="reset-link-facts">
              <li>{user.displayName} chooses a new password with it. You never see or choose it.</li>
              <li>It works once, and expires after the time shown when it is created.</li>
              <li>Creating it ends any earlier reset link for {user.displayName}.</li>
              <li>Using it signs {user.displayName} out everywhere else. Their API tokens keep working.</li>
            </ul>
            {error !== undefined && <p className="sql-import-error" role="alert">{error}</p>}
            <div className="sql-import-actions">
              <button type="button" onClick={onClose} disabled={busy}>Cancel</button>
              <button type="button" className="sql-import-create" ref={primary} onClick={create} disabled={busy}>
                {busy ? 'Creating…' : 'Create link'}
              </button>
            </div>
          </>
        )}

        {stage.kind === 'created' && (
          <>
            <p className="reset-link-warning" role="alert">
              Anyone who has this link can set {user.displayName}’s password until it expires. Send it only to {user.displayName}, by a
              channel you trust. It is shown once and cannot be shown again.
            </p>
            <div className="sql-import-field">
              <label htmlFor={`${headingId}-link`}>Reset link</label>
              <div className="reset-link-row">
                <input
                  id={`${headingId}-link`}
                  ref={linkField}
                  className="reset-link-field"
                  type="text"
                  readOnly
                  value={stage.link}
                  onFocus={(e) => { e.currentTarget.select(); }}
                />
                <button type="button" className="sql-import-create" ref={primary} onClick={() => { copy(stage.link); }}>
                  {copied === 'yes' ? 'Copied' : 'Copy link'}
                </button>
              </div>
            </div>
            <p className="sql-import-help">
              Expires <DateTime iso={stage.created.expiresAt} /> ({String(hours)} hours from now). Works once.
            </p>
            <p className="sql-import-help" role="status">
              {copied === 'yes' && 'Link copied to the clipboard.'}
              {copied === 'failed' && 'Could not copy automatically: the link is selected, so press Ctrl+C (Cmd+C on a Mac).'}
            </p>
            <div className="sql-import-actions">
              <button type="button" onClick={onClose}>Close</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
