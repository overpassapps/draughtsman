/**
 * The signed-in user's own account page (AU-03; design/atlas/login-admin.md, "Account, tokens and the security log"):
 * change your own password. The current password is the confirmation, so there is no extra step; the server ends every
 * other session of the account (this one stays) and leaves API tokens alone. The "repeat" field never reaches the server.
 */
import { useState, type FormEvent } from 'react';
import { AuthError, MIN_PASSWORD_LENGTH, changePassword } from '../model/authClient.ts';

export function AccountPage() {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState(false);

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (busy) {
      return;
    }
    setDone(false);
    if (next.length < MIN_PASSWORD_LENGTH) {
      setError(`The new password must be at least ${String(MIN_PASSWORD_LENGTH)} characters.`);
      return;
    }
    if (next !== repeat) {
      setError('The two new passwords do not match.');
      return;
    }
    setBusy(true);
    setError(undefined);
    void changePassword(current, next)
      .then(() => {
        setCurrent('');
        setNext('');
        setRepeat('');
        setDone(true);
      })
      .catch((cause: unknown) => {
        setError(cause instanceof AuthError ? cause.message : 'Could not change the password.');
      })
      .finally(() => {
        setBusy(false);
      });
  };

  return (
    <div className="admin-page">
      <h1 className="admin-title">Account</h1>
      <h2 className="admin-subtitle">Change password</h2>
      <p className="admin-intro">Changing your password signs you out everywhere else. Your API tokens keep working.</p>
      {error !== undefined && (
        <p className="admin-banner" role="alert">
          {error}
        </p>
      )}
      {done && (
        <p className="admin-notice" role="status">
          Password changed. Your other sessions were signed out.
        </p>
      )}
      <form className="admin-form account-form" onSubmit={submit}>
        <div>
          <label htmlFor="account-current">Current password</label>
          <input id="account-current" type="password" autoComplete="current-password" value={current} disabled={busy} onChange={(e) => { setCurrent(e.target.value); }} required />
        </div>
        <div>
          <label htmlFor="account-new">New password</label>
          <input id="account-new" type="password" autoComplete="new-password" value={next} disabled={busy} onChange={(e) => { setNext(e.target.value); }} required />
        </div>
        <div>
          <label htmlFor="account-repeat">Repeat new password</label>
          <input id="account-repeat" type="password" autoComplete="new-password" value={repeat} disabled={busy} onChange={(e) => { setRepeat(e.target.value); }} required />
        </div>
        <button type="submit" disabled={busy || current.length === 0 || next.length === 0 || repeat.length === 0}>
          Change password
        </button>
      </form>
    </div>
  );
}
