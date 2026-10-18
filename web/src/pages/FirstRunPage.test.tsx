// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { AuthStatus, AuthUser } from '../model/authClient.ts';
import { FirstRunPage } from './FirstRunPage.tsx';

function themeResponse(): Response {
  return new Response(JSON.stringify({ name: 'default', productName: 'Acme Diagrams', logo: null, favicon: null, mode: 'system' }), { status: 200 });
}

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function routeFetch(onFirstRun: () => Response): typeof fetch {
  return (input: RequestInfo | URL) => {
    const url = requestUrl(input);
    if (url === '/api/theme') {
      return Promise.resolve(themeResponse());
    }
    if (url === '/api/auth/first-run') {
      return Promise.resolve(onFirstRun());
    }
    throw new Error(`unexpected fetch: ${url}`);
  };
}

function flush(): Promise<void> {
  return act(() => Promise.resolve());
}

describe('FirstRunPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('mentions the minimum password length and disables submit until the form is valid', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response('{}', { status: 201 }))));
    render(<FirstRunPage onSignedIn={() => undefined} />);
    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams')).toBeTruthy();
    });
    expect(screen.getByText(/at least 12 characters/i)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Create administrator' })).toHaveProperty('disabled', true);
  });

  it('creates the administrator and calls onSignedIn', async () => {
    const onSignedIn = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(
        routeFetch(
          () =>
            new Response(
              JSON.stringify({ id: 'usr_1', email: 'e@x.com', displayName: 'Eric', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true }),
              { status: 201 },
            ),
        ),
      ),
    );
    render(<FirstRunPage onSignedIn={onSignedIn} />);

    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Eric' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'a-long-enough-password' } });
    fireEvent.change(screen.getByLabelText('Setup code'), { target: { value: 'ABCDEFGHJK' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create administrator' }));

    await flush();
    await waitFor(() => {
      expect(onSignedIn).toHaveBeenCalled();
    });
  });

  it('shows a 409 as an inline error rather than throwing', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(
        routeFetch(
          () =>
            new Response(JSON.stringify({ title: 'First run already completed', status: 409, detail: 'An administrator already exists; sign in instead.' }), {
              status: 409,
            }),
        ),
      ),
    );
    render(<FirstRunPage onSignedIn={() => undefined} />);

    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Eric' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'a-long-enough-password' } });
    fireEvent.change(screen.getByLabelText('Setup code'), { target: { value: 'ABCDEFGHJK' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create administrator' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveProperty('textContent', 'An administrator already exists; sign in instead.');
    });
  });
});


describe('FirstRunPage setup-code help (PR-02)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function mount(hints?: Parameters<typeof FirstRunPage>[0]['hints']) {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response('{}', { status: 201 }))));
    return render(<FirstRunPage onSignedIn={() => undefined} {...(hints === undefined ? {} : { hints })} />);
  }

  it('names the data folder the server is running from', async () => {
    mount({ dataFolder: '/srv/draughtsman-data', host: 'console' });
    await waitFor(() => {
      expect(screen.getByText(/Draughtsman is running from/)).toBeTruthy();
    });
    expect(screen.getByText('/srv/draughtsman-data').tagName).toBe('CODE');
  });

  it.each([
    ['container', /docker logs/, 'Docker'],
    ['systemd', /journalctl -u draughtsman/, 'Linux service'],
    ['launchd', /\/srv\/d\/logs\/draughtsman\.log/, 'macOS service'],
    ['windowsService', /first-run-code --DRAUGHTSMAN_DATA "\/srv\/d"/, 'Windows service'],
    ['console', /window that started Draughtsman/, 'Running in a terminal'],
  ] as const)('keeps the help closed until asked, then names how to read the log and marks the detected host (%s)', async (host, how, title) => {
    const { container } = mount({ dataFolder: '/srv/d', host });
    await waitFor(() => {
      expect(screen.getByText('Where do I find it?')).toBeTruthy();
    });
    const details = container.querySelector('details.auth-help');
    // Closed by default (QA P12) so Create administrator stays on screen in a short window; the person opens it when they need it.
    expect(details?.hasAttribute('open')).toBe(false);
    details?.setAttribute('open', '');
    const detected = container.querySelector('li[data-detected="true"]');
    expect(detected?.textContent).toMatch(how);
    expect(detected?.textContent).toContain(title);
    expect(detected?.textContent).toContain('This server looks like this.');
    // Only the recognised host is marked; every way is still listed for someone whose guess was wrong.
    expect(container.querySelectorAll('li[data-detected="true"]')).toHaveLength(1);
    expect(container.querySelectorAll('details.auth-help li')).toHaveLength(5);
  });

  it('lists every way but stays closed, with no folder line, when the server sent no hints', async () => {
    const { container } = mount();
    await waitFor(() => {
      expect(screen.getByText('Where do I find it?')).toBeTruthy();
    });
    expect(container.querySelector('details.auth-help')?.hasAttribute('open')).toBe(false);
    expect(container.querySelector('li[data-detected="true"]')).toBeNull();
    expect(screen.queryByText(/Draughtsman is running from/)).toBeNull();
    expect(container.querySelector('details.auth-help')?.textContent).toContain('<data folder>/logs/draughtsman.log');
  });

  it('never prints a setup code: the field starts empty', async () => {
    mount({ dataFolder: '/srv/d', host: 'console' });
    await waitFor(() => {
      expect(screen.getByLabelText('Setup code')).toBeTruthy();
    });
    expect(screen.getByLabelText<HTMLInputElement>('Setup code').value).toBe('');
  });
});

describe('FirstRunPage when the browser keeps no session (8.1)', () => {
  const user: AuthUser = { id: 'usr_1', email: 'e@x.com', displayName: 'Eric', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function fill(): void {
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Eric' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'a-long-enough-password' } });
    fireEvent.change(screen.getByLabelText('Setup code'), { target: { value: 'ABCDEFGHJK' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create administrator' }));
  }

  it('leaves the busy state and says why when the refreshed status still has no user', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response(JSON.stringify(user), { status: 201 }))));
    const noSession: AuthStatus = { firstRunRequired: false, user: null, passwordReset: { selfService: false } };
    render(<FirstRunPage onSignedIn={() => Promise.resolve(noSession)} />);

    fill();

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe(
        'Signed in, but your browser did not keep the session. If this address starts with http:// while forceHttps is on, use https://.',
      );
    });
    expect(screen.getByLabelText('Email')).toHaveProperty('disabled', false);
    expect(screen.getByLabelText('Setup code')).toHaveProperty('disabled', false);
    expect(screen.getByRole('button', { name: 'Create administrator' })).toHaveProperty('disabled', false);
  });

  it('shows no such message when the refreshed status carries the user', async () => {
    const onSignedIn = vi.fn(() => Promise.resolve<AuthStatus>({ firstRunRequired: false, user, passwordReset: { selfService: false } }));
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(routeFetch(() => new Response(JSON.stringify(user), { status: 201 }))));
    render(<FirstRunPage onSignedIn={onSignedIn} />);

    fill();

    await waitFor(() => {
      expect(onSignedIn).toHaveBeenCalled();
    });
    await flush();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
