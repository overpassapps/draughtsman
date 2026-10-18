/**
 * CSRF header attachment for every state-changing request made while cookie-authenticated (docs/api.md "## Auth"):
 * the server (re)issues a non-HttpOnly `XSRF-TOKEN` cookie on first-run, login and logout, and 403s any POST/PUT/
 * PATCH/DELETE that does not echo it back as `X-XSRF-TOKEN` (src/Draughtsman.Server/Auth/CsrfMiddleware.cs). A
 * bearer-token request never needs this - a token cannot be replayed by a browser the way an ambient cookie can -
 * but this SPA only ever calls its own API from the browser over the session cookie, so every mutating fetch here
 * attaches it unconditionally; the cookie is simply absent before any sign-in has issued it, which is exactly when
 * first-run and login (both exempt) are the only mutating calls that can happen. Guarded against a missing global
 * `document` so model-layer unit tests can run in the plain Node environment (no jsdom) like the rest of
 * `model/*.test.ts` - there is no cookie jar there either way.
 */
export function readCsrfCookie(): string | undefined {
  if (typeof document === 'undefined') {
    return undefined;
  }
  const match = /(?:^|;\s*)XSRF-TOKEN=([^;]*)/.exec(document.cookie);
  const value = match?.[1];
  return value === undefined ? undefined : decodeURIComponent(value);
}

/** Spread into a mutating fetch call's `headers` object. */
export function csrfHeaders(): Record<string, string> {
  const token = readCsrfCookie();
  return token === undefined ? {} : { 'X-XSRF-TOKEN': token };
}
