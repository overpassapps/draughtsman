/**
 * The instance security log (AU-04; design/atlas/login-admin.md, "Account, tokens and the security log"): who signed
 * in, failed to, changed a password or role, made or revoked a token, newest first, admins only. Read-only evidence:
 * a filter by event and a pager, and nothing that edits a row. The server never records a secret, so nothing here
 * needs redacting.
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useCallback, useEffect, useState } from 'react';
import { DateTime } from '../app/DateTime.tsx';
import { SECURITY_EVENT_LABELS, listSecurityEvents, type AuthUser, type SecurityEvent } from '../model/authClient.ts';

interface Props {
  currentUser: AuthUser;
}

const PAGE_SIZE = 50;

type ListState = { kind: 'loading' } | { kind: 'ok'; items: SecurityEvent[]; total: number } | { kind: 'error'; message: string };

function describeEvent(type: string): string {
  return SECURITY_EVENT_LABELS[type] ?? type;
}

export function SecurityLogPage({ currentUser }: Props) {
  const [state, setState] = useState<ListState>({ kind: 'loading' });
  const [type, setType] = useState('');
  const [offset, setOffset] = useState(0);
  const isAdmin = currentUser.role === 'Admin';

  const reload = useCallback((): Promise<void> => {
    return listSecurityEvents({ type, offset, limit: PAGE_SIZE })
      .then((page) => {
        setState({ kind: 'ok', items: page.items, total: page.total });
      })
      .catch((cause: unknown) => {
        setState({ kind: 'error', message: cause instanceof Error ? cause.message : 'Unknown error' });
      });
  }, [type, offset]);

  useEffect(() => {
    if (isAdmin) {
      void reload();
    }
  }, [isAdmin, reload]);

  // The list goes back to "loading" from the handlers that change what is asked for, not from the effect.
  const show = (nextType: string, nextOffset: number): void => {
    setState({ kind: 'loading' });
    setType(nextType);
    setOffset(nextOffset);
  };

  if (!isAdmin) {
    return <NotAllowed />;
  }

  const total = state.kind === 'ok' ? state.total : 0;
  const first = total === 0 ? 0 : offset + 1;
  const last = Math.min(offset + PAGE_SIZE, total);

  return (
    <div className="admin-page admin-page-wide">
      <h1 className="admin-title">Security log</h1>
      <p className="admin-intro">Sign-ins, password and role changes, and API tokens, newest first. The log records who and from where, never a password or token.</p>
      <div className="admin-form security-filter">
        <div>
          <label htmlFor="security-type">Event</label>
          <select
            id="security-type"
            value={type}
            onChange={(e) => {
              show(e.target.value, 0);
            }}
          >
            <option value="">All events</option>
            {Object.entries(SECURITY_EVENT_LABELS).map(([id, label]) => (
              <option key={id} value={id}>{label}</option>
            ))}
          </select>
        </div>
        <div className="admin-row-actions">
          <button type="button" disabled={offset === 0 || state.kind === 'loading'} onClick={() => { show(type, Math.max(0, offset - PAGE_SIZE)); }}>
            Newer
          </button>
          <button type="button" disabled={offset + PAGE_SIZE >= total || state.kind === 'loading'} onClick={() => { show(type, offset + PAGE_SIZE); }}>
            Older
          </button>
          {state.kind === 'ok' && <span aria-live="polite">{total === 0 ? '0 events' : `${String(first)}–${String(last)} of ${String(total)}`}</span>}
        </div>
      </div>

      {state.kind === 'loading' && <p className="page-message">Loading the log…</p>}
      {state.kind === 'error' && <p className="page-message" role="alert">Cannot load the log: {state.message}</p>}
      {state.kind === 'ok' && state.items.length === 0 && <p className="page-message">Nothing has been recorded yet.</p>}
      {state.kind === 'ok' && state.items.length > 0 && (
        <div className="admin-table-wrap" role="region" aria-label="Security events" tabIndex={0}>
          <table className="admin-table">
            <thead>
              <tr>
                <th scope="col">When</th>
                <th scope="col">Event</th>
                <th scope="col">Who</th>
                <th scope="col">Account</th>
                <th scope="col">From</th>
                <th scope="col">Detail</th>
              </tr>
            </thead>
            <tbody>
              {state.items.map((event) => (
                <tr key={event.id}>
                  <td><DateTime iso={event.at} /></td>
                  <td>{describeEvent(event.type)}</td>
                  <td>{event.actorLabel ?? '—'}</td>
                  <td>{event.targetLabel ?? '—'}</td>
                  <td>{event.sourceIp ?? '—'}</td>
                  <td>{event.detail ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
