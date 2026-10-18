// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SecurityLogPage } from './SecurityLogPage.tsx';
import type { AuthUser } from '../model/authClient.ts';

const ADMIN: AuthUser = { id: 'usr_1', email: 'a@x.com', displayName: 'A', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };

function page(items: object[], total = items.length, offset = 0): Response {
  return new Response(JSON.stringify({ items, total, offset, limit: 50 }), { status: 200 });
}

describe('SecurityLogPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows events with readable names, who, which account, from where and the detail', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(() =>
        Promise.resolve(
          page([
            { id: 2, at: '2026-09-30T10:00:00.000Z', type: 'sign_in.failed', targetLabel: 'jane@x.com', sourceIp: '203.0.113.9', detail: 'wrong password' },
            { id: 1, at: '2026-09-30T09:00:00.000Z', type: 'user.role_changed', actorLabel: 'a@x.com', targetLabel: 'jane@x.com', detail: 'Editor to Admin' },
          ]),
        ),
      ),
    );
    render(<SecurityLogPage currentUser={ADMIN} />);

    await waitFor(() => {
      expect(screen.getByRole('cell', { name: 'Sign-in failed' })).toBeTruthy();
    });
    const failed = screen.getByRole('cell', { name: 'Sign-in failed' }).closest('tr') as HTMLElement;
    expect(failed.textContent).toContain('203.0.113.9');
    expect(failed.textContent).toContain('wrong password');
    expect(screen.getByRole('cell', { name: 'Role changed' }).closest('tr')?.textContent).toContain('Editor to Admin');
    expect(screen.getByText('1–2 of 2')).toBeTruthy();
  });

  it('asks the server for the chosen event type and goes back to the first page', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(page([{ id: 1, at: '2026-09-30T09:00:00.000Z', type: 'sign_out' }]))));
    render(<SecurityLogPage currentUser={ADMIN} />);
    await waitFor(() => {
      expect(screen.getByRole('cell', { name: 'Signed out' })).toBeTruthy();
    });

    vi.mocked(fetch).mockImplementation(() => Promise.resolve(page([])));
    fireEvent.change(screen.getByLabelText('Event'), { target: { value: 'token.created' } });

    await waitFor(() => {
      expect(screen.getByText('Nothing has been recorded yet.')).toBeTruthy();
    });
    const urls = vi.mocked(fetch).mock.calls.map(([url]) => url as string);
    expect(urls[0]).toBe('/api/admin/security-events?offset=0&limit=50');
    expect(urls.at(-1)).toBe('/api/admin/security-events?type=token.created&offset=0&limit=50');
  });

  it('pages with Older and Newer', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(page([{ id: 1, at: '2026-09-30T09:00:00.000Z', type: 'sign_out' }], 120))));
    render(<SecurityLogPage currentUser={ADMIN} />);
    await waitFor(() => {
      expect(screen.getByText('1–50 of 120')).toBeTruthy();
    });
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Newer' }).disabled).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: 'Older' }));
    await waitFor(() => {
      expect(vi.mocked(fetch).mock.calls.at(-1)?.[0]).toBe('/api/admin/security-events?offset=50&limit=50');
    });
    await waitFor(() => {
      expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Newer' }).disabled).toBe(false);
    });
  });

  it('refuses a non-admin without calling the server', () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(page([]))));
    render(<SecurityLogPage currentUser={{ ...ADMIN, role: 'Editor' }} />);

    expect(screen.getByRole('alert').textContent).toContain('administrator');
    expect(vi.mocked(fetch)).not.toHaveBeenCalled();
  });

  it('reports a failed load', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('no', { status: 500 }))));
    render(<SecurityLogPage currentUser={ADMIN} />);

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toContain('Cannot load the log');
    });
  });
});
