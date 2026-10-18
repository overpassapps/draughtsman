// @vitest-environment jsdom
import { csrfHeaders, readCsrfCookie } from './csrf.ts';

describe('readCsrfCookie', () => {
  afterEach(() => {
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT';
  });

  it('reads the XSRF-TOKEN cookie value', () => {
    document.cookie = 'XSRF-TOKEN=abc123';
    expect(readCsrfCookie()).toBe('abc123');
  });

  it('is undefined when no session has issued the cookie yet', () => {
    expect(readCsrfCookie()).toBeUndefined();
  });

  it('decodes a URL-encoded value', () => {
    document.cookie = `XSRF-TOKEN=${encodeURIComponent('a+b/c')}`;
    expect(readCsrfCookie()).toBe('a+b/c');
  });
});

describe('csrfHeaders', () => {
  afterEach(() => {
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT';
  });

  it('attaches X-XSRF-TOKEN once a session has issued the cookie', () => {
    document.cookie = 'XSRF-TOKEN=abc123';
    expect(csrfHeaders()).toEqual({ 'X-XSRF-TOKEN': 'abc123' });
  });

  it('is empty before any cookie exists, so first-run and login are unaffected', () => {
    expect(csrfHeaders()).toEqual({});
  });
});
