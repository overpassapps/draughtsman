import { AboutError, fetchAbout, supportHref } from './aboutClient.ts';
import { StatusError, fetchAdminStatus } from './statusClient.ts';

const respond = (status: number, body: unknown): typeof fetch => vi.fn(() => Promise.resolve(new Response(JSON.stringify(body), { status })));

describe('fetchAbout', () => {
  const body = { version: '0.1.0', buildDate: '2026-09-30', supportContact: 'a@b.co', noticesUrl: '/THIRD-PARTY-NOTICES.txt' };

  it('reads a well-formed body', async () => {
    expect(await fetchAbout(respond(200, body))).toEqual(body);
  });

  it('reads a body from an earlier server that still sends a licence state, and refuses a missing field and an error status', async () => {
    expect(await fetchAbout(respond(200, { ...body, licenceState: 'unlicensed' }))).toMatchObject(body);
    await expect(fetchAbout(respond(200, { version: '1' }))).rejects.toBeInstanceOf(AboutError);
    await expect(fetchAbout(respond(401, {}))).rejects.toMatchObject({ status: 401 });
  });

  it('links only something that looks like an email address', () => {
    expect(supportHref('draughtsman@overpass.co.uk')).toBe('mailto:draughtsman@overpass.co.uk');
    expect(supportHref(' draughtsman@overpass.co.uk ')).toBe('mailto:draughtsman@overpass.co.uk');
    expect(supportHref('javascript:alert(1)')).toBeUndefined();
    expect(supportHref('call us on 0141')).toBeUndefined();
  });
});

describe('fetchAdminStatus', () => {
  const body = {
    version: '0.1.0', buildDate: '2026-09-30', dataFolder: '/d', host: 'console', logHint: 'x',
    storage: { provider: 'sqlite' }, sidecar: { status: 'running', restarts: 0 }, pdf: { enabled: false, available: false }, ai: { enabled: false },
  };

  it('reads a well-formed body', async () => {
    expect((await fetchAdminStatus(respond(200, body))).sidecar.status).toBe('running');
  });

  it('says it is for administrators on a 403, and refuses an unknown sidecar state', async () => {
    await expect(fetchAdminStatus(respond(403, {}))).rejects.toMatchObject({ status: 403, message: 'Only an administrator can see this.' });
    await expect(fetchAdminStatus(respond(200, { ...body, sidecar: { status: 'melting', restarts: 0 } }))).rejects.toBeInstanceOf(StatusError);
  });
});
