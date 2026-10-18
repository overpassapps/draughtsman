import {
  AuthError,
  ResetLinkError,
  completePasswordReset,
  createResetLink,
  fetchAuthStatus,
  firstRun,
  login,
  logout,
  requestPasswordReset,
  resetLinkOf,
} from './authClient.ts';
import { clearSessionEndedNotice, currentSessionEndedNotice } from './sessionEnded.ts';

function jsonResponse(status: number, body: unknown): typeof fetch {
  return vi.fn(() => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })));
}

describe('fetchAuthStatus first-run hints', () => {
  it('passes the data folder and host kind through while first-run is open', async () => {
    const status = await fetchAuthStatus(jsonResponse(200, { firstRunRequired: true, firstRun: { dataFolder: '/data', host: 'container' } }));

    expect(status).toEqual({ firstRunRequired: true, user: null, firstRun: { dataFolder: '/data', host: 'container' }, passwordReset: { selfService: false } });
  });

  it('has no firstRun key at all once the instance is set up, rather than an undefined one', async () => {
    const status = await fetchAuthStatus(jsonResponse(200, { firstRunRequired: false }));

    expect(Object.keys(status)).toEqual(['firstRunRequired', 'user', 'passwordReset']);
  });
});

describe('fetchAuthStatus', () => {
  it('normalises an omitted user (the server writes nulls omitted, never "user": null) to null', async () => {
    const fetchImpl = jsonResponse(200, { firstRunRequired: false });

    const status = await fetchAuthStatus(fetchImpl);

    expect(status).toEqual({ firstRunRequired: false, user: null, passwordReset: { selfService: false } });
  });

  it('passes a present user through unchanged', async () => {
    const user = { id: 'usr_1', email: 'eric@example.com', displayName: 'Eric', role: 'Admin', createdAt: '2026-09-29T00:00:00.000Z', active: true };
    const fetchImpl = jsonResponse(200, { firstRunRequired: false, user });

    const status = await fetchAuthStatus(fetchImpl);

    expect(status).toEqual({ firstRunRequired: false, user, passwordReset: { selfService: false } });
  });

  it('throws a plain error on a non-2xx response', async () => {
    const fetchImpl = vi.fn(() => Promise.resolve(new Response('', { status: 500 })));
    await expect(fetchAuthStatus(fetchImpl)).rejects.toThrow('500');
  });
});

describe('login', () => {
  it('returns the user on success', async () => {
    const user = { id: 'usr_1', email: 'eric@example.com', displayName: 'Eric', role: 'Admin', createdAt: '2026-09-29T00:00:00.000Z', active: true };
    await expect(login('eric@example.com', 'correcthorsebattery', jsonResponse(200, user))).resolves.toEqual(user);
  });

  it('raises AuthError with the generic message on 401, the same for a wrong password or an unknown email', async () => {
    const detail = 'Email or password is incorrect.';
    const wrongPassword = login('eric@example.com', 'wrong', jsonResponse(401, { title: 'Sign-in failed', detail }));
    const unknownUser = login('nobody@example.com', 'whatever', jsonResponse(401, { title: 'Sign-in failed', detail }));

    await expect(wrongPassword).rejects.toBeInstanceOf(AuthError);
    await expect(wrongPassword).rejects.toMatchObject({ status: 401, message: detail });
    await expect(unknownUser).rejects.toMatchObject({ status: 401, message: detail });
  });

  it('raises AuthError with the throttle detail on 429', async () => {
    const fetchImpl = jsonResponse(429, { title: 'Too many attempts', detail: 'Too many failed sign-ins; try again in 15 minutes.' });
    await expect(login('eric@example.com', 'wrong', fetchImpl)).rejects.toMatchObject({ status: 429 });
  });
});

describe('firstRun', () => {
  it('raises AuthError on 409 once first-run is already closed', async () => {
    const fetchImpl = jsonResponse(409, { title: 'First run already completed', detail: 'An administrator already exists; sign in instead.' });
    await expect(firstRun('a@example.com', 'A', 'correcthorsebattery', 'ABCDEFGHJK', fetchImpl)).rejects.toMatchObject({ status: 409 });
  });

  it('raises AuthError on 400 for a short password', async () => {
    const fetchImpl = jsonResponse(400, { title: 'Invalid request', detail: 'Password must be at least 12 characters.' });
    const failure = firstRun('a@example.com', 'A', 'short', 'ABCDEFGHJK', fetchImpl);
    await expect(failure).rejects.toBeInstanceOf(AuthError);
    await expect(failure).rejects.toMatchObject({ message: 'Password must be at least 12 characters.' });
  });

  it('sends the setup code in the request body', async () => {
    const fetchImpl = jsonResponse(201, { id: 'usr_1', email: 'a@example.com', displayName: 'A', role: 'Admin', createdAt: '2026-09-29T00:00:00.000Z', active: true });
    await firstRun('a@example.com', 'A', 'correcthorsebattery', 'ABCDEFGHJK', fetchImpl);

    const [, init] = vi.mocked(fetchImpl).mock.calls.at(0) ?? [];
    const body = JSON.parse(init?.body as string) as { setupCode?: string };
    expect(body.setupCode).toBe('ABCDEFGHJK');
  });

  it('raises AuthError on 403 for a wrong setup code', async () => {
    const fetchImpl = jsonResponse(403, { title: 'Invalid setup code', detail: 'The setup code is incorrect or expired.' });
    const failure = firstRun('a@example.com', 'A', 'correcthorsebattery', 'wrong', fetchImpl);
    await expect(failure).rejects.toBeInstanceOf(AuthError);
    await expect(failure).rejects.toMatchObject({ status: 403, message: 'The setup code is incorrect or expired.' });
  });
});

describe('logout', () => {
  it('resolves on 204', async () => {
    const fetchImpl = vi.fn(() => Promise.resolve(new Response(null, { status: 204 })));
    await expect(logout(fetchImpl)).resolves.toBeUndefined();
  });

  it('throws on failure', async () => {
    const fetchImpl = vi.fn(() => Promise.resolve(new Response('', { status: 500 })));
    await expect(logout(fetchImpl)).rejects.toThrow('500');
  });
});

describe('fetchAuthStatus after a session was ended (QA S9)', () => {
  afterEach(() => {
    clearSessionEndedNotice();
  });

  const withHeader = (value: string | null, body: unknown): typeof fetch =>
    vi.fn(() => Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: value === null ? {} : { 'X-Draughtsman-Session-Ended': value } })));

  it('notes the reason the server gives when a reload finds the cookie dead', async () => {
    await fetchAuthStatus(withHeader('revoked', { firstRunRequired: false }));
    expect(currentSessionEndedNotice()).toEqual({ reason: 'revoked', keptWork: false });
  });

  it('notes nothing for a caller the server gave no reason, or one who is still signed in', async () => {
    await fetchAuthStatus(withHeader(null, { firstRunRequired: false }));
    expect(currentSessionEndedNotice()).toBeUndefined();
    const user = { id: 'usr_1', email: 'e@x.com', displayName: 'E', role: 'Editor', createdAt: '2026-09-29T00:00:00.000Z', active: true };
    await fetchAuthStatus(withHeader('revoked', { firstRunRequired: false, user }));
    expect(currentSessionEndedNotice()).toBeUndefined();
  });
});

describe('password recovery calls', () => {
  it('reads whether "Forgot password?" is offered from the status, and treats a missing answer as no', async () => {
    expect((await fetchAuthStatus(jsonResponse(200, { firstRunRequired: false, passwordReset: { selfService: true } }))).passwordReset).toEqual({ selfService: true });
    expect((await fetchAuthStatus(jsonResponse(200, { firstRunRequired: false, passwordReset: { selfService: false } }))).passwordReset).toEqual({ selfService: false });
    expect((await fetchAuthStatus(jsonResponse(200, { firstRunRequired: false }))).passwordReset).toEqual({ selfService: false });
  });

  it('asks for a link with the email in the body and returns the server\'s one sentence', async () => {
    const fetchImpl = jsonResponse(202, { message: 'If an account with that address can be reset, we have emailed it a link.' });

    const message = await requestPasswordReset('e@x.com', fetchImpl);

    expect(message).toBe('If an account with that address can be reset, we have emailed it a link.');
    const [url, init] = vi.mocked(fetchImpl).mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/auth/password-reset/request');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ email: 'e@x.com' });
  });

  it('turns a throttled or unavailable request into the server\'s wording', async () => {
    const throttled = jsonResponse(429, { title: 'Too many attempts', detail: 'Too many attempts from this network address; try again in 15 minutes.' });
    await expect(requestPasswordReset('e@x.com', throttled)).rejects.toMatchObject({ status: 429, message: expect.stringContaining('try again in 15 minutes') as unknown });
    await expect(requestPasswordReset('e@x.com', jsonResponse(404, { detail: 'Emailed reset links are not set up on this instance.' }))).rejects.toBeInstanceOf(AuthError);
  });

  it('sends the secret and the password in the body, never in the address', async () => {
    const fetchImpl = vi.fn(() => Promise.resolve(new Response(null, { status: 204 })));

    await completePasswordReset('dpr_secret', 'a-new-password-1', fetchImpl);

    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/auth/password-reset/complete');
    expect(url).not.toContain('dpr_secret');
    expect(JSON.parse(init.body as string)).toEqual({ token: 'dpr_secret', newPassword: 'a-new-password-1' });
  });

  it('tells a refused link from a problem with the password', async () => {
    const refused = completePasswordReset('dpr_x', 'a-new-password-1', jsonResponse(400, { title: 'Link not valid', detail: 'This reset link is not valid.' }));
    await expect(refused).rejects.toMatchObject({ linkRefused: true, status: 400 });
    const tooShort = completePasswordReset('dpr_x', 'short', jsonResponse(400, { title: 'Invalid request', detail: 'Password must be at least 12 characters.' }));
    await expect(tooShort).rejects.toMatchObject({ linkRefused: false, message: 'Password must be at least 12 characters.' });
    await expect(completePasswordReset('dpr_x', 'a-new-password-1', jsonResponse(429, { title: 'Too many attempts', detail: 'slow down' }))).rejects.toBeInstanceOf(ResetLinkError);
  });

  it('creates an administrator link and builds the address from the origin when the server gave none', async () => {
    const created = { token: 'dpr_abc', expiresAt: '2030-01-02T09:00:00.000Z', expiresInHours: 24, userEmail: 'p@x.com' };

    const result = await createResetLink('usr_1', jsonResponse(201, created));

    expect(result.token).toBe('dpr_abc');
    expect(resetLinkOf(result, 'https://draughtsman.example.com')).toBe('https://draughtsman.example.com/reset-password#token=dpr_abc');
    expect(resetLinkOf({ ...result, link: 'https://public.example.com/reset-password#token=dpr_abc' }, 'http://localhost:5180')).toBe('https://public.example.com/reset-password#token=dpr_abc');
    await expect(createResetLink('usr_1', jsonResponse(409, { detail: 'p@x.com is deactivated.' }))).rejects.toMatchObject({ status: 409 });
  });
});
