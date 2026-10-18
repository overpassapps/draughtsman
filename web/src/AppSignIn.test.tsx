// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from './App.tsx';

/**
 * The browser-side half of 8.1: the server answers the sign-in or first-run call 200 (the account exists, the
 * session was issued) but the browser throws the Secure cookie away (forceHttps reached over plain http), so the
 * very next GET /api/auth/status says nobody is signed in. The page must come back to life and say why.
 */
const NOT_KEPT = 'Signed in, but your browser did not keep the session. If this address starts with http:// while forceHttps is on, use https://.';

const user = { id: 'usr_1', email: 'e@x.com', displayName: 'Eric', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** `states` is what GET /api/auth/status answers, one entry per call (the last repeats). */
function serverFetch(states: object[]): typeof fetch {
  let statusCalls = 0;
  return (input: RequestInfo | URL) => {
    const url = requestUrl(input);
    if (url === '/api/theme') {
      return Promise.resolve(json({ name: 'default', productName: 'Acme Diagrams', logo: null, favicon: null, mode: 'system' }));
    }
    if (url === '/api/auth/status') {
      const state = states[Math.min(statusCalls, states.length - 1)] ?? {};
      statusCalls += 1;
      return Promise.resolve(json(state));
    }
    if (url === '/api/auth/login') {
      return Promise.resolve(json(user));
    }
    if (url === '/api/auth/first-run') {
      return Promise.resolve(json(user, 201));
    }
    return Promise.resolve(json({}, 404));
  };
}

describe('App sign-in when the browser keeps no session (8.1)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sign-in: re-enables the form and explains, instead of freezing on "Signing in…"', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(serverFetch([{ firstRunRequired: false }])));
    render(<App />);

    await waitFor(() => {
      expect(screen.getByLabelText('Email')).toBeTruthy();
    });
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe(NOT_KEPT);
    });
    expect(screen.getByLabelText('Email')).toHaveProperty('disabled', false);
    expect(screen.getByLabelText('Password')).toHaveProperty('disabled', false);
  });

  it('first run: the sign-in page that replaces it carries the same explanation', async () => {
    // First answer: no administrator yet. Second (after the 201): the administrator exists, but no session came with it.
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(serverFetch([{ firstRunRequired: true }, { firstRunRequired: false }])));
    render(<App />);

    await waitFor(() => {
      expect(screen.getByLabelText('Setup code')).toBeTruthy();
    });
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Eric' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'a-long-enough-password' } });
    fireEvent.change(screen.getByLabelText('Setup code'), { target: { value: 'ABCDEFGHJK' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create administrator' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe(NOT_KEPT);
    });
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeTruthy();
  });

  it('shows no such notice when the session was kept', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(serverFetch([{ firstRunRequired: false }, { firstRunRequired: false, user }])),
    );
    render(<App />);

    await waitFor(() => {
      expect(screen.getByLabelText('Email')).toBeTruthy();
    });
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.queryByLabelText('Password')).toBeNull();
    });
    expect(screen.queryByText(NOT_KEPT)).toBeNull();
  });
});
