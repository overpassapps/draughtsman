// @vitest-environment jsdom
import { render, screen, waitFor, within } from '@testing-library/react';
import type { AdminStatus } from '../model/statusClient.ts';
import { StatusPage } from './StatusPage.tsx';

const user = (role: 'Admin' | 'Editor') => ({ id: 'usr_1', email: 'e@x.com', displayName: 'E', role, createdAt: '2026-01-01T00:00:00.000Z', active: true }) as const;

const HEALTHY: AdminStatus = {
  version: '0.1.0',
  buildDate: '2026-09-30',
  dataFolder: '/srv/draughtsman',
  host: 'systemd',
  logHint: 'The journal: journalctl -u draughtsman (use the unit name the service was installed under).',
  storage: { provider: 'sqlite', file: '/srv/draughtsman/data.db' },
  sidecar: { status: 'running', version: '0.1.0', node: 'v22.19.0', restarts: 0 },
  pdf: { enabled: true, available: true },
  ai: { enabled: true, provider: 'ollama', model: 'llama3' },
};

function serve(status: AdminStatus | number): string[] {
  const calls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      calls.push(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
      return Promise.resolve(typeof status === 'number' ? new Response('{}', { status }) : new Response(JSON.stringify(status), { status: 200 }));
    }),
  );
  return calls;
}

const row = (key: string): HTMLElement => {
  const found = document.querySelector<HTMLElement>(`[data-row="${key}"]`);
  if (found === null) {
    throw new Error(`no status row ${key}`);
  }
  return found;
};

describe('StatusPage (PR-05)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows every component as OK on a healthy instance, with no degraded banner', async () => {
    serve(HEALTHY);
    render(<StatusPage currentUser={user('Admin')} />);

    await screen.findByRole('heading', { name: 'System status' });
    await waitFor(() => {
      expect(row('sidecar')).toBeTruthy();
    });
    for (const key of ['sidecar', 'pdf', 'ai', 'storage', 'data']) {
      expect(row(key).getAttribute('data-tone')).toBe('ok');
    }
    expect(within(row('sidecar')).getByText('OK')).toBeTruthy();
    expect(within(row('storage')).getByText(/data\.db/)).toBeTruthy();
    expect(within(row('data')).getByText('/srv/draughtsman')).toBeTruthy();
    expect(within(row('data')).getByText(/journalctl -u draughtsman/)).toBeTruthy();
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('says what stops working when the layout service is down, in words, with the supervisor error and a banner', async () => {
    serve({ ...HEALTHY, sidecar: { status: 'unavailable', restarts: 3, error: 'Node was not found on PATH' } });
    render(<StatusPage currentUser={user('Admin')} />);

    await waitFor(() => {
      expect(row('sidecar')).toBeTruthy();
    });
    expect(row('sidecar').getAttribute('data-tone')).toBe('problem');
    expect(within(row('sidecar')).getByText('Not running')).toBeTruthy();
    expect(within(row('sidecar')).getByText(/Layout, Tidy, SQL import/)).toBeTruthy();
    expect(within(row('sidecar')).getByText('Node was not found on PATH')).toBeTruthy();
    expect(screen.getByRole('status').textContent).toMatch(/layout service is not running/);
  });

  it('puts the peer address, the scheme and the check summary in a Connection row that links to the check', async () => {
    serve({
      ...HEALTHY,
      connection: { peer: '172.17.0.1', peerTrusted: false, forwardedHeadersSeen: true, scheme: 'http', throttleAddress: '172.17.0.1', checks: { pass: 5, warn: 1, fail: 0 }, problems: ['Reverse proxy and client address'] },
    });
    render(<StatusPage currentUser={user('Admin')} />);

    await waitFor(() => {
      expect(row('connection')).toBeTruthy();
    });
    expect(row('connection').getAttribute('data-tone')).toBe('warn');
    expect(within(row('connection')).getByText('Check')).toBeTruthy();
    expect(within(row('connection')).getAllByText('172.17.0.1').length).toBeGreaterThan(0);
    expect(row('connection').textContent).toContain('NOT trusted');
    expect(row('connection').textContent).toContain('1 warning');
    expect(row('connection').textContent).toContain('Reverse proxy and client address');
    expect(within(row('connection')).getByRole('link', { name: 'Open the connection check' }).getAttribute('href')).toBe('/admin/connection');
  });

  it('calls a failing connection check a problem, a clean one OK, and shows no row for a server that predates it', async () => {
    const base = { peer: '10.0.0.2', peerTrusted: true, forwardedHeadersSeen: true, scheme: 'https', throttleAddress: '198.51.100.7', problems: [] as string[] };
    serve({ ...HEALTHY, connection: { ...base, checks: { pass: 4, warn: 0, fail: 1 }, problems: ['Free disk space'] } });
    const failing = render(<StatusPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(row('connection').getAttribute('data-tone')).toBe('problem');
    });
    expect(within(row('connection')).getByText('Problem')).toBeTruthy();
    failing.unmount();

    serve({ ...HEALTHY, connection: { ...base, checks: { pass: 6, warn: 0, fail: 0 } } });
    const clean = render(<StatusPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(row('connection').getAttribute('data-tone')).toBe('ok');
    });
    expect(within(row('connection')).getByText('OK')).toBeTruthy();
    expect(row('connection').textContent).toContain('a proxy this server trusts');
    clean.unmount();

    serve(HEALTHY);
    render(<StatusPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(row('storage')).toBeTruthy();
    });
    expect(document.querySelector('[data-row="connection"]')).toBeNull();
  });

  it('tells an optional feature that is switched off (PDF, AI) from a fault: Off, not a problem', async () => {
    serve({ ...HEALTHY, pdf: { enabled: false, available: false }, ai: { enabled: false } });
    render(<StatusPage currentUser={user('Admin')} />);

    await waitFor(() => {
      expect(row('pdf')).toBeTruthy();
    });
    expect(within(row('pdf')).getByText('Off')).toBeTruthy();
    expect(row('pdf').getAttribute('data-tone')).toBe('off');
    expect(within(row('pdf')).getByText('export.chromium.enabled')).toBeTruthy();
    expect(within(row('ai')).getByText('Off')).toBeTruthy();
    expect(row('ai').getAttribute('data-tone')).toBe('off');
  });

  it('calls a PDF component that is enabled but cannot be used a problem', async () => {
    serve({ ...HEALTHY, pdf: { enabled: true, available: false, path: '/opt/chrome/chrome' } });
    render(<StatusPage currentUser={user('Admin')} />);

    await waitFor(() => {
      expect(row('pdf')).toBeTruthy();
    });
    expect(row('pdf').getAttribute('data-tone')).toBe('problem');
    expect(within(row('pdf')).getByText('Not installed')).toBeTruthy();
    expect(within(row('pdf')).getByText('/opt/chrome/chrome')).toBeTruthy();
  });

  it('shows the server hint for a PDF component that is enabled but did not launch, and none when it works', async () => {
    serve({ ...HEALTHY, pdf: { enabled: true, available: false, hint: 'Install it with npx playwright@1.2.3 install chromium.' } });
    const { unmount } = render(<StatusPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(row('pdf')).toBeTruthy();
    });
    expect(row('pdf').textContent).toContain('npx playwright@1.2.3 install chromium');
    unmount();

    serve({ ...HEALTHY, pdf: { enabled: true, available: true } });
    render(<StatusPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(within(row('pdf')).getByText('OK')).toBeTruthy();
    });
    expect(row('pdf').textContent).not.toContain('playwright');
  });

  it('sends an Editor nothing: no request, and the same message the other admin pages give', () => {
    const calls = serve(HEALTHY);
    render(<StatusPage currentUser={user('Editor')} />);

    expect(screen.getByRole('alert').textContent).toMatch(/administrator/);
    expect(calls).toEqual([]);
    expect(document.querySelector('[data-row]')).toBeNull();
  });

  it('shows the refusal, with Retry, when the server says no', async () => {
    serve(403);
    render(<StatusPage currentUser={user('Admin')} />);

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toMatch(/Only an administrator can see this/);
    });
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });
});
