/**
 * The admin Connection check's client (docs/api.md "## Connection check"): the findings only the server can make
 * (`GET /api/admin/selfcheck`), and the round trip only a browser can run. The page calls the echo twice (step 1 sets a test cookie and answers with an
 * ETag and the same tag in its body, step 2 says whether the browser sent the cookie back), records what it saw of the response headers a proxy may
 * have changed, and hands that to the server to be judged (`POST /api/admin/selfcheck/roundtrip`), so every rule and every line of fix text lives in
 * one place and is tested there, against fake proxies. This file only runs the calls and carries the answer. The server omits null fields.
 */
import { csrfHeaders } from './csrf.ts';

export type CheckStatus = 'pass' | 'info' | 'warn' | 'fail';

export interface CheckFix {
  nginx?: string;
  caddy?: string;
  cloudflare?: string;
  yaml?: string;
  /** A command to run or an environment variable to set (the doctor's ownership fixes), where the fix is not a configuration file. */
  shell?: string;
}

export interface CheckItem {
  id: string;
  title: string;
  status: CheckStatus;
  summary: string;
  detail?: string[];
  facts?: Record<string, string>;
  fix?: CheckFix;
}

export interface SelfCheckResult {
  generatedAt: string;
  summary: { pass: number; warn: number; fail: number };
  items: CheckItem[];
}

/** What the browser saw of one call to the echo. Every member is optional: a call that failed is reported as what is missing. */
export interface RoundTripStep {
  status?: number | undefined;
  etagHeader?: string | undefined;
  contentEncoding?: string | undefined;
  bodyTag?: string | undefined;
  cookieReceived?: boolean | undefined;
  cookieWasSecure?: boolean | undefined;
  serverTime?: string | undefined;
}

export interface RoundTripObservation {
  pageScheme?: 'http' | 'https';
  first?: RoundTripStep;
  second?: RoundTripStep;
  /** The browser's clock, milliseconds since 1970, at the middle of the first call. */
  clientTimeMs?: number;
  failure?: string;
}

export class SelfCheckError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'SelfCheckError';
    this.status = status;
  }
}

export const ECHO_PATH = '/api/admin/selfcheck/echo';

const STATUSES: readonly string[] = ['pass', 'info', 'warn', 'fail'];

/** The scheme of the address in the browser: the one thing the server cannot see for itself, and what a Secure cookie depends on. */
export function currentPageScheme(): 'http' | 'https' {
  return typeof window !== 'undefined' && window.location.protocol === 'https:' ? 'https' : 'http';
}

function shapeOf(body: unknown): SelfCheckResult | undefined {
  if (typeof body !== 'object' || body === null) {
    return undefined;
  }
  const candidate = body as Partial<SelfCheckResult>;
  if (!Array.isArray(candidate.items) || candidate.summary === undefined || typeof candidate.generatedAt !== 'string') {
    return undefined;
  }
  if (!candidate.items.every((item) => typeof item.id === 'string' && typeof item.title === 'string' && STATUSES.includes(item.status) && typeof item.summary === 'string')) {
    return undefined;
  }
  return candidate as SelfCheckResult;
}

export async function fetchSelfCheck(scheme: 'http' | 'https' = currentPageScheme(), fetchImpl: typeof fetch = fetch): Promise<SelfCheckResult> {
  const response = await fetchImpl(`/api/admin/selfcheck?pageScheme=${scheme}`, { headers: { Accept: 'application/json' }, cache: 'no-store' });
  if (!response.ok) {
    throw new SelfCheckError(response.status, response.status === 403 ? 'Only an administrator can run the connection check.' : 'Could not read the connection check.');
  }
  const result = shapeOf((await response.json()) as unknown);
  if (result === undefined) {
    throw new SelfCheckError(response.status, 'The connection check was not in the expected shape.');
  }
  return result;
}

async function callEcho(step: 1 | 2, fetchImpl: typeof fetch): Promise<RoundTripStep> {
  // no-store: the browser must go to the network each time, or a cached answer would show a proxy nothing; same-origin credentials carry the session and the test cookie.
  const response = await fetchImpl(`${ECHO_PATH}?step=${step.toString()}`, { headers: { Accept: 'application/json' }, cache: 'no-store', credentials: 'same-origin' });
  const reading: RoundTripStep = {
    status: response.status,
    etagHeader: response.headers.get('etag') ?? undefined,
    contentEncoding: response.headers.get('content-encoding') ?? undefined,
  };
  if (!response.ok) {
    return reading;
  }
  try {
    const body = (await response.json()) as { tag?: unknown; cookieReceived?: unknown; cookieWasSecure?: unknown; serverTime?: unknown };
    return {
      ...reading,
      bodyTag: typeof body.tag === 'string' ? body.tag : undefined,
      cookieReceived: typeof body.cookieReceived === 'boolean' ? body.cookieReceived : undefined,
      cookieWasSecure: typeof body.cookieWasSecure === 'boolean' ? body.cookieWasSecure : undefined,
      serverTime: typeof body.serverTime === 'string' ? body.serverTime : undefined,
    };
  } catch {
    return reading;
  }
}

/** The page's own part of the check: two calls to the echo and the browser's clock. Never throws; what went wrong is in the observation. */
export async function runRoundTrip(fetchImpl: typeof fetch = fetch, now: () => number = Date.now): Promise<RoundTripObservation> {
  const observation: RoundTripObservation = { pageScheme: currentPageScheme() };
  try {
    const before = now();
    observation.first = await callEcho(1, fetchImpl);
    const after = now();
    observation.clientTimeMs = Math.round((before + after) / 2);
    observation.second = await callEcho(2, fetchImpl);
  } catch (cause: unknown) {
    observation.failure = cause instanceof Error ? cause.message : 'The browser could not reach the server.';
  }
  return observation;
}

/** Hands the round trip to the server to be judged: pass, warn or fail, with the words and the fix text. */
export async function judgeRoundTrip(observation: RoundTripObservation, fetchImpl: typeof fetch = fetch): Promise<SelfCheckResult> {
  const response = await fetchImpl('/api/admin/selfcheck/roundtrip', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(observation),
  });
  if (!response.ok) {
    throw new SelfCheckError(response.status, 'Could not judge the browser round trip.');
  }
  const result = shapeOf((await response.json()) as unknown);
  if (result === undefined) {
    throw new SelfCheckError(response.status, 'The browser round trip answer was not in the expected shape.');
  }
  return result;
}
