// @vitest-environment jsdom
/**
 * Accessibility of the admin Connection check (design/atlas/login-admin.md, the admin pages' rules): axe-core over the page with findings in every state
 * (a pass, a warning with its fix and facts open, a failure), the same pattern as recoveryAxe.test.tsx. Colour contrast is off in jsdom (it has no layout)
 * and is checked at token compile instead. The page is rendered inside a `<main>` because the app shell supplies it in the real app.
 */
import { render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import type { AuthUser } from '../model/authClient.ts';
import { ConnectionCheckPage } from '../pages/ConnectionCheckPage.tsx';

const ADMIN: AuthUser = { id: 'usr_a', email: 'ada@acme.example', displayName: 'Ada Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };

const SERVER = {
  generatedAt: '2026-10-06T12:00:00.000Z',
  summary: { pass: 1, warn: 1, fail: 0 },
  items: [
    {
      id: 'proxy',
      title: 'Reverse proxy and client address',
      status: 'warn',
      summary: 'Proxy headers arrived from 172.17.0.1, which is not listed in server.knownProxies.',
      detail: ['Add it to server.knownProxies and restart.'],
      facts: { 'Direct peer address': '172.17.0.1', 'X-Forwarded-For': '198.51.100.7' },
      fix: { yaml: 'server:\n  knownProxies:\n    - "172.17.0.1"', nginx: 'proxy_set_header X-Forwarded-Proto $scheme;', caddy: 'Caddy already sends them.', cloudflare: 'Nothing to change in Cloudflare.' },
    },
    { id: 'disk', title: 'Free disk space', status: 'pass', summary: '50 GiB free of 100 GiB.', facts: { Free: '50 GiB' } },
  ],
};

const TRIP = {
  generatedAt: '2026-10-06T12:00:00.000Z',
  summary: { pass: 0, warn: 0, fail: 1 },
  items: [{ id: 'cookie', title: 'Session cookie round trip', status: 'fail', summary: 'The browser did not send the test cookie back.', fix: { nginx: '# look for proxy_hide_header Set-Cookie' } }],
};

async function violations(root: Element): Promise<string[]> {
  const result = await axe.run(root, { rules: { 'color-contrast': { enabled: false } }, resultTypes: ['violations'] });
  return result.violations.flatMap((violation) => violation.nodes.map((node) => `${violation.id}: ${node.target.join(' ')}`));
}

describe('Connection check accessibility', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('has no axe violations with a pass, a warning and a failure on the page', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>((input) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
        if (url.startsWith('/api/admin/selfcheck/echo')) {
          return Promise.resolve(new Response(JSON.stringify({ step: 1, tag: '"sc-1"', cookieReceived: false, cookieWasSecure: false, scheme: 'http', serverTime: '2026-10-06T12:00:00.000Z' }), { status: 200, headers: { ETag: '"sc-1"' } }));
        }
        return Promise.resolve(new Response(JSON.stringify(url === '/api/admin/selfcheck/roundtrip' ? TRIP : SERVER), { status: 200 }));
      }),
    );
    const { container } = render(
      <main>
        <ConnectionCheckPage currentUser={ADMIN} />
      </main>,
    );
    await waitFor(() => {
      expect(document.querySelector('[data-row="cookie"]')).toBeTruthy();
      expect(document.querySelector('[data-row="proxy"]')).toBeTruthy();
    });
    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toMatch(/1 failed, 1 warning, 1 passed\./);
    });

    expect(await violations(container)).toEqual([]);
  });

  it('has no axe violations while it is still running, and when a half failed', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => new Promise<Response>(() => undefined)));
    const running = render(
      <main>
        <ConnectionCheckPage currentUser={ADMIN} />
      </main>,
    );
    expect(await violations(running.container)).toEqual([]);
    running.unmount();

    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 500 }))));
    const failed = render(
      <main>
        <ConnectionCheckPage currentUser={ADMIN} />
      </main>,
    );
    await waitFor(() => {
      expect(screen.getAllByRole('alert').length).toBeGreaterThan(0);
    });
    expect(await violations(failed.container)).toEqual([]);
  });
});
