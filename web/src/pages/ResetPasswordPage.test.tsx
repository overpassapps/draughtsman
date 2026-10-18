// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { forgetResetToken } from '../model/resetToken.ts';
import { ResetPasswordPage } from './ResetPasswordPage.tsx';

interface Call {
  url: string;
  body: Record<string, unknown>;
}

function stubServer(complete: (body: Record<string, unknown>) => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      if (url === '/api/theme') {
        return Promise.resolve(new Response(JSON.stringify({ name: 'default', productName: 'Acme Diagrams', logo: null, favicon: null, mode: 'system' }), { status: 200 }));
      }
      const body = JSON.parse(init?.body as string) as Record<string, unknown>;
      calls.push({ url, body });
      return Promise.resolve(complete(body));
    }),
  );
  return calls;
}

const REFUSED = new Response(JSON.stringify({ title: 'Link not valid', detail: 'This reset link is not valid. It may have expired or already been used. Ask for a new one.' }), { status: 400 });

function openLink(hash = '#token=dpr_the_secret'): void {
  window.history.replaceState(null, '', `/reset-password${hash}`);
}

function fill(password: string, confirm = password): void {
  fireEvent.change(screen.getByLabelText('New password'), { target: { value: password } });
  fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: confirm } });
}

describe('ResetPasswordPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    forgetResetToken();
    window.history.replaceState(null, '', '/');
  });

  it('shows the rule and what saving does before anything is typed, and takes the secret out of the address bar', () => {
    openLink();
    stubServer(() => new Response(null, { status: 204 }));

    render(<ResetPasswordPage onFinished={() => undefined} />);

    expect(window.location.hash).toBe('');
    expect(screen.getByRole('heading', { name: 'Choose a new password' })).toBeTruthy();
    const field = screen.getByLabelText('New password');
    expect(screen.getByText('At least 12 characters.').id).toBe(field.getAttribute('aria-describedby'));
    expect(screen.getByText('Saving signs you out everywhere else. Your API tokens keep working.')).toBeTruthy();
    expect(document.activeElement).toBe(field);
  });

  it('sends the secret and the password in the body, then offers sign-in and signs nobody in', async () => {
    openLink();
    const calls = stubServer(() => new Response(null, { status: 204 }));
    const onFinished = vi.fn();
    render(<ResetPasswordPage onFinished={onFinished} />);

    fill('a-brand-new-password');
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));

    expect((await screen.findByRole('status')).textContent).toBe('Every other session was signed out. Sign in with your new password.');
    expect(calls).toEqual([{ url: '/api/auth/password-reset/complete', body: { token: 'dpr_the_secret', newPassword: 'a-brand-new-password' } }]);
    expect(calls[0]?.url).not.toContain('dpr_');
    const go = screen.getByRole('button', { name: 'Go to sign in' });
    await waitFor(() => {
      expect(document.activeElement).toBe(go);
    });
    fireEvent.click(go);
    expect(onFinished).toHaveBeenCalledOnce();
  });

  it('says a short password or a mismatch inline and does not spend the link', () => {
    openLink();
    const calls = stubServer(() => new Response(null, { status: 204 }));
    render(<ResetPasswordPage onFinished={() => undefined} />);

    fill('short');
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));
    expect(screen.getByRole('alert').textContent).toBe('Password must be at least 12 characters.');

    fill('a-brand-new-password', 'a-different-password');
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));
    expect(screen.getByRole('alert').textContent).toBe('The two passwords do not match.');
    expect(calls).toEqual([]);
  });

  it('shows the one refusal for a link that is unknown, used or expired, and then forgets the secret', async () => {
    openLink();
    const calls = stubServer(() => REFUSED.clone());
    const onFinished = vi.fn();
    render(<ResetPasswordPage onFinished={onFinished} selfService />);

    fill('a-brand-new-password');
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));

    expect((await screen.findByRole('alert')).textContent).toBe('This reset link is not valid. It may have expired or already been used. Ask for a new one.');
    expect(screen.getByRole('heading', { name: 'Link not valid' })).toBeTruthy();
    expect(screen.getByText(/choose “Forgot password\?”/)).toBeTruthy();
    expect(calls).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Go to sign in' }));
    expect(onFinished).toHaveBeenCalledOnce();
  });

  it('keeps the form for a problem with the password that came back from the server', async () => {
    openLink();
    stubServer(() => new Response(JSON.stringify({ title: 'Too many attempts', detail: 'Too many attempts from this network address; try again in 15 minutes.' }), { status: 429 }));
    render(<ResetPasswordPage onFinished={() => undefined} />);

    fill('a-brand-new-password');
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));

    expect((await screen.findByRole('alert')).textContent).toContain('try again in 15 minutes');
    expect(screen.getByRole('button', { name: 'Save new password' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Link not valid' })).toBeNull();
  });

  it('starts again with the new secret when a second link is pasted into the same tab', async () => {
    openLink('#token=dpr_first');
    const calls = stubServer(() => REFUSED.clone());
    render(<ResetPasswordPage onFinished={() => undefined} />);
    fill('a-brand-new-password');
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));
    await screen.findByRole('heading', { name: 'Link not valid' });

    act(() => {
      window.history.replaceState(null, '', '/reset-password#token=dpr_second');
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(screen.getByRole('heading', { name: 'Choose a new password' })).toBeTruthy();
    expect(window.location.hash).toBe('');
    fill('a-brand-new-password');
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));
    await screen.findByRole('heading', { name: 'Link not valid' });
    expect(calls.map((c) => c.body.token)).toEqual(['dpr_first', 'dpr_second']);
  });

  it('says what the page is for when there is no secret in the address, and sends nothing', () => {
    openLink('');
    const calls = stubServer(() => new Response(null, { status: 204 }));

    render(<ResetPasswordPage onFinished={() => undefined} />);

    expect(screen.getByRole('status').textContent).toBe('This page opens from a reset link. Open the link you were sent, or ask for a new one.');
    expect(screen.queryByLabelText('New password')).toBeNull();
    expect(calls).toEqual([]);
  });
});
