/**
 * The admin Connection check (docs/troubleshooting.md, "Troubleshooting an install"): where an install behind nginx, Caddy, Cloudflare or Docker finds out
 * why sign-in does not stick, saves fail with 400, or everyone shares one lock-out, without reading a log. Two groups of findings, each pass, warning or
 * fail, each with what the server saw and what to change for nginx, Caddy, Cloudflare and draughtsman.yaml:
 * what only the server can see (the proxy headers, the peer, the scheme it believes, the sign-in throttle's address, the Host header, the data folder, the
 * disk) and what only this browser can see (does a Secure cookie come back, does an ETag survive the proxy, was the response compressed in transit, do the
 * two clocks agree). Administrators only: a non-admin never sends a request, and the server refuses it anyway (403). Read-only: nothing here changes anything.
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useEffect, useState } from 'react';
import type { AuthUser } from '../model/authClient.ts';
import {
  currentPageScheme,
  fetchSelfCheck,
  judgeRoundTrip,
  runRoundTrip,
  type CheckFix,
  type CheckItem,
  type CheckStatus,
  type SelfCheckResult,
} from '../model/connectionCheckClient.ts';

interface Props {
  currentUser: AuthUser;
}

const STATE_WORD: Record<CheckStatus, string> = { pass: 'Pass', info: 'Note', warn: 'Warning', fail: 'Fail' };
const TONE: Record<CheckStatus, 'ok' | 'warn' | 'problem'> = { pass: 'ok', info: 'ok', warn: 'warn', fail: 'problem' };
const FIX_LABELS: readonly { key: keyof CheckFix; label: string }[] = [
  { key: 'yaml', label: 'draughtsman.yaml' },
  { key: 'shell', label: 'Command' },
  { key: 'nginx', label: 'nginx' },
  { key: 'caddy', label: 'Caddy' },
  { key: 'cloudflare', label: 'Cloudflare' },
];

function FixBlocks({ fix }: { fix: CheckFix }) {
  return (
    <>
      {FIX_LABELS.map(({ key, label }) => {
        const text = fix[key];
        if (text === undefined) {
          return null;
        }
        return (
          <div key={key} className="check-fix" data-fix={key}>
            <p className="check-fix-title">{label}</p>
            {key === 'cloudflare' && !text.includes('\n') ? (
              <p className="check-fix-prose">{text}</p>
            ) : (
              <pre className="check-pre">
                <code>{text}</code>
              </pre>
            )}
          </div>
        );
      })}
    </>
  );
}

function CheckRow({ item }: { item: CheckItem }) {
  const facts = item.facts === undefined ? [] : Object.entries(item.facts);
  const needsAction = item.status === 'warn' || item.status === 'fail';
  const hasFix = item.fix !== undefined && Object.values(item.fix).some((text) => text !== undefined);
  return (
    <li className="status-row check-row" data-row={item.id} data-tone={TONE[item.status]} data-status={item.status}>
      <span className="status-label">{item.title}</span>
      <span className="status-state">{STATE_WORD[item.status]}</span>
      <div className="status-detail">
        <p className="check-summary">{item.summary}</p>
        {item.detail !== undefined && item.detail.length > 0 && (
          <ul className="check-detail">
            {item.detail.map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
        )}
        {hasFix && item.fix !== undefined && (
          <details className="check-more" open={needsAction}>
            <summary>What to change</summary>
            <FixBlocks fix={item.fix} />
          </details>
        )}
        {facts.length > 0 && (
          <details className="check-more">
            <summary>What was seen</summary>
            <dl className="check-facts">
              {facts.map(([name, value]) => (
                <div key={name} className="check-fact">
                  <dt>{name}</dt>
                  <dd>{value}</dd>
                </div>
              ))}
            </dl>
          </details>
        )}
      </div>
    </li>
  );
}

function Group({ title, intro, result, loading, error }: { title: string; intro: string; result: SelfCheckResult | undefined; loading: boolean; error: string | undefined }) {
  return (
    <section className="check-group" aria-busy={loading}>
      <h2 className="admin-subtitle">{title}</h2>
      <p className="admin-intro">{intro}</p>
      {error !== undefined && (
        <p className="page-message" role="alert">
          {error}
        </p>
      )}
      {result === undefined && error === undefined && <p className="page-message">Checking…</p>}
      {result !== undefined && (
        <ul className="status-list">
          {result.items.map((item) => (
            <CheckRow key={item.id} item={item} />
          ))}
        </ul>
      )}
    </section>
  );
}

function summarise(...results: (SelfCheckResult | undefined)[]): { pass: number; warn: number; fail: number } {
  return results.reduce(
    (total, result) => ({ pass: total.pass + (result?.summary.pass ?? 0), warn: total.warn + (result?.summary.warn ?? 0), fail: total.fail + (result?.summary.fail ?? 0) }),
    { pass: 0, warn: 0, fail: 0 },
  );
}

/** What one run of the check has found so far. `id` says which run: a result from a run that has been replaced is never shown. */
interface Outcome {
  id: number;
  server?: SelfCheckResult;
  trip?: SelfCheckResult;
  serverError?: string;
  tripError?: string;
  serverDone: boolean;
  tripDone: boolean;
}

export function ConnectionCheckPage({ currentUser }: Props) {
  const isAdmin = currentUser.role === 'Admin';
  // Bumping the run number runs the check again; the effect below starts a run per number and ignores a result that arrives after it was replaced.
  const [runId, setRunId] = useState(1);
  const [outcome, setOutcome] = useState<Outcome>({ id: 0, serverDone: false, tripDone: false });

  useEffect(() => {
    if (!isAdmin) {
      return;
    }
    let replaced = false;
    const update = (change: Partial<Outcome>): void => {
      if (!replaced) {
        setOutcome((previous) => ({ ...(previous.id === runId ? previous : { id: runId, serverDone: false, tripDone: false }), ...change }));
      }
    };
    const scheme = currentPageScheme();
    fetchSelfCheck(scheme)
      .then((server) => {
        update({ server, serverDone: true });
      })
      .catch((cause: unknown) => {
        update({ serverError: cause instanceof Error ? cause.message : 'Could not read the connection check.', serverDone: true });
      });
    runRoundTrip()
      .then((observation) => judgeRoundTrip(observation))
      .then((trip) => {
        update({ trip, tripDone: true });
      })
      .catch((cause: unknown) => {
        update({ tripError: cause instanceof Error ? cause.message : 'Could not run the browser round trip.', tripDone: true });
      });
    return () => {
      replaced = true;
    };
  }, [isAdmin, runId]);

  if (!isAdmin) {
    return <NotAllowed />;
  }

  const current: Outcome = outcome.id === runId ? outcome : { id: runId, serverDone: false, tripDone: false };
  const running = !(current.serverDone && current.tripDone);
  const totals = summarise(current.server, current.trip);
  return (
    <div className="admin-page">
      <h1 className="admin-title">Connection check</h1>
      <p className="admin-intro">
        Tests the path between your browser and this server: whether the proxy in front of it is believed, whether sign-in cookies and version tags survive it, and whether the data folder and disk are fit. Read-only, for administrators, and nothing
        secret is shown. If something fails, the fix is given for nginx, Caddy, Cloudflare and draughtsman.yaml. More in the Troubleshooting an install section of the documentation.
      </p>
      <p className="check-summary-line" role="status">
        {running ? (
          'Running the check…'
        ) : (
          <>
            <strong>{totals.fail}</strong> failed, <strong>{totals.warn}</strong> {totals.warn === 1 ? 'warning' : 'warnings'}, <strong>{totals.pass}</strong> passed.
          </>
        )}
      </p>
      <Group
        title="What this browser sees"
        intro="Two calls through whatever sits between you and the server: a test cookie, an ETag, any compression, and the two clocks."
        result={current.trip}
        loading={!current.tripDone}
        error={current.tripError}
      />
      <Group
        title="What the server sees"
        intro="The proxy headers this request arrived with, who the server thinks you are, the address the sign-in lock-out uses, and whether the data folder and disk are fit."
        result={current.server}
        loading={!current.serverDone}
        error={current.serverError}
      />
      <p>
        <button
          type="button"
          onClick={() => {
            setRunId((number) => number + 1);
          }}
          disabled={running}
        >
          Run again
        </button>
      </p>
    </div>
  );
}
