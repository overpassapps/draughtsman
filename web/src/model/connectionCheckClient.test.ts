import { ECHO_PATH, fetchSelfCheck, judgeRoundTrip, runRoundTrip, SelfCheckError, type RoundTripObservation } from './connectionCheckClient.ts';

function echoBody(step: 1 | 2, overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({ step, tag: '"sc-0123"', cookieReceived: step === 2, cookieWasSecure: false, scheme: 'http', serverTime: '2026-10-06T12:00:00.000Z', ...overrides });
}

/** A browser's view of the echo through a proxy that does `rewrite` to the headers of every response. */
function fakeProxy(rewrite: (headers: Headers) => void = () => undefined, body: (step: 1 | 2) => string = echoBody): { fetch: typeof fetch; urls: string[] } {
  const urls: string[] = [];
  const impl = vi.fn<typeof fetch>((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    urls.push(url);
    const step = url.endsWith('step=2') ? 2 : 1;
    const headers = new Headers({ 'Content-Type': 'application/json', ETag: '"sc-0123"' });
    rewrite(headers);
    return Promise.resolve(new Response(body(step), { status: 200, headers }));
  });
  return { fetch: impl, urls };
}

describe('runRoundTrip', () => {
  it('calls the echo twice, step 1 then step 2, and records what the browser saw of each', async () => {
    const proxy = fakeProxy();
    const times = [1_000, 1_400];

    const observation = await runRoundTrip(proxy.fetch, () => times.shift() ?? 0);

    expect(proxy.urls).toEqual([`${ECHO_PATH}?step=1`, `${ECHO_PATH}?step=2`]);
    expect(observation.first).toMatchObject({ status: 200, etagHeader: '"sc-0123"', bodyTag: '"sc-0123"', cookieReceived: false, serverTime: '2026-10-06T12:00:00.000Z' });
    expect(observation.second).toMatchObject({ status: 200, cookieReceived: true });
    expect(observation.first?.contentEncoding).toBeUndefined();
    // The browser's clock at the middle of the first call: what the server compares with its own.
    expect(observation.clientTimeMs).toBe(1_200);
    expect(observation.failure).toBeUndefined();
  });

  it('sees a proxy weaken the ETag', async () => {
    const observation = await runRoundTrip(fakeProxy((headers) => {
      headers.set('ETag', 'W/"sc-0123"');
    }).fetch);

    expect(observation.first?.etagHeader).toBe('W/"sc-0123"');
    expect(observation.first?.bodyTag).toBe('"sc-0123"');
    expect(observation.second?.etagHeader).toBe('W/"sc-0123"');
  });

  it('sees a proxy strip the ETag', async () => {
    const observation = await runRoundTrip(fakeProxy((headers) => {
      headers.delete('ETag');
    }).fetch);

    expect(observation.first?.etagHeader).toBeUndefined();
    expect(observation.first?.bodyTag).toBe('"sc-0123"');
  });

  it('sees a proxy add a content-encoding', async () => {
    const observation = await runRoundTrip(fakeProxy((headers) => {
      headers.set('Content-Encoding', 'gzip');
    }).fetch);

    // The fake leaves the body readable; a real browser decodes it. What matters is that the header the proxy added is reported.
    expect(observation.first?.contentEncoding).toBe('gzip');
    expect(observation.second?.contentEncoding).toBe('gzip');
  });

  it('records a cookie that never came back', async () => {
    const observation = await runRoundTrip(fakeProxy(undefined, (step) => echoBody(step, { cookieReceived: false })).fetch);

    expect(observation.second?.cookieReceived).toBe(false);
  });

  it('never throws: a connection that drops is reported as the failure, with whatever was seen before it', async () => {
    let calls = 0;
    const flaky = vi.fn<typeof fetch>(() => {
      calls += 1;
      return calls === 1 ? Promise.resolve(new Response(echoBody(1), { status: 200, headers: { ETag: '"sc-0123"' } })) : Promise.reject(new TypeError('Failed to fetch'));
    });

    const observation = await runRoundTrip(flaky);

    expect(observation.first?.status).toBe(200);
    expect(observation.second).toBeUndefined();
    expect(observation.failure).toBe('Failed to fetch');
  });

  it('keeps the status of a call that did not answer 200 and does not read its body', async () => {
    const blocked = vi.fn<typeof fetch>(() => Promise.resolve(new Response('<html>challenge</html>', { status: 403, headers: { 'Content-Type': 'text/html' } })));

    const observation = await runRoundTrip(blocked);

    expect(observation.first).toEqual({ status: 403, etagHeader: undefined, contentEncoding: undefined });
  });

  it('survives a body that is not JSON', async () => {
    const odd = vi.fn<typeof fetch>(() => Promise.resolve(new Response('not json', { status: 200, headers: { ETag: '"x"' } })));

    const observation = await runRoundTrip(odd);

    expect(observation.first).toMatchObject({ status: 200, etagHeader: '"x"' });
    expect(observation.first?.bodyTag).toBeUndefined();
  });

  it('reports the page as http outside a browser', async () => {
    expect((await runRoundTrip(fakeProxy().fetch)).pageScheme).toBe('http');
  });
});

describe('fetchSelfCheck', () => {
  const RESULT = { generatedAt: '2026-10-06T12:00:00.000Z', summary: { pass: 1, warn: 0, fail: 0 }, items: [{ id: 'proxy', title: 'Reverse proxy', status: 'pass', summary: 'ok' }] };

  it('sends the scheme of the address in the browser and returns the findings', async () => {
    const urls: string[] = [];
    const impl = vi.fn<typeof fetch>((input) => {
      urls.push(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
      return Promise.resolve(new Response(JSON.stringify(RESULT), { status: 200 }));
    });

    const result = await fetchSelfCheck('https', impl);

    expect(urls).toEqual(['/api/admin/selfcheck?pageScheme=https']);
    expect(result.items[0]?.id).toBe('proxy');
  });

  it('says an administrator is needed for a 403 and refuses a body in the wrong shape', async () => {
    await expect(fetchSelfCheck('http', () => Promise.resolve(new Response('{}', { status: 403 })))).rejects.toThrow('Only an administrator');
    await expect(fetchSelfCheck('http', () => Promise.resolve(new Response('{"items":[{"id":1}]}', { status: 200 })))).rejects.toBeInstanceOf(SelfCheckError);
    await expect(fetchSelfCheck('http', () => Promise.resolve(new Response('{}', { status: 500 })))).rejects.toThrow('Could not read');
  });
});

describe('judgeRoundTrip', () => {
  it('posts what the browser saw as JSON, with the CSRF header, and returns the server\'s judgement', async () => {
    const observation: RoundTripObservation = { pageScheme: 'https', first: { status: 200, etagHeader: 'W/"x"', bodyTag: '"x"' }, clientTimeMs: 5 };
    const seen: { url: string; method?: string | undefined; body?: string | undefined; headers?: HeadersInit | undefined }[] = [];
    const impl = vi.fn<typeof fetch>((input, init) => {
      seen.push({ url: typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url, method: init?.method, body: init?.body as string, headers: init?.headers });
      return Promise.resolve(new Response(JSON.stringify({ generatedAt: 'x', summary: { pass: 0, warn: 1, fail: 0 }, items: [{ id: 'etag', title: 'ETag', status: 'warn', summary: 'weakened' }] }), { status: 200 }));
    });

    const result = await judgeRoundTrip(observation, impl);

    expect(seen[0]?.url).toBe('/api/admin/selfcheck/roundtrip');
    expect(seen[0]?.method).toBe('POST');
    expect(JSON.parse(seen[0]?.body ?? '{}')).toEqual(observation);
    expect(result.items[0]?.status).toBe('warn');
  });

  it('refuses an answer in the wrong shape and a failed request', async () => {
    await expect(judgeRoundTrip({}, () => Promise.resolve(new Response('{}', { status: 200 })))).rejects.toBeInstanceOf(SelfCheckError);
    await expect(judgeRoundTrip({}, () => Promise.resolve(new Response('{}', { status: 403 })))).rejects.toThrow('Could not judge');
  });
});
