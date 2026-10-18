/**
 * A deliberately small path router: two content routes and a home page do not justify a routing dependency yet.
 * Replace with a proper router when the editor shell (M2) needs nested layouts and query state.
 */

import { useEffect, useState } from 'react';

export type Route =
  | { page: 'home' }
  | { page: 'example' }
  | { page: 'try' }
  | { page: 'document'; id: string }
  | { page: 'login' }
  | { page: 'account' }
  | { page: 'adminUsers' }
  | { page: 'adminSecurity' }
  | { page: 'adminTokens' }
  | { page: 'adminBranding' }
  | { page: 'adminThemes' }
  | { page: 'adminAi' }
  | { page: 'adminEmail' }
  | { page: 'resetPassword' }
  | { page: 'adminStatus' }
  | { page: 'adminConnection' }
  | { page: 'about' }
  | { page: 'notFound'; path: string };

export function matchRoute(pathname: string): Route {
  const path = pathname.replace(/\/+$/, '') || '/';
  if (path === '/') {
    return { page: 'home' };
  }
  if (path === '/example') {
    return { page: 'example' };
  }
  if (path === '/try') {
    return { page: 'try' };
  }
  if (path === '/login') {
    return { page: 'login' };
  }
  if (path === '/account') {
    return { page: 'account' };
  }
  if (path === '/admin' || path === '/admin/users') {
    // The bare admin address lands on the first admin page (QA P17), which tells an Editor it is not theirs.
    return { page: 'adminUsers' };
  }
  if (path === '/admin/security') {
    return { page: 'adminSecurity' };
  }
  if (path === '/admin/tokens') {
    return { page: 'adminTokens' };
  }
  if (path === '/admin/branding') {
    return { page: 'adminBranding' };
  }
  if (path === '/admin/themes') {
    return { page: 'adminThemes' };
  }
  if (path === '/admin/ai') {
    return { page: 'adminAi' };
  }
  if (path === '/admin/email') {
    return { page: 'adminEmail' };
  }
  if (path === '/reset-password') {
    return { page: 'resetPassword' };
  }
  if (path === '/admin/status') {
    return { page: 'adminStatus' };
  }
  if (path === '/admin/connection') {
    return { page: 'adminConnection' };
  }
  if (path === '/about') {
    return { page: 'about' };
  }
  const document = /^\/d\/([^/]+)$/.exec(path);
  if (document?.[1] !== undefined) {
    try {
      return { page: 'document', id: decodeURIComponent(document[1]) };
    } catch {
      return { page: 'notFound', path };
    }
  }
  return { page: 'notFound', path };
}

/**
 * A leave guard: told that the user is about to leave the current page, it calls `proceed` to let them (now, or later
 * after asking) or never calls it to keep them here. One at a time - the open editor while it holds unsaved edits.
 */
export type LeaveBlocker = (proceed: () => void) => void;

let blocker: LeaveBlocker | undefined;
// The location the app itself last put on screen. A popstate whose location differs was the browser's (back, forward),
// not ours, and is the one a guard has to undo before it renders.
let shownHref = '';
// Where that location sits in this tab's history. Every entry the app creates carries a `{ draughtsmanIndex }` state, so
// a blocked back or forward can be undone with `history.go` (no entry added) by the distance it moved.
let shownIndex = 0;

const INDEX_KEY = 'draughtsmanIndex';

function currentHref(): string {
  return window.location.pathname + window.location.search + window.location.hash;
}

function indexOf(state: unknown): number | undefined {
  const value = typeof state === 'object' && state !== null ? (state as Record<string, unknown>)[INDEX_KEY] : undefined;
  return typeof value === 'number' ? value : undefined;
}

function stamp(index: number): Record<string, number> {
  return { [INDEX_KEY]: index };
}

/** Takes the location on screen as the one the app is showing; the first entry of a fresh tab has no index yet. */
function adoptLocation(): void {
  shownHref = currentHref();
  shownIndex = indexOf(window.history.state) ?? 0;
}

// The route state's subscribers: told after the guard has had its say, and never for a navigation it held back.
const subscribers = new Set<() => void>();
let listening = false;

function notify(): void {
  for (const subscriber of [...subscribers]) {
    subscriber();
  }
}

/**
 * The one `popstate` listener. Browsers run listeners on `window` in registration order, capture flag or not (Chrome
 * does; the route state used to register first, so it unmounted the dirty editor, and with it the guard, before the
 * guard's own listener looked), so the guard cannot be a second listener that hopes to run first. Instead this decides
 * before anyone else hears of the change: a browser back or forward the guard holds is undone and is never passed on;
 * anything else updates the shown location and goes to the subscribers.
 */
function onPopState(event: PopStateEvent): void {
  if (blocker !== undefined && currentHref() !== shownHref) {
    holdBrowserNavigation(blocker, event);
    return;
  }
  shownHref = currentHref();
  shownIndex = indexOf(event.state) ?? shownIndex;
  notify();
}

function holdBrowserNavigation(hold: LeaveBlocker, event: PopStateEvent): void {
  const target = currentHref();
  const targetIndex = indexOf(event.state);
  // History cannot be vetoed, only undone: the browser has already moved, so move it back. `go` by the distance
  // travelled adds no entry, so Cancel leaves the history exactly as it was; its own popstate lands on the shown
  // location and is a no-op. An entry with no index (made before the app stamped it) cannot be measured, and is put
  // right by pushing the current page over it instead.
  if (targetIndex !== undefined && targetIndex !== shownIndex) {
    window.history.go(shownIndex - targetIndex);
  } else {
    shownIndex += 1;
    window.history.pushState(stamp(shownIndex), '', shownHref);
  }
  hold(() => {
    // Rewrites the entry the app is sitting on to where the user was headed, then routes there without asking again.
    window.history.replaceState(stamp(shownIndex), '', target);
    shownHref = currentHref();
    notify();
  });
}

function syncListener(): void {
  const wanted = subscribers.size > 0 || blocker !== undefined;
  if (wanted && !listening) {
    window.addEventListener('popstate', onPopState);
  } else if (!wanted && listening) {
    window.removeEventListener('popstate', onPopState);
  }
  listening = wanted;
}

function subscribe(subscriber: () => void): () => void {
  if (subscribers.size === 0 && blocker === undefined) {
    adoptLocation();
  }
  subscribers.add(subscriber);
  syncListener();
  return () => {
    subscribers.delete(subscriber);
    syncListener();
  };
}

/** The current location's pathname, updated on history navigation. */
export function usePathname(): string {
  const [pathname, setPathname] = useState(() => window.location.pathname);
  useEffect(
    () =>
      subscribe(() => {
        setPathname(window.location.pathname);
      }),
    [],
  );
  return pathname;
}

/**
 * Installs (or, with undefined, removes) the leave guard for in-app navigation and the browser's back and forward.
 * Closing or reloading the tab is not covered here: only the browser's own `beforeunload` prompt can ask for that.
 */
export function setLeaveBlocker(next: LeaveBlocker | undefined): void {
  if (next !== undefined && blocker === undefined) {
    adoptLocation();
  }
  blocker = next;
  syncListener();
}

/** Runs `proceed` once the leave guard, if there is one, lets the user go. For an action that has to happen before a `navigate`. */
export function guardedLeave(proceed: () => void): void {
  if (blocker === undefined) {
    proceed();
  } else {
    blocker(proceed);
  }
}

/**
 * Client-side navigation for in-app links, which the leave guard may hold back. `force` skips it, for a caller that has
 * already asked (`guardedLeave`) before doing the work that ends here.
 */
export function navigate(path: string, options: { force?: boolean } = {}): void {
  const go = (): void => {
    shownIndex += 1;
    window.history.pushState(stamp(shownIndex), '', path);
    shownHref = currentHref();
    notify();
  };
  if (options.force === true) {
    go();
  } else {
    guardedLeave(go);
  }
}
