import assert from 'node:assert/strict';
import { test } from 'node:test';
import { bareVersion, compareVersionHeaders, quotedStrong } from '../lib/versions.mjs';

test('every spelling of a version names the same bare version', () => {
  for (const v of ['W/"dv1:abc"', '"dv1:abc"', 'dv1:abc', ' w/"dv1:abc" ']) assert.equal(bareVersion(v), 'dv1:abc');
  assert.equal(bareVersion(''), null);
  assert.equal(bareVersion(undefined), null);
  assert.equal(bareVersion('""'), null);
  assert.equal(quotedStrong('W/"dv1:abc"'), '"dv1:abc"');
  assert.equal(quotedStrong(null), null);
});

test('a healthy read: strong ETag and X-Document-Version agree', () => {
  const r = compareVersionHeaders({ etag: '"dv1:abc"', xDocumentVersion: 'dv1:abc' });
  assert.deepEqual([r.etagState, r.xdvState, r.agree, r.usable, r.version], ['strong', 'present', true, true, 'dv1:abc']);
  assert.deepEqual(r.notes, []);
});

test('a CDN weakened the ETag: reported as weak, still usable, still agrees', () => {
  const r = compareVersionHeaders({ etag: 'W/"dv1:abc"', xDocumentVersion: 'dv1:abc' });
  assert.equal(r.etagState, 'weak');
  assert.equal(r.agree, true);
  assert.equal(r.usable, true);
  assert.match(r.notes.join(' '), /weakened/);
});

test('a proxy removed the ETag: missing, but X-Document-Version carries the save', () => {
  const r = compareVersionHeaders({ etag: null, xDocumentVersion: 'dv1:abc' });
  assert.deepEqual([r.etagState, r.xdvState, r.agree, r.usable, r.version], ['missing', 'present', null, true, 'dv1:abc']);
  assert.match(r.notes.join(' '), /ETag header did not arrive/);
});

test('only a weak ETag left (X-Document-Version stripped): the ETag is the fallback', () => {
  const r = compareVersionHeaders({ etag: 'W/"dv1:abc"', xDocumentVersion: undefined });
  assert.equal(r.usable, true);
  assert.equal(r.version, 'dv1:abc');
  assert.match(r.notes.join(' '), /X-Document-Version did not arrive/);
});

test('both stripped: unusable, and says so', () => {
  const r = compareVersionHeaders({});
  assert.equal(r.usable, false);
  assert.equal(r.version, null);
  assert.match(r.notes.join(' '), /no version header arrived at all/);
});

test('two headers naming different versions are flagged', () => {
  const r = compareVersionHeaders({ etag: '"dv1:aaa"', xDocumentVersion: 'dv1:bbb' });
  assert.equal(r.agree, false);
  assert.match(r.notes.join(' '), /different versions/);
});
