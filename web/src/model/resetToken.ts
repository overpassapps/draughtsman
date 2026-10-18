/**
 * The secret in a password-reset link lives in the URL fragment (`/reset-password#token=…`), which a browser never sends to the
 * server, so it is in no access log, no proxy log and no Referer header, and a mail scanner that fetches the link cannot spend it.
 * The page takes it out of the address bar the moment it has read it (`history.replaceState`), so it is not left in the history
 * entry, a screenshot of the address bar or a bookmark, and keeps it in memory only.
 *
 * Reading is idempotent on purpose: React in development mounts a component twice, and the second mount must find what the first
 * took from the fragment. A fragment that carries a token replaces what was held (a second link opened in the same tab); one that
 * does not leaves it alone.
 */

let held: string | undefined;

/** Looks for `token=` in a fragment such as `#token=dpr_abc`. */
export function tokenInFragment(hash: string): string | undefined {
  const value = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash).get('token');
  return value === null || value.trim() === '' ? undefined : value.trim();
}

/** Takes the token from the address bar (removing it there) and returns whatever is held. */
export function takeResetToken(): string | undefined {
  const fromAddress = tokenInFragment(window.location.hash);
  if (fromAddress !== undefined) {
    held = fromAddress;
  }
  if (window.location.hash !== '') {
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
  }
  return held;
}

/** Forgets the token once it has been used (or refused). */
export function forgetResetToken(): void {
  held = undefined;
}
