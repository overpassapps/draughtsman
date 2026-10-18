// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import { AboutPage } from './AboutPage.tsx';

const ABOUT = { version: '0.1.0', buildDate: '2026-09-30', supportContact: 'draughtsman@overpass.co.uk', docsUrl: 'https://docs.example.com/d', noticesUrl: '/THIRD-PARTY-NOTICES.txt' };
const user = (role: 'Admin' | 'Editor') => ({ id: 'usr_1', email: 'e@x.com', displayName: 'E', role, createdAt: '2026-01-01T00:00:00.000Z', active: true }) as const;

function serve(about: Record<string, unknown> | number): string[] {
  const calls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      calls.push(url);
      if (url === '/api/about') {
        return Promise.resolve(typeof about === 'number' ? new Response('{}', { status: about }) : new Response(JSON.stringify(about), { status: 200 }));
      }
      return Promise.resolve(new Response(JSON.stringify({ name: 'T', productName: 'Acme Diagrams', favicon: null, mode: 'dark' }), { status: 200 }));
    }),
  );
  return calls;
}

describe('AboutPage (PR-04)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows an Editor the version, build date, support contact, docs and notices, and no admin links', async () => {
    const calls = serve(ABOUT);
    render(<AboutPage currentUser={user('Editor')} />);

    await screen.findByText('0.1.0');
    expect(screen.getByText('30 September 2026')).toBeTruthy();
    expect(screen.queryByText('Licence', { selector: 'dt' })).toBeNull();
    expect(screen.queryByText(/evaluation/i)).toBeNull();
    expect(screen.getByRole('link', { name: 'draughtsman@overpass.co.uk' }).getAttribute('href')).toBe('mailto:draughtsman@overpass.co.uk');
    const docs = screen.getByRole('link', { name: /docs\.example\.com/ });
    expect(docs.getAttribute('href')).toBe('https://docs.example.com/d');
    expect(docs.getAttribute('rel')).toBe('noopener noreferrer');
    expect(screen.getByRole('link', { name: /Open the notices/ }).getAttribute('href')).toBe('/THIRD-PARTY-NOTICES.txt');
    // The negative direction: nothing admin-only is shown or even requested.
    expect(screen.queryByRole('link', { name: 'system status' })).toBeNull();
    expect(calls).not.toContain('/api/admin/licence');
    expect(calls).not.toContain('/api/admin/status');
  });

  it('gives an Administrator an extra link to the system status page', async () => {
    serve(ABOUT);
    render(<AboutPage currentUser={user('Admin')} />);

    await screen.findByText('0.1.0');
    expect(screen.queryByRole('link', { name: 'licence details' })).toBeNull();
    expect(screen.getByRole('link', { name: 'system status' }).getAttribute('href')).toBe('/admin/status');
  });

  it('shows a support contact that is not an email as plain text, and omits Documentation when none is configured', async () => {
    const without: Record<string, unknown> = { ...ABOUT };
    Reflect.deleteProperty(without, 'docsUrl');
    serve({ ...without, supportContact: 'Ring the service desk on 0141 000 0000' });
    render(<AboutPage currentUser={user('Editor')} />);

    await screen.findByText('Ring the service desk on 0141 000 0000');
    expect(screen.queryByRole('link', { name: /service desk/ })).toBeNull();
    expect(screen.queryByText('Documentation')).toBeNull();
  });

  it('words a documentation link to this server’s own copy (a path, not an address) as such', async () => {
    serve({ ...ABOUT, docsUrl: '/docs/' });
    render(<AboutPage currentUser={user('Editor')} />);

    const link = await screen.findByRole('link', { name: /The guides on this server/ });
    expect(link.getAttribute('href')).toBe('/docs/');
    // The other direction: a configured address is shown as itself.
    expect(screen.queryByText('https://docs.example.com/d')).toBeNull();
  });

  it('shows a configured documentation address as itself', async () => {
    serve(ABOUT);
    render(<AboutPage currentUser={user('Editor')} />);

    expect((await screen.findByRole('link', { name: /docs.example.com/ })).getAttribute('href')).toBe('https://docs.example.com/d');
  });

  it('reports a failed read instead of an empty page', async () => {
    serve(500);
    render(<AboutPage currentUser={user('Editor')} />);

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toMatch(/Could not read the version details/);
    });
  });
});
