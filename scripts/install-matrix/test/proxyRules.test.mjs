import assert from 'node:assert/strict';
import { test } from 'node:test';
import { forwardedRequestHeaders, shouldGzip, transformResponseHeaders, weakenEtag } from '../lib/proxyRules.mjs';

test('forwarded request: X-Forwarded-Proto https, X-Forwarded-For appended, CF-Connecting-IP, identity encoding asked of the origin', () => {
  const out = forwardedRequestHeaders({ host: 'h', 'x-forwarded-for': '198.51.100.7', 'accept-encoding': 'gzip, br', connection: 'keep-alive' }, '203.0.113.9');
  assert.equal(out['x-forwarded-for'], '198.51.100.7, 203.0.113.9');
  assert.equal(out['x-forwarded-proto'], 'https');
  assert.equal(out['cf-connecting-ip'], '203.0.113.9');
  assert.equal(out['accept-encoding'], undefined);
  assert.equal(out.host, 'h');
});

test('weaken: strong becomes weak once, weak and missing are left alone', () => {
  assert.equal(weakenEtag('"dv1:abc"'), 'W/"dv1:abc"');
  assert.equal(weakenEtag('W/"dv1:abc"'), 'W/"dv1:abc"');
  assert.equal(weakenEtag(undefined), undefined);
});

test('gzip only JSON that is not already encoded, for a visitor that accepts gzip', () => {
  assert.equal(shouldGzip({ 'content-type': 'application/json; charset=utf-8' }, { 'accept-encoding': 'gzip, deflate' }), true);
  assert.equal(shouldGzip({ 'content-type': 'application/json' }, { 'accept-encoding': 'identity' }), false);
  assert.equal(shouldGzip({ 'content-type': 'application/json', 'content-encoding': 'br' }, { 'accept-encoding': 'gzip' }), false);
  assert.equal(shouldGzip({ 'content-type': 'image/png' }, { 'accept-encoding': 'gzip' }), false);
});

const up = { etag: '"dv1:abc"', 'x-document-version': 'dv1:abc', 'content-type': 'application/json', 'content-length': '10', 'set-cookie': ['a=b'] };

test('weak mode: gzip, W/ ETag, X-Document-Version untouched, length dropped, Set-Cookie kept', () => {
  const out = transformResponseHeaders('weak', up, { gzipped: true });
  assert.equal(out.etag, 'W/"dv1:abc"');
  assert.equal(out['x-document-version'], 'dv1:abc');
  assert.equal(out['content-encoding'], 'gzip');
  assert.equal(out['content-length'], undefined);
  assert.match(out.vary, /Accept-Encoding/);
  assert.deepEqual(out['set-cookie'], ['a=b']);
});

test('noetag mode removes the ETag only', () => {
  const out = transformResponseHeaders('noetag', up, { gzipped: true });
  assert.equal(out.etag, undefined);
  assert.equal(out['x-document-version'], 'dv1:abc');
});

test('strip-both mode removes the ETag AND X-Document-Version (the breakage proof)', () => {
  const out = transformResponseHeaders('strip-both', up, { gzipped: false });
  assert.equal(out.etag, undefined);
  assert.equal(out['x-document-version'], undefined);
  assert.equal(out['content-length'], '10');
});

test('pass mode changes nothing; an unknown mode is refused; the input is not mutated', () => {
  const copy = JSON.stringify(up);
  assert.equal(transformResponseHeaders('pass', up, { gzipped: false }).etag, '"dv1:abc"');
  assert.throws(() => transformResponseHeaders('nope', up, { gzipped: false }), /unknown proxy mode/);
  assert.equal(JSON.stringify(up), copy);
});
