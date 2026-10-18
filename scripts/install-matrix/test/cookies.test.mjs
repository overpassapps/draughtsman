import assert from 'node:assert/strict';
import { test } from 'node:test';
import { CookieJar } from '../lib/cookies.mjs';

const SESSION = '__Host-ds_session=abc; path=/; secure; httponly; samesite=lax';

test('strict jar over https keeps a Secure __Host- cookie', () => {
  const jar = new CookieJar();
  jar.store([SESSION], 'https://localhost:8443/api/auth/login');
  assert.equal(jar.header(), '__Host-ds_session=abc');
  assert.deepEqual(jar.dropped, []);
});

test('strict jar over plain http DROPS a Secure cookie, even on 127.0.0.1 (as Safari and any non-localhost address do), and says why', () => {
  const jar = new CookieJar();
  jar.store([SESSION, 'XSRF-TOKEN=t; path=/; secure'], 'http://127.0.0.1:5180/api/auth/login');
  assert.equal(jar.header(), undefined);
  assert.equal(jar.dropped.length, 2);
  assert.match(jar.dropped[0].reason, /Secure over plain http/);
});

test('strict jar over plain http keeps a cookie that is not Secure (forceHttps off)', () => {
  const jar = new CookieJar();
  jar.store(['ds_session=abc; path=/; httponly; samesite=lax'], 'http://127.0.0.1:5180/x');
  assert.equal(jar.header(), 'ds_session=abc');
});

test('lenient jar (curl-like) keeps everything', () => {
  const jar = new CookieJar({ strict: false });
  jar.store([SESSION], 'http://127.0.0.1:5180/x');
  assert.equal(jar.header(), '__Host-ds_session=abc');
});

test('a __Host- cookie without Path=/ is refused even over https', () => {
  const jar = new CookieJar();
  jar.store(['__Host-x=1; secure; path=/api'], 'https://h/');
  assert.equal(jar.header(), undefined);
});

test('expiry and Max-Age=0 delete a cookie; several cookies join into one header', () => {
  const jar = new CookieJar({ strict: false });
  jar.store(['a=1; path=/', 'b=2; path=/'], 'http://h/');
  assert.equal(jar.header(), 'a=1; b=2');
  jar.store(['a=; max-age=0; path=/'], 'http://h/');
  assert.equal(jar.header(), 'b=2');
  jar.store(['b=2; expires=Thu, 01 Jan 1970 00:00:00 GMT'], 'http://h/');
  assert.equal(jar.header(), undefined);
});
