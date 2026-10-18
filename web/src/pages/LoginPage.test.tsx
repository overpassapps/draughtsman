// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { clearSessionEndedNotice, currentSessionEndedNotice, noteSessionEnded } from '../model/sessionEnded.ts';
import type { AuthStatus, AuthUser } from '../model/authClient.ts';
import { LoginPage } from './LoginPage.tsx';

function themeResponse(): Response {
  return new Response(JSON.stringify({ name: 'default', productName: 'Acme Diagrams', logo: null, favicon: null, mode: 'system' }), { status: 200 });
}

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function routeFetch(onLogin: (body: unknown) => Response): typeof fetch {
  return (input: RequestInfo | URL, init?: RequestInit) => {
    const url = requestUrl(input);
    if (url === '/api/theme') {
      return Promise.resolve(themeResponse());
    }
    if (url === '/api/auth/login') {
      const body: unknown = init?.body === undefined ? undefined : JSON.parse(init.body as string);
      return Promise.resolve(onLogin(body));
    }
    throw new Error(`unexpected fetch: ${url}`);
  };
}

function flush(): Promise<void> {
  return act(() => Promise.resolve());
}

describe('LoginPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the chrome theme product name and focuses the email field', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response('{}', { status: 200 }))));
    render(<LoginPage onSignedIn={() => undefined} />);
    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams')).toBeTruthy();
    });
    expect(document.activeElement).toBe(screen.getByLabelText('Email'));
  });

  it('signs in and calls onSignedIn', async () => {
    const onSignedIn = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(
        routeFetch(() => new Response(JSON.stringify({ id: 'usr_1', email: 'e@x.com', displayName: 'E', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true }), { status: 200 })),
      ),
    );
    render(<LoginPage onSignedIn={onSignedIn} />);

    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await flush();
    await waitFor(() => {
      expect(onSignedIn).toHaveBeenCalled();
    });
  });

  it('shows the exact generic error on a failed sign-in', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(
        routeFetch(
          () =>
            new Response(JSON.stringify({ title: 'Sign-in failed', status: 401, detail: 'Email or password is incorrect.' }), {
              status: 401,
            }),
        ),
      ),
    );
    render(<LoginPage onSignedIn={() => undefined} />);

    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveProperty('textContent', 'Email or password is incorrect.');
    });
  });
});

describe('LoginPage session-ended notice (QA S9)', () => {
  beforeEach(() => {
    clearSessionEndedNotice();
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response('{}', { status: 200 }))));
  });
  afterEach(() => {
    clearSessionEndedNotice();
    vi.unstubAllGlobals();
  });

  it('says nothing when the person simply arrived', async () => {
    render(<LoginPage onSignedIn={() => undefined} />);
    await flush();
    expect(screen.queryByTestId('session-ended')).toBeNull();
  });

  it('says why a session that was working a moment ago is gone, and never names who ended it', async () => {
    noteSessionEnded('revoked');
    render(<LoginPage onSignedIn={() => undefined} />);
    await flush();
    const notice = screen.getByTestId('session-ended');
    expect(notice.textContent).toMatch(/signed out because your password was changed/);
    expect(notice.textContent).not.toMatch(/administrator (reset|changed)|by an admin/i);
    expect(notice.textContent).not.toMatch(/kept in this browser/);
    expect(notice.getAttribute('role')).toBe('status');
  });

  it.each([
    ['deactivated', /account was deactivated/],
    ['expired', /session expired/],
    ['unknown', /no longer valid/],
  ] as const)('tells a %s session apart', async (reason, words) => {
    noteSessionEnded(reason);
    render(<LoginPage onSignedIn={() => undefined} />);
    await flush();
    expect(screen.getByTestId('session-ended').textContent).toMatch(words);
  });

  it('adds that unsaved changes were kept in this browser only when an editor really kept them', async () => {
    const unsubscribe = (await import('../model/sessionEnded.ts')).onSessionEnded(() => true);
    noteSessionEnded('revoked');
    unsubscribe();
    render(<LoginPage onSignedIn={() => undefined} />);
    await flush();
    expect(screen.getByTestId('session-ended').textContent).toMatch(/changes you had not saved are kept in this browser/);
  });

  it('forgets the notice once the person has signed in', async () => {
    noteSessionEnded('expired');
    render(<LoginPage onSignedIn={() => undefined} />);
    await flush();
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'a@b.c' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => { expect(currentSessionEndedNotice()).toBeUndefined(); });
  });
});

describe('LoginPage when the browser keeps no session (8.1)', () => {
  const user: AuthUser = { id: 'usr_1', email: 'e@x.com', displayName: 'E', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };
  const noSession: AuthStatus = { firstRunRequired: false, user: null, passwordReset: { selfService: false } };

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function signIn(): void {
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  }

  it('leaves the busy state and says why when the refreshed status still has no user', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response(JSON.stringify(user), { status: 200 }))));
    render(<LoginPage onSignedIn={() => Promise.resolve(noSession)} />);

    signIn();

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe(
        'Signed in, but your browser did not keep the session. If this address starts with http:// while forceHttps is on, use https://.',
      );
    });
    expect(screen.getByLabelText('Email')).toHaveProperty('disabled', false);
    expect(screen.getByLabelText('Password')).toHaveProperty('disabled', false);
    expect(screen.getByRole('button', { name: 'Sign in' })).toHaveProperty('disabled', false);
  });

  it('shows no such message when the refreshed status carries the user', async () => {
    const onSignedIn = vi.fn(() => Promise.resolve<AuthStatus>({ firstRunRequired: false, user, passwordReset: { selfService: false } }));
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response(JSON.stringify(user), { status: 200 }))));
    render(<LoginPage onSignedIn={onSignedIn} />);

    signIn();

    await waitFor(() => {
      expect(onSignedIn).toHaveBeenCalled();
    });
    await flush();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('shows the notice App passes when this page was mounted after a first run that kept no session', () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response('{}', { status: 200 }))));
    render(<LoginPage onSignedIn={() => undefined} sessionNotKept />);
    expect(screen.getByRole('alert').textContent).toContain('your browser did not keep the session');
  });
});
