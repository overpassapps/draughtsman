/**
 * The page both kinds of password-reset link open (design/atlas/password-recovery.md): choose a new password. Outside the app
 * shell, like sign-in and first-run. The secret comes from the URL fragment and is taken out of the address bar at once
 * (model/resetToken.ts); it goes to the server in a request body, never an address. The password rule and the consequence
 * (signed out everywhere else) are said before the person types, a short password or a mismatch is said inline without spending the
 * link, and nothing signs the person in afterwards.
 */
import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import {
  MIN_PASSWORD_LENGTH,
  PASSWORD_TOO_SHORT_MESSAGE,
  ResetLinkError,
  completePasswordReset,
} from '../model/authClient.ts';
import { forgetResetToken, takeResetToken } from '../model/resetToken.ts';
import { AuthBrandLogo } from '../theme/AuthBrandLogo.tsx';
import { useChromeBrand } from '../theme/useChromeBrand.ts';

interface Props {
  /** Called when the person asks to go to sign in: the caller refreshes who is signed in and leaves this page. */
  onFinished: () => void;
  /** Whether the sign-in page can email a new link, so the refused-link message may say so. */
  selfService?: boolean;
}

type Phase = 'form' | 'done' | 'refused';

export function ResetPasswordPage({ onFinished, selfService = false }: Props) {
  const brand = useChromeBrand();
  const [token, setToken] = useState(() => takeResetToken());
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [phase, setPhase] = useState<Phase>('form');

  // A second link pasted into this tab changes only the fragment, so the page is not reloaded: take the new secret and start again.
  useEffect(() => {
    const onHashChange = (): void => {
      const next = takeResetToken();
      if (next !== undefined) {
        setToken(next);
        setPassword('');
        setConfirm('');
        setError(undefined);
        setBusy(false);
        setPhase('form');
      }
    };
    window.addEventListener('hashchange', onHashChange);
    return () => {
      window.removeEventListener('hashchange', onHashChange);
    };
  }, []);

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (busy || token === undefined) {
      return;
    }
    if (password.length < MIN_PASSWORD_LENGTH) {
      setError(PASSWORD_TOO_SHORT_MESSAGE);
      return;
    }
    if (password !== confirm) {
      setError('The two passwords do not match.');
      return;
    }
    setBusy(true);
    setError(undefined);
    void completePasswordReset(token, password)
      .then(() => {
        forgetResetToken();
        setPhase('done');
      })
      .catch((cause: unknown) => {
        setBusy(false);
        if (cause instanceof ResetLinkError && cause.linkRefused) {
          forgetResetToken();
          setPhase('refused');
          return;
        }
        setError(cause instanceof Error ? cause.message : 'Could not change the password.');
      });
  };

  const card = (title: string, body: ReactNode) => (
    <main className="auth-page">
      <div className="auth-card">
        <AuthBrandLogo brand={brand} />
        <p className="auth-product">{brand?.productName ?? 'Draughtsman'}</p>
        <h1 className="auth-title">{title}</h1>
        {body}
      </div>
    </main>
  );

  if (phase === 'done') {
    return card(
      'Password changed',
      <>
        <p className="auth-notice" role="status">Every other session was signed out. Sign in with your new password.</p>
        <button type="button" className="auth-primary" autoFocus onClick={onFinished}>Go to sign in</button>
      </>,
    );
  }

  if (phase === 'refused') {
    return card(
      'Link not valid',
      <>
        <p className="auth-banner" role="alert">This reset link is not valid. It may have expired or already been used. Ask for a new one.</p>
        {selfService && <p className="auth-hint">The sign-in page can email you a new link: choose “Forgot password?”.</p>}
        <button type="button" className="auth-primary" autoFocus onClick={onFinished}>Go to sign in</button>
      </>,
    );
  }

  if (token === undefined) {
    return card(
      'Choose a new password',
      <>
        <p className="auth-notice" role="status">This page opens from a reset link. Open the link you were sent, or ask for a new one.</p>
        <button type="button" className="auth-primary" autoFocus onClick={onFinished}>Go to sign in</button>
      </>,
    );
  }

  return card(
    'Choose a new password',
    <>
      {error !== undefined && (
        <p className="auth-banner" role="alert">
          {error}
        </p>
      )}
      <form onSubmit={submit}>
        <label htmlFor="reset-password">New password</label>
        <input
          id="reset-password"
          type="password"
          autoComplete="new-password"
          autoFocus
          value={password}
          disabled={busy}
          aria-describedby="reset-password-rule"
          onChange={(e) => { setPassword(e.target.value); setError(undefined); }}
          required
        />
        <span id="reset-password-rule" className="auth-hint">At least {MIN_PASSWORD_LENGTH} characters.</span>
        <label htmlFor="reset-password-confirm">Confirm new password</label>
        <input
          id="reset-password-confirm"
          type="password"
          autoComplete="new-password"
          value={confirm}
          disabled={busy}
          onChange={(e) => { setConfirm(e.target.value); setError(undefined); }}
          required
        />
        <p className="auth-hint" id="reset-password-effect">Saving signs you out everywhere else. Your API tokens keep working.</p>
        <button type="submit" disabled={busy || password.length === 0 || confirm.length === 0}>
          {busy ? 'Saving…' : 'Save new password'}
        </button>
      </form>
    </>,
  );
}
