// @vitest-environment jsdom
/**
 * Accessibility checks of the password-recovery screens (design/atlas/password-recovery.md), the same axe-core pattern as
 * loginAxe.test.tsx: the sign-in card's forgot step, the page a reset link opens in each of its states, the administrator's reset-link
 * dialog in both of its states, and the Email page. The admin pages are rendered inside a `<main>` because the app shell supplies
 * it in the real app (AppShell.tsx).
 */

import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import { forgetResetToken } from '../model/resetToken.ts';
import type { AuthUser } from '../model/authClient.ts';
import type { EmailSettings } from '../model/emailClient.ts';
import { EmailPage } from '../pages/EmailPage.tsx';
import { LoginPage } from '../pages/LoginPage.tsx';
import { ResetPasswordPage } from '../pages/ResetPasswordPage.tsx';
import { UsersPage } from '../pages/UsersPage.tsx';

const ADMIN: AuthUser = { id: 'usr_a', email: 'ada@acme.example', displayName: 'Ada Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };
const ERIN: AuthUser = { id: 'usr_e', email: 'erin@acme.example', displayName: 'Erin Editor', role: 'Editor', createdAt: '2026-01-02T00:00:00.000Z', active: true };

const SMTP: EmailSettings = {
  configured: true,
  provider: 'smtp',
  from: 'Draughtsman <noreply@acme.example>',
  host: 'smtp.acme.example',
  port: 587,
  tls: 'starttls',
  user: 'mailer',
  passwordSet: true,
  passwordKind: 'saved',
  passwordUnreadable: false,
  passwordSource: 'saved on this server (encrypted)',
  locked: { port: 'Set in draughtsman.yaml. Edit that file to change it.' },
  hasSaved: true,
  publicUrl: 'https://draughtsman.acme.example',
  selfServiceEnabled: true,
  selfServiceAvailable: true,
  emailedLinkMinutes: 60,
  adminLinkHours: 24,
  problems: [],
};

async function violations(root: Element): Promise<string[]> {
  const result = await axe.run(root, { rules: { 'color-contrast': { enabled: false } }, resultTypes: ['violations'] });
  return result.violations.flatMap((violation) => violation.nodes.map((node) => `${violation.id}: ${node.target.join(' ')}`));
}

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function serve(routes: Record<string, () => Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = requestUrl(input);
      if (url === '/api/theme') {
        return Promise.resolve(new Response(JSON.stringify({ name: 'default', productName: 'Acme Diagrams', logo: null, favicon: null, mode: 'system' }), { status: 200 }));
      }
      const route = routes[url];
      if (route === undefined) {
        throw new Error(`unexpected fetch: ${url}`);
      }
      return Promise.resolve(route());
    }),
  );
}

describe('password recovery accessibility (axe-core)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    forgetResetToken();
    window.history.replaceState(null, '', '/');
  });

  it('finds nothing on the sign-in card with Forgot password?, the forgot step, or the answer', async () => {
    serve({ '/api/auth/password-reset/request': () => new Response(JSON.stringify({ message: 'If an account with that address can be reset, we have emailed it a link.' }), { status: 202 }) });
    const { container } = render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: true }} />);
    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams')).toBeTruthy();
    });
    expect(await violations(container)).toEqual([]);

    fireEvent.click(screen.getByRole('button', { name: 'Forgot password?' }));
    expect(await violations(container)).toEqual([]);

    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'erin@acme.example' } });
    fireEvent.click(screen.getByRole('button', { name: 'Email me a link' }));
    await screen.findByTestId('reset-requested');
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the sign-in card that says to ask an administrator', async () => {
    serve({});
    const { container } = render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: false }} />);
    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams')).toBeTruthy();
    });
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the reset page: the form, an inline error, the done state, a refused link, and no link at all', async () => {
    window.history.replaceState(null, '', '/reset-password#token=dpr_the_secret');
    serve({ '/api/auth/password-reset/complete': () => new Response(null, { status: 204 }) });
    const { container } = render(<ResetPasswordPage onFinished={() => undefined} />);
    expect(await violations(container)).toEqual([]);

    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'short' } });
    fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: 'short' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));
    expect(screen.getByRole('alert')).toBeTruthy();
    expect(await violations(container)).toEqual([]);

    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'a-brand-new-password' } });
    fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: 'a-brand-new-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));
    await screen.findByRole('heading', { name: 'Password changed' });
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the refused-link and no-link states of the reset page', async () => {
    window.history.replaceState(null, '', '/reset-password#token=dpr_gone');
    serve({ '/api/auth/password-reset/complete': () => new Response(JSON.stringify({ title: 'Link not valid', detail: 'This reset link is not valid.' }), { status: 400 }) });
    const refused = render(<ResetPasswordPage onFinished={() => undefined} selfService />);
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'a-brand-new-password' } });
    fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: 'a-brand-new-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new password' }));
    await screen.findByRole('heading', { name: 'Link not valid' });
    expect(await violations(refused.container)).toEqual([]);
    refused.unmount();

    window.history.replaceState(null, '', '/reset-password');
    const none = render(<ResetPasswordPage onFinished={() => undefined} />);
    expect(await violations(none.container)).toEqual([]);
  });

  it('finds nothing in the Create reset link dialog, before and after the link exists', async () => {
    serve({
      '/api/users': () => new Response(JSON.stringify([ADMIN, ERIN]), { status: 200 }),
      '/api/users/usr_e/password-reset-link': () => new Response(JSON.stringify({ token: 'dpr_abc', expiresAt: '2030-01-02T09:00:00.000Z', expiresInHours: 24, userEmail: 'erin@acme.example' }), { status: 201 }),
    });
    const { container } = render(
      <main>
        <UsersPage currentUser={ADMIN} />
      </main>,
    );
    await screen.findByText('Erin Editor');
    fireEvent.click(screen.getByRole('button', { name: 'Create reset link' }));
    expect(await violations(container)).toEqual([]);

    fireEvent.click(screen.getByRole('button', { name: 'Create link' }));
    await screen.findByRole('textbox', { name: 'Reset link' });
    await act(() => Promise.resolve());
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the Email page: editable, with locked fields, a starting point chosen, a test result shown, and not configured', async () => {
    serve({
      '/api/admin/email': () => new Response(JSON.stringify(SMTP), { status: 200 }),
      '/api/admin/email/test': () => new Response(JSON.stringify({ ok: false, message: 'Could not connect to smtp.acme.example:587: ConnectionRefused.' }), { status: 200 }),
    });
    const configured = render(
      <main>
        <EmailPage currentUser={ADMIN} />
      </main>,
    );
    await screen.findByRole('form', { name: 'Outgoing email settings' });
    fireEvent.change(screen.getByLabelText('Start from'), { target: { value: 'microsoft365' } });
    fireEvent.click(screen.getByRole('button', { name: 'Test saved settings' }));
    await screen.findByTestId('email-test-result');
    expect(await violations(configured.container)).toEqual([]);
    fireEvent.click(screen.getByRole('button', { name: 'Remove saved settings' }));
    expect(await violations(configured.container)).toEqual([]);
    configured.unmount();

    serve({
      '/api/admin/email': () =>
        new Response(
          JSON.stringify({ configured: false, selfServiceEnabled: true, selfServiceAvailable: false, emailedLinkMinutes: 60, adminLinkHours: 24, port: 587, tls: 'starttls', passwordSet: false, passwordKind: 'none', passwordUnreadable: false, locked: { host: 'Set in draughtsman.yaml. Edit that file to change it.', password: 'Read from the environment variable DRAUGHTSMAN_SMTP_PASSWORD. Change it there.' }, hasSaved: false, problems: ['email.smtp.host is not set: the mail server to send through.'] }),
          { status: 200 },
        ),
    });
    const notConfigured = render(
      <main>
        <EmailPage currentUser={ADMIN} />
      </main>,
    );
    await screen.findByTestId('email-status');
    expect(await violations(notConfigured.container)).toEqual([]);
  });
});
