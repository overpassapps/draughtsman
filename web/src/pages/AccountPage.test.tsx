// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AccountPage } from './AccountPage.tsx';

function fill(current: string, next: string, repeat: string): void {
  fireEvent.change(screen.getByLabelText('Current password'), { target: { value: current } });
  fireEvent.change(screen.getByLabelText('New password'), { target: { value: next } });
  fireEvent.change(screen.getByLabelText('Repeat new password'), { target: { value: repeat } });
}

describe('AccountPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('changes the password, tells the user other sessions ended, and clears the fields', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(null, { status: 204 }))));
    render(<AccountPage />);

    fill('the-old-password', 'a-much-better-password', 'a-much-better-password');
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('other sessions were signed out');
    });
    const call = vi.mocked(fetch).mock.calls[0];
    expect(call?.[0]).toBe('/api/auth/password');
    expect(call?.[1]?.method).toBe('POST');
    expect(JSON.parse(call?.[1]?.body as string)).toEqual({ currentPassword: 'the-old-password', newPassword: 'a-much-better-password' });
    expect(screen.getByLabelText<HTMLInputElement>('Current password').value).toBe('');
    expect(screen.getByLabelText<HTMLInputElement>('New password').value).toBe('');
  });

  it("shows the server's refusal and keeps what was typed when the current password is wrong", async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify({ detail: 'The current password is incorrect.' }), { status: 400 }))),
    );
    render(<AccountPage />);

    fill('wrong', 'a-much-better-password', 'a-much-better-password');
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe('The current password is incorrect.');
    });
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByLabelText<HTMLInputElement>('New password').value).toBe('a-much-better-password');
  });

  it('refuses a mismatch or a short password in the browser without calling the server', () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(null, { status: 204 }))));
    render(<AccountPage />);

    fill('old', 'a-much-better-password', 'a-different-password');
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(screen.getByRole('alert').textContent).toContain('do not match');

    fill('old', 'short', 'short');
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(screen.getByRole('alert').textContent).toContain('at least 12');

    expect(vi.mocked(fetch)).not.toHaveBeenCalled();
  });
});
