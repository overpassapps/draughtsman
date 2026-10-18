/**
 * First run (design/atlas/login-admin.md; build-spec-v0.1.md section 7): with no users at all, this replaces the
 * login card until the first administrator exists. Closed for good the instant any user does (409 from
 * AuthEndpoints.FirstRun), so a caller who lands here late should sign in instead rather than retry.
 */
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { AuthError, MIN_PASSWORD_LENGTH, SESSION_NOT_KEPT_MESSAGE, firstRun, type AuthStatus, type FirstRunHints } from '../model/authClient.ts';
import { AuthBrandLogo } from '../theme/AuthBrandLogo.tsx';
import { useChromeBrand } from '../theme/useChromeBrand.ts';
import { SetupCodeHelp } from './SetupCodeHelp.tsx';

interface Props {
  /** See LoginPage: a refreshed status that still has no user means the browser kept no session. */
  onSignedIn: () => Promise<AuthStatus | undefined> | undefined;
  /** From `GET /api/auth/status` while first-run is open; absent from an older server or a test. */
  hints?: FirstRunHints | undefined;
}

export function FirstRunPage({ onSignedIn, hints }: Props) {
  const brand = useChromeBrand();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [setupCode, setSetupCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const emailField = useRef<HTMLInputElement>(null);

  useEffect(() => {
    emailField.current?.focus();
  }, []);

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    void firstRun(email, displayName, password, setupCode)
      .then(async () => {
        const status = await onSignedIn();
        if (status?.user === null) {
          // The administrator exists but the browser dropped the session cookie (forceHttps over plain http).
          setBusy(false);
          setError(SESSION_NOT_KEPT_MESSAGE);
        }
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof AuthError ? cause.message : 'Could not create the administrator account.');
      });
  };

  return (
    <main className="auth-page">
      <div className="auth-card">
        <AuthBrandLogo brand={brand} />
        <p className="auth-product">{brand?.productName ?? 'Draughtsman'}</p>
        <h1 className="auth-title">Create the first administrator</h1>
        {error !== undefined && (
          <p className="auth-banner" role="alert">
            {error}
          </p>
        )}
        <form onSubmit={submit}>
          <label htmlFor="first-run-email">Email</label>
          <input
            id="first-run-email"
            ref={emailField}
            type="email"
            autoComplete="username"
            value={email}
            disabled={busy}
            onChange={(e) => { setEmail(e.target.value); }}
            required
          />
          <label htmlFor="first-run-name">Display name</label>
          <input
            id="first-run-name"
            type="text"
            autoComplete="name"
            value={displayName}
            disabled={busy}
            onChange={(e) => { setDisplayName(e.target.value); }}
            required
          />
          <label htmlFor="first-run-password">Password</label>
          <input
            id="first-run-password"
            type="password"
            autoComplete="new-password"
            value={password}
            disabled={busy}
            minLength={MIN_PASSWORD_LENGTH}
            onChange={(e) => { setPassword(e.target.value); }}
            required
          />
          <p className="auth-hint">At least {MIN_PASSWORD_LENGTH} characters.</p>
          <label htmlFor="first-run-setup-code">Setup code</label>
          <input
            id="first-run-setup-code"
            type="text"
            autoComplete="off"
            value={setupCode}
            disabled={busy}
            onChange={(e) => { setSetupCode(e.target.value); }}
            required
          />
          <p className="auth-hint">Printed to the server log when Draughtsman starts.</p>
          <SetupCodeHelp hints={hints} />
          <button
            type="submit"
            disabled={busy || email.trim().length === 0 || displayName.trim().length === 0 || password.length < MIN_PASSWORD_LENGTH || setupCode.trim().length === 0}
          >
            {busy ? 'Creating…' : 'Create administrator'}
          </button>
        </form>
        {hints !== undefined && (
          <p className="auth-hint auth-data-folder">
            Draughtsman is running from <code>{hints.dataFolder}</code>.
          </p>
        )}
      </div>
    </main>
  );
}
