/**
 * The sign-in screen (design/atlas/login-admin.md): one centred card on the chrome theme's surface with the
 * instance's own logo and product name, never Overpass's. The error band is the server's own generic message either
 * way ("Email or password is incorrect.") - AuthEndpoints.InvalidCredentials never reveals which half was wrong.
 * OIDC and "keep me signed in" are out of scope for this build (the launch brief); sessions are long-lived and
 * revocable, not renewed by a checkbox.
 */
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { AuthError, SESSION_NOT_KEPT_MESSAGE, login, requestPasswordReset, type AuthStatus, type PasswordResetHints } from '../model/authClient.ts';
import { clearSessionEndedNotice, currentSessionEndedNotice, sessionEndedMessage } from '../model/sessionEnded.ts';
import { AuthBrandLogo } from '../theme/AuthBrandLogo.tsx';
import { useChromeBrand } from '../theme/useChromeBrand.ts';

interface Props {
  /**
   * Called once the server has accepted the sign-in. It may return the refreshed status: one that still has no user
   * means the browser did not keep the session (forceHttps over plain http), and the form says so.
   */
  onSignedIn: () => Promise<AuthStatus | undefined> | undefined;
  /** App sets this when a first run succeeded but the browser kept no session, so this page opens already explaining why. */
  sessionNotKept?: boolean;
  /** From `GET /api/auth/status`: whether "Forgot password?" is offered. Absent means it is not. */
  passwordReset?: PasswordResetHints;
}

/** The card's three faces: sign in, ask for a reset link by email, and the answer to that request. */
type Step = 'signin' | 'forgot' | 'sent';

export function LoginPage({ onSignedIn, passwordReset, sessionNotKept = false }: Props) {
  const brand = useChromeBrand();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  // App learns a first run kept no session only after this page has replaced the first-run one, so `sessionNotKept`
  // arrives as a prop change as often as at mount. It stands in for an error until the person's next attempt.
  const [attempted, setAttempted] = useState(false);
  const shownError = error ?? (sessionNotKept && !attempted ? SESSION_NOT_KEPT_MESSAGE : undefined);
  const [step, setStep] = useState<Step>('signin');
  const [sentMessage, setSentMessage] = useState('');
  const emailField = useRef<HTMLInputElement>(null);
  const forgotEmailField = useRef<HTMLInputElement>(null);
  const selfService = passwordReset?.selfService === true;
  // Why the person is here when they were not a moment ago (S9): read once, so it stays up while they type.
  const [ended] = useState(() => {
    const notice = currentSessionEndedNotice();
    return notice === undefined ? undefined : sessionEndedMessage(notice);
  });

  useEffect(() => {
    emailField.current?.focus();
  }, []);

  useEffect(() => {
    if (step === 'forgot') {
      forgotEmailField.current?.focus();
    }
  }, [step]);

  const askForLink = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    void requestPasswordReset(email.trim())
      .then((message) => {
        setBusy(false);
        setSentMessage(message);
        setStep('sent');
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof AuthError ? cause.message : 'Could not ask for a reset link.');
      });
  };

  const backToSignIn = (): void => {
    setError(undefined);
    setStep('signin');
  };

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (busy) {
      return;
    }
    setBusy(true);
    setAttempted(true);
    setError(undefined);
    void login(email, password)
      .then(async () => {
        clearSessionEndedNotice();
        const status = await onSignedIn();
        if (status?.user === null) {
          // The server said 200 and the next status check still shows nobody: the cookie was dropped, so this form
          // stays on screen. Without this it would sit on "Signing in…" for ever.
          setBusy(false);
          setError(SESSION_NOT_KEPT_MESSAGE);
        }
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof AuthError ? cause.message : 'Could not sign in.');
      });
  };

  const header = (title: string) => (
    <>
      <AuthBrandLogo brand={brand} />
      <p className="auth-product">{brand?.productName ?? 'Draughtsman'}</p>
      <h1 className="auth-title">{title}</h1>
    </>
  );

  if (step === 'sent') {
    return (
      <main className="auth-page">
        <div className="auth-card">
          {header('Check your email')}
          <p className="auth-notice" role="status" data-testid="reset-requested">
            {sentMessage}
          </p>
          <p className="auth-hint">If nothing arrives, look in your spam folder, or ask your administrator for a reset link.</p>
          <button type="button" className="auth-secondary" onClick={backToSignIn}>Back to sign in</button>
        </div>
      </main>
    );
  }

  if (step === 'forgot') {
    return (
      <main className="auth-page">
        <div className="auth-card">
          {header('Reset your password')}
          <p className="auth-hint">Enter the email address you sign in with. If an account matches, we will email you a link.</p>
          {error !== undefined && (
            <p className="auth-banner" role="alert">
              {error}
            </p>
          )}
          <form
            onSubmit={askForLink}
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                backToSignIn();
              }
            }}
          >
            <label htmlFor="forgot-email">Email</label>
            <input
              id="forgot-email"
              ref={forgotEmailField}
              type="email"
              autoComplete="username"
              value={email}
              disabled={busy}
              onChange={(e) => { setEmail(e.target.value); }}
              required
            />
            <button type="submit" disabled={busy || email.trim().length === 0}>
              {busy ? 'Sending…' : 'Email me a link'}
            </button>
          </form>
          <button type="button" className="auth-secondary" onClick={backToSignIn}>Back to sign in</button>
        </div>
      </main>
    );
  }

  return (
    <main className="auth-page">
      <div className="auth-card">
        {header('Sign in')}
        {ended !== undefined && (
          <p className="auth-notice" role="status" data-testid="session-ended">
            {ended}
          </p>
        )}
        {shownError !== undefined && (
          <p className="auth-banner" role="alert">
            {shownError}
          </p>
        )}
        <form onSubmit={submit}>
          <label htmlFor="login-email">Email</label>
          <input
            id="login-email"
            ref={emailField}
            type="email"
            autoComplete="username"
            value={email}
            disabled={busy}
            onChange={(e) => { setEmail(e.target.value); }}
            required
          />
          <label htmlFor="login-password">Password</label>
          <input
            id="login-password"
            type="password"
            autoComplete="current-password"
            value={password}
            disabled={busy}
            onChange={(e) => { setPassword(e.target.value); }}
            required
          />
          <button type="submit" disabled={busy || email.trim().length === 0 || password.length === 0}>
            {busy ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
        {selfService ? (
          <button
            type="button"
            className="auth-link-button"
            onClick={() => {
              setError(undefined);
              setStep('forgot');
            }}
          >
            Forgot password?
          </button>
        ) : (
          <p className="auth-hint auth-forgot-hint" data-testid="forgot-hint">
            Forgot your password? Ask your administrator for a reset link.
          </p>
        )}
      </div>
    </main>
  );
}
