/**
 * The app-level auth gate's own state: `GET /api/auth/status` once on load, decides first-run vs. login vs. the
 * signed-in shell (App.tsx), and re-checks itself whenever any API call comes back 401 (apiAuthWatch.ts) so a
 * session that expired or was revoked elsewhere falls back to the login page instead of leaving a broken screen up.
 */
import { useCallback, useEffect, useState } from 'react';
import { watchForSignOut } from './apiAuthWatch.ts';
import { fetchAuthStatus, type AuthStatus } from '../model/authClient.ts';

type State = { kind: 'loading' } | { kind: 'ok'; status: AuthStatus } | { kind: 'error'; message: string };

/**
 * `refresh` resolves with the status it found, or `undefined` when the check itself failed (the gate then shows its
 * own error). A sign-in page awaits it to learn whether the session it just asked for actually stuck.
 */
export type AuthStatusState = State & { refresh: () => Promise<AuthStatus | undefined> };

export function useAuthStatus(): AuthStatusState {
  const [state, setState] = useState<State>({ kind: 'loading' });

  const refresh = useCallback((): Promise<AuthStatus | undefined> => {
    return fetchAuthStatus()
      .then((status): AuthStatus => {
        setState({ kind: 'ok', status });
        return status;
      })
      .catch((cause: unknown): undefined => {
        setState({ kind: 'error', message: cause instanceof Error ? cause.message : 'Unknown error' });
        return undefined;
      });
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    watchForSignOut(() => {
      void refresh();
    });
  }, [refresh]);

  return { ...state, refresh };
}
