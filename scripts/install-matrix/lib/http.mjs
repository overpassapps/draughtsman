// The journey's HTTP client: Node's global fetch, a strict cookie jar (lib/cookies.mjs), the CSRF header, and per-call evidence that is redacted at
// the moment it is captured. Nothing here prints; callers decide what goes in a report.
import { CookieJar } from './cookies.mjs';
import { bodyExcerpt, describeSetCookie, pickEvidenceHeaders } from './redact.mjs';

export class Client {
  /** @param {string} baseUrl @param {{strict?: boolean, secrets?: string[], timeoutMs?: number}} options */
  constructor(baseUrl, { strict = true, secrets = [], timeoutMs = 60000 } = {}) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.jar = new CookieJar({ strict });
    this.secrets = secrets;
    this.timeoutMs = timeoutMs;
    this.bearer = undefined;
  }

  get scheme() {
    return new URL(this.baseUrl).protocol.replace(':', '');
  }

  /**
   * One request. Returns { status, headers, text, json, bytes, setCookies, evidence() }.
   * `evidence()` is the redacted record a report may hold: status, the evidence headers, Set-Cookie NAMES with their attributes, and a body excerpt.
   */
  async request(method, path, { json, body, headers = {}, anonymous = false, raw = false } = {}) {
    const send = { Accept: 'application/json', ...headers };
    const cookie = anonymous ? undefined : this.jar.header();
    if (cookie) send.Cookie = cookie;
    if (!anonymous && this.bearer) send.Authorization = `Bearer ${this.bearer}`;
    if (!anonymous && method !== 'GET' && method !== 'HEAD') {
      const xsrf = this.jar.get('XSRF-TOKEN');
      if (xsrf) send['X-XSRF-TOKEN'] = decodeURIComponent(xsrf);
    }
    let payload;
    if (json !== undefined) { send['Content-Type'] = 'application/json'; payload = JSON.stringify(json); }
    else if (body !== undefined) payload = body;
    const url = this.baseUrl + path;
    const response = await fetch(url, { method, headers: send, body: payload, redirect: 'manual', signal: AbortSignal.timeout(this.timeoutMs) });
    const setCookies = typeof response.headers.getSetCookie === 'function' ? response.headers.getSetCookie() : [];
    this.jar.store(setCookies, url);
    const bytes = Buffer.from(await response.arrayBuffer());
    const text = raw ? '' : bytes.toString('utf8');
    let parsed;
    if (!raw) { try { parsed = JSON.parse(text); } catch { parsed = undefined; } }
    const secrets = this.secrets;
    return {
      status: response.status,
      headers: response.headers,
      text,
      json: parsed,
      bytes,
      setCookies,
      evidence: () => ({
        request: `${method} ${path}`,
        status: response.status,
        headers: pickEvidenceHeaders(response.headers),
        setCookie: setCookies.map(describeSetCookie),
        body: raw ? `(${bytes.length} bytes)` : bodyExcerpt(text, secrets),
      }),
    };
  }
}
