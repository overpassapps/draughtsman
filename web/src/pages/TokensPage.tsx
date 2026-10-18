/**
 * A signed-in user's own API tokens (build-spec-v0.1.md section 7): MCP and scripting access without a session
 * cookie. The raw secret is shown exactly once, at creation (docs/api.md `POST /api/tokens`), alongside a
 * copy-ready `claude mcp add` snippet - the bearer-token equivalent of docs/api.md's curl example.
 */
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { DateTime } from '../app/DateTime.tsx';
import { useCurrentUser } from '../app/currentUser.ts';
import {
  AuthError,
  createToken,
  listAllTokens,
  listTokens,
  revokeToken,
  type AdminApiTokenSummary,
  type ApiTokenSummary,
  type CreatedApiToken,
} from '../model/authClient.ts';
import { formatDateTime } from '../model/dateFormat.ts';

type ListState = { kind: 'loading' } | { kind: 'ok'; tokens: ApiTokenSummary[] } | { kind: 'error'; message: string };

/** The one-time panel's "expires …" text: the same format as every table, with the zone on hover where the tag allows. */
function formatDate(iso: string | undefined): string {
  return iso === undefined ? 'Never' : formatDateTime(iso);
}

/** Lifetimes offered at creation; the server's own ceiling (`auth.tokens.maxLifetimeDays`) still applies and is reported if exceeded. */
const LIFETIMES: { days: number; label: string }[] = [
  { days: 7, label: '7 days' },
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' },
  { days: 180, label: '180 days' },
  { days: 365, label: '1 year' },
];
const DEFAULT_LIFETIME_DAYS = 90;

function statusOf(token: ApiTokenSummary): string {
  if (token.revoked) {
    return 'Revoked';
  }
  return token.expired === true ? 'Expired' : 'Active';
}

function mcpSnippet(token: string): string {
  return `claude mcp add --transport http draughtsman ${window.location.origin}/mcp --header "Authorization: Bearer ${token}"`;
}

/** Best-effort copy: the async Clipboard API needs a secure context, so fall back to the native copy command. */
async function copyToClipboard(text: string): Promise<void> {
  try {
    const clipboard = (navigator as { clipboard?: Clipboard }).clipboard;
    if (clipboard !== undefined) {
      await clipboard.writeText(text);
      return;
    }
  } catch {
    // Fall through to the native command below.
  }
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.style.position = 'fixed';
  area.style.opacity = '0';
  document.body.appendChild(area);
  area.select();
  try {
    // eslint-disable-next-line @typescript-eslint/no-deprecated
    document.execCommand('copy');
  } finally {
    area.remove();
  }
}

type AllState = { kind: 'loading' } | { kind: 'ok'; tokens: AdminApiTokenSummary[] } | { kind: 'error'; message: string };

function ownerOf(token: AdminApiTokenSummary): string {
  return token.ownerName ?? token.ownerEmail ?? token.ownerId;
}

export function TokensPage() {
  // An administrator also sees (and can revoke) everyone's tokens; the server enforces it, this only decides whether to ask.
  const isAdmin = useCurrentUser()?.role === 'Admin';
  const [all, setAll] = useState<AllState>({ kind: 'loading' });
  const [state, setState] = useState<ListState>({ kind: 'loading' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [name, setName] = useState('');
  const [lifetimeDays, setLifetimeDays] = useState(DEFAULT_LIFETIME_DAYS);
  const [readOnly, setReadOnly] = useState(false);
  const [created, setCreated] = useState<CreatedApiToken>();

  const reload = useCallback((): Promise<void> => {
    const everyone = isAdmin
      ? listAllTokens()
          .then((tokens) => { setAll({ kind: 'ok', tokens }); })
          .catch((cause: unknown) => { setAll({ kind: 'error', message: cause instanceof Error ? cause.message : 'Unknown error' }); })
      : Promise.resolve();
    const own = listTokens()
      .then((tokens) => {
        setState({ kind: 'ok', tokens });
        setBusy(false);
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setState({ kind: 'error', message: cause instanceof Error ? cause.message : 'Unknown error' });
      });
    return Promise.all([own, everyone]).then(() => undefined);
  }, [isAdmin]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const create = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    const trimmed = name.trim();
    if (trimmed.length === 0 || busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    void createToken(trimmed, { expiresInDays: lifetimeDays, scope: readOnly ? 'read' : 'full' })
      .then((token) => {
        setCreated(token);
        setName('');
        setReadOnly(false);
        return reload();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof AuthError ? cause.message : 'Could not create the token.');
      });
  };

  const revoke = (token: ApiTokenSummary): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    void revokeToken(token.id)
      .then(() => reload())
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof Error ? cause.message : 'Could not revoke the token.');
      });
  };

  const tokens = state.kind === 'ok' ? state.tokens : [];
  const everyones = all.kind === 'ok' ? all.tokens : [];

  return (
    <div className="admin-page">
      <h1 className="admin-title">API tokens</h1>
      {isAdmin && <h2 className="admin-subtitle admin-subtitle-first">Your tokens</h2>}
      {error !== undefined && (
        <p className="admin-banner" role="alert">
          {error}
        </p>
      )}

      {created !== undefined && (
        <div className="tokens-created" role="status">
          <p>
            <strong>{created.name}</strong> was created
            {created.expiresAt !== undefined && <> and expires {formatDate(created.expiresAt)}</>}
            {created.scope === 'read' && <>; it is read-only</>}. Copy the token now — it will not be shown again.
          </p>
          <label htmlFor="tokens-created-secret">Token</label>
          <div className="tokens-copy-row">
            <input id="tokens-created-secret" className="tokens-copy-field" readOnly value={created.token} onFocus={(e) => { e.target.select(); }} />
            <button type="button" onClick={() => { void copyToClipboard(created.token); }}>Copy</button>
          </div>
          <label htmlFor="tokens-created-snippet">claude mcp add</label>
          <div className="tokens-copy-row">
            <input
              id="tokens-created-snippet"
              className="tokens-copy-field"
              readOnly
              value={mcpSnippet(created.token)}
              onFocus={(e) => { e.target.select(); }}
            />
            <button type="button" onClick={() => { void copyToClipboard(mcpSnippet(created.token)); }}>Copy</button>
          </div>
          <button type="button" onClick={() => { setCreated(undefined); }}>Done</button>
        </div>
      )}

      {state.kind === 'loading' && <p className="page-message">Loading tokens…</p>}
      {state.kind === 'error' && <p className="page-message" role="alert">Cannot list tokens: {state.message}</p>}
      {state.kind === 'ok' && tokens.length === 0 && <p className="page-message">No tokens yet.</p>}
      {state.kind === 'ok' && tokens.some((token) => token.expiresAt === undefined && !token.revoked) && (
        <p className="admin-intro">A token marked “No expiry” was created before tokens expired and never expires on its own. Revoke it and create a replacement.</p>
      )}
      {state.kind === 'ok' && tokens.length > 0 && (
        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Created</th>
                <th scope="col">Last used</th>
                <th scope="col">Expires</th>
                <th scope="col">Access</th>
                <th scope="col">Status</th>
                <th scope="col"><span className="visually-hidden">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {tokens.map((token) => (
                <tr key={token.id}>
                  <td>{token.name}</td>
                  <td><DateTime iso={token.createdAt} /></td>
                  <td><DateTime iso={token.lastUsedAt} /></td>
                  <td><DateTime iso={token.expiresAt} empty="No expiry" /></td>
                  <td>{token.scope === 'read' ? 'Read-only' : 'Full'}</td>
                  <td>{statusOf(token)}</td>
                  <td>
                    {!token.revoked && (
                      <button type="button" onClick={() => { revoke(token); }} disabled={busy}>Revoke</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {isAdmin && (
        <section aria-labelledby="all-tokens-heading">
          <h2 id="all-tokens-heading" className="admin-subtitle">Everyone&rsquo;s tokens</h2>
          <p className="admin-intro">Every API token on this server, whoever made it. The secret is never shown or stored, only who owns the token and when it was used. Revoking one ends its access straight away and is recorded in the security log.</p>
          {all.kind === 'loading' && <p className="page-message">Loading every token…</p>}
          {all.kind === 'error' && <p className="page-message" role="alert">Cannot list every token: {all.message}</p>}
          {all.kind === 'ok' && everyones.length === 0 && <p className="page-message">Nobody has made a token yet.</p>}
          {all.kind === 'ok' && everyones.length > 0 && (
            <div className="admin-table-wrap">
              <table className="admin-table" aria-label="Every person's API tokens">
                <thead>
                  <tr>
                    <th scope="col">Owner</th>
                    <th scope="col">Name</th>
                    <th scope="col">Created</th>
                    <th scope="col">Last used</th>
                    <th scope="col">Expires</th>
                    <th scope="col">Access</th>
                    <th scope="col">Status</th>
                    <th scope="col"><span className="visually-hidden">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  {everyones.map((token) => (
                    <tr key={token.id}>
                      <td>
                        {ownerOf(token)}
                        {token.ownerEmail !== undefined && token.ownerEmail !== ownerOf(token) && <span className="admin-hint admin-owner-email">{token.ownerEmail}</span>}
                      </td>
                      <td>{token.name}</td>
                      <td><DateTime iso={token.createdAt} /></td>
                      <td><DateTime iso={token.lastUsedAt} /></td>
                      <td><DateTime iso={token.expiresAt} empty="No expiry" /></td>
                      <td>{token.scope === 'read' ? 'Read-only' : 'Full'}</td>
                      <td>{statusOf(token)}</td>
                      <td>
                        {!token.revoked && (
                          <button type="button" onClick={() => { revoke(token); }} disabled={busy} aria-label={`Revoke ${token.name} for ${ownerOf(token)}`}>Revoke</button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      <h2 className="admin-subtitle">Create token</h2>
      <form className="admin-form" onSubmit={create}>
        <label htmlFor="new-token-name">Name</label>
        <input id="new-token-name" type="text" value={name} disabled={busy} onChange={(e) => { setName(e.target.value); }} required />
        <div>
          <label htmlFor="new-token-lifetime">Expires in</label>
          <select id="new-token-lifetime" value={lifetimeDays} disabled={busy} onChange={(e) => { setLifetimeDays(Number(e.target.value)); }}>
            {LIFETIMES.map((lifetime) => (
              <option key={lifetime.days} value={lifetime.days}>{lifetime.label}</option>
            ))}
          </select>
        </div>
        <div className="tokens-readonly">
          <input id="new-token-readonly" type="checkbox" checked={readOnly} disabled={busy} onChange={(e) => { setReadOnly(e.target.checked); }} aria-describedby="new-token-readonly-hint" />
          <label htmlFor="new-token-readonly">Read-only</label>
          <span id="new-token-readonly-hint" className="tokens-hint">Can list and read documents; cannot create, change or delete.</span>
        </div>
        <button type="submit" disabled={busy || name.trim().length === 0}>Create token</button>
      </form>
    </div>
  );
}
