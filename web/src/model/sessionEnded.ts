/**
 * Why the person is looking at the sign-in screen when they were not a moment ago (QA S9). The server tells a client
 * whose session cookie it refuses *why*, in one coarse word (`X-Draughtsman-Session-Ended`: revoked, deactivated or
 * expired - never who did it, so an Editor does not learn which administrator reset their password). `apiAuthWatch`
 * notes it from the 401 an API call got, `fetchAuthStatus` from the status call a reload makes, and the sign-in page
 * reads it back, so a revoked session ends with a sentence instead of a screen that changed by itself.
 *
 * An open editor is told first (`onSessionEnded`): it keeps its unsaved edits in this browser as a local copy
 * (editor/localCopy.ts) before the page goes, and says so in the notice, so nothing is lost with the session.
 */

export const SESSION_ENDED_HEADER = 'X-Draughtsman-Session-Ended';

export type SessionEndReason = 'revoked' | 'deactivated' | 'expired' | 'unknown';

export interface SessionEndedNotice {
  reason: SessionEndReason;
  /** True when the editor that was open kept edits it had not yet saved. */
  keptWork: boolean;
}

/** A listener returns true when it kept unsaved work aside because of this. */
type Listener = () => boolean | undefined;

let notice: SessionEndedNotice | undefined;
// After a deliberate sign-out nothing is "ended behind the person's back"; a request still in flight must not say it was.
let suppressed = false;
const listeners = new Set<Listener>();

/** Reads the header value into a reason; anything unrecognised, or absent, is `unknown` (still a session that ended). */
export function parseReason(value: string | null): SessionEndReason {
  return value === 'revoked' || value === 'deactivated' || value === 'expired' ? value : 'unknown';
}

/** Registers an editor (or anything else holding unsaved work); returns how to stop. */
export function onSessionEnded(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Records that the session is gone. Safe to call once per failed request: the first specific reason stands, and listeners run once per episode. */
export function noteSessionEnded(reason: SessionEndReason): void {
  if (suppressed) {
    return;
  }
  if (notice !== undefined) {
    if (notice.reason === 'unknown' && reason !== 'unknown') {
      notice = { ...notice, reason };
    }
    return;
  }
  let keptWork = false;
  for (const listener of listeners) {
    keptWork = (listener() ?? false) || keptWork;
  }
  notice = { reason, keptWork };
}

export function currentSessionEndedNotice(): SessionEndedNotice | undefined {
  return notice;
}

/** Called when the person signs in (the notice has been answered) and by tests. */
export function clearSessionEndedNotice(): void {
  notice = undefined;
  suppressed = false;
}

/** Called by a deliberate sign-out: whatever 401 arrives next is the sign-out, not a surprise. Cleared by the next sign-in. */
export function suppressSessionEndedNotice(): void {
  notice = undefined;
  suppressed = true;
}

const KEPT = ' The changes you had not saved are kept in this browser: sign in and open the document again to get them back.';

export function sessionEndedMessage(value: SessionEndedNotice): string {
  const head = {
    revoked: 'You were signed out because your password was changed, or because you signed out in another window.',
    deactivated: 'You were signed out because your account was deactivated. Ask an administrator if you think that is a mistake.',
    expired: 'You were signed out because your session expired.',
    unknown: 'You were signed out: your session is no longer valid.',
  }[value.reason];
  return value.keptWork ? head + KEPT : head;
}
