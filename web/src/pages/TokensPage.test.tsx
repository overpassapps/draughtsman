// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { CurrentUserContext } from '../app/currentUser.ts';
import type { AuthUser } from '../model/authClient.ts';
import { TokensPage } from './TokensPage.tsx';

function listResponse(): Response {
  return new Response(JSON.stringify([{ id: 'tok_1', name: 'CI', createdAt: '2026-01-01T00:00:00.000Z', revoked: false }]), { status: 200 });
}

function flush(): Promise<void> {
  return act(() => Promise.resolve());
}

describe('TokensPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists existing tokens', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(listResponse())));
    render(<TokensPage />);
    await waitFor(() => {
      expect(screen.getByText('CI')).toBeTruthy();
    });
    expect(screen.getByRole('button', { name: 'Revoke' })).toBeTruthy();
  });

  it('creates a token, shows the raw secret once with the claude mcp add snippet, and lists it', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(listResponse())));
    render(<TokensPage />);
    await waitFor(() => {
      expect(screen.getByText('CI')).toBeTruthy();
    });

    vi.mocked(fetch).mockResolvedValueOnce(
      new Response(JSON.stringify({ id: 'tok_2', name: 'Laptop', createdAt: '2026-01-02T00:00:00.000Z', token: 'dst_secret123' }), { status: 201 }),
    );
    vi.mocked(fetch).mockResolvedValueOnce(
      new Response(
        JSON.stringify([
          { id: 'tok_1', name: 'CI', createdAt: '2026-01-01T00:00:00.000Z', revoked: false },
          { id: 'tok_2', name: 'Laptop', createdAt: '2026-01-02T00:00:00.000Z', revoked: false },
        ]),
        { status: 200 },
      ),
    );

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Laptop' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create token' }));

    await flush();
    await waitFor(() => {
      expect(screen.getByDisplayValue('dst_secret123')).toBeTruthy();
    });
    expect(screen.getByDisplayValue(/claude mcp add --transport http draughtsman .*dst_secret123/)).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByRole('cell', { name: 'Laptop' })).toBeTruthy();
    });
  });

  it('sends the chosen lifetime and read-only scope, and says so in the one-time panel', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(listResponse())));
    render(<TokensPage />);
    await waitFor(() => {
      expect(screen.getByText('CI')).toBeTruthy();
    });

    vi.mocked(fetch).mockResolvedValueOnce(
      new Response(
        JSON.stringify({ id: 'tok_3', name: 'Reader', createdAt: '2026-01-02T00:00:00.000Z', expiresAt: '2026-02-01T00:00:00.000Z', scope: 'read', token: 'dst_readonly' }),
        { status: 201 },
      ),
    );
    vi.mocked(fetch).mockResolvedValueOnce(listResponse());

    // The form opens on the default: 90 days, Full access.
    expect(screen.getByLabelText<HTMLInputElement>('Expires in').value).toBe('90');
    expect(screen.getByLabelText<HTMLInputElement>('Read-only').checked).toBe(false);

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Reader' } });
    fireEvent.change(screen.getByLabelText('Expires in'), { target: { value: '30' } });
    fireEvent.click(screen.getByLabelText('Read-only'));
    fireEvent.click(screen.getByRole('button', { name: 'Create token' }));

    await waitFor(() => {
      expect(screen.getByDisplayValue('dst_readonly')).toBeTruthy();
    });
    const post = vi.mocked(fetch).mock.calls.find(([url, init]) => url === '/api/tokens' && init?.method === 'POST');
    expect(JSON.parse(post?.[1]?.body as string)).toEqual({ name: 'Reader', expiresInDays: 30, scope: 'read' });
    expect(screen.getByRole('status').textContent).toContain('it is read-only');

    // Read-only is not sticky: the next token starts at Full again.
    expect(screen.getByLabelText<HTMLInputElement>('Read-only').checked).toBe(false);
  });

  it('shows expiry, access and status, and flags a token that never expires', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(() =>
        Promise.resolve(
          new Response(
            JSON.stringify([
              { id: 'tok_a', name: 'Old', createdAt: '2026-01-01T00:00:00.000Z', revoked: false, scope: 'full' },
              { id: 'tok_b', name: 'Lapsed', createdAt: '2026-01-01T00:00:00.000Z', revoked: false, expiresAt: '2026-01-31T00:00:00.000Z', expired: true, scope: 'read' },
              { id: 'tok_c', name: 'Gone', createdAt: '2026-01-01T00:00:00.000Z', revoked: true, expiresAt: '2026-04-01T00:00:00.000Z', expired: false, scope: 'full' },
            ]),
            { status: 200 },
          ),
        ),
      ),
    );
    render(<TokensPage />);
    await waitFor(() => {
      expect(screen.getByText('Old')).toBeTruthy();
    });

    const rowOf = (name: string): HTMLElement => screen.getByRole('cell', { name }).closest('tr') as HTMLElement;
    expect(rowOf('Old').textContent).toContain('No expiry');
    expect(rowOf('Old').textContent).toContain('Full');
    expect(rowOf('Old').textContent).toContain('Active');
    expect(rowOf('Lapsed').textContent).toContain('Read-only');
    expect(rowOf('Lapsed').textContent).toContain('Expired');
    // An expired token has nothing left to revoke only if already revoked; a lapsed-but-unrevoked one still can be.
    expect(rowOf('Gone').textContent).toContain('Revoked');
    expect(rowOf('Gone').querySelector('button')).toBeNull();
    // The no-expiry notice is shown because a live token has none.
    expect(screen.getByText(/created before tokens expired/)).toBeTruthy();
  });

  it('revokes a token', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(listResponse())));
    render(<TokensPage />);
    await waitFor(() => {
      expect(screen.getByText('CI')).toBeTruthy();
    });

    vi.mocked(fetch).mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.mocked(fetch).mockResolvedValueOnce(
      new Response(JSON.stringify([{ id: 'tok_1', name: 'CI', createdAt: '2026-01-01T00:00:00.000Z', revoked: true }]), { status: 200 }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Revoke' }));

    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/tokens/tok_1', expect.objectContaining({ method: 'DELETE' }));
    });
  });
});

const ADMIN: AuthUser = { id: 'usr_a', email: 'ada@acme.example', displayName: 'Ada Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };
const EDITOR: AuthUser = { ...ADMIN, id: 'usr_e', email: 'erin@acme.example', displayName: 'Erin Editor', role: 'Editor' };

const ALL = [
  { id: 'tok_own', name: 'Ada laptop', createdAt: '2026-09-01T10:00:00.000Z', revoked: false, scope: 'full', expiresAt: '2026-12-01T10:00:00.000Z', ownerId: 'usr_a', ownerEmail: 'ada@acme.example', ownerName: 'Ada Admin' },
  { id: 'tok_erin', name: 'Erin CI', createdAt: '2026-09-02T10:00:00.000Z', lastUsedAt: '2026-10-01T02:50:42.000Z', revoked: false, scope: 'read', expiresAt: '2026-11-01T10:00:00.000Z', ownerId: 'usr_e', ownerEmail: 'erin@acme.example', ownerName: 'Erin Editor' },
];

function serveTokens(): string[] {
  const calls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      calls.push(`${init?.method ?? 'GET'} ${url}`);
      if (url === '/api/admin/tokens') {
        return Promise.resolve(new Response(JSON.stringify(ALL), { status: 200 }));
      }
      if (url === '/api/tokens' && (init?.method ?? 'GET') === 'GET') {
        return Promise.resolve(new Response(JSON.stringify([ALL[0]]), { status: 200 }));
      }
      return Promise.resolve(new Response(null, { status: 204 }));
    }),
  );
  return calls;
}

describe('TokensPage for an administrator (QA S6)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists every person’s tokens with their owner, scope and last use, never a secret', async () => {
    serveTokens();
    render(<CurrentUserContext.Provider value={ADMIN}><TokensPage /></CurrentUserContext.Provider>);

    const table = await screen.findByRole('table', { name: 'Every person\'s API tokens' });
    const row = within(table).getByText('Erin CI').closest('tr') as HTMLElement;
    expect(within(row).getByText('Erin Editor')).toBeTruthy();
    expect(within(row).getByText('erin@acme.example')).toBeTruthy();
    expect(within(row).getByText('Read-only')).toBeTruthy();
    expect(within(row).getByText('Active')).toBeTruthy();
    // Last used and expiry carry the one date format, not a locale string.
    expect(within(row).getAllByText(/^\d{1,2} [A-Z][a-z]{2} \d{4}, \d{2}:\d{2}$/).length).toBeGreaterThanOrEqual(3);
    expect(screen.getByRole('heading', { name: 'Everyone’s tokens' })).toBeTruthy();
    expect(table.textContent).not.toMatch(/dst_/);
  });

  it('revokes someone else’s token from the list by id and reads both lists again', async () => {
    const calls = serveTokens();
    render(<CurrentUserContext.Provider value={ADMIN}><TokensPage /></CurrentUserContext.Provider>);
    const table = await screen.findByRole('table', { name: 'Every person\'s API tokens' });

    // The accessible name says whose token it is and still contains the visible word (WCAG 2.5.3).
    const revoke = within(table).getByRole('button', { name: 'Revoke Erin CI for Erin Editor' });
    expect(revoke.textContent).toBe('Revoke');
    fireEvent.click(revoke);

    await waitFor(() => { expect(calls).toContain('DELETE /api/tokens/tok_erin'); });
    await waitFor(() => { expect(calls.filter((call) => call === 'GET /api/admin/tokens')).toHaveLength(2); });
  });

  it('asks for the all-tokens list only for an administrator: an Editor, or no signed-in user at all, gets their own list and no such section', async () => {
    const calls = serveTokens();
    const editor = render(<CurrentUserContext.Provider value={EDITOR}><TokensPage /></CurrentUserContext.Provider>);
    await screen.findByText('Ada laptop');
    expect(screen.queryByRole('heading', { name: 'Everyone’s tokens' })).toBeNull();
    expect(screen.queryByText('Erin CI')).toBeNull();
    expect(calls).not.toContain('GET /api/admin/tokens');
    editor.unmount();

    render(<TokensPage />);
    await screen.findByText('Ada laptop');
    expect(calls).not.toContain('GET /api/admin/tokens');
  });
});
