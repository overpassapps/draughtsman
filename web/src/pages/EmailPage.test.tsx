// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { AuthUser } from '../model/authClient.ts';
import type { EmailSettings } from '../model/emailClient.ts';
import { EmailPage } from './EmailPage.tsx';

const ADMIN: AuthUser = { id: 'usr_a', email: 'ada@acme.example', displayName: 'Ada Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };
const EDITOR: AuthUser = { ...ADMIN, id: 'usr_e', email: 'erin@acme.example', displayName: 'Erin Editor', role: 'Editor' };

const BASE = { selfServiceEnabled: true, selfServiceAvailable: true, emailedLinkMinutes: 60, adminLinkHours: 24, problems: [], locked: {}, hasSaved: false, passwordUnreadable: false };

const SMTP: EmailSettings = {
  ...BASE,
  configured: true,
  provider: 'smtp',
  from: 'Draughtsman <noreply@acme.example>',
  host: 'smtp.acme.example',
  port: 587,
  tls: 'starttls',
  user: 'mailer',
  passwordSet: true,
  passwordKind: 'saved',
  passwordSource: 'saved on this server (encrypted)',
  publicUrl: 'https://draughtsman.acme.example',
  hasSaved: true,
};

const NOTHING: EmailSettings = { ...BASE, configured: false, selfServiceAvailable: false, port: 587, tls: 'starttls', passwordSet: false, passwordKind: 'none' };

interface Call {
  method: string;
  url: string;
  body?: Record<string, unknown> | undefined;
}

function serve(settings: EmailSettings, replies: { put?: () => Response; test?: () => Response; remove?: () => Response } = {}): { calls: Call[]; writes: () => Call[] } {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const method = init?.method ?? 'GET';
      calls.push({ method, url, body: typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : undefined });
      if (url === '/api/admin/email/test') {
        return Promise.resolve(replies.test?.() ?? new Response(JSON.stringify({ ok: true, message: 'Test email to ada@acme.example handed to smtp.acme.example:587.' }), { status: 200 }));
      }
      if (method === 'PUT') {
        return Promise.resolve(replies.put?.() ?? new Response(JSON.stringify(settings), { status: 200 }));
      }
      if (method === 'DELETE') {
        return Promise.resolve(replies.remove?.() ?? new Response(JSON.stringify(NOTHING), { status: 200 }));
      }
      return Promise.resolve(new Response(JSON.stringify(settings), { status: 200 }));
    }),
  );
  return { calls, writes: () => calls.filter((c) => c.method !== 'GET') };
}

const input = (label: string): HTMLInputElement => screen.getByLabelText<HTMLInputElement>(label);

async function open(settings: EmailSettings, replies?: Parameters<typeof serve>[1]): Promise<ReturnType<typeof serve>> {
  const server = serve(settings, replies);
  render(<EmailPage currentUser={ADMIN} />);
  await screen.findByRole('form', { name: 'Outgoing email settings' });
  return server;
}

describe('EmailPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the effective settings in the form and says where the password is, never the password', async () => {
    await open(SMTP);

    expect(input('Mail server').value).toBe('smtp.acme.example');
    expect(input('Port').value).toBe('587');
    expect(screen.getByLabelText<HTMLSelectElement>('Encryption').value).toBe('starttls');
    expect(input('User name').value).toBe('mailer');
    expect(input('From address').value).toBe('Draughtsman <noreply@acme.example>');
    expect(input('Public address').value).toBe('https://draughtsman.acme.example');
    expect(screen.getByTestId('email-status').textContent).toBe('Email is set up. Messages go through smtp.acme.example:587.');
    expect(screen.getByTestId('email-password-state').textContent).toBe('Password: set (saved on this server (encrypted)). It is never shown again.');
    expect(input('New password').value).toBe('');
    expect(screen.getByText('On. A link is emailed and works for 60 minutes.')).toBeTruthy();
  });

  it('says email is not set up, and where people go instead, when nothing is configured', async () => {
    await open(NOTHING);

    expect(screen.getByTestId('email-status').textContent).toContain('Create reset link');
    expect(screen.getByTestId('email-password-state').textContent).toBe('Password: not set.');
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Test saved settings' }).disabled).toBe(true);
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Remove saved settings' }).disabled).toBe(true);
  });

  it('lists what is wrong above the form', async () => {
    await open({ ...SMTP, configured: false, selfServiceAvailable: false, problems: ['email.smtp.host is not set: the mail server to send through.'] });

    expect(screen.getAllByRole('alert').some((a) => a.textContent.includes('email.smtp.host is not set'))).toBe(true);
    expect(screen.getByText('Off: it needs working email and a public address.')).toBeTruthy();
  });

  it('states the honest limit: no implicit TLS on 465, offer 587 STARTTLS instead', async () => {
    await open(NOTHING);

    expect(screen.getByText(/Port 465 \(implicit TLS\) is not supported/).textContent).toContain('port 587 STARTTLS');
  });

  describe('locked fields', () => {
    const LOCKED: EmailSettings = {
      ...SMTP,
      hasSaved: false,
      passwordKind: 'environment',
      passwordSource: 'environment variable DRAUGHTSMAN_SMTP_PASSWORD',
      locked: {
        host: 'Set in draughtsman.yaml. Edit that file to change it.',
        tls: 'Set in draughtsman.yaml. Edit that file to change it.',
        password: 'Read from the environment variable DRAUGHTSMAN_SMTP_PASSWORD. Change it there.',
      },
    };

    it('shows a field draughtsman.yaml sets as read-only with its reason, and leaves the rest editable', async () => {
      await open(LOCKED);

      const host = input('Mail server');
      expect(host.readOnly).toBe(true);
      expect(host.getAttribute('aria-readonly')).toBe('true');
      expect(screen.getByTestId('email-host-locked').textContent).toBe('Set in draughtsman.yaml. Edit that file to change it.');
      expect(host.getAttribute('aria-describedby')).toContain('email-host-locked');
      expect(screen.getByLabelText<HTMLSelectElement>('Encryption').disabled).toBe(true);
      expect(screen.getByTestId('email-tls-locked')).toBeTruthy();
      expect(input('User name').readOnly).toBe(false);
      expect(input('From address').readOnly).toBe(false);
      expect(input('Public address').readOnly).toBe(false);
      expect(screen.queryByTestId('email-from-locked')).toBeNull();
    });

    it('shows a password the environment sets as locked: no field to type one into', async () => {
      await open(LOCKED);

      expect(screen.getByTestId('email-password-locked').textContent).toContain('DRAUGHTSMAN_SMTP_PASSWORD');
      expect(screen.queryByLabelText('New password')).toBeNull();
      expect(screen.queryByLabelText('Password')).toBeNull();
      expect(screen.getByTestId('email-password-state').textContent).toContain('set (environment variable DRAUGHTSMAN_SMTP_PASSWORD)');
    });

    it('does not let a starting point overwrite a locked field', async () => {
      await open(LOCKED);

      fireEvent.change(screen.getByLabelText('Start from'), { target: { value: 'google' } });

      expect(input('Mail server').value).toBe('smtp.acme.example');
      expect(screen.getByLabelText<HTMLSelectElement>('Encryption').value).toBe('starttls');
    });

    it('with email set to write files, locks the mail server fields and leaves From and the public address', async () => {
      const reason = 'Email is set to write files in draughtsman.yaml (email.provider: file), so no mail server is used.';
      await open({ ...NOTHING, configured: true, provider: 'file', outboxFolder: '/data/outbox', from: 'files@acme.example', locked: { provider: 'x', host: reason, port: reason, tls: reason, user: reason, password: reason } });

      expect(input('Mail server').readOnly).toBe(true);
      expect(input('Port').readOnly).toBe(true);
      expect(input('User name').readOnly).toBe(true);
      expect(screen.getByLabelText<HTMLSelectElement>('Start from').disabled).toBe(true);
      expect(input('Public address').readOnly).toBe(false);
      expect(screen.getByTestId('email-status').textContent).toContain('nothing is sent');
      expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Test these values' }).disabled).toBe(false);
    });
  });

  describe('starting points', () => {
    it('fills the server fields from a provider and stores nothing', async () => {
      const server = await open(NOTHING);

      fireEvent.change(screen.getByLabelText('Start from'), { target: { value: 'microsoft365' } });

      expect(input('Mail server').value).toBe('smtp.office365.com');
      expect(input('Port').value).toBe('587');
      expect(screen.getByLabelText<HTMLSelectElement>('Encryption').value).toBe('starttls');
      expect(screen.getByTestId('email-gotcha').textContent).toContain('SMTP AUTH');
      // Nothing was sent anywhere: only the initial read happened.
      expect(server.writes()).toEqual([]);
    });

    it('fills SendGrid’s fixed user name but leaves From, the public address and the password alone', async () => {
      await open(SMTP);
      fireEvent.change(input('From address'), { target: { value: 'Mine <mine@acme.example>' } });
      fireEvent.change(input('New password'), { target: { value: 'typed-secret' } });

      fireEvent.change(screen.getByLabelText('Start from'), { target: { value: 'sendgrid' } });

      expect(input('Mail server').value).toBe('smtp.sendgrid.net');
      expect(input('User name').value).toBe('apikey');
      expect(input('From address').value).toBe('Mine <mine@acme.example>');
      expect(input('Public address').value).toBe('https://draughtsman.acme.example');
      expect(input('New password').value).toBe('typed-secret');
      expect(screen.getByTestId('email-gotcha').textContent).toContain('apikey');
    });

    it('offers every provider the brief names, each with a one-line gotcha', async () => {
      await open(NOTHING);
      const select = screen.getByLabelText<HTMLSelectElement>('Start from');
      const names = within(select).getAllByRole('option').map((o) => o.textContent);

      expect(names).toEqual(['Choose a provider…', 'Microsoft 365', 'Google Workspace', 'Amazon SES', 'SendGrid', 'Postmark', 'Mailgun', 'My own mail server']);
      for (const id of ['google', 'ses', 'postmark', 'mailgun', 'own']) {
        fireEvent.change(select, { target: { value: id } });
        expect(screen.getByTestId('email-gotcha').textContent.length).toBeGreaterThan(40);
      }
    });
  });

  describe('save', () => {
    it('is off until something changes, then sends the form, applies the answer and says no restart is needed', async () => {
      const after: EmailSettings = { ...SMTP, host: 'smtp.office365.com' };
      const server = await open(SMTP, { put: () => new Response(JSON.stringify(after), { status: 200 }) });
      const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save' });
      expect(save.disabled).toBe(true);

      fireEvent.change(input('Mail server'), { target: { value: 'smtp.office365.com' } });
      expect(save.disabled).toBe(false);
      expect(screen.getByText('You have unsaved changes.')).toBeTruthy();
      fireEvent.click(save);

      expect((await screen.findByTestId('email-notice')).textContent).toContain('no restart is needed');
      const put = server.writes()[0];
      expect(put).toMatchObject({ method: 'PUT', url: '/api/admin/email' });
      expect(put?.body).toMatchObject({ host: 'smtp.office365.com', port: 587, tls: 'starttls', user: 'mailer', passwordAction: 'keep' });
      expect(put?.body).not.toHaveProperty('password');
      await waitFor(() => { expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Save' }).disabled).toBe(true); });
    });

    it('shows the server’s refusal as an alert and marks the field it names', async () => {
      await open(NOTHING, {
        put: () =>
          new Response(
            JSON.stringify({ title: 'Cannot save these settings', detail: 'The mail server must be a plain host name such as smtp.example.com, not a web address.', errors: [{ field: 'host', message: 'The mail server must be a plain host name such as smtp.example.com, not a web address.' }] }),
            { status: 400 },
          ),
      });
      fireEvent.change(input('Mail server'), { target: { value: 'https://smtp.example.com' } });

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));

      expect((await screen.findByTestId('email-error')).textContent).toContain('not a web address');
      expect(input('Mail server').getAttribute('aria-invalid')).toBe('true');
      expect(input('Mail server').value).toBe('https://smtp.example.com');
    });
  });

  describe('the password', () => {
    it('keeps the saved password by default: Save sends “keep” and no password', async () => {
      const server = await open(SMTP);
      fireEvent.change(input('From address'), { target: { value: 'Other <other@acme.example>' } });

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await screen.findByTestId('email-notice');

      expect(screen.getByRole<HTMLInputElement>('radio', { name: 'Keep it' }).checked).toBe(true);
      expect(server.writes()[0]?.body).toMatchObject({ passwordAction: 'keep' });
      expect(server.writes()[0]?.body).not.toHaveProperty('password');
    });

    it('replaces it when one is typed, sends it once, and drops it from the page', async () => {
      const server = await open(SMTP);

      fireEvent.change(input('New password'), { target: { value: 'a-new-secret' } });
      expect(screen.getByRole<HTMLInputElement>('radio', { name: 'Replace it with the one typed below' }).checked).toBe(true);
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await screen.findByTestId('email-notice');

      expect(server.writes()[0]?.body).toMatchObject({ passwordAction: 'replace', password: 'a-new-secret' });
      expect(input('New password').value).toBe('');
      expect(document.body.innerHTML).not.toContain('a-new-secret');
    });

    it('clears it only when asked to, and a typed one is thrown away with that choice', async () => {
      const server = await open(SMTP);
      fireEvent.change(input('New password'), { target: { value: 'typed-then-abandoned' } });

      fireEvent.click(screen.getByRole('radio', { name: 'Remove it' }));
      expect(input('New password').value).toBe('');
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await screen.findByTestId('email-notice');

      expect(server.writes()[0]?.body).toMatchObject({ passwordAction: 'clear' });
      expect(server.writes()[0]?.body).not.toHaveProperty('password');
    });

    it('with none saved, typing one replaces and emptying it goes back to keep', async () => {
      const server = await open(NOTHING);

      fireEvent.change(input('Password'), { target: { value: 'first' } });
      fireEvent.change(input('Password'), { target: { value: '' } });
      fireEvent.change(input('Mail server'), { target: { value: 'smtp.acme.example' } });
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await screen.findByTestId('email-notice');

      expect(server.writes()[0]?.body).toMatchObject({ passwordAction: 'keep' });
    });

    it('says a saved password that could not be decrypted must be entered again', async () => {
      await open({ ...SMTP, passwordSet: false, passwordKind: 'none', passwordUnreadable: true });

      expect(screen.getByTestId('email-password-state').textContent).toContain('could not be read');
    });

    const SECRET_RING = { state: 'protected' as const, provider: 'key' as const, message: 'ok', unreadableKeys: 0, migration: 'none' as const };

    it('shows the server’s reason and what to do for a saved password it cannot read', async () => {
      const message = 'The stored SMTP password cannot be read. Set keyRing.legacyInstallPaths to the old install folder and restart.';
      await open({ ...SMTP, passwordSet: false, passwordKind: 'none', passwordUnreadable: true, keyRing: { ...SECRET_RING, storedSecrets: [{ name: 'SMTP password', file: 'email/settings.json', state: 'unreadable', message }] } });

      expect(screen.getByTestId('email-password-state').textContent).toContain('keyRing.legacyInstallPaths');
    });

    it('notes a saved password the server moved off the install folder', async () => {
      await open({ ...SMTP, keyRing: { ...SECRET_RING, storedSecrets: [{ name: 'SMTP password', file: 'email/settings.json', state: 'reprotected', message: 'The stored SMTP password was re-protected.' }] } });

      expect(screen.getByTestId('email-stored-secret').textContent).toContain('re-protected');
    });

    it('warns while the key ring that encrypts the saved password is not protected at rest', async () => {
      await open({ ...SMTP, keyRing: { state: 'unprotected', provider: 'none', reason: 'disabled', message: 'x', unreadableKeys: 0, migration: 'none' } });

      expect(screen.getByTestId('email-keyring').textContent).toContain('not protected at rest');
    });

    it('does not warn when the key ring is protected, or when no password is saved', async () => {
      const protectedRing = { state: 'protected' as const, provider: 'keychain' as const, message: 'ok', unreadableKeys: 0, migration: 'none' as const };
      const first = serve({ ...SMTP, keyRing: protectedRing });
      const { unmount } = render(<EmailPage currentUser={ADMIN} />);
      await screen.findByRole('form', { name: 'Outgoing email settings' });
      expect(screen.queryByTestId('email-keyring')).toBeNull();
      unmount();
      first.calls.length = 0;
      serve({ ...NOTHING, keyRing: { ...protectedRing, state: 'unprotected', provider: 'none' } });
      render(<EmailPage currentUser={ADMIN} />);
      await screen.findByRole('form', { name: 'Outgoing email settings' });
      expect(screen.queryByTestId('email-keyring')).toBeNull();
    });
  });

  describe('testing', () => {
    it('tests the saved settings: one POST with no body, the success sentence as a status', async () => {
      const server = await open(SMTP);

      fireEvent.click(screen.getByRole('button', { name: 'Test saved settings' }));

      const result = await screen.findByTestId('email-test-result');
      expect(result.getAttribute('role')).toBe('status');
      expect(result.textContent).toBe('Sent using the saved settings. Test email to ada@acme.example handed to smtp.acme.example:587.');
      expect(server.writes()).toEqual([{ method: 'POST', url: '/api/admin/email/test', body: undefined }]);
    });

    it('tests the form as typed without saving it: the values travel, nothing is saved, and the typed password is not kept', async () => {
      const server = await open(NOTHING);
      fireEvent.change(input('Mail server'), { target: { value: 'smtp.acme.example' } });
      fireEvent.change(input('User name'), { target: { value: 'mailer' } });
      fireEvent.change(input('Password'), { target: { value: 'typed-secret' } });
      fireEvent.change(input('From address'), { target: { value: 'noreply@acme.example' } });

      fireEvent.click(screen.getByRole('button', { name: 'Test these values' }));

      const result = await screen.findByTestId('email-test-result');
      expect(result.textContent).toContain('using the values in the form (nothing was saved)');
      const calls = server.writes();
      expect(calls).toHaveLength(1);
      expect(calls[0]?.url).toBe('/api/admin/email/test');
      expect(calls[0]?.body).toMatchObject({ values: { host: 'smtp.acme.example', user: 'mailer', from: 'noreply@acme.example', passwordAction: 'replace', password: 'typed-secret' } });
      expect(calls.some((c) => c.method === 'PUT')).toBe(false);
    });

    it('shows the failure as an alert, naming which settings were tested', async () => {
      await open(SMTP, { test: () => new Response(JSON.stringify({ ok: false, message: 'Could not connect to smtp.acme.example:587: ConnectionRefused.' }), { status: 200 }) });

      fireEvent.click(screen.getByRole('button', { name: 'Test saved settings' }));

      const result = await screen.findByTestId('email-test-result');
      expect(result.getAttribute('role')).toBe('alert');
      expect(result.textContent).toContain('Failed using the saved settings. Could not connect to smtp.acme.example:587');
    });

    it('shows the throttle sentence when the server refuses another test', async () => {
      await open(SMTP, { test: () => new Response(JSON.stringify({ title: 'Too many tests', detail: 'That is 5 test emails in 15 minutes; wait a little before sending another.' }), { status: 429 }) });

      fireEvent.click(screen.getByRole('button', { name: 'Test saved settings' }));

      expect((await screen.findByTestId('email-error')).textContent).toContain('wait a little');
    });

    it('says the test goes only to the administrator’s own address', async () => {
      await open(SMTP);

      expect(screen.getByText(/one message to your own address \(ada@acme.example\), and only there/)).toBeTruthy();
    });
  });

  describe('remove saved settings', () => {
    it('asks first, then removes, and shows the settings as they now are', async () => {
      const server = await open(SMTP);

      fireEvent.click(screen.getByRole('button', { name: 'Remove saved settings' }));
      expect(server.writes()).toEqual([]);
      fireEvent.click(screen.getByRole('button', { name: 'Yes, remove the saved settings' }));

      expect((await screen.findByTestId('email-notice')).textContent).toContain('removed');
      expect(server.writes()[0]).toMatchObject({ method: 'DELETE', url: '/api/admin/email' });
      expect(input('Mail server').value).toBe('');
      expect(screen.getByTestId('email-password-state').textContent).toBe('Password: not set.');
    });

    it('can be cancelled without removing anything', async () => {
      const server = await open(SMTP);

      fireEvent.click(screen.getByRole('button', { name: 'Remove saved settings' }));
      fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

      expect(server.writes()).toEqual([]);
      expect(screen.getByRole('button', { name: 'Remove saved settings' })).toBeTruthy();
    });
  });

  it('is not the page of an Editor: they are told so and nothing is requested', () => {
    const { calls } = serve(SMTP);
    render(<EmailPage currentUser={EDITOR} />);

    expect(screen.queryByRole('form', { name: 'Outgoing email settings' })).toBeNull();
    expect(calls).toEqual([]);
  });

  it('offers Retry when the settings cannot be read', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 500 }))));
    render(<EmailPage currentUser={ADMIN} />);

    expect((await screen.findByRole('alert')).textContent).toContain('500');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });
});
