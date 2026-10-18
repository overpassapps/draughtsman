// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { CheckItem, SelfCheckResult } from '../model/connectionCheckClient.ts';
import { ConnectionCheckPage } from './ConnectionCheckPage.tsx';

const user = (role: 'Admin' | 'Editor') => ({ id: 'usr_1', email: 'e@x.com', displayName: 'E', role, createdAt: '2026-01-01T00:00:00.000Z', active: true }) as const;

const result = (items: CheckItem[]): SelfCheckResult => ({
  generatedAt: '2026-10-06T12:00:00.000Z',
  summary: { pass: items.filter((i) => i.status === 'pass' || i.status === 'info').length, warn: items.filter((i) => i.status === 'warn').length, fail: items.filter((i) => i.status === 'fail').length },
  items,
});

const SERVER_ITEMS: CheckItem[] = [
  {
    id: 'proxy',
    title: 'Reverse proxy and client address',
    status: 'warn',
    summary: 'Proxy headers arrived from 172.17.0.1, which is not listed in server.knownProxies, so the server IGNORES them.',
    detail: ['If 172.17.0.1 is your reverse proxy, add it to server.knownProxies and restart.'],
    facts: { 'Direct peer address': '172.17.0.1', 'X-Forwarded-For': '198.51.100.7' },
    fix: { yaml: 'server:\n  knownProxies:\n    - "172.17.0.1"', nginx: 'proxy_set_header X-Forwarded-Proto $scheme;', caddy: 'Caddy already sends them.', cloudflare: 'Nothing to change in Cloudflare for this.' },
  },
  { id: 'disk', title: 'Free disk space', status: 'pass', summary: '50 GiB free of 100 GiB.' },
];

const TRIP_ITEMS: CheckItem[] = [
  { id: 'cookie', title: 'Session cookie round trip', status: 'fail', summary: 'The browser did not send the test cookie back.', fix: { nginx: '# look for proxy_hide_header Set-Cookie' } },
  { id: 'etag', title: 'ETag survives the proxy', status: 'warn', summary: 'A proxy or CDN changed the ETag (issue 63).', fix: { cloudflare: 'Turn on Respect Strong ETags (Caching > Configuration).' } },
  { id: 'clock', title: 'Server clock and your clock', status: 'pass', summary: 'The clocks differ by +1 seconds.' },
];

interface Calls {
  urls: string[];
  posted: unknown[];
}

function serve(options: { server?: CheckItem[] | number; trip?: CheckItem[] | number; echoETag?: string | null } = {}): Calls {
  const calls: Calls = { urls: [], posted: [] };
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      calls.urls.push(url);
      if (url.startsWith('/api/admin/selfcheck/echo')) {
        const headers = new Headers({ 'Content-Type': 'application/json' });
        if (options.echoETag !== null) {
          headers.set('ETag', options.echoETag ?? '"sc-1"');
        }
        const step = url.endsWith('step=2') ? 2 : 1;
        return Promise.resolve(new Response(JSON.stringify({ step, tag: '"sc-1"', cookieReceived: step === 2, cookieWasSecure: false, scheme: 'http', serverTime: '2026-10-06T12:00:00.000Z' }), { status: 200, headers }));
      }
      if (url === '/api/admin/selfcheck/roundtrip') {
        calls.posted.push(JSON.parse(init?.body as string));
        const trip = options.trip ?? TRIP_ITEMS;
        return Promise.resolve(typeof trip === 'number' ? new Response('{}', { status: trip }) : new Response(JSON.stringify(result(trip)), { status: 200 }));
      }
      if (url.startsWith('/api/admin/selfcheck')) {
        const server = options.server ?? SERVER_ITEMS;
        return Promise.resolve(typeof server === 'number' ? new Response('{}', { status: server }) : new Response(JSON.stringify(result(server)), { status: 200 }));
      }
      return Promise.resolve(new Response('{}', { status: 404 }));
    }),
  );
  return calls;
}

const row = (key: string): HTMLElement => {
  const found = document.querySelector<HTMLElement>(`[data-row="${key}"]`);
  if (found === null) {
    throw new Error(`no row ${key}`);
  }
  return found;
};

describe('ConnectionCheckPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('runs both halves on opening and shows each finding with a word, not only a colour', async () => {
    const calls = serve();
    render(<ConnectionCheckPage currentUser={user('Admin')} />);

    await screen.findByRole('heading', { name: 'Connection check' });
    await waitFor(() => {
      expect(row('proxy')).toBeTruthy();
      expect(row('cookie')).toBeTruthy();
    });

    expect(within(row('proxy')).getByText('Warning')).toBeTruthy();
    expect(row('proxy').getAttribute('data-tone')).toBe('warn');
    expect(within(row('cookie')).getByText('Fail')).toBeTruthy();
    expect(row('cookie').getAttribute('data-tone')).toBe('problem');
    expect(within(row('disk')).getByText('Pass')).toBeTruthy();
    expect(calls.urls).toContain('/api/admin/selfcheck?pageScheme=http');
    expect(calls.urls.filter((url) => url.startsWith('/api/admin/selfcheck/echo'))).toEqual(['/api/admin/selfcheck/echo?step=1', '/api/admin/selfcheck/echo?step=2']);
    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toMatch(/1 failed, 2 warnings, 2 passed\./);
    });
  });

  it('opens the fix for a warning or a failure, with the text for each place and the facts folded away', async () => {
    serve();
    render(<ConnectionCheckPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(row('proxy')).toBeTruthy();
    });

    const fix = row('proxy').querySelector('details.check-more');
    expect(fix?.hasAttribute('open')).toBe(true);
    expect(within(row('proxy')).getByText('draughtsman.yaml')).toBeTruthy();
    expect(row('proxy').querySelector('[data-fix="yaml"] pre')?.textContent).toBe('server:\n  knownProxies:\n    - "172.17.0.1"');
    expect(row('proxy').querySelector('[data-fix="nginx"] pre')?.textContent).toContain('proxy_set_header X-Forwarded-Proto $scheme;');
    expect(row('proxy').querySelector('[data-fix="caddy"]')).toBeTruthy();
    expect(row('proxy').querySelector('[data-fix="cloudflare"]')).toBeTruthy();
    // The raw header values the server saw are one click away, closed by default.
    const facts = within(row('proxy')).getByText('What was seen').closest('details');
    expect(facts?.hasAttribute('open')).toBe(false);
    expect(facts?.textContent).toContain('198.51.100.7');
    // A passing row has nothing to change.
    expect(within(row('disk')).queryByText('What to change')).toBeNull();
  });

  it('hands the browser round trip to the server exactly as the browser saw it, a weakened ETag included', async () => {
    const calls = serve({ echoETag: 'W/"sc-1"' });
    render(<ConnectionCheckPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(calls.posted).toHaveLength(1);
    });

    expect(calls.posted[0]).toMatchObject({
      pageScheme: 'http',
      first: { status: 200, etagHeader: 'W/"sc-1"', bodyTag: '"sc-1"', cookieReceived: false },
      second: { status: 200, cookieReceived: true },
    });
  });

  it('runs again on request and replaces the findings', async () => {
    const calls = serve();
    render(<ConnectionCheckPage currentUser={user('Admin')} />);
    await waitFor(() => {
      expect(row('proxy')).toBeTruthy();
    });
    const callsBefore = calls.urls.length;

    fireEvent.click(screen.getByRole('button', { name: 'Run again' }));

    await waitFor(() => {
      expect(calls.urls.length).toBeGreaterThan(callsBefore);
      expect(screen.getByRole('button', { name: 'Run again' }).hasAttribute('disabled')).toBe(false);
    });
    expect(calls.posted).toHaveLength(2);
  });

  it('says what went wrong in each half on its own, without losing the other', async () => {
    serve({ server: 500, trip: 403 });
    render(<ConnectionCheckPage currentUser={user('Admin')} />);

    await waitFor(() => {
      expect(screen.getAllByRole('alert')).toHaveLength(2);
    });
    expect(screen.getByText('Could not read the connection check.')).toBeTruthy();
    expect(screen.getByText('Could not judge the browser round trip.')).toBeTruthy();
  });

  it('is not for an editor: it says so and sends no request', () => {
    const calls = serve();
    render(<ConnectionCheckPage currentUser={user('Editor')} />);

    expect(screen.queryByRole('heading', { name: 'Connection check' })).toBeNull();
    expect(calls.urls).toEqual([]);
  });
});
