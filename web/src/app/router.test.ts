// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import { guardedLeave, matchRoute, navigate, setLeaveBlocker, usePathname } from './router.ts';

describe('matchRoute password recovery', () => {
  it('knows the page a reset link opens and the admin Email page, with or without a trailing slash', () => {
    expect(matchRoute('/reset-password')).toEqual({ page: 'resetPassword' });
    expect(matchRoute('/reset-password/')).toEqual({ page: 'resetPassword' });
    expect(matchRoute('/admin/email')).toEqual({ page: 'adminEmail' });
    expect(matchRoute('/reset-password/extra')).toEqual({ page: 'notFound', path: '/reset-password/extra' });
  });
});

describe('matchRoute', () => {
  it('matches the three pages', () => {
    expect(matchRoute('/')).toEqual({ page: 'home' });
    expect(matchRoute('/example')).toEqual({ page: 'example' });
    expect(matchRoute('/example/')).toEqual({ page: 'example' });
    expect(matchRoute('/try')).toEqual({ page: 'try' });
    expect(matchRoute('/d/doc_order_service')).toEqual({ page: 'document', id: 'doc_order_service' });
  });

  it('matches the admin AI settings page', () => {
    expect(matchRoute('/admin/ai')).toEqual({ page: 'adminAi' });
    expect(matchRoute('/admin/ai/')).toEqual({ page: 'adminAi' });
  });

  it('matches the account and security log pages', () => {
    expect(matchRoute('/account')).toEqual({ page: 'account' });
    expect(matchRoute('/account/')).toEqual({ page: 'account' });
    expect(matchRoute('/admin/security')).toEqual({ page: 'adminSecurity' });
    expect(matchRoute('/admin/security/')).toEqual({ page: 'adminSecurity' });
    expect(matchRoute('/admin/security/extra')).toEqual({ page: 'notFound', path: '/admin/security/extra' });
  });

  it('matches the admin Connection check page, with or without a trailing slash, and nothing beneath it', () => {
    expect(matchRoute('/admin/connection')).toEqual({ page: 'adminConnection' });
    expect(matchRoute('/admin/connection/')).toEqual({ page: 'adminConnection' });
    expect(matchRoute('/admin/connection/extra')).toEqual({ page: 'notFound', path: '/admin/connection/extra' });
  });

  it('no longer has an admin Licence page', () => {
    expect(matchRoute('/admin/licence')).toEqual({ page: 'notFound', path: '/admin/licence' });
  });

  it('decodes the document id', () => {
    expect(matchRoute('/d/a%20b')).toEqual({ page: 'document', id: 'a b' });
  });

  it('reports a malformed percent-encoded id as not found instead of throwing', () => {
    expect(matchRoute('/d/%E0%A4%A')).toEqual({ page: 'notFound', path: '/d/%E0%A4%A' });
  });

  it('reports anything else as not found', () => {
    expect(matchRoute('/d/')).toEqual({ page: 'notFound', path: '/d' });
    expect(matchRoute('/d/x/y')).toEqual({ page: 'notFound', path: '/d/x/y' });
    expect(matchRoute('/nope')).toEqual({ page: 'notFound', path: '/nope' });
  });
});

/** Calls what the guard was handed; a function so the caller's own narrowing of `held.proceed` cannot see through it. */
function release(held: { proceed: (() => void) | undefined }): void {
  held.proceed?.();
}

describe('the leave guard', () => {
  afterEach(() => {
    setLeaveBlocker(undefined);
  });

  it('runs a navigation straight away with no guard, and hands it to the guard when there is one', () => {
    const route = renderHook(() => usePathname());
    act(() => { navigate('/a'); });
    expect(route.result.current).toBe('/a');

    const held: { proceed: (() => void) | undefined } = { proceed: undefined };
    setLeaveBlocker((proceed) => { held.proceed = proceed; });
    act(() => { navigate('/b'); });
    expect(window.location.pathname).toBe('/a');
    act(() => { release(held); });
    expect(window.location.pathname).toBe('/b');
    expect(route.result.current).toBe('/b');

    // `force` is a navigation the caller already asked about; guardedLeave is the asking.
    held.proceed = undefined;
    act(() => { navigate('/c', { force: true }); });
    expect(window.location.pathname).toBe('/c');
    expect(held.proceed).toBeUndefined();
    const ran = { value: false };
    guardedLeave(() => { ran.value = true; });
    expect(ran.value).toBe(false);
    act(() => { release(held); });
    expect(ran.value).toBe(true);

    setLeaveBlocker(undefined);
    act(() => { navigate('/d'); });
    expect(window.location.pathname).toBe('/d');
    expect(route.result.current).toBe('/d');
    route.unmount();
  });

  it('undoes a browser back the guard holds, and lets it through when told to proceed', () => {
    window.history.replaceState(null, '', '/first');
    window.history.pushState(null, '', '/second');
    const route = renderHook(() => usePathname());
    const held: { proceed: (() => void) | undefined } = { proceed: undefined };
    setLeaveBlocker((proceed) => { held.proceed = proceed; });

    // Back: the URL has already changed when popstate fires.
    window.history.replaceState(null, '', '/first');
    act(() => { window.dispatchEvent(new PopStateEvent('popstate')); });
    expect(window.location.pathname).toBe('/second');
    expect(route.result.current).toBe('/second');

    act(() => { release(held); });
    expect(window.location.pathname).toBe('/first');
    expect(route.result.current).toBe('/first');
    route.unmount();
  });
});

/**
 * Chrome runs the listeners on `window` in the order they were registered, whatever their capture flag (QA round two,
 * BUG 1); jsdom follows the spec and runs capture listeners first, which is why the ordering never showed here. This
 * records every popstate listener as it is added and fires them in registration order, the way Chrome does.
 */
function chromePopstate() {
  const listeners: EventListenerOrEventListenerObject[] = [];
  const add = window.addEventListener.bind(window);
  const remove = window.removeEventListener.bind(window);
  vi.spyOn(window, 'addEventListener').mockImplementation((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | AddEventListenerOptions) => {
    if (type === 'popstate') {
      listeners.push(listener);
    }
    add(type, listener, options);
  });
  vi.spyOn(window, 'removeEventListener').mockImplementation((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | EventListenerOptions) => {
    if (type === 'popstate') {
      const at = listeners.indexOf(listener);
      if (at >= 0) {
        listeners.splice(at, 1);
      }
    }
    remove(type, listener, options);
  });
  return {
    /** The browser has moved to `url` (entry `index`) and now fires popstate. */
    fire(url: string, index: number | undefined): void {
      window.history.replaceState(index === undefined ? null : { draughtsmanIndex: index }, '', url);
      const event = new PopStateEvent('popstate', { state: window.history.state as unknown });
      const stopped = { value: false };
      event.stopImmediatePropagation = () => { stopped.value = true; };
      for (const listener of [...listeners]) {
        if (stopped.value) {
          break;
        }
        if (typeof listener === 'function') {
          listener.call(window, event);
        } else {
          listener.handleEvent(event);
        }
      }
    },
  };
}

describe('the leave guard against real browser back and forward', () => {
  afterEach(() => {
    setLeaveBlocker(undefined);
    vi.restoreAllMocks();
  });

  /** The editor's situation: the route state mounted first (the app), the guard installed later (the document went dirty). */
  function openDirtyEditor() {
    window.history.replaceState({ draughtsmanIndex: 0 }, '', '/');
    const browser = chromePopstate();
    const route = renderHook(() => usePathname());
    act(() => { navigate('/d/doc'); });
    expect(route.result.current).toBe('/d/doc');
    const held: { proceed: (() => void) | undefined; asked: number } = { proceed: undefined, asked: 0 };
    setLeaveBlocker((proceed) => { held.proceed = proceed; held.asked += 1; });
    const go = vi.spyOn(window.history, 'go').mockImplementation(() => undefined);
    const push = vi.spyOn(window.history, 'pushState');
    return { browser, route, held, go, push };
  }

  it('asks before the route changes when Back arrives with the route listener registered first', () => {
    const { browser, route, held, go, push } = openDirtyEditor();
    act(() => { browser.fire('/', 0); });
    expect(held.asked).toBe(1);
    // The route never moved, so the editor was never unmounted from under its own guard...
    expect(route.result.current).toBe('/d/doc');
    // ...and the history was put right by travelling back one entry, not by adding one.
    expect(go).toHaveBeenCalledWith(1);
    expect(push).not.toHaveBeenCalled();
    act(() => { release(held); });
    expect(route.result.current).toBe('/');
    expect(window.location.pathname).toBe('/');
    route.unmount();
  });

  it('holds Forward too, undoing it the other way', () => {
    const { browser, route, held, go, push } = openDirtyEditor();
    setLeaveBlocker(undefined);
    act(() => { browser.fire('/', 0); });
    expect(route.result.current).toBe('/');
    act(() => { navigate('/d/other'); });
    act(() => { navigate('/d/doc'); });
    act(() => { browser.fire('/d/other', 1); });
    expect(route.result.current).toBe('/d/other');
    setLeaveBlocker((proceed) => { held.proceed = proceed; held.asked += 1; });
    push.mockClear();
    act(() => { browser.fire('/d/doc', 2); });
    expect(held.asked).toBe(1);
    expect(route.result.current).toBe('/d/other');
    expect(go).toHaveBeenCalledWith(-1);
    expect(push).not.toHaveBeenCalled();
    act(() => { release(held); });
    expect(route.result.current).toBe('/d/doc');
    route.unmount();
  });

  it('leaves the history and the page alone when the question is answered Cancel', () => {
    const { browser, route, held, go } = openDirtyEditor();
    act(() => { browser.fire('/', 0); });
    // Cancel never calls proceed. The browser's restoring popstate then lands on the shown page and changes nothing.
    go.mockClear();
    act(() => { browser.fire('/d/doc', 1); });
    expect(held.asked).toBe(1);
    expect(go).not.toHaveBeenCalled();
    expect(route.result.current).toBe('/d/doc');
    // Still guarded afterwards: a second Back asks again.
    act(() => { browser.fire('/', 0); });
    expect(held.asked).toBe(2);
    expect(route.result.current).toBe('/d/doc');
    route.unmount();
  });

  it('pushes the current page back over an entry the app never stamped, since it cannot measure the distance', () => {
    const { browser, route, held, go, push } = openDirtyEditor();
    act(() => { browser.fire('/', undefined); });
    expect(held.asked).toBe(1);
    expect(go).not.toHaveBeenCalled();
    expect(push).toHaveBeenCalledTimes(1);
    expect(window.location.pathname).toBe('/d/doc');
    expect(route.result.current).toBe('/d/doc');
    route.unmount();
  });

  it('follows Back straight away once nothing guards the page', () => {
    const { browser, route } = openDirtyEditor();
    setLeaveBlocker(undefined);
    act(() => { browser.fire('/', 0); });
    expect(route.result.current).toBe('/');
    route.unmount();
  });
});
