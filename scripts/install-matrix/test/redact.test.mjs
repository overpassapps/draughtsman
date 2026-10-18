import assert from 'node:assert/strict';
import { test } from 'node:test';
import { bodyExcerpt, describeSetCookie, pickEvidenceHeaders, redactText } from '../lib/redact.mjs';

test('headers: only evidence headers survive; Set-Cookie, Cookie and Authorization never do', () => {
  const headers = new Headers({ ETag: 'W/"dv1:abc"', 'X-Document-Version': 'dv1:abc', 'Set-Cookie': '__Host-ds_session=SECRETVALUE; secure', Cookie: 'ds_session=SECRETVALUE', Authorization: 'Bearer dst_aaaaaaaaaaaaaaaaaaaaaaaa', 'Content-Type': 'application/json', Server: 'nginx' });
  const picked = pickEvidenceHeaders(headers);
  assert.deepEqual(Object.keys(picked).sort(), ['content-type', 'etag', 'server', 'x-document-version']);
  assert.equal(JSON.stringify(picked).includes('SECRETVALUE'), false);
  assert.equal(JSON.stringify(picked).includes('dst_'), false);
});

test('headers: a plain object is read too, with names lower-cased', () => {
  assert.deepEqual(pickEvidenceHeaders({ ETag: '"x"', 'Set-Cookie': 'a=b' }), { etag: '"x"' });
});

test('Set-Cookie: only the name and the attributes are described, never the value', () => {
  const described = describeSetCookie('__Host-ds_session=VERYSECRET; expires=Thu, 05 Nov 2026 11:17:10 GMT; path=/; secure; samesite=lax; httponly');
  assert.equal(described, '__Host-ds_session (Path=/, Secure, SameSite=lax, HttpOnly)');
  assert.equal(described.includes('VERYSECRET'), false);
  assert.equal(describeSetCookie('XSRF-TOKEN=abc'), 'XSRF-TOKEN');
  assert.match(describeSetCookie('ds_session=; max-age=0; path=/'), /Max-Age=0/);
});

test('text: known secret shapes are scrubbed', () => {
  const text = [
    'token dst_abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG',
    'link dpr_abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG',
    'Authorization: Bearer abcdefghijklmnop',
    'Cookie: ds_session=abc123; XSRF-TOKEN=zzz',
    'set ds_session=abc123 and XSRF-TOKEN=zzz inline',
    '{"email":"a@b.test","password":"hunter2hunter2","setupCode":"ABCD234567","token":"dst_x"}',
    'First-run setup code: ABCD234567',
  ].join('\n');
  const clean = redactText(text);
  for (const leak of ['abcdefghijklmnopqrstuvwxyz0123456789', 'abc123', 'zzz', 'hunter2hunter2', 'ABCD234567', 'abcdefghijklmnop']) assert.equal(clean.includes(leak), false, `leaked ${leak}`);
  assert.match(clean, /a@b\.test/); // the email is not a secret
});

test('text: exact secrets the caller holds are removed wherever they appear, and a short string is not mistaken for one', () => {
  assert.equal(redactText('pw is Zq9Xk2LmN8 here, Zq9Xk2LmN8 again', ['Zq9Xk2LmN8']), 'pw is [redacted] here, [redacted] again');
  assert.equal(redactText('ok ab ok', ['ab']), 'ok ab ok');
});

test('excerpt: redacts before it cuts, so a secret straddling the cut cannot survive', () => {
  const body = `${'x'.repeat(1195)}dst_abcdefghijklmnopqrstuvwxyz0123456789`;
  const out = bodyExcerpt(body, [], 1200);
  assert.equal(out.includes('dst_abcdefghij'), false);
  assert.equal(bodyExcerpt('short'), 'short');
});
