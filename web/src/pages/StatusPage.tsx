/**
 * The admin status page (design/atlas/help-about-status.md, PR-05): one row per component the product otherwise
 * degrades around without saying so - the layout service (sidecar), PDF export, the AI provider, storage and the data
 * folder - with the state as a word and, when it is not the good one, what stops working. Read-only. Administrators only:
 * a non-admin never sends the request, and the server refuses it anyway (403).
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useCallback, useEffect, useState, type ReactNode } from 'react';
import type { AuthUser } from '../model/authClient.ts';
import { fetchAdminStatus, type AdminStatus, type ConnectionStatus } from '../model/statusClient.ts';

interface Props {
  currentUser: AuthUser;
}

interface Row {
  key: string;
  label: string;
  /** One word: OK, Not running, Off, Not installed. Never conveyed by colour alone. */
  state: string;
  /** ok: working; off: an optional feature the admin has not turned on (not a fault); warn: works, but something an administrator should look at; problem: something that should work does not. */
  tone: 'ok' | 'off' | 'warn' | 'problem';
  detail: ReactNode;
}

function sidecarRow(status: AdminStatus): Row {
  const { sidecar } = status;
  const facts = [sidecar.version !== undefined ? `Service ${sidecar.version}` : undefined, sidecar.node !== undefined ? `Node ${sidecar.node}` : undefined, sidecar.restarts > 0 ? `${String(sidecar.restarts)} restart${sidecar.restarts === 1 ? '' : 's'}` : undefined].filter((fact) => fact !== undefined).join(', ');
  if (sidecar.status === 'running') {
    return { key: 'sidecar', label: 'Layout service', state: 'OK', tone: 'ok', detail: facts };
  }
  const words = { starting: 'Starting', unavailable: 'Not running', stopped: 'Stopped' } as const;
  return {
    key: 'sidecar',
    label: 'Layout service',
    state: words[sidecar.status],
    tone: 'problem',
    detail: (
      <>
        Layout, Tidy, SQL import, AI generation and exported connector routes are not working. Editing and saving still work.
        {sidecar.error !== undefined && <> The service reported: <code>{sidecar.error}</code></>}
        {sidecar.status === 'unavailable' && <> Check that Node 22 is installed, or set <code>sidecar.nodePath</code> in draughtsman.yaml.</>}
      </>
    ),
  };
}

function pdfRow(status: AdminStatus): Row {
  const { pdf } = status;
  if (!pdf.enabled) {
    return { key: 'pdf', label: 'PDF export', state: 'Off', tone: 'off', detail: <>Server-side PDF, for scripts and agents, is off. File &gt; Export PDF… still prints from each person’s own browser. Turn it on with <code>export.chromium.enabled</code> in draughtsman.yaml once Chromium is installed.</> };
  }
  if (!pdf.available) {
    return { key: 'pdf', label: 'PDF export', state: 'Not installed', tone: 'problem', detail: <>Enabled in draughtsman.yaml, but the Chromium component could not be launched{pdf.path !== undefined && <> (<code>{pdf.path}</code>)</>}. The Export PDF dialog offers browser print only until it can.{pdf.hint !== undefined && <> {pdf.hint}</>}</> };
  }
  return { key: 'pdf', label: 'PDF export', state: 'OK', tone: 'ok', detail: pdf.path !== undefined ? <>Using <code>{pdf.path}</code>.</> : 'Using the installed Chromium component.' };
}

function aiRow(status: AdminStatus): Row {
  const { ai } = status;
  return ai.enabled
    ? { key: 'ai', label: 'AI provider', state: 'OK', tone: 'ok', detail: [ai.provider, ai.model].filter((part) => part !== undefined && part !== '').join(', ') }
    : { key: 'ai', label: 'AI provider', state: 'Off', tone: 'off', detail: <>Describing diagrams in words is hidden. Configure a provider under <a href="/admin/ai">AI settings</a>.</> };
}

function connectionRow(connection: ConnectionStatus): Row {
  const { checks } = connection;
  const tone = checks.fail > 0 ? 'problem' : checks.warn > 0 ? 'warn' : 'ok';
  return {
    key: 'connection',
    label: 'Connection',
    state: checks.fail > 0 ? 'Problem' : checks.warn > 0 ? 'Check' : 'OK',
    tone,
    detail: (
      <>
        This request came from <code>{connection.peer}</code>, {connection.peerTrusted ? 'a proxy this server trusts' : connection.forwardedHeadersSeen ? 'which sent proxy headers that are NOT trusted, so they are ignored' : 'a direct connection'}; the server believes it is{' '}
        <code>{connection.scheme}</code>, and the sign-in lock-out counts it as <code>{connection.throttleAddress}</code>. {checks.fail.toString()} failed, {checks.warn.toString()} {checks.warn === 1 ? 'warning' : 'warnings'}, {checks.pass.toString()} passed
        {connection.problems.length > 0 && <> ({connection.problems.join('; ')})</>}. <a href="/admin/connection">Open the connection check</a>
      </>
    ),
  };
}

function rows(status: AdminStatus): Row[] {
  return [
    sidecarRow(status),
    pdfRow(status),
    aiRow(status),
    { key: 'storage', label: 'Storage', state: 'OK', tone: 'ok', detail: status.storage.file !== undefined ? <>{status.storage.provider === 'sqlite' ? 'SQLite' : status.storage.provider}, <code>{status.storage.file}</code></> : status.storage.provider === 'postgres' ? 'Postgres' : status.storage.provider },
    { key: 'data', label: 'Data folder', state: 'OK', tone: 'ok', detail: <><code>{status.dataFolder}</code>. Log: {status.logHint}</> },
    ...(status.connection === undefined ? [] : [connectionRow(status.connection)]),
    { key: 'version', label: 'Version', state: 'OK', tone: 'ok', detail: <>{status.version}, built {status.buildDate}.</> },
  ];
}

export function StatusPage({ currentUser }: Props) {
  const [status, setStatus] = useState<AdminStatus>();
  const [error, setError] = useState<string>();
  const isAdmin = currentUser.role === 'Admin';

  const load = useCallback((): void => {
    void fetchAdminStatus()
      .then((next) => {
        setError(undefined);
        setStatus(next);
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Could not read the system status.');
      });
  }, []);

  useEffect(() => {
    if (isAdmin) {
      load();
    }
  }, [isAdmin, load]);

  if (!isAdmin) {
    return <NotAllowed />;
  }

  if (status === undefined) {
    return (
      <div className="admin-page">
        <h1 className="admin-title">System status</h1>
        {error !== undefined ? (
          <p className="page-message" role="alert">
            {error} <button type="button" onClick={load}>Retry</button>
          </p>
        ) : (
          <p className="page-message">Loading status…</p>
        )}
      </div>
    );
  }

  const list = rows(status);
  const degraded = list.filter((row) => row.tone === 'problem' && row.key === 'sidecar');

  return (
    <div className="admin-page">
      <h1 className="admin-title">System status</h1>
      <p className="admin-intro">What this server is running and what is not. Read-only: nothing here restarts or changes anything.</p>
      {degraded.length > 0 && (
        <p className="admin-banner" role="status">
          The layout service is not running, so some features are degraded. Editing and saving still work.
        </p>
      )}
      <ul className="status-list">
        {list.map((row) => (
          <li key={row.key} className="status-row" data-tone={row.tone} data-row={row.key}>
            <span className="status-label">{row.label}</span>
            <span className="status-state">{row.state}</span>
            <span className="status-detail">{row.detail}</span>
          </li>
        ))}
      </ul>
      <p><button type="button" onClick={load}>Refresh</button></p>
    </div>
  );
}
