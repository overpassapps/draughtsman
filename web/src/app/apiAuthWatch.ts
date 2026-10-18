/**
 * Watches every same-origin fetch response for a 401, the server's signal that the session cookie is no longer
 * valid (expired, signed out elsewhere, or the account deactivated - docs/api.md "## Auth"). A per-call check in
 * every domain client (documentClient.ts, authClient.ts, the raw fetches in DocumentPage/DocumentViewer/TryPage/
 * DslPanel) would mean threading a callback through all of them; wrapping `fetch` once at startup instead means the
 * whole app falls back to the login page the moment a call comes back unauthorized, without every caller having to
 * know about it. `/api/auth/login` and `/api/auth/first-run` are excluded: a wrong password there is an expected
 * 401/400 the page itself already shows inline, not a session going away.
 */
import { noteSessionEnded, parseReason, SESSION_ENDED_HEADER } from '../model/sessionEnded.ts';

let installed = false;

const EXEMPT_PATHS = ['/api/auth/login', '/api/auth/first-run'];

function requestUrl(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.toString();
  }
  return input.url;
}

/** Installs the watch once per page load; later calls with a different callback are ignored. */
export function watchForSignOut(onUnauthorized: () => void): void {
  if (installed) {
    return;
  }
  installed = true;
  const native = window.fetch.bind(window);
  window.fetch = async (...args: Parameters<typeof fetch>): Promise<Response> => {
    const response = await native(...args);
    if (response.status === 401 && !EXEMPT_PATHS.some((path) => requestUrl(args[0]).includes(path))) {
      // First, so an open editor can keep its unsaved edits aside before the page swaps to the sign-in screen (S9).
      noteSessionEnded(parseReason(response.headers.get(SESSION_ENDED_HEADER)));
      onUnauthorized();
    }
    return response;
  };
}
