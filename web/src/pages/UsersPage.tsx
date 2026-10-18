/**
 * The minimal admin Users page (build-spec-v0.1.md section 7; design/atlas/login-admin.md's Users row, scoped down
 * for this task): list, add a local user, change role, reset password or create a one-time reset link (the person chooses
 * their own password with it), deactivate/reactivate. `PATCH /api/users/{id}`
 * already refuses an admin who tries to deactivate or demote themselves (400); the controls for the caller's own row
 * are grey/omitted here too rather than offering a button that can only fail.
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { DateTime } from '../app/DateTime.tsx';
import { ResetLinkDialog } from './ResetLinkDialog.tsx';
import {
  AuthError,
  MIN_PASSWORD_LENGTH,
  PASSWORD_TOO_SHORT_MESSAGE,
  createUser,
  listUsers,
  updateUser,
  type AuthUser,
  type UserRole,
} from '../model/authClient.ts';

interface Props {
  currentUser: AuthUser;
}

type ListState = { kind: 'loading' } | { kind: 'ok'; users: AuthUser[] } | { kind: 'error'; message: string };

export function UsersPage({ currentUser }: Props) {
  const [state, setState] = useState<ListState>({ kind: 'loading' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [newEmail, setNewEmail] = useState('');
  const [newName, setNewName] = useState('');
  const [newRole, setNewRole] = useState<UserRole>('Editor');
  const [newPassword, setNewPassword] = useState('');
  const [resetFor, setResetFor] = useState<string>();
  const [resetValue, setResetValue] = useState('');
  // Said beside the field it is about (the page banner is easy to miss under a long table), with the server's own wording.
  const [resetError, setResetError] = useState<string>();
  // The person whose reset link is being created (the dialog owns the rest), and the button to give focus back to when it closes.
  const [linkFor, setLinkFor] = useState<AuthUser>();
  const linkOpener = useRef<HTMLButtonElement | null>(null);

  const reload = useCallback((): Promise<void> => {
    return listUsers()
      .then((users) => {
        setState({ kind: 'ok', users });
        setBusy(false);
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setState({ kind: 'error', message: cause instanceof Error ? cause.message : 'Unknown error' });
      });
  }, []);

  const isAdmin = currentUser.role === 'Admin';

  useEffect(() => {
    // An Editor is shown "not allowed" and sends nothing: the server would answer 403 to the list anyway.
    if (isAdmin) {
      void reload();
    }
  }, [isAdmin, reload]);

  if (!isAdmin) {
    return <NotAllowed />;
  }

  const users = state.kind === 'ok' ? state.users : [];

  const addUser = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (busy) {
      return;
    }
    if (newPassword.length < MIN_PASSWORD_LENGTH) {
      // The button is no longer silently disabled by length: say what is wrong, in the words the server would use.
      setError(PASSWORD_TOO_SHORT_MESSAGE);
      return;
    }
    setBusy(true);
    setError(undefined);
    void createUser({ email: newEmail, displayName: newName, role: newRole, password: newPassword })
      .then(() => {
        setNewEmail('');
        setNewName('');
        setNewRole('Editor');
        setNewPassword('');
        return reload();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof AuthError ? cause.message : 'Could not add the user.');
      });
  };

  const changeRole = (user: AuthUser, role: UserRole): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    void updateUser(user.id, { role })
      .then(() => reload())
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof AuthError ? cause.message : 'Could not change the role.');
      });
  };

  const toggleActive = (user: AuthUser): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    void updateUser(user.id, { active: !user.active })
      .then(() => reload())
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof AuthError ? cause.message : 'Could not update the user.');
      });
  };

  const beginReset = (user: AuthUser): void => {
    setResetFor(user.id);
    setResetValue('');
    setResetError(undefined);
  };

  const commitReset = (user: AuthUser): void => {
    if (busy) {
      return;
    }
    if (resetValue.length < MIN_PASSWORD_LENGTH) {
      setResetError(PASSWORD_TOO_SHORT_MESSAGE);
      return;
    }
    setResetError(undefined);
    setBusy(true);
    setError(undefined);
    void updateUser(user.id, { password: resetValue })
      .then(() => {
        setResetFor(undefined);
        return reload();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setResetError(cause instanceof AuthError ? cause.message : 'Could not reset the password.');
      });
  };

  return (
    <div className="admin-page">
      <h1 className="admin-title">Users</h1>
      {error !== undefined && (
        <p className="admin-banner" role="alert">
          {error}
        </p>
      )}

      {state.kind === 'loading' && <p className="page-message">Loading users…</p>}
      {state.kind === 'error' && <p className="page-message" role="alert">Cannot list users: {state.message}</p>}
      {state.kind === 'ok' && (
        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead>
              <tr>
                <th scope="col">Email</th>
                <th scope="col">Name</th>
                <th scope="col">Role</th>
                <th scope="col">Status</th>
                <th scope="col">Last sign-in</th>
                <th scope="col"><span className="visually-hidden">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => {
                const self = user.id === currentUser.id;
                const resetting = resetFor === user.id;
                return (
                  <tr key={user.id}>
                    <td>{user.email}</td>
                    <td>{user.displayName}</td>
                    <td>
                      <label className="visually-hidden" htmlFor={`role-${user.id}`}>Role for {user.displayName}</label>
                      <select
                        id={`role-${user.id}`}
                        value={user.role}
                        disabled={busy || self}
                        onChange={(e) => { changeRole(user, e.target.value as UserRole); }}
                      >
                        <option value="Admin">Admin</option>
                        <option value="Editor">Editor</option>
                      </select>
                    </td>
                    <td>{user.active ? 'Active' : 'Deactivated'}</td>
                    <td><DateTime iso={user.lastSignInAt} /></td>
                    <td className="admin-row-actions">
                      {resetting ? (
                        <span className="admin-reset">
                          <label htmlFor={`reset-${user.id}`}>New password for {user.displayName}</label>
                          <span className="admin-reset-row">
                            <input
                              id={`reset-${user.id}`}
                              type="password"
                              autoComplete="new-password"
                              value={resetValue}
                              aria-describedby={`reset-hint-${user.id}`}
                              aria-invalid={resetError !== undefined}
                              onChange={(e) => { setResetValue(e.target.value); setResetError(undefined); }}
                              onKeyDown={(e) => {
                                if (e.key === 'Enter') { commitReset(user); }
                                if (e.key === 'Escape') { setResetFor(undefined); }
                              }}
                            />
                            <button type="button" onClick={() => { commitReset(user); }} disabled={busy || resetValue.length === 0}>
                              Save
                            </button>
                            <button type="button" onClick={() => { setResetFor(undefined); }} disabled={busy}>Cancel</button>
                          </span>
                          <span id={`reset-hint-${user.id}`} className={resetError === undefined ? 'admin-hint' : 'admin-hint admin-hint-error'} role={resetError === undefined ? undefined : 'alert'}>
                            {resetError ?? `At least ${String(MIN_PASSWORD_LENGTH)} characters. ${user.displayName} is signed out everywhere and their API tokens stop working.`}
                          </span>
                        </span>
                      ) : (
                        <button type="button" onClick={() => { beginReset(user); }} disabled={busy}>Reset password</button>
                      )}
                      {!self && user.active && !resetting && (
                        <button
                          type="button"
                          onClick={(e) => {
                            linkOpener.current = e.currentTarget;
                            setLinkFor(user);
                          }}
                          disabled={busy}
                        >
                          Create reset link
                        </button>
                      )}
                      {!self && (
                        <button type="button" onClick={() => { toggleActive(user); }} disabled={busy}>
                          {user.active ? 'Deactivate' : 'Reactivate'}
                        </button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {linkFor !== undefined && (
        <ResetLinkDialog
          user={linkFor}
          onClose={() => {
            setLinkFor(undefined);
            linkOpener.current?.focus();
          }}
        />
      )}

      <h2 className="admin-subtitle">Add user</h2>
      <form className="admin-form" onSubmit={addUser}>
        <div className="admin-field">
          <label htmlFor="new-user-email">Email</label>
          <input id="new-user-email" type="email" value={newEmail} disabled={busy} onChange={(e) => { setNewEmail(e.target.value); }} required />
        </div>
        <div className="admin-field">
          <label htmlFor="new-user-name">Display name</label>
          <input id="new-user-name" type="text" value={newName} disabled={busy} onChange={(e) => { setNewName(e.target.value); }} required />
        </div>
        <div className="admin-field">
          <label htmlFor="new-user-role">Role</label>
          <select id="new-user-role" value={newRole} disabled={busy} onChange={(e) => { setNewRole(e.target.value as UserRole); }}>
            <option value="Editor">Editor</option>
            <option value="Admin">Admin</option>
          </select>
        </div>
        <div className="admin-field">
          <label htmlFor="new-user-password">Password</label>
          <input
            id="new-user-password"
            type="password"
            autoComplete="new-password"
            value={newPassword}
            disabled={busy}
            aria-describedby="new-user-password-hint"
            onChange={(e) => { setNewPassword(e.target.value); }}
            required
          />
          <span id="new-user-password-hint" className="admin-hint">At least {MIN_PASSWORD_LENGTH} characters.</span>
        </div>
        <button type="submit" disabled={busy || newEmail.trim().length === 0 || newName.trim().length === 0 || newPassword.length === 0}>
          Add user
        </button>
      </form>
    </div>
  );
}
